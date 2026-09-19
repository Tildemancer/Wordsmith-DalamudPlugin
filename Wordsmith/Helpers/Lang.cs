
using System.Net.Http;
using System.Threading.Tasks;
using WeCantSpell.Hunspell;

namespace Wordsmith.Helpers;

/// <summary>
/// The dictionary and the suggestions drawn from it, backed by Hunspell.
/// </summary>
public static partial class Lang
{
    /// <summary>The loaded dictionary, or null until <see cref="Init()"/> succeeds.</summary>
    private static WordList? _hunspell;

    /// <summary>
    /// The other English dictionary (US vs GB), accepted and suggested alongside the first.
    /// </summary>
    private static WordList? _alternate;

    /// <summary>The flat word list, used only when no affix dictionary can be found.</summary>
    private static readonly HashSet<string> _dictionary = [];

    /// <summary>Words the user has taught it, held apart so they survive a reload.</summary>
    private static readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names supplied from outside, such as the game's own places and characters. Kept
    /// apart from the user's own additions: never saved, never listed as removable.
    /// </summary>
    private static readonly HashSet<string> _supplementary = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many supplied names are being accepted.</summary>
    public static int SupplementaryCount
    {
        get
        {
            lock ( _sync )
                return _supplementary.Count;
        }
    }

    /// <summary>Guards the dictionary: names arrive on a background thread, checks run on the drawing one.</summary>
    private static readonly object _sync = new();

    /// <summary>
    /// Accepts a set of names as correctly spelled, and as words worth suggesting.
    /// Safe to call before the dictionary loads; early additions are folded in later.
    /// </summary>
    public static void AddSupplementaryWords(IEnumerable<string> words)
    {
        List<string> batch = [];

        foreach ( string word in words )
        {
            string trimmed = word.Trim();
            if ( trimmed.Length < 2 )
                continue;

            // Added to the set inside Absorb, under the lock.
            batch.Add( trimmed );

            // Handed over in batches so the drawing thread never waits on the whole set.
            if ( batch.Count >= 512 )
                Absorb( batch );
        }

        Absorb( batch );
    }

    /// <summary>Folds a batch of names into the loaded dictionary, then empties it.</summary>
    private static void Absorb(List<string> batch)
    {
        if ( batch.Count == 0 )
            return;

        lock ( _sync )
        {
            foreach ( string word in batch )
            {
                _ = _supplementary.Add( word );

                // Not tracked as ours to remove: a game name is never the user's to unlearn.
                Introduce( word, track: false );
            }
        }

        batch.Clear();
    }

    /// <summary>Words this added to the dictionary, which are therefore its to take back out.</summary>
    private static readonly HashSet<string> _inserted = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a word the dictionary does not already have. The Check is required: a duplicate
    /// entry means unlearning it later removes the real word too. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private static void Introduce(string word, bool track = true)
    {
        if ( _hunspell is null || _hunspell.Check( word ) )
            return;

        if ( _hunspell.Add( word ) && track )
            _ = _inserted.Add( word );
    }

    /// <summary>Puts the names collected so far into a dictionary that has just loaded.</summary>
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
    /// outlives the module inside a host plugin, so it must be dropped by hand.
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
            _ignored.Clear();
        }

        Enabled = false;
    }

    /// <summary>How many words the dictionary holds, for diagnosing an empty check.</summary>
    public static int WordCount
    {
        get
        {
            lock ( _sync )
                return ( _hunspell?.RootCount ?? _dictionary.Count ) + _custom.Count + _supplementary.Count;
        }
    }

    /// <summary>Whether the real affix dictionary is in use, rather than the fallback list.</summary>
    public static bool UsingAffixDictionary => _hunspell is not null;

    /// <summary>True once Init() has loaded a language file.</summary>
    public static bool Enabled
    {
        get => field; set => field = value;
    } = false;

    /// <summary>
    /// Verifies that the string exists in the hash table
    /// </summary>
    /// <param name="key">String to search for.</param>
    /// <returns><see langword="true""/> if the word is in the dictionary</returns>
    public static bool IsWord(string key) => IsWord(key, true);

    /// <summary>
    /// Verifies that the string exists in the hash table
    /// </summary>
    /// <param name="key">String to search for.</param>
    /// <param name="lowercase">If <see langword="true"/> then the string is also tried in lowercase.</param>
    /// <returns><see langword="true""/> if the word is in the dictionary</returns>
    public static bool IsWord(string key, bool lowercase)
    {
        string trimmed = key.Trim();

        // One lock for the whole lookup; every set below can be written from a background thread.
        lock (_sync)
        {
            if (_custom.Contains(trimmed) || _supplementary.Contains(trimmed) || _ignored.Contains(trimmed))
                return true;

            if (_hunspell is null)
                return _dictionary.Contains(lowercase ? key.ToLower() : key);

            return Known(_hunspell) || Known(_alternate);
        }

        // As written first: capitalisation is meaningful to an affix dictionary ("Paris" vs "paris").
        bool Known(WordList? list) =>
            list is not null && (list.Check(key) || (lowercase && list.Check(key.ToLower())));
    }

    /// <summary>Words left alone without learning them. Cleared on restart.</summary>
    private static readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stops flagging a word for the rest of the session.</summary>
    public static void IgnoreWord(string word)
    {
        string trimmed = word.Trim();
        if (trimmed.Length == 0)
            return;

        lock (_sync)
            _ = _ignored.Add(trimmed);
    }

    /// <summary>Flags the word again, undoing <see cref="IgnoreWord"/>.</summary>
    public static void UnignoreWord(string word)
    {
        lock (_sync)
            _ = _ignored.Remove(word.Trim());
    }

    /// <summary>Whether a word is being left alone for this session.</summary>
    public static bool IsIgnored(string word)
    {
        lock (_sync)
            return _ignored.Contains(word.Trim());
    }

    private static void ValidateAndAddWord(string candidate)
    {
        // A candidate entry may hold several words.
        string[] splits = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string s in splits)
            _ = _dictionary.Add(s.ToLower());
    }

    /// <summary>Takes one of the user's own entries, which may hold several words.</summary>
    private static void AddCustomWord(string candidate)
    {
        foreach (string s in candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lock (_sync)
            {
                _ = _custom.Add(s);

                // Also into the dictionary, so it can be suggested and not merely accepted.
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

    /// <summary>Load the language file and enable spell checks.</summary>
    public static void Init() => Init(false);

    private static void Init(bool notify)
    {
        ValidateConfiguration();
        _dictionary.Clear();

        Task t = new(() =>
        {
            // Affix dictionary first; the flat lists are the fallback.
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
                //Wordsmith.PluginInterface.UiBuilder.AddNotification($"Failed to load the dictionary {Wordsmith.Configuration.DictionaryFile}. Spellcheck disabled.", "Wordsmith", Dalamud.Interface.Internal.Notifications.NotificationType.Warning);
            }
            else
            {
                foreach (string word in Wordsmith.Configuration.CustomDictionaryEntries)
                    AddCustomWord(word);

                Enabled = true;
                if( notify )
                {
                    _ = Wordsmith.NotificationManager.AddNotification(new()
                    {
                        Content = $"Successfully loaded the dictionary.\n{_dictionary.Count} unique words.",
                        Title = "Wordsmith",
                        Type = Dalamud.Interface.ImGuiNotification.NotificationType.Success
                    });
                    //Wordsmith.PluginInterface.UiBuilder.AddNotification($"Successfully loaded the dictionary.\n{_dictionary.Count} unique words.", "Wordsmith", Dalamud.Interface.Internal.Notifications.NotificationType.Success);
                }
            }
        });
        t.Start();
    }

    /// <summary>
    /// Reinitialize the dictionary.
    /// </summary>
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

            // Caught separately: a loaded dictionary is worth keeping even if folding
            // the supplied names into it fails.
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

    /// <summary>Reads one .aff/.dic pair, or null when it is not there.</summary>
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

    /// <summary>Picks a dictionary from the configured language name.</summary>
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

    /// <summary>Loads the specified language file.</summary>
    private static bool LoadLanguageFile()
    {
        Match m = LocalDictionaryRegex().Match( Wordsmith.Configuration.DictionaryFile );
        if ( !m.Success )
        {
            Wordsmith.PluginLog.Debug( $"Not configured for local language file. Skipping LoadLanguageFile()." );
            return false;
        }

        string title = m.Groups[1].Value;

        string filepath = Path.Combine(Wordsmith.PluginInterface.AssemblyLocation.Directory?.FullName!, $"Dictionaries\\{title}"); // Wordsmith.Configuration.DictionaryFile.Replace($"local: ", "")}");

        if (!File.Exists(filepath))
            return false;

        try
        {
            string[] lines = File.ReadAllLines(filepath);

            foreach( string l in lines )
            {
                if( !l.StartsWith( '#' ) && l.Trim().Length > 0 )
                    ValidateAndAddWord( l ); //_dictionary.Add( l.Trim().ToLower() );
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

            // Into the dictionary as well, so it can be suggested and not only accepted.
            Introduce( trimmed );
        }

        // Stored as typed; the lookup ignores case either way.
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

            // Only a word this put into the dictionary is this one's to take back out.
            if ( _inserted.Remove( trimmed ) && !_supplementary.Contains( trimmed ) )
                _ = _hunspell?.Remove( trimmed );
        }

        // Entries added before the custom list kept its casing are still lowercase.
        _ = Wordsmith.Configuration.CustomDictionaryEntries.Remove( trimmed );
        _ = Wordsmith.Configuration.CustomDictionaryEntries.Remove( trimmed.ToLower() );
        Wordsmith.Configuration.Save();
    }

    /// <summary>Words that might have been meant instead, best first.</summary>
    internal static IReadOnlyList<string> GetSuggestions(string word)
    {
        if ( word.Length == 0 )
            throw new Exception( $"GetSuggestions({word}) failed. Word must have length." );

        if ( _hunspell is not null )
        {
            try
            {
                // Held throughout: Suggest walks the dictionary as it goes.
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
    /// Merges two ranked lists by taking from each in turn. They are sorted on separate
    /// scales, so alternating is what keeps both first choices near the top.
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

    /// <summary>
    /// The original unranked suggestions, used when only a flat word list loaded.
    /// </summary>
    private static IReadOnlyList<string> LegacySuggestions(string word)
    {
        bool isCapped = WordRegex().IsMatch( word ); //"ABCDEFGHIJKLMNOPQRSTUVWXYZ".Contains(word[0]);

        word = word.ToLower();

        // GenerateAway starts first; it is by far the longest.
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
            // z toggles between vowel and consonant generation.
            for ( int z = 0; z < 2; z++ )
            {
                for ( int x = 0; x < word.Length; ++x )
                {
                    for ( int y = 0; y < letters.Length; ++y )
                    {
                        char[] chars = word.ToCharArray();

                        // Vowels first; they are the more common mistake.
                        if( "aAeEiIoOuUyY".Contains( chars[x] ) == ( z == 0 ) )
                        {
                            chars[x] = letters[y];
                            string test = new(chars);

                            if( ( !filter || IsWord( test ) ) && !results.Contains( test ) )
                                results.Add( isCapped ? test.CaplitalizeFirst() : test );
                        }

                        // Wrong character type for this pass: skip its 26 letters.
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
