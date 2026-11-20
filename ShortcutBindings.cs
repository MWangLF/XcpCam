using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XcpCam;

public enum Shortcut { Undo, Redo, Delete, AnchorTimeline, AnchorCamera, Select, ExtendSelection, Box, ZoomX, ZoomY, ScrollY, ToggleCut }

/// <summary>Only overrides are persisted. Keyboard chords store the virtual key in the low byte and modifiers above it.</summary>
public sealed class ShortcutBindings
{
    public Dictionary<Shortcut, int> Overrides { get; set; } = new();
    private static readonly string[] Modifiers = ["无", "Ctrl", "Shift", "Ctrl+Shift", "Alt", "Ctrl+Alt", "Shift+Alt", "Ctrl+Shift+Alt"];
    private static readonly (Shortcut action, string label, int value)[] Entries = [
        (Shortcut.Undo, "撤销", 0x15A), (Shortcut.Redo, "重做", 0), (Shortcut.Delete, "删除所选节点", 0x2E),
        (Shortcut.AnchorTimeline, "曲线锚定时间轴", 0x454), (Shortcut.AnchorCamera, "曲线锚定镜头", 0x443),
        (Shortcut.Select, "单击多选（游戏画面、时间轴、简单曲线）", 1),
        (Shortcut.ExtendSelection, "单击加选（时间轴、曲线编辑器）／叠加高级通道", 2),
        (Shortcut.Box, "空白处拖动框选", 1), (Shortcut.ZoomX, "滚轮缩放横轴", 1),
        (Shortcut.ZoomY, "滚轮缩放纵轴／轨道行", 5), (Shortcut.ScrollY, "滚轮纵向滚动", 4),
        (Shortcut.ToggleCut, "切换所选镜头节点的切镜", 0) ];
    internal static bool IsKeyboard(Shortcut action) => action <= Shortcut.AnchorCamera || action == Shortcut.ToggleCut;
    private static readonly Dictionary<int, string> Keys = BuildKeys();
    internal int Get(Shortcut action) => Overrides.TryGetValue(action, out int value) ? value : Entries[(int)action].value;
    internal bool Held(Shortcut action)
    {
        var io = ImGui.GetIO();
        return Get(action) == ((io.KeyCtrl ? 1 : 0) | (io.KeyShift ? 2 : 0) | (io.KeyAlt ? 4 : 0));
    }

    private static Dictionary<int, string> BuildKeys()
    {
        var keys = new Dictionary<int, string> { [0] = "未绑定", [8] = "Backspace", [9] = "Tab", [13] = "Enter", [27] = "Esc", [32] = "Space",
            [33] = "Page Up", [34] = "Page Down", [35] = "End", [36] = "Home", [37] = "←", [38] = "↑", [39] = "→", [40] = "↓", [45] = "Insert", [46] = "Delete" };
        for(int key = '0'; key <= '9'; key++) keys.Add(key, ((char)key).ToString());
        for(int key = 'A'; key <= 'Z'; key++) keys.Add(key, ((char)key).ToString());
        for(int i = 0; i < 24; i++) keys.Add(112 + i, $"F{i + 1}");
        for(int i = 0; i < 10; i++) keys.Add(96 + i, $"Num {i}");
        foreach(var (key, label) in new[] { (106,"Num *"), (107,"Num +"), (109,"Num -"), (110,"Num ."), (111,"Num /"),
            (186,";"), (187,"="), (188,","), (189,"-"), (190,"."), (191,"/"), (192,"`"), (219,"["), (220,"\\"), (221,"]"), (222,"'") }) keys.Add(key, label);
        return keys;
    }

    internal bool Draw(ref string message)
    {
        bool changed = false;
        foreach(var (action, label, _) in Entries)
        {
            ImGui.PushID((int)action);
            int current = Get(action), value = current;
            bool keyboard = IsKeyboard(action);
            int mods = keyboard ? current >> 8 : current;
            ImGui.SetNextItemWidth(150);
            if(ImGui.BeginCombo("##mods", mods < 0 ? "未绑定" : Modifiers[mods & 7]))
            {
                for(int i = keyboard ? 0 : -1; i < Modifiers.Length; i++)
                    if(ImGui.Selectable(i < 0 ? "未绑定" : Modifiers[i], mods == i)) value = keyboard ? (i << 8) | (current & 255) : i;
                ImGui.EndCombo();
            }
            if(keyboard)
            {
                ImGui.SameLine(); ImGui.SetNextItemWidth(105);
                if(ImGui.BeginCombo("##key", Keys.GetValueOrDefault(current & 255, "未知")))
                {
                    foreach(var (key, name) in Keys)
                        if(ImGui.Selectable(name, key == (current & 255))) value = (current & 0x700) | key;
                    ImGui.EndCombo();
                }
            }
            ImGui.SameLine(); ImGui.TextUnformatted(label);
            if(value != current)
            {
                bool wheel = action is >= Shortcut.ZoomX and <= Shortcut.ScrollY;
                var conflict = Entries.FirstOrDefault(e => e.action != action && Get(e.action) == value &&
                    (keyboard && (value & 255) != 0 && IsKeyboard(e.action) || wheel && value >= 0 && e.action is >= Shortcut.ZoomX and <= Shortcut.ScrollY));
                if(conflict.label != null) message = $"该组合已用于「{conflict.label}」，请先更改或清除原绑定。";
                else { Overrides[action] = value; changed = true; message = ""; }
            }
            ImGui.PopID();
        }
        if(ImGui.Button("恢复默认快捷键")) { Overrides.Clear(); message = ""; changed = true; }
        if(message.Length > 0) ImGui.TextWrapped(message);
        return changed;
    }
}
