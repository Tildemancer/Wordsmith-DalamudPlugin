// TildeTools: written for this fork, not part of upstream Wordsmith.

using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Wordsmith;

public static class Hosting
{
    #region Settings file

    private const string ConfigFileName = "Wordsmith.json";

    private static bool _hosted;

    internal static bool IsHosted => _hosted;

    // Before construction. Dalamud writes settings to whoever asked, and a hosted copy asks
    // with the host's interface, so saving through Dalamud wipes the host's file
    public static void HostInOwnFile() => _hosted = true;

    private static string ConfigPath => PathBeside(Wordsmith.PluginInterface.ConfigFile);

    private static string PathBeside(FileInfo other)
    {
        DirectoryInfo directory = other.Directory
            ?? throw new InvalidOperationException("Dalamud's configuration folder is unavailable.");

        return Path.Combine(directory.FullName, ConfigFileName);
    }

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

    // Settings existed but couldn't be read, so saving is refused and defaults never overwrite them
    private static bool _loadFailed;

    // Hosted, Dalamud would hand back the host's settings object
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

    private static void Write(string path, Configuration config)
    {
        string json = JsonConvert.SerializeObject(config, Formatting.Indented, SerializerSettings);

        // Written beside and moved in, so a failed write leaves no half-file
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);

        if (File.Exists(path))
            File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temporary, path);
    }

    public enum Rescued
    {
        Nothing,

        Adopted,

        SetAside,
    }

    // Before HostInOwnFile, a hosted Wordsmith saved defaults over the host's file. Oops. Our own
    // file always wins, the host's copy is set aside. Runs before Dalamud fills anything in, so it throws
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

    #endregion

    #region Splitting and sending

    // Optional splitter. With one, the breaks and markers are its, and the button sends the lot.
    // Every call falls back to Wordsmith's own behaviour

    private const int RequiredApiVersion = 1;

    private static ICallGateSubscriber<int>? _apiVersion;
    private static ICallGateSubscriber<string, int, List<string>>? _splitLine;
    private static ICallGateSubscriber<string, int, bool>? _sendLine;

    internal static void Initialise()
    {
        _apiVersion = Wordsmith.PluginInterface.GetIpcSubscriber<int>("TildeTools.Split.ApiVersion");
        _splitLine = Wordsmith.PluginInterface.GetIpcSubscriber<string, int, List<string>>("TildeTools.Split.SplitLine");
        _sendLine = Wordsmith.PluginInterface.GetIpcSubscriber<string, int, bool>("TildeTools.Split.SendLine");
    }

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

    // Null means no splitter, fall back to Wordsmith's own
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

    // False means it declined
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
