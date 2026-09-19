using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Wordsmith;

/// <summary>
/// How Wordsmith behaves when it is not the only thing running: where it keeps its
/// settings, and who breaks up the lines it sends.
/// </summary>
public static class Hosting
{
    #region Settings file

    /// <summary>Wordsmith's own settings file, beside every other plugin's.</summary>
    private const string ConfigFileName = "Wordsmith.json";

    private static bool _hosted;

    /// <summary>True when Wordsmith is running inside another plugin.</summary>
    internal static bool IsHosted => _hosted;

    /// <summary>
    /// Keeps settings in Wordsmith's own file rather than the host's.
    ///
    /// Dalamud writes plugin settings to whichever plugin asked, and a hosted copy
    /// asks with the HOST's interface. So saving through Dalamud puts Wordsmith's
    /// settings in the host's file and wipes out whatever the host had there.
    ///
    /// MUST be called before the plugin is constructed: the settings are read on
    /// the way up.
    /// </summary>
    public static void HostInOwnFile() => _hosted = true;

    /// <summary>Where the settings live once the plugin is running.</summary>
    private static string ConfigPath => PathBeside(Wordsmith.PluginInterface.ConfigFile);

    /// <summary>Wordsmith's settings file, in the same folder as the given one.</summary>
    private static string PathBeside(FileInfo other)
    {
        DirectoryInfo directory = other.Directory
            ?? throw new InvalidOperationException("Dalamud's configuration folder is unavailable.");

        return Path.Combine(directory.FullName, ConfigFileName);
    }

    /// <summary>Matches how Dalamud writes plugin settings; stored objects carry a "$type".</summary>
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Objects,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        SerializationBinder = new LocalAssemblyBinder(),
    };

    /// <summary>
    /// Resolves types named in the settings file against the running copy of Wordsmith.
    /// Without this the serializer loads a SECOND copy of the assembly.
    /// </summary>
    private sealed class LocalAssemblyBinder : DefaultSerializationBinder
    {
        private static readonly Assembly Ours = typeof(Configuration).Assembly;
        private static readonly string OurName = Ours.GetName().Name!;

        public override Type BindToType(string? assemblyName, string typeName)
        {
            // Resolve the whole name, not our assembly first: a runtime generic
            // can have our types as its arguments.
            string qualified = assemblyName == null ? typeName : $"{typeName}, {assemblyName}";

            Type? resolved = Type.GetType(qualified, ResolveAssembly, ResolveType, throwOnError: false);
            if (resolved != null)
                return resolved;

            return base.BindToType(assemblyName, typeName);
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

    /// <summary>Settings existed but could not be read. Saving is refused so defaults NEVER overwrite them.</summary>
    private static bool _loadFailed;

    /// <summary>Reads the settings. When hosted, Dalamud would hand back the HOST's settings object.</summary>
    internal static Configuration LoadConfig()
    {
        if (!_hosted)
            return Wordsmith.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        _loadFailed = false;

        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
                return new Configuration();

            Configuration? loaded = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path), SerializerSettings);
            if (loaded != null)
                return loaded;

            _loadFailed = true;
            Wordsmith.PluginLog.Error($"Wordsmith's settings at {path} read as empty; they will not be overwritten.");
        }
        catch (Exception e)
        {
            _loadFailed = true;
            Wordsmith.PluginLog.Error(
                $"Could not read Wordsmith's settings at {ConfigPath}. " +
                $"Running on defaults; the file will NOT be overwritten.\n{e}");
        }

        return new Configuration();
    }

    /// <summary>Writes the settings back to wherever they were read from.</summary>
    internal static void SaveConfig(Configuration config)
    {
        if (!_hosted)
        {
            Wordsmith.PluginInterface.SavePluginConfig(config);
            return;
        }

        if (_loadFailed)
        {
            Wordsmith.PluginLog.Warning(
                "Refusing to save Wordsmith's settings: the existing ones could not be read, " +
                "and writing now would replace them with defaults.");
            return;
        }

        try
        {
            Write(ConfigPath, config);
        }
        catch (Exception e)
        {
            Wordsmith.PluginLog.Error($"Could not save Wordsmith's settings.\n{e}");
        }
    }

    /// <summary>Writes settings to a file, leaving no half-written file behind on failure.</summary>
    private static void Write(string path, Configuration config)
    {
        string json = JsonConvert.SerializeObject(config, Formatting.Indented, SerializerSettings);

        // Write beside the target and move into place: a failed write leaves no half-file.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);

        if (File.Exists(path))
            File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temporary, path);
    }

    /// <summary>What was found in a host plugin's own settings file.</summary>
    public enum Rescued
    {
        /// <summary>Not Wordsmith's settings. Nothing was touched.</summary>
        Nothing,

        /// <summary>Wordsmith had no settings of its own, so these became them.</summary>
        Adopted,

        /// <summary>Wordsmith's own settings are the better ones, so these were only set aside.</summary>
        SetAside,
    }

    /// <summary>
    /// Takes back settings that an earlier version saved into the host's own file.
    ///
    /// Before <see cref="HostInOwnFile"/> existed, a hosted Wordsmith went through
    /// Dalamud, which reads and writes whichever plugin asked. So it read the HOST's
    /// settings, failed to make them ours, and started from defaults; then it saved
    /// those defaults over the host's file. Everything the user had tuned was still
    /// sitting safely in Wordsmith's own file, untouched.
    ///
    /// Which is why a settings file of our own always wins: it holds real settings,
    /// where the host's file holds defaults and at most a few days of changes made
    /// while this was broken. Those are set aside rather than thrown away.
    ///
    /// Called before Wordsmith itself is running, so it must not touch anything
    /// Dalamud fills in later. Throws rather than logs, for the same reason.
    /// </summary>
    /// <param name="hostConfigFile">The host plugin's own settings file.</param>
    /// <param name="path">Where the settings ended up, when there were any.</param>
    public static Rescued RescueSettingsFrom(FileInfo hostConfigFile, out string? path)
    {
        path = null;

        hostConfigFile.Refresh();
        if (!hostConfigFile.Exists)
            return Rescued.Nothing;

        string text = File.ReadAllText(hostConfigFile.FullName);

        // Recognised from the text rather than by deserialising, because the host's
        // plugin may not be able to resolve our type at all.
        if (!text.Contains(typeof(Configuration).FullName + ", ", StringComparison.Ordinal))
            return Rescued.Nothing;

        string mine = PathBeside(hostConfigFile);

        if (File.Exists(mine))
        {
            // Kept whole and unread, so whatever was changed in the meantime can still
            // be fished out by hand. Written once and never again: going back to an
            // affected version would fill the host's file with plain defaults, and
            // those must not replace the copy taken here.
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

    #endregion

    #region Splitting and sending

    // Optional cooperation with a plugin that splits and sends chat messages.
    //
    // Wordsmith breaks text into pieces for copying out by hand, one at a time. Where
    // a splitter is present it can do the breaking up and the sending, so the button
    // sends the whole thing instead of filling the clipboard piece by piece.
    //
    // The splitter becomes the authority on where the breaks fall, so the pieces shown
    // on screen are the ones that will actually be sent. Its markers and tags come
    // with it, which is why Wordsmith's own are left off while it is in charge. Two
    // sets would end up on every line.
    //
    // Every call falls back to Wordsmith's own behaviour, so with no splitter
    // installed nothing here changes anything.

    private const int RequiredApiVersion = 1;

    private static ICallGateSubscriber<int>? _apiVersion;
    private static ICallGateSubscriber<string, int, List<string>>? _splitLine;
    private static ICallGateSubscriber<string, int, bool>? _sendLine;

    /// <summary>Set up the gates once the plugin interface is available.</summary>
    internal static void Initialise()
    {
        _apiVersion = Wordsmith.PluginInterface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        _splitLine = Wordsmith.PluginInterface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        _sendLine = Wordsmith.PluginInterface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
    }

    /// <summary>True when a compatible splitter is installed and answering.</summary>
    internal static bool SplitterAvailable
    {
        get
        {
            try
            {
                return _apiVersion != null && _apiVersion.InvokeFunc() >= RequiredApiVersion;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Asks the splitter how a finished chat line divides up. Null when there is no
    /// splitter, so the caller falls back to Wordsmith's own.
    /// </summary>
    internal static List<string>? Split(string line)
    {
        if (_splitLine == null)
            return null;

        try
        {
            List<string> chunks = _splitLine.InvokeFunc(line, 0);
            return chunks is { Count: > 0 } ? chunks : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hands a finished chat line to the splitter to send. False means it declined
    /// and Wordsmith should do whatever it would have done.
    /// </summary>
    internal static bool Send(string line)
    {
        if (_sendLine == null)
            return false;

        try
        {
            return _sendLine.InvokeFunc(line, 0);
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
