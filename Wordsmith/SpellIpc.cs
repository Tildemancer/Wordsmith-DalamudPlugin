// TildeTools: written for this fork, not part of upstream Wordsmith.

using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Wordsmith.Helpers;

namespace Wordsmith;

internal sealed partial class SpellIpc : System.IDisposable
{
    private const int ApiVersion = 1;
    private const string Prefix = "TildeTools.Spell.";

    private readonly ICallGateProvider<int> _apiVersion;
    private readonly ICallGateProvider<string, List<int>> _check;
    private readonly ICallGateProvider<string, List<string>> _suggest;
    private readonly ICallGateProvider<string, bool> _addToDictionary;
    private readonly ICallGateProvider<string, bool> _ignore;
    private readonly ICallGateProvider<object?> _available;

    internal SpellIpc()
    {
        _apiVersion = Wordsmith.PluginInterface.GetIpcProvider<int>($"{Prefix}ApiVersion");
        _check = Wordsmith.PluginInterface.GetIpcProvider<string, List<int>>($"{Prefix}Check");
        _suggest = Wordsmith.PluginInterface.GetIpcProvider<string, List<string>>($"{Prefix}Suggest");
        _addToDictionary = Wordsmith.PluginInterface.GetIpcProvider<string, bool>($"{Prefix}AddToDictionary");
        _ignore = Wordsmith.PluginInterface.GetIpcProvider<string, bool>($"{Prefix}Ignore");
        _available = Wordsmith.PluginInterface.GetIpcProvider<object?>($"{Prefix}Available");

        _apiVersion.RegisterFunc(() => ApiVersion);
        _check.RegisterFunc(Check);
        _suggest.RegisterFunc(Suggest);
        _addToDictionary.RegisterFunc(AddToDictionary);
        _ignore.RegisterFunc(IgnoreWord);

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

    private static bool _reportedBadPosition;

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

    [System.Text.RegularExpressions.GeneratedRegex(@"^/(tell|t|w|whisper|send)\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TellRegex();

    // Flattened: start, length, start, length
    private static List<int> Check(string text)
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
                if (index >= unfinished)
                    continue;

                if (index < commandEnds)
                    continue;

                if (index < 0 || length < 1)
                {
                    if (!_reportedBadPosition)
                    {
                        _reportedBadPosition = true;
                        Wordsmith.PluginLog.Warning(
                            $"Spellcheck: a flagged word had no usable position: index {index}, length {length}.");
                    }

                    continue;
                }

                positions.Add(index);
                positions.Add(length);
            }
        }
        catch (System.Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Spellcheck over IPC failed.");
        }

        return positions;
    }

    // Segment -> its misspellings, (index, length) within it
    private static readonly Dictionary<string, List<(int Index, int Length)>> Segments = [];
    private static int _segmentsGeneration = -1;

    private const int SegmentLength = 256;
    private const int MostSegments = 512;

    // A segment at a time, each kept until Lang.Generation moves
    // The checker takes each word alone, so a cut between words changes nothing
    // Typing at the end rechecks only the last: 6 ms a keystroke at 16000 characters before, measured
    private static List<(int Index, int Length)> Misspellings(string text)
    {
        lock (Segments)
        {
            if (_segmentsGeneration != Lang.Generation || Segments.Count > MostSegments)
            {
                Segments.Clear();
                _segmentsGeneration = Lang.Generation;
            }

            List<(int Index, int Length)> all = [];
            for (var start = 0; start < text.Length;)
            {
                var end = SegmentEnd(text, start);
                var segment = text[start..end];

                if (!Segments.TryGetValue(segment, out var found))
                    Segments[segment] = found = [.. SpellChecker.CheckString(segment).Select(w => (w.WordIndex, w.WordLength))];

                foreach (var (index, length) in found)
                    all.Add((start + index, length));

                start = end;
            }

            return all;
        }
    }

    // SegmentLength in, then past the rest of that word and the separators after it, the three Words() splits on
    private static int SegmentEnd(string text, int start)
    {
        var end = System.Math.Min(text.Length, start + SegmentLength);

        while (end < text.Length && text[end] is not (' ' or '\r' or '\n'))
            end++;

        while (end < text.Length && text[end] is ' ' or '\r' or '\n')
            end++;

        return end;
    }

    private static List<string> Suggest(string word)
    {
        try
        {
            return Lang.Enabled ? [.. Lang.GetSuggestions(word)] : [];
        }
        catch (System.Exception ex)
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
        catch (System.Exception ex)
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
        catch (System.Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Ignoring a word over IPC failed.");
            return false;
        }
    }

    public void Dispose()
    {
        _apiVersion.UnregisterFunc();
        _check.UnregisterFunc();
        _suggest.UnregisterFunc();
        _addToDictionary.UnregisterFunc();
        _ignore.UnregisterFunc();
    }
}
