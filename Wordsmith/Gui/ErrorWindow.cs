using Dalamud.Bindings.ImGui;
using System.Text.Json; // Add this for JsonSerializerOptions


namespace Wordsmith.Gui;

// TildeTools
internal sealed class ErrorWindow( Dictionary<string, object> dump ) : MessageBox( $"Wordsmith Error", Hosting.IsHosted ? _HOSTED_MESSAGE : _MESSAGE, ButtonStyle.YesNo, Callback)
// TildeTools ends
{
    private const string _MESSAGE = "Wordsmith has encountered an error.\nCopy error dump to clipboard and open bug report page?\n\nWARNING: I WILL be able to see anything and everything\ntyped as part of the log.";
    // TildeTools
    // No bug report page on a modified build
    private const string _HOSTED_MESSAGE = "Wordsmith has encountered an error.\nCopy the error dump to the clipboard?\n\nIt includes anything you typed in Wordsmith.";
    // TildeTools ends
    private static readonly JsonSerializerOptions _jsonOptions = new() { IncludeFields = true }; // Cache the options
    internal Dictionary<string, object> ErrorDump = dump;

    public static void Callback(MessageBox mb)
    {
        if ( mb is ErrorWindow ew )
        {
            try
            {
                if ( ew.Result == DialogResult.Yes )
                {
                    foreach ( string key in ew.ErrorDump.Keys )
                    {
                        if ( ew.ErrorDump[key] is IntPtr )
                            _ = ew.ErrorDump.Remove( key );
                    }
                    ImGui.SetClipboardText( JsonSerializer.Serialize( ew.ErrorDump, _jsonOptions ) );
                    // TildeTools
                    if ( !Hosting.IsHosted )
                        _ = System.Diagnostics.Process.Start( new System.Diagnostics.ProcessStartInfo( "https://github.com/MythicPalette/Wordsmith-DalamudPlugin/issues" ) { UseShellExecute = true } );
                    // TildeTools ends
                }
            }
            catch ( Exception e )
            {
                Wordsmith.PluginLog.Error( e.ToString() );
            }
        }
        WordsmithUI.RemoveWindow( mb );
    }
}
