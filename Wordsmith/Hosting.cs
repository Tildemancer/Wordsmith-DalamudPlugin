// TildeTools: written for this fork, not part of upstream Wordsmith.

using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Wordsmith.Gui;

namespace Wordsmith;

public static class Hosting
{
    private const string ConfigFileName = "Wordsmith.json";

    internal static bool IsHosted { get; private set; }

    // Before construction
    // Hosted, PluginInterface is the host's, so SavePluginConfig would overwrite the host's file
    public static void HostInOwnFile() => IsHosted = true;

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

    private static string ConfigPath => PathBeside(Wordsmith.PluginInterface.ConfigFile);

    private static string PathBeside(FileInfo other) => Path.Combine(other.DirectoryName!, ConfigFileName);

    // Matches how Dalamud writes settings, so stored objects carry "$type"
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = new LocalAssemblyBinder(),
    };

    // Resolves against the running Wordsmith, or the serializer loads a second copy of the assembly
    private sealed class LocalAssemblyBinder : DefaultSerializationBinder
    {
        private static readonly Assembly Ours = typeof(Configuration).Assembly;
        private static readonly string OurName = Ours.GetName().Name!;

        public override Type BindToType(string? assemblyName, string typeName)
        {
            // Whole name, not ours first: a runtime generic can take our types as arguments
            string qualified = assemblyName == null ? typeName : $"{typeName}, {assemblyName}";
            return Type.GetType(qualified, ResolveAssembly, ResolveType, throwOnError: false) ?? base.BindToType(assemblyName, typeName);
        }

        private static Assembly? ResolveAssembly(AssemblyName name) =>
            string.Equals(name.Name, OurName, StringComparison.Ordinal)
                ? Ours
                : Assembly.Load(name);

        private static Type? ResolveType(Assembly? assembly, string name, bool ignoreCase) =>
            assembly == null
                ? Type.GetType(name, throwOnError: false, ignoreCase)
                : assembly.GetType(name, throwOnError: false, ignoreCase);
    }

    // Settings existed but couldn't be read, so saving is refused and defaults never overwrite them
    private static bool _loadFailed;

    // Hosted, Dalamud would hand back the host's settings object
    internal static Configuration LoadConfig()
    {
        if (!IsHosted)
            return Wordsmith.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _loadFailed = false;
        string path = ConfigPath;

        try
        {
            if (!File.Exists(path))
                return new Configuration();

            return JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path), SerializerSettings) ?? throw new InvalidDataException("It read as empty");
        }
        catch (Exception e)
        {
            Wordsmith.PluginLog.Error(e, $"Could not read Wordsmith's settings at {path}. Running on defaults, and the file won't be overwritten.");
        }

        _loadFailed = true;
        Wordsmith.NotificationManager.AddNotification(new() { Title = "Wordsmith", Content = $"Couldn't read its settings, so none are saved until {path} is fixed or removed.", Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning });
        return new Configuration();
    }

    // False when refused or failed, so the save isn't reported done
    internal static bool SaveConfig(Configuration config)
    {
        if (!IsHosted)
        {
            Wordsmith.PluginInterface.SavePluginConfig(config);
            return true;
        }

        if (_loadFailed)
        {
            Wordsmith.PluginLog.Warning("Refusing to save Wordsmith's settings: the existing ones could not be read, and writing now would replace them with defaults.");
            return false;
        }

        try
        {
            Write(ConfigPath, config);
            return true;
        }
        catch (Exception e)
        {
            Wordsmith.PluginLog.Error(e, "Could not save Wordsmith's settings.");
            return false;
        }
    }

    private static void Write(string path, Configuration config)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(config, Formatting.Indented, SerializerSettings));

        if (File.Exists(path))
            File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temporary, path);
    }

    public enum Rescued { Nothing, Adopted, SetAside }

    // Up to 0.3.4 a hosted Wordsmith saved into the host's file, see TildeTools.Configuration.Load
    // An existing Wordsmith.json wins, the host's copy goes to Wordsmith.json.hosted
    // Throws: runs before Wordsmith's PluginLog is injected
    public static Rescued RescueSettingsFrom(FileInfo hostConfigFile, out string? path)
    {
        path = null;

        hostConfigFile.Refresh();
        if (!hostConfigFile.Exists)
            return Rescued.Nothing;

        string text = File.ReadAllText(hostConfigFile.FullName);

        // By text, not by deserialising: the host may not be able to resolve our type
        if (!text.Contains(typeof(Configuration).FullName + ", ", StringComparison.Ordinal))
            return Rescued.Nothing;

        string mine = PathBeside(hostConfigFile);

        if (File.Exists(mine))
        {
            // Kept whole and written once, so an affected version going back can't replace it with defaults
            string aside = mine + ".hosted";
            if (!File.Exists(aside))
                File.Copy(hostConfigFile.FullName, aside);

            path = aside;
            return Rescued.SetAside;
        }

        Configuration? stranded = JsonConvert.DeserializeObject<Configuration>(text, SerializerSettings);
        if (stranded == null)
            return Rescued.Nothing;

        Write(mine, stranded);

        path = mine;
        return Rescued.Adopted;
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
    internal static List<string> Suggest(string word, int most) => Ask(_suggest, gate => gate.InvokeFunc(word, most), []);

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
