// Author: Lady Defile
// Description: Wordsmith is a plugin for Dalamud; a plugin API for Final Fantasy XIV Online.
// The purpose of Wordsmith is to make roleplay easier. Roleplay in FFXIV requires typing
// into a small, single horizontal line with very little width. This makes it impossible to
// proofread your text for spelling and grammatical errors. The other option is to type in
// external programs but then copy/paste is a bit of guess work. Wordsmith aims to solve this
// by being the perfect-ish roleplay text editor.
//
// A few notes to anyone looking to explore this code and/or my future self if I forget how all of
// this code works:
// *    Wordsmith.cs is the main plugin interface and the entry point for Dalamud. It handles the
//          initialization of the plugin and text commands.
// *    WordsmithUI.cs manages the creation, showing/hiding, and disposal of GUI windows.
// *    Extensions.cs has several extension methods.
//
// Files in the Helpers namespace are processing code removed from the file that uses them.
// This was done to help cut down on file bloat and separate functions from UI where possible.
//
// Files in the Gui namespace are GUI windows that are displayed to the user at one point or
// another for a variety of reasons.
//
// While the code may appear complicated, I've done my best to simplify and compartmentalize
// anything that I can to keep things easy to pick up. It should not be too difficult for
// others or my future self to (re)visit the code in these files and quickly rediscover
// the functions of each file as well as the flow of the program.
#region GLOBAL USING
global using System;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Numerics;
global using System.Collections.Generic;
global using Dalamud.Logging;
global using System.Text.RegularExpressions;
#endregion

using Dalamud.Game.Command;
using Dalamud.Data;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Wordsmith.Gui;
using Wordsmith.Helpers;
using System.Threading;
using Dalamud.Interface;

namespace Wordsmith;

public sealed class Wordsmith : IDalamudPlugin
{
    #region Constants
    /// <summary>
    /// Plugin name.
    /// </summary>
    internal const string APPNAME = "Wordsmith";

    /// <summary>
    /// The default height of a button.
    /// </summary>
    internal const int BUTTON_Y = 25;

    /// <summary>
    /// Command to open a new or specific scratch pad.
    /// </summary>
    private const string _SCRATCH_CMD_STRING = "/scratchpad";

    /// <summary>
    /// Command to open the settings window.
    /// </summary>
    private const string _SETTINGS_CMD_STRING = "/wordsmith";

    /// <summary>
    /// Command to open the thesaurus.
    /// </summary>
    private const string _THES_CMD_STRING = "/thesaurus";

    /// <summary>
    /// The maximum length of scratch text.
    /// </summary>
    internal const int MAX_SCRATCH_LENGTH = 32768 ;
    #endregion


    #region Plugin Services
    // All plugin services are automatically populated by Dalamud. These are important
    // classes that are used to directly interface with the plugin API.
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;
    #endregion

    [PluginService]
    internal static IPluginLog PluginLog { get; private set; } = null!;

    private SpellIpc? _spellIpc;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static INotificationManager NotificationManager { get; private set; } = null!;


    /// <summary>
    /// <see cref="Configuration"/> holding all configurable data for the plugin.
    /// </summary>
    internal static Configuration Configuration { get; private set; } = null!;

    /// <summary>
    /// <see cref="WebManifest"/> containing notice and language information.
    /// </summary>
    internal static WebManifest WebManifest { get; private set; } = null!;

    /// <summary>
    /// The author's donation link, for a host that gathers its credits in one place.
    ///
    /// Exposed as the address alone so the host need not see the manifest type, and
    /// read from the manifest rather than written down anywhere, since the author can
    /// change where it points. Empty until the manifest has been fetched.
    /// </summary>
    public static string KofiUrl => WebManifest?.Kofi ?? string.Empty;

    #region Constructor and Disposer
    /// <summary>
    /// Default constructor and initializer for the Wordsmith plugin.
    /// </summary>
    public Wordsmith()
    {
        try
        {
            Construct();
        }
        catch
        {
            // TildeTools
            // Gates go back first, or they're still held next load and a taken name throws again.
            // Each step guarded, a half-built plugin can fail its own teardown
            try { this._spellIpc?.Dispose(); } catch { /* nothing left to try */ }
            try { DisposeInner(); } catch { /* nothing left to try */ }

            throw;
        }
    }

    private void Construct()
    {
        // Get the configuration.
        Configuration = Hosting.LoadConfig();

        // TildeTools
        Hosting.Initialise();

        // TildeTools
        _spellIpc = new SpellIpc();

        //PluginInterface.UiBuilder.LoadImage( Path.Combine(PluginInterface.AssemblyLocation.Directory!.FullName, "mwlogo.png" ));

        // Add commands for the different windows
        // Note that the first two use inline functions but scratch pads require a little text parsing.
        _ = CommandManager.AddHandler(_THES_CMD_STRING, new CommandInfo( ( c, a ) => WordsmithUI.ShowThesaurus() ) { HelpMessage = "Display the thesaurus window." });
        _ = CommandManager.AddHandler(_SETTINGS_CMD_STRING, new CommandInfo( ( c, a ) => WordsmithUI.ShowSettings()) { HelpMessage = "Display the configuration window." });
        _ = CommandManager.AddHandler(_SCRATCH_CMD_STRING, new CommandInfo( OnScratchCommand )
        {
            HelpMessage = "Opens or creates a scratch pad. Follow with a number or custom name if you like. (i.e. \"/scratchpad 5\" or \"/scratchpad Juliet\")"
        });

        PluginInterface.UiBuilder.OpenMainUi += WordsmithUI.ShowScratchPad;


        // TildeTools
        // Off the draw thread: three blocking HTTP attempts froze the game. Nothing needs it at startup
        WebManifest = new();
        _ = System.Threading.Tasks.Task.Run( () =>
        {
            try
            {
                WebManifest = Git.GetManifest();
            }
            catch ( Exception e )
            {
                PluginLog.Error( $"Could not fetch the web manifest.\n{e}" );
            }
        } );

        // Register handlers for draw and openconfig events.
        PluginInterface.UiBuilder.Draw += WordsmithUI.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += WordsmithUI.ShowSettings;

        // Initialize the dictionary.
        Lang.Init();
    }

    /// <summary>
    /// Disposal method for cleaning the plugin.
    /// </summary>
    public void Dispose()
    {
        this._spellIpc?.Dispose();
        DisposeInner();
    }

    private void DisposeInner()
    {
        // Remove events.
        PluginInterface.UiBuilder.Draw -= WordsmithUI.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= WordsmithUI.ShowSettings;

        // Remove command handlers.
        _ = CommandManager.RemoveHandler(_THES_CMD_STRING);
        _ = CommandManager.RemoveHandler(_SETTINGS_CMD_STRING);
        _ = CommandManager.RemoveHandler(_SCRATCH_CMD_STRING);
        PluginInterface.UiBuilder.OpenMainUi -= WordsmithUI.ShowScratchPad;

        // Dispose of the UI
        WordsmithUI.Dispose();

        // TildeTools
        // Static, so inside a host they'd outlive the module, holding ~20MB
        Lang.Unload();
    }

    internal static void ResetConfig()
    {
        Configuration = new();
        Configuration.Save();
    }
    #endregion
    #region Event Callbacks
    private void OnScratchCommand(string command, string args)
    {
        if ( int.TryParse( args.Trim(), out int x ) )
            WordsmithUI.ShowScratchPad( x );
        else if ( args.Trim().Length > 3 )
            WordsmithUI.ShowScratchPad( args );
        else
            WordsmithUI.ShowScratchPad();
    }
    internal static void ReloadManifest() => WebManifest = Git.GetManifest();
    #endregion
}