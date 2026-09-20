// TildeTools: written for this fork, not part of upstream Wordsmith.

using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Wordsmith.Helpers;

namespace Wordsmith;

/// <summary>Offers Wordsmith's spellchecker to other plugins.</summary>
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

    /// <summary>Reported once, not on every keystroke.</summary>
    private static bool _reportedState;

    private static void ReportState()
    {
        if (_reportedState)
            return;

        _reportedState = true;
        Wordsmith.PluginLog.Information(
            $"Spellcheck: answering with {Lang.WordCount} words loaded, enabled: {Lang.Enabled}.");
    }

    /// <summary>Reported once. A broken result repeats every frame.</summary>
    private static bool _reportedBadPosition;

    /// <summary>
    /// Where the word being typed starts, or the end of the text when the last
    /// keystroke finished a word. Nothing past here gets marked.
    /// </summary>
    private static int UnfinishedWordAt(string text)
    {
        if (text.Length == 0)
            return 0;

        char last = text[^1];

        // Apostrophe and hyphen stay inside a word.
        if (char.IsWhiteSpace(last) || (char.IsPunctuation(last) && last != '\'' && last != '-'))
            return text.Length;

        int start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;

        return start;
    }

    /// <summary>Where a leading slash command ends, or zero when the text is not one.</summary>
    private static int CommandEndsAt(string text)
    {
        if (text.Length == 0 || text[0] != '/')
            return 0;

        int end = text.IndexOf(' ');
        if (end < 0)
            return text.Length;

        // A tell's target is the next word, so skip that too.
        if (TellRegex().IsMatch(text))
        {
            int target = text.IndexOf(' ', end + 1);
            if (target > 0)
                return target;
        }

        return end;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/(tell|t|w|whisper|send)\s", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TellRegex();

    /// <summary>
    /// Finds the misspelled words in a string. Positions come back flattened:
    /// start, length, start, length.
    /// </summary>
    private static List<int> Check(string text)
    {
        var positions = new List<int>();

        try
        {
            ReportState();

            if (!Lang.Enabled || string.IsNullOrEmpty(text))
                return positions;

            var found = SpellChecker.CheckString(text);

            int unfinished = UnfinishedWordAt(text);
            int commandEnds = CommandEndsAt(text);

            foreach (var word in found)
            {
                // Still being typed.
                if (word.WordIndex >= unfinished)
                    continue;

                if (word.WordIndex < commandEnds)
                    continue;

                if (word.WordIndex < 0 || word.WordLength < 1)
                {
                    if (!_reportedBadPosition)
                    {
                        _reportedBadPosition = true;
                        Wordsmith.PluginLog.Warning(
                            $"Spellcheck: a flagged word had no usable position: index {word.WordIndex}, length {word.WordLength}.");
                    }

                    continue;
                }

                positions.Add(word.WordIndex);
                positions.Add(word.WordLength);
            }
        }
        catch (System.Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Spellcheck over IPC failed.");
        }

        return positions;
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

    /// <summary>Leaves a word alone for the rest of the session without learning it.</summary>
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
