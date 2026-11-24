using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

public enum Shortcut { Undo, Redo, Delete, AnchorTimeline, AnchorCamera, Select, ExtendSelection, Box, ZoomX, ZoomY, ScrollY, ToggleCut }

/// <summary>Only overrides are persisted. Keyboard chords store the virtual key in the low byte and modifiers above it.</summary>
public sealed class ShortcutBindings
{
    public Dictionary<Shortcut, int> Overrides { get; set; } = new();
    private static readonly string[] ModifierSuffixes = ["", "Ctrl", "Shift", "Ctrl+Shift", "Alt", "Ctrl+Alt", "Shift+Alt", "Ctrl+Shift+Alt"];
    private static readonly (Shortcut action, string key, int value)[] Entries = [
        (Shortcut.Undo, "shortcut.act.undo", 0x15A),
        (Shortcut.Redo, "shortcut.act.redo", 0),
        (Shortcut.Delete, "shortcut.act.delete", 0x2E),
        (Shortcut.ToggleCut, "shortcut.act.toggle_cut", 0),
        (Shortcut.AnchorTimeline, "shortcut.act.anchor_timeline", 0x454),
        (Shortcut.AnchorCamera, "shortcut.act.anchor_camera", 0x443),
        (Shortcut.Select, "shortcut.act.select", 1),
        (Shortcut.ExtendSelection, "shortcut.act.extend_selection", 2),
        (Shortcut.Box, "shortcut.act.box", 1),
        (Shortcut.ZoomX, "shortcut.act.zoom_x", 1),
        (Shortcut.ZoomY, "shortcut.act.zoom_y", 5),
        (Shortcut.ScrollY, "shortcut.act.scroll_y", 4),
    ];

    internal static bool IsKeyboard(Shortcut action) => action <= Shortcut.AnchorCamera || action == Shortcut.ToggleCut;
    private static readonly Dictionary<int, string> Keys = BuildKeys();
    internal int Get(Shortcut action) => Overrides.TryGetValue(action, out int value) ? value : Entries.First(e => e.action == action).value;

    private static string ModifierLabel(int index) => index == 0 ? I18n.T("common.none") : ModifierSuffixes[index];
    private static string KeyLabel(int code) => code == 0 ? I18n.T("common.unbound") : Keys.GetValueOrDefault(code, I18n.T("common.unknown"));

    internal bool Held(Shortcut action)
    {
        var io = ImGui.GetIO();
        return Get(action) == ((io.KeyCtrl ? 1 : 0) | (io.KeyShift ? 2 : 0) | (io.KeyAlt ? 4 : 0));
    }

    private static Dictionary<int, string> BuildKeys()
    {
        var keys = new Dictionary<int, string>
        {
            [0] = "", [8] = "Backspace", [9] = "Tab", [13] = "Enter", [27] = "Esc", [32] = "Space",
            [33] = "Page Up", [34] = "Page Down", [35] = "End", [36] = "Home",
            [37] = "←", [38] = "↑", [39] = "→", [40] = "↓", [45] = "Insert", [46] = "Delete",
        };
        for(int key = '0'; key <= '9'; key++) keys.Add(key, ((char)key).ToString());
        for(int key = 'A'; key <= 'Z'; key++) keys.Add(key, ((char)key).ToString());
        for(int i = 0; i < 24; i++) keys.Add(112 + i, $"F{i + 1}");
        for(int i = 0; i < 10; i++) keys.Add(96 + i, $"Num {i}");
        foreach(var (key, label) in new[]
        {
            (106, "Num *"), (107, "Num +"), (109, "Num -"), (110, "Num ."), (111, "Num /"),
            (186, ";"), (187, "="), (188, ","), (189, "-"), (190, "."), (191, "/"),
            (192, "`"), (219, "["), (220, "\\"), (221, "]"), (222, "'"),
        }) keys.Add(key, label);
        return keys;
    }

    internal bool Draw(ref string message, bool brioTheme = true)
    {
        bool changed = false;
        float s = XcpStyle.Scale;

        string resetLabel = I18n.T("common.reset_default");
        float resetW = ImGui.CalcTextSize(resetLabel).X + 34f * s;
        float availW = ImGui.GetContentRegionAvail().X;

        ImGui.AlignTextToFramePadding();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(160f * s, availW - resetW - 10f * s));
        ImGui.TextDisabled(I18n.T("shortcut.hint"));
        ImGui.PopTextWrapPos();
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX() + 8f * s, availW - resetW));
        if(UiWidgets.IconTextButton("reset_keys", FontAwesomeIcon.Undo, resetLabel,
            I18n.T("shortcut.reset_tip"), brioTheme: brioTheme))
        {
            Overrides.Clear();
            message = "";
            changed = true;
        }

        if(message.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(XcpStyle.Warning, message);
        }

        ImGui.Spacing();
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX;
        if(ImGui.BeginTable("##shortcuts_table", 2, flags, new Vector2(0, MathF.Max(160f * s, ImGui.GetContentRegionAvail().Y))))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(I18n.T("shortcut.col.action"), ImGuiTableColumnFlags.WidthStretch, 1.15f);
            ImGui.TableSetupColumn(I18n.T("shortcut.col.binding"), ImGuiTableColumnFlags.WidthFixed, 235f * s);
            ImGui.TableHeadersRow();

            DrawCategoryHeader(I18n.T("shortcut.cat.keyboard"), brioTheme);
            foreach(var entry in Entries.Where(e => IsKeyboard(e.action)))
                changed |= DrawRow(entry.action, I18n.T(entry.key), ref message, s);

            DrawCategoryHeader(I18n.T("shortcut.cat.mouse"), brioTheme);
            foreach(var entry in Entries.Where(e => !IsKeyboard(e.action)))
                changed |= DrawRow(entry.action, I18n.T(entry.key), ref message, s);

            ImGui.EndTable();
        }
        return changed;
    }

    private static void DrawCategoryHeader(string title, bool brioTheme)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(brioTheme ? XcpStyle.Info : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], title);
        ImGui.TableSetColumnIndex(1);
    }

    private bool DrawRow(Shortcut action, string label, ref string message, float s)
    {
        bool changed = false;
        ImGui.TableNextRow();
        ImGui.PushID((int)action);

        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);

        ImGui.TableSetColumnIndex(1);
        int current = Get(action), value = current;
        bool keyboard = IsKeyboard(action);
        int mods = keyboard ? current >> 8 : current;

        ImGui.SetNextItemWidth(keyboard ? 120f * s : 225f * s);
        if(ImGui.BeginCombo("##mods", mods < 0 ? I18n.T("common.unbound") : ModifierLabel(mods & 7)))
        {
            for(int i = keyboard ? 0 : -1; i < ModifierSuffixes.Length; i++)
                if(ImGui.Selectable(i < 0 ? I18n.T("common.unbound") : ModifierLabel(i), mods == i))
                    value = keyboard ? (i << 8) | (current & 255) : i;
            ImGui.EndCombo();
        }
        if(keyboard)
        {
            ImGui.SameLine(0, 4f * s);
            ImGui.SetNextItemWidth(100f * s);
            if(ImGui.BeginCombo("##key", KeyLabel(current & 255)))
            {
                foreach(var (key, _) in Keys)
                    if(ImGui.Selectable($"{KeyLabel(key)}##k{key}", key == (current & 255))) value = (current & 0x700) | key;
                ImGui.EndCombo();
            }
        }
        if(value != current)
        {
            bool wheel = action is >= Shortcut.ZoomX and <= Shortcut.ScrollY;
            var conflict = Entries.FirstOrDefault(e => e.action != action && Get(e.action) == value &&
                (keyboard && (value & 255) != 0 && IsKeyboard(e.action) || wheel && value >= 0 && e.action is >= Shortcut.ZoomX and <= Shortcut.ScrollY));
            if(conflict.key != null) message = I18n.T("shortcut.conflict", I18n.T(conflict.key));
            else { Overrides[action] = value; changed = true; message = ""; }
        }

        ImGui.PopID();
        return changed;
    }
}
