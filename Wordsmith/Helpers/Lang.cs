
using System.Net.Http;
using System.Threading.Tasks;
using WeCantSpell.Hunspell;

namespace Wordsmith.Helpers;

/// <summary>
/// The dictionary and the suggestions drawn from it.
///
/// This is Hunspell — the checker behind Chrome, Firefox and LibreOffice — reading
/// the same affix-compressed dictionaries they do. An affix dictionary stores root
/// words plus rules for the forms built from them, so it knows that "reworked"
/// follows from "work" without listing it, and a suggestion can be built by
/// applying those rules in reverse.
///
/// It replaced a flat word list whose suggestions were generated four ways and
/// returned in the order the generators happened to emit them, with nothing scoring
/// or ranking the results. That is why "mispelled" used to suggest "dispelled".
/// </summary>
public static partial class Lang
{
    /// <summary>The loaded dictionary, or null until <see cref="Init()"/> succeeds.</summary>
    private static WordList? _hunspell;

    /// <summary>
    /// The other English dictionary, accepted alongside the first.
    ///
    /// The game's own English is British — armour, realise, harbour — while most
    /// players type American, and a roleplayer switching between them is not making
    /// a mistake worth marking. Both spellings are accepted, and both are offered
    /// when suggesting, so neither is treated as the wrong one.
    /// </summary>
    private static WordList? _alternate;

    /// <summary>
    /// The flat word list, used only when no affix dictionary can be found.
    ///
    /// This is the old engine, kept as a fallback so a missing dictionary file
    /// leaves spellchecking degraded rather than absent.
    /// </summary>
    private static readonly HashSet<string> _dictionary = [];

    /// <summary>
    /// Words the user has taught it, held apart from the dictionary so they survive
    /// a reload and can be taken back out again.
    /// </summary>
    private static readonly HashSet<string> _custom = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names supplied from outside, such as the game's own places and characters.
    ///
    /// No English dictionary contains Thanalan or Y'shtola, and a roleplayer writes
    /// little else. These are kept apart from the user's own additions so they are
    /// never written into the configuration and never listed as words to remove;
    /// whoever supplies them owns them, and they arrive again on the next load.
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

    /// <summary>
    /// Guards the dictionary while names are being added to it.
    ///
    /// They arrive on a background thread and the text being typed is checked on the
    /// drawing one, so without this the dictionary would be read while it was being
    /// written to.
    /// </summary>
    private static readonly object _sync = new();

    /// <summary>
    /// Accepts a set of names as correctly spelled, and as words worth suggesting.
    ///
    /// They go into the dictionary itself rather than beside it, so a near miss finds
    /// them: "Gridani" offers "Gridania" only because the dictionary has heard of it.
    /// The separate set is kept as well, since a name entered this way is matched by
    /// the dictionary only as it was capitalised.
    ///
    /// Safe to call before or after the dictionary loads. Anything added early is
    /// still accepted, and is folded into the dictionary when one arrives.
    /// </summary>
    public static void AddSupplementaryWords(IEnumerable<string> words)
    {
        List<string> batch = [];

        foreach ( string word in words )
        {
            string trimmed = word.Trim();
            if ( trimmed.Length < 2 )
                continue;

            // Added to the set inside Absorb, under the lock. Adding it here instead
            // would write the set from this thread while the drawing thread reads it.
            batch.Add( trimmed );

            // Handed over a little at a time, so the drawing thread is never kept
            // waiting on the whole set.
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

                // Not tracked as ours to remove: a game name is never the user's to
                // unlearn, so tracking tens of thousands of them only retains strings
                // nothing can act on.
                Introduce( word, track: false );
            }
        }

        batch.Clear();
    }

    /// <summary>
    /// Words this added to the dictionary, which are therefore its to take back out.
    /// </summary>
    private static readonly HashSet<string> _inserted = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a word the dictionary does not already have.
    ///
    /// The check matters: adding a word it knows gives it a second entry for that
    /// word, and removing one of those removes both — so learning "colour" and then
    /// unlearning it would take the real "colour" with it, and every form built from
    /// it. Caller must hold <see cref="_sync"/>.
    /// </summary>
    private static void Introduce(string word, bool track = true)
    {
        if ( _hunspell is null || _hunspell.Check( word ) )
            return;

        if ( _hunspell.Add( word ) && track )
            _ = _inserted.Add( word );
    }

    /// <summary>
    /// Puts the names collected so far into a dictionary that has just loaded.
    ///
    /// Without this, names supplied while the dictionary was still being read would
    /// be accepted but never suggested.
    /// </summary>
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
    /// Releases the dictionaries and everything gathered alongside them.
    ///
    /// These are static, so within a host plugin they outlive the module unless it
    /// says so: switching Wordsmith off would leave both Hunspell dictionaries and
    /// every game name resident for something no longer running. Supplied names are
    /// dropped too, since whoever supplied them hands them over again on the next load.
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

    /// <summary>
    /// Active becomes true after Init() has successfully loaded a language file.
    /// </summary>
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

        // One lock for the whole lookup. Every set below is written by a background
        // thread — the vocabulary loader, or a word added from another plugin — while
        // this runs on the drawing thread, and a HashSet read during a write can throw
        // or silently miss.
        lock (_sync)
        {
            if (_custom.Contains(trimmed) || _supplementary.Contains(trimmed) || _ignored.Contains(trimmed))
                return true;

            if (_hunspell is null)
                return _dictionary.Contains(lowercase ? key.ToLower() : key);

            return Known(_hunspell) || Known(_alternate);
        }

        // Tried as written first, because capitalisation is meaningful to an affix
        // dictionary: it knows "Paris" is a word and "paris" is not. Folding the case
        // afterwards keeps a sentence's first word from being flagged for it.
        bool Known(WordList? list) =>
            list is not null && (list.Check(key) || (lowercase && list.Check(key.ToLower())));
    }

    /// <summary>
    /// Words to leave alone for now without learning them: a name, a bit of slang,
    /// something spelled oddly on purpose. Cleared when the game restarts, which is
    /// what separates it from adding to the dictionary.
    /// </summary>
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
        // Split and trim the candidate into all possible words. This should break entries with multiple words into single entries.
        string[] splits = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string s in splits)
            _ = _dictionary.Add(s.ToLower());
    }

    /// <summary>
    /// Takes one of the user's own entries, which may hold several words.
    ///
    /// These go to the custom list rather than into the dictionary, so they are the
    /// same words whichever dictionary is loaded underneath them.
    /// </summary>
    private static void AddCustomWord(string candidate)
    {
        foreach (string s in candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            lock (_sync)
            {
                _ = _custom.Add(s);

                // Also a word the dictionary should be able to suggest, not merely one
                // it stops marking.
                Introduce(s);
            }
        }
    }

    private static void ValidateConfiguration()
    {
        Match m = DictionaryFileRegex().Match( Wordsmith.Configuration.DictionaryFile );

        // If the configuration does not have a web or local setting, set to default.
        if ( !m.Success )
        {
            Wordsmith.Configuration.DictionaryFile = "web: lang_en";
            Wordsmith.Configuration.Save();
        }
    }

    /// <summary>
    /// Load the language file and enable spell checks.
    /// </summary>
    public static void Init() => Init(false);

    private static void Init(bool notify)
    {
        ValidateConfiguration();
        _dictionary.Clear();

        // Validate the entry in the configuration

        Task t = new(() =>
        {
            // The affix dictionary first: it is the one that knows word forms and can
            // rank its suggestions. The flat lists below are the fallback.
            bool loaded = LoadAffixDictionary();

            if ( !loaded )
                loaded = LoadWebLanguage();

            // If web loading failed, load the file
            if ( !loaded )
                loaded = LoadLanguageFile();

            // If both failed to load then present the failure notification
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
                // Add all of the custom dictionary entries to the dictionary
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
    /// Loads a Hunspell dictionary pair shipped beside the plugin.
    ///
    /// Which pair is chosen follows the existing dictionary setting, so "web: lang_en"
    /// and "local: lang_en" both land on en_US; anything naming GB, UK or British
    /// takes en_GB. A setting that matches nothing leaves this to the flat lists.
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

            // Names may have been supplied while this was still reading. Caught on its
            // own, because a dictionary that loaded is worth keeping even if folding
            // the names into it fails: letting that failure reach the handler below
            // would throw away a working dictionary and drop silently back to the flat
            // word list, with nothing but a log line to say the engine had changed.
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
    /// Where the bundled dictionaries sit.
    ///
    /// Taken from this assembly rather than from the plugin interface, because when
    /// Wordsmith runs inside another plugin the interface reports that host's folder
    /// and the files travel with this assembly.
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

        // If the dictionary isn't in the manifest the user may have a custom dictionary
        // file that they prefer to use. Check for its existence here.
        if ( !Wordsmith.WebManifest.IsLoaded || !Wordsmith.WebManifest.Dictionaries.Contains(title) )
            return false;

        try
        {
            // Load the dictionary array
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

    /// <summary>
    /// Loads the specified language file.
    /// </summary>
    private static bool LoadLanguageFile()
    {
        Match m = LocalDictionaryRegex().Match( Wordsmith.Configuration.DictionaryFile );
        if ( !m.Success )
        {
            Wordsmith.PluginLog.Debug( $"Not configured for local language file. Skipping LoadLanguageFile()." );
            return false;
        }

        string title = m.Groups[1].Value;

        // Get the filepath of the dictionary file
        string filepath = Path.Combine(Wordsmith.PluginInterface.AssemblyLocation.Directory?.FullName!, $"Dictionaries\\{title}"); // Wordsmith.Configuration.DictionaryFile.Replace($"local: ", "")}");

        // If the file doesn't exist then abort
        if (!File.Exists(filepath))
            return false;

        try
        {
            // Read the content to an array.
            string[] lines = File.ReadAllLines(filepath);

            // Iterate over each word and add it to the dictionary
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

    /// <summary>
    /// Attempts to add a word to the custom dictionary.
    /// </summary>
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

        // Kept as typed rather than folded down, so a name added as "Ashwood" is
        // remembered that way. The lookup ignores case either way.
        Wordsmith.Configuration.CustomDictionaryEntries.Add( trimmed );
        Wordsmith.Configuration.Save();
        return true;
    }

    /// <summary>
    /// Attempt to remove a word from the custom dictionary
    /// </summary>
    /// <param name="word">String to remove</param>
    public static void RemoveDictionaryEntry(string word)
    {
        string trimmed = word.Trim();

        lock ( _sync )
        {
            _ = _custom.Remove( trimmed );
            _ = _dictionary.Remove( trimmed.ToLower() );

            // Only a word this put into the dictionary is this one's to take back out.
            // A real word was never ours, and a name from the game's own data is not
            // the user's to remove.
            if ( _inserted.Remove( trimmed ) && !_supplementary.Contains( trimmed ) )
                _ = _hunspell?.Remove( trimmed );
        }

        // Entries added before the custom list kept its casing are still lowercase.
        _ = Wordsmith.Configuration.CustomDictionaryEntries.Remove( trimmed );
        _ = Wordsmith.Configuration.CustomDictionaryEntries.Remove( trimmed.ToLower() );
        Wordsmith.Configuration.Save();
    }

    /// <summary>
    /// Words that might have been meant instead, best first.
    ///
    /// Hunspell ranks what it generates: it scores candidates by how far they are
    /// from what was typed, weighs a known confusion of letters above an arbitrary
    /// one, and sorts before cutting the list. The old engine did none of that, which
    /// is why its answers were as good as the order its generators ran in.
    /// </summary>
    internal static IReadOnlyList<string> GetSuggestions(string word)
    {
        if ( word.Length == 0 )
            throw new Exception( $"GetSuggestions({word}) failed. Word must have length." );

        if ( _hunspell is not null )
        {
            try
            {
                // Held throughout, because Suggest walks the dictionary as it goes and
                // names may still be arriving on another thread.
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
    /// Merges two ranked lists by taking from each in turn.
    ///
    /// Each dictionary has already sorted its own answers by how likely they are, and
    /// there is no common scale to sort them against each other on. Alternating keeps
    /// both first choices near the top, which for a misspelling that differs between
    /// the two spellings is the pair of words actually worth offering.
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
    /// The original generate-and-take suggestions, for when only a flat word list
    /// loaded. Unranked, and kept only because it is better than nothing.
    /// </summary>
    private static IReadOnlyList<string> LegacySuggestions(string word)
    {
        // Check if the first character is capitalized.
        bool isCapped = WordRegex().IsMatch( word ); //"ABCDEFGHIJKLMNOPQRSTUVWXYZ".Contains(word[0]);

        // Get the lowercase version of the word for the remaining tests.
        word = word.ToLower();

        // Generate all of the possible suggestions. We start the GenerateAway thread first as it
        // is by far the longest process.
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
        // Collect the transposes.
        transpose.Wait();
        AddResults( transpose );

        // Collect the aways.
        aways.Wait();
        AddResults( aways );

        // Collect the splits.
        splits.Wait();
        AddResults( splits );

        // Collect the deleted characters.
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
            // Get the chars.
            char[] chars = word.ToCharArray();

            // Get the char at x
            char y = chars[x];

            // Move the char from x+1 to x
            chars[x] = chars[x + 1];

            // Overwite char at x+1 with x.
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
        // for index
        // split into two words
        // if both splits are words
        // if check word one
        // then if check word two
        // add word one + word two
        // return results
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
            // This will toggle between vowel and consonant generation
            for ( int z = 0; z < 2; z++ )
            {
                for ( int x = 0; x < word.Length; ++x )
                {
                    for ( int y = 0; y < letters.Length; ++y )
                    {
                        char[] chars = word.ToCharArray();

                        // Start with vowel replacements, these are more common than
                        // consonant mistakes.
                        if( "aAeEiIoOuUyY".Contains( chars[x] ) == ( z == 0 ) )
                        {
                            chars[x] = letters[y];
                            string test = new(chars);

                            if( ( !filter || IsWord( test ) ) && !results.Contains( test ) )
                                results.Add( isCapped ? test.CaplitalizeFirst() : test );
                        }

                        // For optimization break out of the y loop to avoid checking this
                        // 26 different times each time the chars[x] is the wrong character type.
                        // i.e. consant when z==0 or vowel when z==1.
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

                // If the inserted character makes a word or not filtering then add it if
                // it is not already in the results.
                if ( (!filter || IsWord( foretest )) && !results.Contains( foretest ) )
                    results.Add( foretest );

                // Append a character to the word
                string afttest = $"{word}{letters[y]}";

                // If the appended character makes a word or not filtering then add it if
                // it is not already in the results.
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
