// TildeTools: written for this fork, not part of upstream Wordsmith.

using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc;
using Wordsmith.Gui;

namespace Wordsmith;

public static class Hosting
{
    private static Func<Configuration>? _load;
    private static Func<Configuration, bool>? _save;

    internal static bool IsHosted => _load != null;

    // Before construction
    // Hosted, PluginInterface is the host's, so the host keeps these settings in a file of their own
    public static void HostInOwnFile(Func<Configuration> load, Func<Configuration, bool> save) => (_load, _save) = (load, save);

    // After construction, which subscribes them to the host's buttons
    public static void ReleaseInstallerButtons()
    {
        Wordsmith.PluginInterface.UiBuilder.OpenMainUi -= WordsmithUI.ShowScratchPad;
        Wordsmith.PluginInterface.UiBuilder.OpenConfigUi -= WordsmithUI.ShowSettings;
    }

    private static SettingsUI? _settings;

    // Drawn in TildeTools' tab, never among WordsmithUI's windows
    // See WordsmithUI.ShowSettings
    public static Window Settings => _settings ??= new SettingsUI();

    internal static bool ShowSettings()
    {
        if (!IsHosted)
            return false;

        Settings.IsOpen = true;
        return true;
    }

    // Empty until Git.GetManifest succeeds
    public static string KofiUrl => Wordsmith.WebManifest?.Kofi ?? string.Empty;

    internal static Configuration LoadConfig() => _load?.Invoke() ?? Wordsmith.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

    // False when refused or failed, so the save isn't reported done
    internal static bool SaveConfig(Configuration config)
    {
        if (_save != null)
            return _save(config);

        Wordsmith.PluginInterface.SavePluginConfig(config);
        return true;
    }

    private const int RequiredApiVersion = 2;

    private static ICallGateSubscriber<int>? _apiVersion;
    private static ICallGateSubscriber<string, int, List<string>>? _splitLine;
    private static ICallGateSubscriber<string, int, bool>? _sendLine;
    private static ICallGateSubscriber<string, int, List<int>>? _bodySpans;
    private static ICallGateSubscriber<string, int, List<int>>? _bodySources;
    private static ICallGateSubscriber<object?>? _available;
    private static ICallGateSubscriber<string, bool>? _isWord;
    private static ICallGateSubscriber<string, int, List<string>>? _suggest;
    private static ICallGateSubscriber<string, bool>? _addToDictionary;
    private static ICallGateSubscriber<bool>? _lookup;
    private static ICallGateSubscriber<object?>? _spellAvailable;

    internal static int SplitterGeneration { get; private set; }

    private static void SplitterChanged() => SplitterGeneration++;

    // Moves on TildeTools.Spell.Available: a dictionary loaded or switched, a word learned in another box
    internal static int SpellerGeneration { get; private set; }

    private static void SpellerChanged() => SpellerGeneration++;

    // A restart reads a new Configuration, so the settings window goes too
    internal static void Shutdown()
    {
        _available?.Unsubscribe(SplitterChanged);
        _spellAvailable?.Unsubscribe(SpellerChanged);
        _settings = null;
    }

    internal static void Initialise()
    {
        var pi = Wordsmith.PluginInterface;
        _available = pi.GetIpcSubscriber<object?>("TildeTools.Split.Available");
        _available.Subscribe(SplitterChanged);
        _apiVersion = pi.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        _splitLine = pi.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        _sendLine = pi.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
        _bodySpans = pi.GetIpcSubscriber<string, int, List<int>>("TildeTools.Split.SplitLineBodySpans");
        _bodySources = pi.GetIpcSubscriber<string, int, List<int>>("TildeTools.Split.SplitLineBodySources");
        _isWord = pi.GetIpcSubscriber<string, bool>("TildeTools.Spell.IsWord");
        _suggest = pi.GetIpcSubscriber<string, int, List<string>>("TildeTools.Spell.SuggestNow");
        _addToDictionary = pi.GetIpcSubscriber<string, bool>("TildeTools.Spell.AddToDictionary");
        _lookup = pi.GetIpcSubscriber<bool>("TildeTools.Spell.Lookup");
        _spellAvailable = pi.GetIpcSubscriber<object?>("TildeTools.Spell.Available");
        _spellAvailable.Subscribe(SpellerChanged);
    }

    private static bool SplitterAvailable => Ask(_apiVersion, gate => gate.InvokeFunc() >= RequiredApiVersion, false);

    // The splitter repeats Wordsmith's OOC tags on every part when they match its own, as they do by default
    private static string FullLine(ScratchPadUI pad, out int textAt)
    {
        string prefix = pad.Header.ToString() is { Length: > 0 } header ? $"{header} " : "";
        string open = pad.UseOOC ? Wordsmith.Configuration.OocOpeningTag : "";
        textAt = prefix.Length + open.Length;
        return $"{prefix}{open}{pad.ScratchString.Unwrap()}{(pad.UseOOC ? Wordsmith.Configuration.OocClosingTag : "")}";
    }

    // With a splitter the breaks fall where it puts them, so the preview matches the send
    // Spans: flat start, length pairs of each part's body within the part
    // Sources: where each body starts in the line, empty if any can't be found
    // A body word's index plus StartIndex is its index in ScratchString.Unwrap(), where the corrections are
    // BodyEnd = 0 without a source, so nothing in the part matches
    internal static List<TextChunk>? Split(ScratchPadUI pad)
    {
        string line = FullLine(pad, out int textAt);
        if (!SplitterAvailable || Ask(_splitLine, gate => gate.InvokeFunc(line, 0), []) is not { Count: > 0 } parts)
            return null;

        List<int> spans = Ask(_bodySpans, gate => gate.InvokeFunc(line, 0), []);
        List<int> sources = Ask(_bodySources, gate => gate.InvokeFunc(line, 0), []);
        return [.. parts.Select((part, i) => i < sources.Count && 2 * i + 1 < spans.Count
            ? new TextChunk(part) { FromSplitter = true, StartIndex = sources[i] - spans[2 * i] - textAt, BodyStart = spans[2 * i], BodyEnd = spans[2 * i] + spans[2 * i + 1] }
            : new TextChunk(part) { FromSplitter = true, BodyEnd = 0 })];
    }

    internal static bool Send(ScratchPadUI pad) => SplitterAvailable && Ask(_sendLine, gate => gate.InvokeFunc(FullLine(pad, out _), 0), false);

    // Lang.IsWord's lowercase goes unused, Speller.IsWord tries as typed then lowercase
    internal static bool IsWord(string word) => Ask(_isWord, gate => gate.InvokeFunc(word), true);

    // most: 0 for all
    // As many as TildeTools' Spelling tab offers
    internal static List<string> Suggest(string word) => Ask(_suggest, gate => gate.InvokeFunc(word, 0), []);

    internal static bool AddToDictionary(string word) => Ask(_addToDictionary, gate => gate.InvokeFunc(word), false);

    // TildeTools' Define window, not Merriam-Webster's API
    internal static bool ShowLookup() => IsHosted && Ask(_lookup, gate => gate.InvokeFunc(), false);

    // HasFunction first: with the module off, IsWord would throw once per word the pad checks
    private static TOut Ask<TGate, TOut>(TGate? gate, Func<TGate, TOut> call, TOut failed)
        where TGate : class, ICallGateSubscriber
    {
        if (gate is not { HasFunction: true })
            return failed;

        try
        {
            return call(gate);
        }
        catch
        {
            return failed;
        }
    }
}
