// TildeTools: written for this fork, not part of upstream Wordsmith.

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Wordsmith.Gui;

internal sealed partial class SettingsUI
{
    // Hosted, Lang loads no dictionary, so upstream's dictionary list and added words would do nothing
    // Its own tab rather than a return from inside upstream's, which would have to end that tab's child and item by hand
    // Upstream's settings above those still apply, and so does the cleaning string: Wordsmith's own checker runs, asking TildeTools
    private void DrawHostedSpellCheckTab()
    {
        using var tab = ImRaii.TabItem("Spell Check##SettingsUITabItem");
        if (!tab.Success)
            return;

        using var child = ImRaii.Child("DictionarySettingsChild", new(-1, GetCanvasSize()));
        if (!child.Success)
            return;

        _ = ImGui.Checkbox("Auto-Spell Check", ref this._autospellcheck);
        ImGuiExt.SetHoveredTooltip("When enabled, spell check will automatically run after a pause in typing is detected.");
        ImGui.SameLine();

        _ = ImGui.Checkbox("Ignore Hyphen-Terminated Words##SettingsUICheckbox", ref this._ignoreHypen);
        ImGuiExt.SetHoveredTooltip("This is useful in roleplay for emulating cut speech.\ni.e. \"How dare yo-,\" she was cut off by the rude man.");
        ImGui.SameLine();

        _ = ImGui.Checkbox("Fix Spacing.", ref this._fixDoubleSpace);
        ImGuiExt.SetHoveredTooltip("When enabled, Scratch Pads will programmatically remove extra\nspaces from your text for you.");
        ImGui.Separator();

        var advanced = Wordsmith.Configuration.ShowAdvancedSettings;
        var barWidth = advanced ? ImGui.GetWindowContentRegionMax().X / 2.0f : ImGui.GetWindowContentRegionMax().X;

        ImGui.SetNextItemWidth(barWidth - 170 * ImGuiHelpers.GlobalScale);
        _ = ImGui.DragInt("Maximum Suggestions", ref this._maxSuggestions, 0.1f, 0, 100);
        ImGuiExt.SetHoveredTooltip("The number of spelling suggestions to return with spell checking. 0 is unlimited results.");

        if (advanced)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(barWidth - 160 * ImGuiHelpers.GlobalScale);
            _ = ImGui.DragFloat("Auto-Spellcheck Delay (Seconds)", ref this._autospellcheckdelay, 0.1f, 0.1f, 100f);
            ImGuiExt.SetHoveredTooltip("The time in seconds to wait after typing stops to spell check.");
            ImGui.Separator();
        }

        ImGui.TextWrapped("The dictionaries and the words you've added are on TildeTools' Spelling tab.");

        if (!advanced)
            return;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionMax().X - this._style.WindowPadding.X - ImGui.CalcTextSize("Cleaning String").X);
        _ = ImGui.InputText("Cleaning String", ref this._punctuationCleaningString, 1024);
        ImGuiExt.SetHoveredTooltip("This is the complete list of punctuation to be cleaned from the start/end of the word when checking for spelling errors.\nWARNING: Altering this can cause undesired behavior.");
    }
}
