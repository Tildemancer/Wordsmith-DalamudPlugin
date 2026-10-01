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

    // A header or command's color from the game's Log Text Colors, null for none.
    internal static Func<string, Vector4?>? HeaderColor { get; private set; }

    // Call before WS is constructed.
    // Hosted, GetPluginConfig returns TT's config, so these settings get their own file.
    public static void HostInOwnFile(Func<Configuration> load, Func<Configuration, bool> save, Func<string, Vector4?> headerColor) =>
        (_load, _save, HeaderColor) = (load, save, headerColor);

    // Call after construction, since the constructor hooks up the installer buttons this takes back.
    public static void ReleaseInstallerButtons()
    {
        Wordsmith.PluginInterface.UiBuilder.OpenMainUi -= WordsmithUI.ShowScratchPad;
        Wordsmith.PluginInterface.UiBuilder.OpenConfigUi -= WordsmithUI.ShowSettings;
    }

    private static SettingsUI? _settings;

    // Drawn in TT's tab, never as WS.
    // See WordsmithUI.ShowSettings
    public static Window Settings => _settings ??= new SettingsUI();

    internal static bool ShowSettings() => IsHosted && (Settings.IsOpen = true);

    // Empty until the manifest's fetched, in the background when hosted.
    public static string KofiUrl => Wordsmith.WebManifest?.Kofi ?? string.Empty;

    internal static Configuration LoadConfig() => _load?.Invoke() ?? Wordsmith.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

    // False when refused or failed, so saves don't lie and say they're done when they're not.
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

    // Only re-asked when TT says something changed, not on every split and send.
    private static bool _splitterAvailable;

    private static void SplitterChanged()
    {
        SplitterGeneration++;
        _splitterAvailable = Ask(_apiVersion, gate => gate.InvokeFunc() >= RequiredApiVersion, false);
    }

    // Goes up on TildeTools.Spell.Available, like a dictionary loading or a word learned elsewhere.
    internal static int SpellerGeneration { get; private set; }

    private static void SpellerChanged() => SpellerGeneration++;

    // Drops the settings window too, it copied values from the old Configuration.
    internal static void Shutdown()
    {
        _available?.Unsubscribe(SplitterChanged);
        _spellAvailable?.Unsubscribe(SpellerChanged);
        _settings = null;
    }

    internal static void Initialize()
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
        SplitterChanged();
    }

    // The pad goes over as one line with its OOC tags once, and the splitter repeats them per part.
    private static string FullLine(ScratchPadUI pad, out int textAt)
    {
        string prefix = pad.Header.ToString() is { Length: > 0 } header ? $"{header} " : "";
        string open = pad.UseOOC ? Wordsmith.Configuration.OocOpeningTag : "";
        textAt = prefix.Length + open.Length;
        return $"{prefix}{open}{pad.ScratchString.Unwrap()}{(pad.UseOOC ? Wordsmith.Configuration.OocClosingTag : "")}";
    }

    // With a splitter, breaks fall where the splitter will send them.
    // Spans = flat (start, length) pairs, each part's body within the part
    // Sources = each body's start in the line, empty if any can't be found
    // A body word's index plus StartIndex is its index in ScratchString.Unwrap(), where corrections are.
    // BodyEnd = 0 without a source, so nothing in that part matches
    internal static List<TextChunk>? Split(ScratchPadUI pad)
    {
        string line = FullLine(pad, out int textAt);
        if (!_splitterAvailable || Ask(_splitLine, gate => gate.InvokeFunc(line, 0), []) is not { Count: > 0 } parts)
            return null;

        List<int> spans = Ask(_bodySpans, gate => gate.InvokeFunc(line, 0), []);
        List<int> sources = Ask(_bodySources, gate => gate.InvokeFunc(line, 0), []);
        return [.. parts.Select((part, i) => i < sources.Count && 2 * i + 1 < spans.Count
            ? new TextChunk(part) { FromSplitter = true, Command = CommandOf(part), StartIndex = sources[i] - spans[2 * i] - textAt, BodyStart = spans[2 * i], BodyEnd = spans[2 * i] + spans[2 * i + 1] }
            : new TextChunk(part) { FromSplitter = true, Command = CommandOf(part), BodyEnd = 0 })];
    }

    // A tell's command includes its target, either <t> or First Last with any @World.
    private static string CommandOf(string part)
    {
        string line = part.Unwrap();
        if (!line.StartsWith('/'))
            return "";

        int end = line.IndexOf(' ');
        if (end < 0)
            return line;

        string command = line[1..end];
        if (!command.Equals("t", StringComparison.OrdinalIgnoreCase) && !command.Equals("tell", StringComparison.OrdinalIgnoreCase))
            return line[..end];

        for (int words = end + 1 < line.Length && line[end + 1] == '<' ? 1 : 2; words > 0 && end >= 0; words--)
            end = line.IndexOf(' ', end + 1);

        return end < 0 ? line : line[..end];
    }

    internal static bool Send(ScratchPadUI pad) => _splitterAvailable && Ask(_sendLine, gate => gate.InvokeFunc(FullLine(pad, out _), 0), false);

    // Passed as typed, since Speller.IsWord tries as typed then lowercase, so Lang's lowercase goes unused.
    internal static bool IsWord(string word) => Ask(_isWord, gate => gate.InvokeFunc(word), true);

    // 0 = TT's offered max
    internal static List<string> Suggest(string word) => Ask(_suggest, gate => gate.InvokeFunc(word, 0), []);

    internal static bool AddToDictionary(string word) => Ask(_addToDictionary, gate => gate.InvokeFunc(word), false);

    // TT's Define window instead of Merriam-Webster's API...
    // TODO, I might come back to this and put Merriam Webster in as another source...
    internal static bool ShowLookup() => IsHosted && Ask(_lookup, gate => gate.InvokeFunc(), false);

    // HasFunction first, or with the module off IsWord throws once per word the pad checks.
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
