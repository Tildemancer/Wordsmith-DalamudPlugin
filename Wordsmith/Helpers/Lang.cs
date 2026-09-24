
using System.Net.Http;
using System.Threading.Tasks;
using WeCantSpell.Hunspell;

namespace Wordsmith.Helpers;

/// <summary>
/// The dictionary and the suggestions drawn from it, backed by Hunspell.
/// </summary>
public static partial class Lang
{
    // TildeTools
    private static WordList? _hunspell;

    /// <summary>
    /// The other English dictionary (US vs GB), accepted and suggested alongside the first.
    /// </summary>
    private static WordList? _alternate;

    // TildeTools
    private static readonly HashSet<string> _dictionary = [];

    // TildeTools
    // Kept apart so they survive a reload
    private static readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names supplied from outside, such as the game's own places and characters. Kept
    /// apart from the user's own additions: never saved, never listed as removable.
    /// </summary>
    private static readonly HashSet<string> _supplementary = new(StringComparer.OrdinalIgnoreCase);

    public static int SupplementaryCount
    {
        get
        {
            lock ( _sync )
                return _supplementary.Count;
        }
    }

    // TildeTools
    // Names arrive on a background thread while checks run on the draw thread
    private static readonly object _sync = new();

    // TildeTools
    // Bumped by every change to what counts as a word, so a cached check knows it's stale
    private static int _generation;

    public static int Generation => _generation;

    /// <summary>
    /// Accepts a set of names as correctly spelled, and as words worth suggesting.
    /// Safe to call before the dictionary loads. Early additions are folded in later.
    /// </summary>
    public static void AddSupplementaryWords(IEnumerable<string> words)
    {
        List<string> batch = [];

        foreach ( string word in words )
        {
            string trimmed = word.Trim();
            if ( trimmed.Length < 2 )
                continue;

            // TildeTools
            batch.Add( trimmed );

            // TildeTools
            // Batches, so the draw thread never waits on the whole set
            if ( batch.Count >= 512 )
                Absorb( batch );
        }

        Absorb( batch );
    }

    // TildeTools
    private static void Absorb(List<string> batch)
    {
        if ( batch.Count == 0 )
            return;

        lock ( _sync )
        {
            foreach ( string word in batch )
            {
                _ = _supplementary.Add( word );

                // TildeTools
                // Not ours to remove: a game name is never the user's to unlearn
                Introduce( word, track: false );
            }

            _generation++;
        }

        batch.Clear();
    }

    /// <summary>
    /// Names that are only true for now: the party you are in, the people who have
    /// spoken to you today. Unlike <see cref="_supplementary"/> these are replaced
    /// wholesale, so they are tracked well enough to take back out again.
    /// </summary>
    private static readonly HashSet<string> _transient = new(StringComparer.OrdinalIgnoreCase);

    // TildeTools
    private static readonly HashSet<string> _transientInserted = new(StringComparer.Ordinal);

    /// <summary>
    /// Replaces the transient names with a new set. Everything previously given here
    /// and not in <paramref name="words"/> stops being accepted.
    /// </summary>
    /// <param name="words">The names to accept from now on.</param>
    /// <param name="suggest">
    /// Whether to offer them as corrections too. A name worth accepting is not always
    /// worth suggesting: a stranger standing next to you should not become the fix for
    /// your typo.
    /// </param>
    public static void SetTransientWords(IEnumerable<string> words, bool suggest)
    {
        HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);

        foreach ( string word in words )
        {
            string trimmed = word.Trim();
            if ( trimmed.Length >= 2 )
                _ = wanted.Add( trimmed );
        }

        lock ( _sync )
        {
            // TildeTools
            // Out first, so a name leaving the set stops being accepted now, not at restart
            Withdraw();

            _transient.Clear();
            foreach ( string word in wanted )
                _ = _transient.Add( word );

            _generation++;

            if ( !suggest || _hunspell is null )
                return;

            foreach ( string word in wanted )
            {
                // TildeTools
                // Never claimed if already there, or taking it back out takes the real word too
                if ( _hunspell.Check( word ) )
                    continue;

                if ( _hunspell.Add( word ) )
                    _ = _transientInserted.Add( word );
            }
        }
    }

    /// <summary>
    /// Removes the transient names we inserted. Caller must hold <see cref="_sync"/>.
    /// A name also held by a lasting tier is left alone. Not ours to remove.
    /// </summary>
    private static void Withdraw()
    {
        if ( _hunspell is not null )
        {
            foreach ( string word in _transientInserted )
            {
                if ( _custom.Contains( word ) || _supplementary.Contains( word ) )
                    continue;

                _hunspell.Remove( word );
            }
        }

        _transientInserted.Clear();
    }

    // TildeTools
    private static readonly HashSet<string> _inserted = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a word the dictionary does not already have. The Check is REQUIRED: a duplicate
    /// entry means unlearning it later takes the real word with it. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private static void Introduce(string word, bool track = true)
    {
        if ( _hunspell is null || _hunspell.Check( word ) )
            return;

        if ( _hunspell.Add( word ) && track )
            _ = _inserted.Add( word );
    }

    // TildeTools
    private static void AbsorbSupplementary()
    {
        lock ( _sync )
        {
            if ( _hunspell is null )
                return;

            foreach ( string word in _supplementary )
                Introduce( word, track: false );
        }
    }

    /// <summary>
    /// Releases the dictionaries and everything gathered alongside them. Static state
    /// outlives the module inside a host plugin, so it all has to be dropped by hand.
    /// </summary>
    public static void Unload()
    {
        lock ( _sync )
        {
            _hunspell = null;
            _alternate = null;

            _dictionary.Clear();
            _custom.Clear();
            _supplementary.Clear();
            _inserted.Clear();
            _transient.Clear();
            _transientInserted.Clear();
            _ignored.Clear();
            _generation++;
        }

        Enabled = false;
    }

    // TildeTools
    public static int WordCount
    {
        get
        {
            lock ( _sync )
                return ( _hunspell?.RootCount ?? _dictionary.Count ) + _custom.Count + _supplementary.Count;
        }
    }

    // TildeTools
    public static bool UsingAffixDictionary => _hunspell is not null;

    // TildeTools
    public static bool Enabled
    {
        get => field; set => field = value;
    } = false;

    /// <summary>
    /// Checks whether the word is in the dictionary.
    /// </summary>
    /// <param name="key">String to search for.</param>
    /// <returns><see langword="true""/> if the word is in the dictionary</returns>
    public static bool IsWord(string key) => IsWord(key, true);

    /// <summary>
    /// Checks whether the word is in the dictionary.
    /// </summary>
    /// <param name="key">String to search for.</param>
    /// <param name="lowercase">If <see langword="true"/> then the string is also tried in lowercase.</param>
    /// <returns><see langword="true""/> if the word is in the dictionary</returns>
    public static bool IsWord(string key, bool lowercase)
    {
        string trimmed = key.Trim();

        // TildeTools
        // One lock for the lookup, every set below can be written from a background thread
        lock (_sync)
        {
            if (_custom.Contains(trimmed) || _supplementary.Contains(trimmed) || _ignored.Contains(trimmed))
                return true;

            if (_transient.Contains(trimmed))
                return true;

            if (_hunspell is null)
                return _dictionary.Contains(lowercase ? key.ToLower() : key);

            return Known(_hunspell) || Known(_alternate);
        }

        // TildeTools
        // As written first, affix lookup is case-sensitive: "Paris" passes, "paris" doesn't
        bool Known(WordList? list) =>
            list is not null && (list.Check(key) || (lowercase && list.Check(key.ToLower())));
    }

    // TildeTools
    private static readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    // TildeTools
    public static void IgnoreWord(string word)
    {
        string trimmed = word.Trim();
        if (trimmed.Length == 0)
            return;

        lock (_sync)
        {
            _ = _ignored.Add(trimmed);
            _generation++;
        }
    }

    public static void UnignoreWord(string word)
    {
        lock (_sync)
        {
            _ = _ignored.Remove(word.Trim());
            _generation++;
        }
    }

    public static bool IsIgnored(string word)
    {
        lock (_sync)
            return _ignored.Contains(word.Trim());
    }

    private static void ValidateAndAddWord(string candidate)
    {
        // TildeTools
        string[] splits = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string s in splits)
            _ = _dictionary.Add(s.ToLower());
    }

    // TildeTools
    private static void AddCustomWord(string candidate)
    {
        foreach (string s in candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lock (_sync)
            {
                _ = _custom.Add(s);
                _generation++;

                // TildeTools
                // Into the dictionary too, so it can be suggested, not just accepted
                Introduce(s);
            }
        }
    }

    private static void ValidateConfiguration()
    {
        Match m = DictionaryFileRegex().Match( Wordsmith.Configuration.DictionaryFile );

        if ( !m.Success )
        {
            Wordsmith.Configuration.DictionaryFile = "web: lang_en";
            Wordsmith.Configuration.Save();
        }
    }

    public static void Init() => Init(false);

    private static void Init(bool notify)
    {
        ValidateConfiguration();
        _dictionary.Clear();

        Task t = new(() =>
        {
            // TildeTools
            bool loaded = LoadAffixDictionary();

            if ( !loaded )
                loaded = LoadWebLanguage();

            if ( !loaded )
                loaded = LoadLanguageFile();

            if (!loaded)
            {
                _ =  Wordsmith.NotificationManager.AddNotification(new()
                {
                    Content = $"Failed to load the dictionary {Wordsmith.Configuration.DictionaryFile}. Spellcheck disabled.",
                    Title = "Wordsmith",
                    Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning
                }) ;
            }
            else
            {
                foreach (string word in Wordsmith.Configuration.CustomDictionaryEntries)
                    AddCustomWord(word);

                Enabled = true;

                // TildeTools
                lock ( _sync )
                    _generation++;

                // TildeTools
                // The first check compiles the checker and wakes the dictionary, about 20 ms on the frame of the first paste
                _ = SpellChecker.CheckString("Warming up the spellcheck, with a mistaek and a Name in it.");

                if( notify )
                {
                    _ = Wordsmith.NotificationManager.AddNotification(new()
                    {
                        Content = $"Successfully loaded the dictionary.\n{_dictionary.Count} unique words.",
                        Title = "Wordsmith",
                        Type = Dalamud.Interface.ImGuiNotification.NotificationType.Success
                    });
                }
            }
        });
        t.Start();
    }

    /// <summary>Reinitialize the dictionary.</summary>
    /// <returns><see langword="true"/> if succesfully reinitialized.</returns>
    public static void Reinit() => Init(true);

    /// <summary>
    /// Loads a Hunspell dictionary pair shipped beside the plugin, chosen from the
    /// existing dictionary setting: en_GB when it names GB, UK or British, else en_US.
    /// </summary>
    private static bool LoadAffixDictionary()
    {
        try
        {
            string directory = DictionaryDirectory();
            string name = PreferredAffixDictionary();
            string other = name == "en_GB" ? "en_US" : "en_GB";

            _hunspell = LoadPair( directory, name );
            if ( _hunspell is null )
                return false;

            _alternate = LoadPair( directory, other );

            // TildeTools
            // A loaded dictionary is worth keeping even if folding names in fails
            try
            {
                AbsorbSupplementary();
            }
            catch ( Exception e )
            {
                Wordsmith.PluginLog.Error( $"Loaded the dictionary but could not add the supplied names.\n{e}" );
            }

            Wordsmith.PluginLog.Information(
                $"Loaded the {name} affix dictionary: {_hunspell.RootCount} root words" +
                ( _alternate is null ? "." : $", with {other} accepted alongside it." ) );

            return true;
        }
        catch ( Exception e )
        {
            _hunspell = null;
            _alternate = null;
            Wordsmith.PluginLog.Error( $"Unable to load an affix dictionary.\n{e}" );
            return false;
        }
    }

    // TildeTools
    private static WordList? LoadPair(string directory, string name)
    {
        string affix = Path.Combine( directory, $"{name}.aff" );
        string words = Path.Combine( directory, $"{name}.dic" );

        if ( File.Exists( affix ) && File.Exists( words ) )
            return WordList.CreateFromFiles( words, affix );

        Wordsmith.PluginLog.Information( $"No affix dictionary at {words}." );
        return null;
    }

    /// <summary>
    /// Where the bundled dictionaries sit. Taken from this assembly, not the plugin
    /// interface, which reports the host's folder when Wordsmith runs inside one.
    /// </summary>
    private static string DictionaryDirectory()
    {
        string? beside = Path.GetDirectoryName( typeof( Lang ).Assembly.Location );

        if ( string.IsNullOrEmpty( beside ) )
            beside = Wordsmith.PluginInterface.AssemblyLocation.Directory?.FullName;

        return Path.Combine( beside ?? string.Empty, "Dictionaries" );
    }

    private static string PreferredAffixDictionary()
    {
        string configured = Wordsmith.Configuration.DictionaryFile ?? string.Empty;

        return BritishDictionaryRegex().IsMatch( configured ) ? "en_GB" : "en_US";
    }

    private static bool LoadWebLanguage()
    {
        Match m = WebDictionaryRegex().Match( Wordsmith.Configuration.DictionaryFile );
        if (!m.Success)
        {
            Wordsmith.PluginLog.Debug( $"Wordsmith is not configured for web dictionary file. Skipping LoadWebLanguage()" );
            return false;
        }

        string title = m.Groups[1].Value;

        if ( !Wordsmith.WebManifest.IsLoaded || !Wordsmith.WebManifest.Dictionaries.Contains(title) )
            return false;

        try
        {
            string[] lines = Git.LoadDictionary(title);
            if ( lines.Length == 0 )
                throw new Exception();

            foreach( string l in lines )
            {
                if( !l.StartsWith( '#' ) && l.Trim().Length > 0 )
                    ValidateAndAddWord( l );
            }

            return true;
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode != System.Net.HttpStatusCode.OK)
                Wordsmith.PluginLog.Error($"Unable to load language from {Wordsmith.Configuration.DictionaryFile}. Http Status Code: {e.StatusCode}");
            else
                Wordsmith.PluginLog.Error($"Unable to load language from {Wordsmith.Configuration.DictionaryFile}.\n{e}");
            return false;
        }
        catch (Exception e)
        {
            Wordsmith.PluginLog.Error($"Unable to load language from {Wordsmith.Configuration.DictionaryFile}.\n{e}");
            return false;
        }
    }

    // TildeTools
    private static bool LoadLanguageFile()
    {
        Match m = LocalDictionaryRegex().Match( Wordsmith.Configuration.DictionaryFile );
        if ( !m.Success )
        {
            Wordsmith.PluginLog.Debug( $"Not configured for local language file. Skipping LoadLanguageFile()." );
            return false;
        }

        string title = m.Groups[1].Value;

        string filepath = Path.Combine(Wordsmith.PluginInterface.AssemblyLocation.Directory?.FullName!, $"Dictionaries\\{title}");

        if (!File.Exists(filepath))
            return false;

        try
        {
            string[] lines = File.ReadAllLines(filepath);

            foreach( string l in lines )
            {
                if( !l.StartsWith( '#' ) && l.Trim().Length > 0 )
                    ValidateAndAddWord( l );
            }

            return true;
        }
        catch (Exception e)
        {
            Wordsmith.PluginLog.Error($"Unable to load language from {Wordsmith.Configuration.DictionaryFile}.\n{e}");
        }
        return false;
    }

    /// <summary>Attempts to add a word to the custom dictionary.</summary>
    /// <param name="word">String to search.</param>
    /// <returns><see langword="true"/> if the word was not in the dictionary already.</returns>
    public static bool AddDictionaryEntry(string word)
    {
        string trimmed = word.Trim();

        if ( trimmed.Length == 0 )
            return false;

        lock ( _sync )
        {
            if ( !_custom.Add( trimmed ) )
                return false;

            // TildeTools
            _generation++;

            // TildeTools
            Introduce( trimmed );
        }

        // TildeTools
        Wordsmith.Configuration.CustomDictionaryEntries.Add( trimmed );
        Wordsmith.Configuration.Save();
        return true;
    }

    /// <summary>Attempt to remove a word from the custom dictionary.</summary>
    /// <param name="word">String to remove</param>
    public static void RemoveDictionaryEntry(string word)
    {
        string trimmed = word.Trim();

        lock ( _sync )
        {
            _ = _custom.Remove( trimmed );
            _ = _dictionary.Remove( trimmed.ToLower() );

            // TildeTools
            _generation++;

            // TildeTools
            // Only a word we put in is ours to take out
            if ( _inserted.Remove( trimmed ) && !_supplementary.Contains( trimmed ) )
                _ = _hunspell?.Remove( trimmed );
        }

        // TildeTools
        // Entries from before the list kept casing are lowercase
        _ = Wordsmith.Configuration.CustomDictionaryEntries.Remove( trimmed );
        _ = Wordsmith.Configuration.CustomDictionaryEntries.Remove( trimmed.ToLower() );
        Wordsmith.Configuration.Save();
    }

    // TildeTools
    internal static IReadOnlyList<string> GetSuggestions(string word)
    {
        if ( word.Length == 0 )
            throw new Exception( $"GetSuggestions({word}) failed. Word must have length." );

        if ( _hunspell is not null )
        {
            try
            {
                // TildeTools
                // Held for the whole call, Suggest walks the dictionary
                lock ( _sync )
                    return Interleave(
                        _hunspell.Suggest( word ),
                        _alternate?.Suggest( word ) ?? [],
                        Wordsmith.Configuration.MaximumSuggestions );
            }
            catch ( Exception e )
            {
                Wordsmith.PluginLog.Error( $"Suggestions for \"{word}\" failed.\n{e}" );
                return [];
            }
        }

        return LegacySuggestions( word );
    }

    /// <summary>
    /// Merges two ranked lists by taking from each in turn. They are ranked on separate
    /// scales, so alternating keeps both first choices near the top.
    /// </summary>
    private static IReadOnlyList<string> Interleave(IEnumerable<string> first, IEnumerable<string> second, int limit)
    {
        List<string> merged = [];
        HashSet<string> seen = new( StringComparer.OrdinalIgnoreCase );

        using IEnumerator<string> a = first.GetEnumerator();
        using IEnumerator<string> b = second.GetEnumerator();

        bool more = true;
        while ( more && merged.Count < limit )
        {
            more = false;

            foreach ( IEnumerator<string> source in (IEnumerator<string>[])[a, b] )
            {
                if ( merged.Count >= limit || !source.MoveNext() )
                    continue;

                more = true;
                if ( seen.Add( source.Current ) )
                    merged.Add( source.Current );
            }
        }

        return merged;
    }

    // TildeTools
    private static IReadOnlyList<string> LegacySuggestions(string word)
    {
        bool isCapped = WordRegex().IsMatch( word );

        word = word.ToLower();

        // TildeTools
        Task<List<string>> aways = new(() => GenerateAway(word, 2, isCapped, true));
        aways.Start();

        Task<List<string>> transpose = new(() => GenerateTranspose(word, isCapped, true));
        transpose.Start();

        Task<List<string>> splits = new(() => GenerateSplits(word));
        splits.Start();

        Task<List<string>> deletes = new(() => GenerateDeletes(word, isCapped, true));
        deletes.Start();

        List<string> results = [];

        void AddResults(Task<List<string>> t)
        {
            int index = 0;
            while ( results.Count <= Wordsmith.Configuration.MaximumSuggestions && index < t.Result.Count && IsWord( t.Result[index] ) )
                results.Add( t.Result[index++] );
        }
        transpose.Wait();
        AddResults( transpose );

        aways.Wait();
        AddResults( aways );

        splits.Wait();
        AddResults( splits );

        deletes.Wait();
        AddResults( deletes );

        return results;
    }

    private static List<string> GenerateTranspose(string word, bool isCapped, bool filter)
    {
        List<string> results = [];

        // Letter swaps
        for (int x = 0; x < word.Length - 1; ++x)
        {
            char[] chars = word.ToCharArray();

            char y = chars[x];
            chars[x] = chars[x + 1];
            chars[x + 1] = y;

            if (!filter || IsWord(new string(chars)))
                results.Add(isCapped ? new string(chars).CaplitalizeFirst() : new string(chars));
        }
        return results;
    }

    private static List<string> GenerateDeletes(string word, bool isCapped, bool filter)
    {
        if (word.Length == 0)
            return [];

        List<string> results = [];
        for (int i = 0; i < word.Length; ++i)
        {
            if (!filter || IsWord(word.Remove(i, 1)))
                results.Add(isCapped ? word.Remove(i, 1).CaplitalizeFirst() : word.Remove(i, 1));
        }

        return results;
    }

    private static List<string> GenerateSplits(string word)
    {
        List<string> results = [];
        for (int i = 1; i < word.Length - 1; ++i)
        {
            string[] splits = [word[0..i], word[i..^0]];
            if (IsWord(splits[0]) && IsWord(splits[1]))
                results.Add($"{splits[0]} {splits[1]}");
        }
        return results;
    }

    private static List<string> GenerateAway(string word, int depth, bool isCapped, bool filter)
    {
        string letters = "abcdefghijklmnopqrstuvwxyz";
        List<string> results = [];
        try
        {
            // TildeTools
            // z toggles vowel and consonant passes
            for ( int z = 0; z < 2; z++ )
            {
                for ( int x = 0; x < word.Length; ++x )
                {
                    for ( int y = 0; y < letters.Length; ++y )
                    {
                        char[] chars = word.ToCharArray();

                        // TildeTools
                        if( "aAeEiIoOuUyY".Contains( chars[x] ) == ( z == 0 ) )
                        {
                            chars[x] = letters[y];
                            string test = new(chars);

                            if( ( !filter || IsWord( test ) ) && !results.Contains( test ) )
                                results.Add( isCapped ? test.CaplitalizeFirst() : test );
                        }

                        // TildeTools
                        else
                        { 
                            break;
                        }
                    }
                }
            }

            for ( int y = 0; y < letters.Length; ++y )
            {
                // Insert a character before the word
                string foretest = $"{letters[y]}{word}";

                if ( (!filter || IsWord( foretest )) && !results.Contains( foretest ) )
                    results.Add( foretest );

                // Append a character to the word
                string afttest = $"{word}{letters[y]}";

                if ( (!filter || IsWord( afttest )) && !results.Contains( afttest ) )
                    results.Add( afttest );
            }

            if ( depth > 1 )
            {
                List<string> parents = [.. results];
                foreach ( string s in parents )
                    results.AddRange( GenerateAway( s, depth - 1, isCapped, depth > 2 ) );
            }
        }
        catch ( Exception e )
        {
            Wordsmith.PluginLog.Error( e.ToString() );
        }
        return results;
    }

    [GeneratedRegex( @"^(?:web|local):\s.+" )]
    private static partial Regex DictionaryFileRegex();
    [GeneratedRegex( @"^(?:web: )*(.+)" )]
    private static partial Regex WebDictionaryRegex();
    [GeneratedRegex( @"^(?:local: )*(.+)" )]
    private static partial Regex LocalDictionaryRegex();
    [GeneratedRegex( @"^\s*[A-Z].*" )]
    private static partial Regex WordRegex();
    [GeneratedRegex( @"(?i)\b(?:gb|uk|british|en[_-]?gb)\b" )]
    private static partial Regex BritishDictionaryRegex();
}
