
#if DEBUG
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;
using Dalamud.Bindings.ImGui;
// TildeTools
using Dalamud.Interface.Utility.Raii;
// TildeTools ends
namespace Wordsmith.Gui;
internal sealed class DebugUI : Window
{
    internal static bool bOverrideSpellcheckLimited = false;
    public DebugUI() : base( $"{Wordsmith.APPNAME} - Debug" )
    {
        this.SizeConstraints = new()
        {
            MinimumSize = new(200, 200),
            MaximumSize = new(9999, 9999)
        };

        this.Flags |= ImGuiWindowFlags.HorizontalScrollbar;

        Wordsmith.PluginLog.Debug( $"DebugUI created." );
    }
    private static string _consoleInput = "";
    private static int _consolePadNumber = 0;

    public override void Draw()
    {
        ImGui.Text( $"Delta: {WordsmithUI.Clock.Delta}" );
        ImGui.Text( $"Longest Delta: {WordsmithUI.Clock.LongestFrame}" );
        if ( ImGui.Button( $"Reset Delta##DebugButton", new( -1, Wordsmith.BUTTON_Y.Scale() ) ) )
            WordsmithUI.Clock.ResetLongest();

        if ( ImGui.CollapsingHeader( $"Wordsmith Configuration##DebugUIHeDebugUICollapsingHeaderader" ) )
            DrawClassData( Wordsmith.Configuration, $"Configuration" );

        if ( ImGui.CollapsingHeader( $"Web manifest##DebugUIHeDebugUICollapsingHeaderader" ) )
            DrawClassData( Wordsmith.WebManifest, $"Web Manifest" );

        // If there is a setting, draw the settings section.
        if ( ImGui.CollapsingHeader( "Settings Data##DebugUIHeDebugUICollapsingHeaderader" ) )
            DrawClassData( WordsmithUI.Windows.FirstOrDefault( x => x.GetType() == typeof( SettingsUI ) ), SettingsUI.GetWindowName() );        

        // If there is a thesaurus, draw the thesaurus section.
        if ( ImGui.CollapsingHeader( "Thesaurus Data##DebugUICollapsingHeader" ) )
            DrawClassData( WordsmithUI.Windows.FirstOrDefault( x => x.GetType() == typeof( ThesaurusUI ) ), ThesaurusUI.GetWindowName() );

        // If there is a MessageBox, draw the MessageBox section.
        if ( ImGui.CollapsingHeader( $"Message Boxes##DebugUICollapsingHeader" ) )
        {
            // TildeTools
            using var indent = ImRaii.PushIndent();
            // TildeTools ends
            foreach( MessageBox mb in WordsmithUI.Windows.Where( w => w.GetType() == typeof( MessageBox ) ) )
            {
                if( ImGui.CollapsingHeader( $"{mb.WindowName}" ) )
                    DrawClassData( mb, $"MessageBoxUI" );
            }

            foreach( ErrorWindow ew in WordsmithUI.Windows.Where( w => w.GetType() == typeof( ErrorWindow ) ) )
            {
                if( ImGui.CollapsingHeader( $"{ew.WindowName}" ) )
                    DrawClassData( ew, $"ErrorWindowUI" );
            }
            // TildeTools: Unindent removed, the using ends it
        }

        // If there is a scratch pad, draw the scratch pad section.
        if ( ImGui.CollapsingHeader( "Scratch Pad Data##DebugUICollapsingHeader" ) )
        {
            // TildeTools
            using var indent = ImRaii.PushIndent();
            // TildeTools ends
            foreach( ScratchPadUI pad in WordsmithUI.Windows.Where( w => w.GetType() == typeof( ScratchPadUI ) ) )
            {
                if( ImGui.CollapsingHeader( $"Scratch Pad {pad.ID}##DebugUICollapsingHeader" ) )
                    DrawClassData( pad, $"ScratchPad{pad.ID}", "NextID" );
            }
            // TildeTools: Unindent removed, the using ends it
        }
        
        if ( ImGui.CollapsingHeader( "Console##ConsoleCollapsingHeader" ) )
        {
            // TildeTools
            // Upstream never unindented here, the using ends it with the block
            using var indent = ImRaii.PushIndent();
            // TildeTools ends
            string[] options = [.. WordsmithUI.Windows.Where(x => x is ScratchPadUI).Select(x => x.WindowName)];
            if ( _consolePadNumber >= options.Length )
                _consolePadNumber = 0;

            Window? w = null;
            // TildeTools
            using var disabled = ImRaii.Disabled( options.Length == 0 );
            // TildeTools ends
            if ( options.Length == 0 )
            {
                // TildeTools: BeginDisabled moved above the if, the using ends it
                _ = ImGui.Combo( "##ScratchPadConsoleSelectionCombo", ref _consolePadNumber, ["None"], 1 );
            }
            else
            {
                _ = ImGui.Combo( "##ScratchPadConsoleSelectionCombo", ref _consolePadNumber, options, options.Length );
                // TildeTools
                w = WordsmithUI.GetWindow( options[_consolePadNumber] ) as Window;
                // TildeTools ends
            }

            // TildeTools
            using ( var frame = ImRaii.ChildFrame( 99, new (ImGui.GetContentRegionAvail().X, WordsmithUI.LineHeight * 15 - Wordsmith.BUTTON_Y.Scale()*2 ) ) ) if ( frame )
            // TildeTools ends
            {
                if ( w is ScratchPadUI pad && Helpers.Console.Log.TryGetValue( pad, out List<string>? value ) )
                    ImGui.TextWrapped( string.Join("\n", value ) );
            }
            // TildeTools: EndChildFrame removed, the using ends it

            ImGui.SetNextItemWidth( -1 );
            if ( ImGui.InputText( $"##ConsoleInputLine", ref _consoleInput, 1024, ImGuiInputTextFlags.EnterReturnsTrue ) )
            {
                if ( w is ScratchPadUI pad )
                    _ = Helpers.Console.ProcessCommand( pad, $"devx {_consoleInput}" );
                _consoleInput = "";
            }
            // TildeTools: EndDisabled and its if removed, the using ends it
        }
    }
    private static void DrawClassData( object? obj, object id, params string[]? excludes )
    {
        if ( obj == null )
            return;

        // TildeTools
        using var indent = ImRaii.PushIndent();
        // TildeTools ends
        // Get the list of results
        IReadOnlyList<(int Type, string Name, string Value) > data = obj.GetProperties(excludes);

        // Draw Properties
        if ( ImGui.CollapsingHeader( $"Properties##{id}DebugUIHeader" ) )
        {
            // TildeTools
            using var innerIndent = ImRaii.PushIndent();
            // TildeTools ends
            foreach ( (int Type, string Name, string Value) in data.Where( d => d.Type == 0 ) )
                ImGui.TextWrapped( $"{Name}\t: {Value.Replace("\r", "\\r").Replace("\n", "\\n")}" );
            // TildeTools: Unindent removed, the using ends it
        }

        // Draw Fields
        if ( ImGui.CollapsingHeader( $"Fields##{id}DebugUIHeader" ) )
        {
            // TildeTools
            using var innerIndent = ImRaii.PushIndent();
            // TildeTools ends
            foreach ( (int Type, string Name, string Value) in data.Where( d => d.Type == 1 ) )
                ImGui.TextWrapped( $"{Name}\t: {Value.Replace( "\r", "\\r" ).Replace( "\n", "\\n" )}" );
            // TildeTools: Unindent removed, the using ends it
        }
        // TildeTools: Unindent removed, the using ends it
    }
}
#endif
