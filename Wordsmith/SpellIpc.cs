// TildeTools: written for this fork, not part of upstream Wordsmith.

using System.Threading.Tasks;
using Dalamud.Plugin.Ipc;
using Wordsmith.Helpers;
using Stamp = (int Generation, bool Hyphen, string Punctuation, int Suggestions);

namespace Wordsmith;

internal sealed partial class SpellIpc : IDisposable
{
    private const int ApiVersion = 1;
    private const string Prefix = "TildeTools.Spell.";

    private readonly ICallGateProvider<int> _apiVersion;
    private readonly ICallGateProvider<string, List<int>> _check;
    private readonly ICallGateProvider<string, List<string>?> _suggest;
    private readonly ICallGateProvider<string, bool> _addToDictionary;
    private readonly ICallGateProvider<string, bool> _ignore;
    private readonly ICallGateProvider<object?> _available;

    internal SpellIpc()
    {
        _apiVersion = Wordsmith.PluginInterface.GetIpcProvider<int>($"{Prefix}ApiVersion");
        _check = Wordsmith.PluginInterface.GetIpcProvider<string, List<int>>($"{Prefix}Check");
        _suggest = Wordsmith.PluginInterface.GetIpcProvider<string, List<string>?>($"{Prefix}Suggest");
        _addToDictionary = Wordsmith.PluginInterface.GetIpcProvider<string, bool>($"{Prefix}AddToDictionary");
        _ignore = Wordsmith.PluginInterface.GetIpcProvider<string, bool>($"{Prefix}Ignore");
        _available = Wordsmith.PluginInterface.GetIpcProvider<object?>($"{Prefix}Available");

        _apiVersion.RegisterFunc(() => ApiVersion);
        _check.RegisterFunc(Check);
        _suggest.RegisterFunc(Suggest);
        _addToDictionary.RegisterFunc(AddToDictionary);
        _ignore.RegisterFunc(IgnoreWord);

        _available.SendMessage();
        Wordsmith.PluginInterface.UiBuilder.Draw += AnnounceLoad;
    }

    private int _announcedLoads;

    // Available first went out before the dictionary loaded, and every box kept the empty answers from then
    // Sent from Draw, on the game's thread, where the boxes read those caches
    private void AnnounceLoad()
    {
        if (Lang.Loads == _announcedLoads)
            return;

        _announcedLoads = Lang.Loads;
        _available.SendMessage();
    }

    private static bool _reportedState;

    private static void ReportState()
    {
        if (_reportedState)
            return;

        _reportedState = true;
        Wordsmith.PluginLog.Information(
            $"Spellcheck: answering with {Lang.WordCount} words loaded, enabled: {Lang.Enabled}.");
    }

    // The start of the word being typed, or the end when the last key finished one. Nothing past here is marked
    private static int UnfinishedWordAt(string text)
    {
        if (text.Length == 0)
            return 0;

        char last = text[^1];

        // Apostrophe and hyphen stay inside a word
        if (char.IsWhiteSpace(last) || (char.IsPunctuation(last) && last != '\'' && last != '-'))
            return text.Length;

        int start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;

        return start;
    }

    private static int CommandEndsAt(string text)
    {
        if (text.Length == 0 || text[0] != '/')
            return 0;

        int end = text.IndexOf(' ');
        if (end < 0)
            return text.Length;

        // A tell's target is two words, First Last@World, or one placeholder like <t>, and is skipped too
        if (TellRegex().IsMatch(text))
        {
            int first = text.IndexOf(' ', end + 1);
            if (first < 0)
                return text.Length;

            if (text[end + 1] == '<')
                return first;

            int second = text.IndexOf(' ', first + 1);
            return second < 0 ? text.Length : second;
        }

        return end;
    }

    [GeneratedRegex(@"^/(tell|t|w|whisper|send)\s", RegexOptions.IgnoreCase)]
    private static partial Regex TellRegex();

    // Flattened: start, length, start, length
    private List<int> Check(string text)
    {
        var positions = new List<int>();

        try
        {
            ReportState();

            if (!Lang.Enabled || string.IsNullOrEmpty(text))
                return positions;

            var found = Misspellings(text);

            int unfinished = UnfinishedWordAt(text);
            int commandEnds = CommandEndsAt(text);

            foreach (var (index, length) in found)
            {
                if (index < commandEnds || index >= unfinished)
                    continue;

                positions.Add(index);
                positions.Add(length);
            }
        }
        catch (Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Spellcheck over IPC failed.");
        }

        return positions;
    }

    // The answers change with the dictionary, and with settings that don't move Lang.Generation
    private static Stamp Current => (Lang.Generation, Wordsmith.Configuration.IgnoreWordsEndingInHyphen,
        Wordsmith.Configuration.PunctuationCleaningList, Wordsmith.Configuration.MaximumSuggestions);

    private static void DropStale<T>(Dictionary<string, T> cache, ref Stamp stamp, int most)
    {
        if (stamp == Current && cache.Count <= most)
            return;

        cache.Clear();
        stamp = Current;
    }

    private readonly Dictionary<string, List<(int Index, int Length)>> _segments = [];
    private Stamp _segmentsStamp;

    private const int SegmentLength = 256;

    // A 32000-byte message and its parts several times over, since full it empties and rechecks the lot
    private const int MostSegments = 4096;

    // CheckString takes each word alone, so a cut between words changes nothing
    // Typing at the end rechecks only the last segment, was 6 ms a keystroke at 16000 characters
    private List<(int Index, int Length)> Misspellings(string text)
    {
        lock (_segments)
        {
            DropStale(_segments, ref _segmentsStamp, MostSegments);

            List<(int Index, int Length)> all = [];
            for (var start = 0; start < text.Length;)
            {
                var end = SegmentEnd(text, start);
                var segment = text[start..end];

                if (!_segments.TryGetValue(segment, out var found))
                    _segments[segment] = found = [.. SpellChecker.CheckString(segment).Select(w => (w.WordIndex, w.WordLength))];

                foreach (var (index, length) in found)
                    all.Add((start + index, length));

                start = end;
            }

            return all;
        }
    }

    // The separators Words() splits on, so every cut falls between words
    private static int SegmentEnd(string text, int start)
    {
        var end = Math.Min(text.Length, start + SegmentLength);

        while (end < text.Length && text[end] is not (' ' or '\r' or '\n'))
            end++;

        while (end < text.Length && text[end] is ' ' or '\r' or '\n')
            end++;

        return end;
    }

    private readonly Dictionary<string, Task<List<string>>> _suggesting = [];
    private Stamp _suggestingStamp;

    private const int MostSuggested = 64;

    // Null while still looking, the menus ask again each frame
    // Off the game's thread, GetSuggestions walks the dictionary for 100 ms and more, measured
    private List<string>? Suggest(string word)
    {
        if (!Lang.Enabled || string.IsNullOrWhiteSpace(word))
            return [];

        lock (_suggesting)
        {
            DropStale(_suggesting, ref _suggestingStamp, MostSuggested);

            if (!_suggesting.TryGetValue(word, out var lookup))
                _suggesting[word] = lookup = Task.Run(() => Lookup(word));

            return lookup.IsCompleted ? lookup.Result : null;
        }
    }

    private static List<string> Lookup(string word)
    {
        try
        {
            return [.. Lang.GetSuggestions(word)];
        }
        catch (Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Spelling suggestions over IPC failed.");
            return [];
        }
    }

    private static bool AddToDictionary(string word)
    {
        try
        {
            if (!Lang.AddDictionaryEntry(word))
                return false;

            Hosting.SaveConfig(Wordsmith.Configuration);
            return true;
        }
        catch (Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Adding a word to the dictionary over IPC failed.");
            return false;
        }
    }

    private static bool IgnoreWord(string word)
    {
        try
        {
            Lang.IgnoreWord(word);
            return true;
        }
        catch (Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Ignoring a word over IPC failed.");
            return false;
        }
    }

    public void Dispose()
    {
        Wordsmith.PluginInterface.UiBuilder.Draw -= AnnounceLoad;
        _apiVersion.UnregisterFunc();
        _check.UnregisterFunc();
        _suggest.UnregisterFunc();
        _addToDictionary.UnregisterFunc();
        _ignore.UnregisterFunc();
    }
}
