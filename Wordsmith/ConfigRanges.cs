// TildeTools: written for this fork, not part of upstream Wordsmith.

namespace Wordsmith;

// The ranges the settings tabs offer, which a ctrl-clicked drag or a hand-edited config can go past
// Applied when the config loads and when settings are saved
// Thesaurus history allows 0, which upstream reads as unlimited
internal static class ConfigRanges
{
    internal static void Clamp(Configuration c)
    {
        c.SearchHistoryCount = Math.Clamp(c.SearchHistoryCount, 0, 100);
        c.ScratchPadMaximumTextLength = Math.Clamp(c.ScratchPadMaximumTextLength, 512, Wordsmith.MAX_SCRATCH_LENGTH);
        c.ScratchPadInputLineHeight = Math.Clamp(c.ScratchPadInputLineHeight, 3, 25);
        c.MaximumSuggestions = Math.Clamp(c.MaximumSuggestions, 0, 100);
        c.AutoSpellCheckDelay = Math.Clamp(c.AutoSpellCheckDelay, 0.1f, 100f);
        foreach (ChunkMarker marker in c.ChunkMarkers)
        {
            marker.Nth = Math.Clamp(marker.Nth, 1u, 100u);
            marker.StartPosition = Math.Clamp(marker.StartPosition, 1u, 100u);
        }
    }
}
