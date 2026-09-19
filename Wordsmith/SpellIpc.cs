using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using Wordsmith.Helpers;

namespace Wordsmith;

/// <summary>
/// Offers Wordsmith's spellchecker to other plugins.
///
/// Registered here rather than by the host because the dictionary, its custom
/// entries and the suggestion logic all live in this assembly. The host only needs
/// to know the gate names.
/// </summary>
internal sealed class SpellIpc : System.IDisposable
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

    /// <summary>
    /// Finds the misspelled words in a string.
    ///
    /// Returns their positions flattened into pairs — start, length, start, length
    /// — because a list of numbers crosses between plugins without either side
    /// needing a shared type. An empty list means nothing was found, or that no
    /// dictionary is loaded.
    /// </summary>
    /// <summary>
    /// Whether the dictionary's state has been reported, so a check that silently
    /// finds nothing explains itself once instead of on every keystroke.
    /// </summary>
    private static bool _reportedState;

    private static void ReportState()
    {
        if (_reportedState)
            return;

        _reportedState = true;
        Wordsmith.PluginLog.Information(
            $"Spellcheck: answering with {Lang.WordCount} words loaded, enabled: {Lang.Enabled}.");
    }

    /// <summary>Reported at most once, since a broken result repeats every frame.</summary>
    private static bool _reportedBadPosition;

    private static List<int> Check(string text)
    {
        var positions = new List<int>();

        try
        {
            ReportState();

            if (!Lang.Enabled || string.IsNullOrEmpty(text))
                return positions;

            var found = SpellChecker.CheckString(text);

            foreach (var word in found)
            {
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

            Wordsmith.PluginInterface.SavePluginConfig(Wordsmith.Configuration);
            return true;
        }
        catch (System.Exception ex)
        {
            Wordsmith.PluginLog.Error(ex, "Adding a word to the dictionary over IPC failed.");
            return false;
        }
    }

    /// <summary>
    /// Leaves a word alone for the rest of the session without learning it.
    ///
    /// Kept here beside the dictionary rather than in each caller, so every text
    /// box that checks spelling agrees about which words are being overlooked —
    /// including Wordsmith's own pad.
    /// </summary>
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
