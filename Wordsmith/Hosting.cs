using System.Collections.Generic;
using Dalamud.Plugin.Ipc;

namespace Wordsmith;

/// <summary>
/// Optional cooperation with a plugin that splits and sends chat messages.
///
/// Wordsmith breaks text into pieces for copying out by hand, one at a time. Where
/// a splitter is present it can do the breaking up and the sending, so the button
/// sends the whole thing instead of filling the clipboard piece by piece.
///
/// The splitter becomes the authority on where the breaks fall, so the pieces shown
/// on screen are the ones that will actually be sent. Its markers and tags come
/// with it, which is why Wordsmith's own are left off while it is in charge. Two
/// sets would end up on every line.
///
/// Every call falls back to Wordsmith's own behaviour, so with no splitter
/// installed nothing here changes anything.
/// </summary>
internal static class Hosting
{
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
            var chunks = _splitLine.InvokeFunc(line, 0);
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
}
