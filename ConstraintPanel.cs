using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// The 约束 (constraints) page: a camera track's constraint stack, modelled on Blender's Object Constraints.
/// Follow Path drives the position; Damped Track / Locked Track / Track To aim the camera at a character's bone
/// and are evaluated after it, top to bottom.
/// </summary>
public sealed class ConstraintPanel
{
    private readonly CamTimeline _tl;
    private readonly BoneSource _bones;
    private readonly Action<string, bool> _status;
    private readonly Action<IReadOnlyList<TrackConstraint>> _record;
    private readonly Func<bool> _recording;
    private readonly Action _stopRecording;
    private int _addType = (int)TrackType.TrackTo;
    private string _boneFilter = "";

    private const string Axes = "X\0Y\0Z\0-X\0-Y\0-Z\0";

    public ConstraintPanel(CamTimeline tl, BoneSource bones, Action<string, bool> status,
        Action<IReadOnlyList<TrackConstraint>> record, Func<bool> recording, Action stopRecording)
    {
        _tl = tl; _bones = bones; _status = status; _record = record; _recording = recording; _stopRecording = stopRecording;
    }

    private void Edit(Action change)
    {
        _tl.Snapshot();
        change();
        _tl.Touch();
    }

    /// <summary>One line on the timeline page describing the active track's constraints.</summary>
    public void DrawSummary(CamTrack track)
    {
        var parts = new List<string>();
        if(track.FollowPath != null) parts.Add($"跟随路径「{track.FollowCurve?.Name ?? "?"}」");
        parts.AddRange(track.TrackConstraints.Where(c => c.Enabled).Select(c => $"{TrackConstraint.TypeName(c.Type)} → {TargetLabel(c)} · {BoneLabel(c.Bone)}"));
        ImGui.TextDisabled(parts.Count == 0 ? "约束：无（在「约束」页添加跟随路径或追踪）" : "约束：" + string.Join("  ·  ", parts) + "（在「约束」页编辑）");
    }

    public void Draw(bool gpose)
    {
        ImGui.TextDisabled("参照 Blender 的物体约束：自上而下求值。跟随路径决定位置，追踪约束在其后决定旋转（可选 FoV）；被约束驱动的值不能在节点上手动修改。");
        ImGui.SetNextItemWidth(180);
        int trackIndex = _tl.ActiveTrackIndex;
        var names = _tl.Tracks.Select(t => t.Name).ToArray();
        if(ImGui.Combo("镜头轨道", ref trackIndex, names, names.Length)) _tl.ActiveTrackIndex = trackIndex;
        var track = _tl.ActiveTrack;

        ImGui.Separator();
        DrawFollowPath(track);

        ImGui.Separator();
        ImGui.TextUnformatted("追踪");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##addtrack", ref _addType, "阻尼追踪\0锁定追踪\0标准追踪\0");
        ImGui.SameLine();
        if(ImGui.Button("添加追踪约束"))
        {
            var c = new TrackConstraint { Type = (TrackType)_addType };
            Edit(() => track.TrackConstraints.Add(c));
            _status($"已为「{track.Name}」添加{TrackConstraint.TypeName(c.Type)}：默认追踪当前 GPose 目标的头部。旋转改由约束控制。", false);
        }
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("阻尼追踪：以最短旋转让相机对准目标，保留节点自身的滚转。\n锁定追踪：只绕锁定轴转动（默认 Y：只左右摇镜，俯仰保持节点值）。\n标准追踪：对准目标并保持向上轴朝上，不产生滚转。");

        if(track.TrackConstraints.Count == 0)
            ImGui.TextDisabled("没有追踪约束。追踪约束可与跟随路径同时使用。");

        for(int i = 0; i < track.TrackConstraints.Count; i++)
            if(!DrawTrack(track, track.TrackConstraints[i], i)) break;

        if(track.TrackConstraints.Count > 0) DrawRecording(track, gpose);
    }

    // ---------------------------------------------------------------- follow path

    private void DrawFollowPath(CamTrack track)
    {
        var target = track.FollowPath is { } fp ? _tl.Curves.FirstOrDefault(c => c.Id == fp.CurveId) : null;
        ImGui.TextUnformatted("跟随路径");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        if(ImGui.BeginCombo("##follow", target?.Name ?? "无（使用节点路径）"))
        {
            if(ImGui.Selectable("无（使用节点路径）", target == null) && track.FollowPath != null) { _tl.ClearFollowPath(track); _status("已移除跟随路径约束，节点保持当前位置。", false); }
            foreach(var curve in _tl.Curves)
                if(ImGui.Selectable($"{curve.Name}##{curve.Id}", curve == target) && curve != target)
                {
                    if(track.FollowPath != null) _tl.ClearFollowPath(track);
                    _tl.SetFollowPath(track, curve);
                    _status($"「{track.Name}」已跟随「{curve.Name}」：首节点在曲线 0%、末节点 100%，其余按帧均分。", false);
                }
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender 跟随路径约束：相机位置沿所选曲线运动，节点决定到达曲线上的位置与时间。\n旋转、FoV 由镜头节点（或下方的追踪约束）控制；高级曲线中的「位置偏移」叠加在路径点上。");
        if(_tl.Curves.Count == 0) { ImGui.SameLine(); ImGui.TextDisabled("先在「曲线」页添加曲线。"); }
        if(track.FollowPath is not { } c) return;
        ImGui.SameLine();
        bool follow = c.FollowCurve, radius = c.UseRadius;
        if(ImGui.Checkbox("跟随曲线", ref follow)) Edit(() => c.FollowCurve = follow);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("相机朝向随曲线方向转动（节点旋转作为相对曲线的旋转），曲线倾斜控制滚转。\n有追踪约束时，追踪在此之后决定最终朝向。");
        ImGui.SameLine();
        if(ImGui.Checkbox("使用半径", ref radius)) Edit(() => c.UseRadius = radius);
        ImGui.SameLine();
        if(ImGui.SmallButton("路径进度按时间均分"))
        {
            Edit(() => { foreach(var shot in track.Shots) shot.ResetFactors(); CurveModel.Ensure(track, _tl.Fps); });
            _status("已恢复：首节点 0%、末节点 100%，其余节点按帧均分在曲线上。", false);
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("清除手动设置的节点路径进度。快慢仍可用曲线编辑器的简单曲线调整。");
        if(c.FollowCurve)
        {
            int forward = (int)c.Forward, up = (int)c.Up;
            ImGui.SetNextItemWidth(60);
            if(ImGui.Combo("前进轴", ref forward, Axes)) Edit(() => c.Forward = (PathAxis)forward);
            ImGui.SameLine(); ImGui.SetNextItemWidth(60);
            if(ImGui.Combo("向上轴", ref up, Axes)) Edit(() => c.Up = (PathAxis)up);
        }
    }

    // ---------------------------------------------------------------- tracking

    private static string BoneLabel(string bone)
    {
        foreach(var (b, label) in BoneSource.Common) if(b == bone) return $"{label} ({bone})";
        return bone;
    }

    private static string TargetLabel(TrackConstraint c) => string.IsNullOrEmpty(c.TargetName) ? "当前 GPose 目标" : c.TargetName;

    /// <summary>Draws one constraint; false when the list changed (stop iterating this frame).</summary>
    private bool DrawTrack(CamTrack track, TrackConstraint c, int index)
    {
        ImGui.PushID(c.Id.ToString());
        try
        {
            bool open = ImGui.CollapsingHeader($"{index + 1}. {TrackConstraint.TypeName(c.Type)} → {TargetLabel(c)} · {BoneLabel(c.Bone)}{(c.Enabled ? "" : "（已停用）")}###hdr",
                ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 150);
            ImGui.BeginDisabled(index == 0);
            if(ImGui.SmallButton("上移")) { Edit(() => (track.TrackConstraints[index - 1], track.TrackConstraints[index]) = (c, track.TrackConstraints[index - 1])); return false; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(index == track.TrackConstraints.Count - 1);
            if(ImGui.SmallButton("下移")) { Edit(() => (track.TrackConstraints[index + 1], track.TrackConstraints[index]) = (c, track.TrackConstraints[index + 1])); return false; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if(ImGui.SmallButton("删除")) { Edit(() => track.TrackConstraints.Remove(c)); _status("已删除追踪约束（可撤销）。", false); return false; }
            if(!open) return true;

            bool enabled = c.Enabled;
            if(ImGui.Checkbox("启用", ref enabled)) Edit(() => c.Enabled = enabled);
            ImGui.SameLine();
            int type = (int)c.Type;
            ImGui.SetNextItemWidth(100);
            if(ImGui.Combo("类型", ref type, "阻尼追踪\0锁定追踪\0标准追踪\0")) Edit(() => c.Type = (TrackType)type);
            ImGui.SameLine();
            float influence = c.Influence;
            ImGui.SetNextItemWidth(120);
            if(ImGui.SliderFloat("影响", ref influence, 0, 1, "%.2f")) { c.Influence = influence; _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Blender 影响：0 不起作用，1 完全对准目标，中间值在约束前后的旋转之间插值。");

            DrawTarget(c);

            var offset = c.TargetOffset;
            ImGui.SetNextItemWidth(220);
            if(ImGui.DragFloat3("目标偏移 (米)", ref offset, .005f)) { c.TargetOffset = offset; _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("世界坐标偏移，加在骨骼位置上。");
            ImGui.SameLine();
            var screen = c.ScreenOffset * 100;
            ImGui.SetNextItemWidth(160);
            if(ImGui.DragFloat2("画面偏移 (%)", ref screen, .2f, -100, 100, "%.1f")) { c.ScreenOffset = Vector2.Clamp(screen / 100, -Vector2.One, Vector2.One); _tl.Touch(); }
            if(ImGui.IsItemActivated()) _tl.Snapshot();
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("目标在画面中的位置：0,0 为画面中心，X 向右、Y 向上，±100% 为画面边缘。\n例如 X = -33% 把目标放在三分线左侧。");

            int trackAxis = (int)c.TrackAxis;
            ImGui.SetNextItemWidth(60);
            if(ImGui.Combo("追踪轴", ref trackAxis, Axes)) Edit(() => c.TrackAxis = (PathAxis)trackAxis);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("对准目标的相机轴。游戏相机朝 -Z 看，一般保持 -Z。");
            if(c.Type == TrackType.TrackTo)
            {
                ImGui.SameLine();
                int up = (int)c.UpAxis;
                ImGui.SetNextItemWidth(60);
                if(ImGui.Combo("向上轴", ref up, Axes)) Edit(() => c.UpAxis = (PathAxis)up);
                if(ImGui.IsItemHovered()) ImGui.SetTooltip("保持朝向世界上方的相机轴（默认 Y）。");
            }
            else if(c.Type == TrackType.Locked)
            {
                ImGui.SameLine();
                int lockAxis = (int)c.LockAxis;
                ImGui.SetNextItemWidth(60);
                if(ImGui.Combo("锁定轴", ref lockAxis, Axes)) Edit(() => c.LockAxis = (PathAxis)lockAxis);
                if(ImGui.IsItemHovered()) ImGui.SetTooltip("只绕这根相机轴转动。Y：左右摇镜；X：上下摇镜。其余方向保持节点的旋转。");
            }
            ImGui.SameLine();
            bool keep = c.KeepFraming;
            if(ImGui.Checkbox("保持取景大小", ref keep)) Edit(() => c.KeepFraming = keep);
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("按相机到目标的距离自动调整 FoV，让目标在画面中的大小保持在轨道第一个节点时的样子（类似滑动变焦）。\n开启后除首节点外，节点的 FoV 不能手动修改。");

            DrawRecordState(track, c);
            return true;
        }
        finally { ImGui.PopID(); }
    }

    private void DrawTarget(TrackConstraint c)
    {
        ImGui.SetNextItemWidth(200);
        if(ImGui.BeginCombo("角色", TargetLabel(c)))
        {
            if(ImGui.Selectable("当前 GPose 目标", string.IsNullOrEmpty(c.TargetName))) Edit(() => { c.TargetName = ""; c.TargetIndex = -1; });
            foreach(var (index, name) in _bones.Characters())
                if(ImGui.Selectable($"{name}##{index}", c.TargetName == name && c.TargetIndex == index))
                    Edit(() => { c.TargetName = name; c.TargetIndex = index; });
            ImGui.EndCombo();
        }
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("要追踪的角色（GPose 中的角色，包括 Brio 生成的）。\n「当前 GPose 目标」会跟着 GPose 选中的目标变化。");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        if(ImGui.BeginCombo("骨骼", BoneLabel(c.Bone), ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##bonefilter", "搜索骨骼名…", ref _boneFilter, 64);
            var character = _bones.Resolve(c);
            var all = character != null ? _bones.BoneNames(character) : [];
            foreach(var (bone, label) in BoneSource.Common)
                if(Matches(bone, label) && (all.Count == 0 || all.Contains(bone)) && ImGui.Selectable($"{label}  {bone}##c{bone}", c.Bone == bone))
                    Edit(() => c.Bone = bone);
            if(all.Count > 0) ImGui.Separator();
            else ImGui.TextDisabled("角色不可用，只显示常用骨骼。");
            foreach(var bone in all)
                if(Matches(bone, "") && ImGui.Selectable(bone, c.Bone == bone)) Edit(() => c.Bone = bone);
            ImGui.EndCombo();
        }
        // live status of the target
        var who = _bones.Resolve(c);
        string error = "角色不存在";
        var live = who != null ? _bones.BoneWorld(who, c.Bone, out error) : null;
        ImGui.SameLine();
        if(live is { } p) ImGui.TextDisabled($"({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})");
        else ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), $"不可用：{error}");
    }

    private bool Matches(string bone, string label)
        => _boneFilter.Length == 0 || bone.Contains(_boneFilter, StringComparison.OrdinalIgnoreCase) || label.Contains(_boneFilter);

    // ---------------------------------------------------------------- recording

    private static (int first, int last) Range(CamTrack track)
        => track.Keys.Count == 0 ? (0, 0) : (track.Keys.Min(k => k.Frame), CamTimeline.TrackLastFrame(track));

    private void DrawRecordState(CamTrack track, TrackConstraint c)
    {
        var (first, last) = Range(track);
        if(c.Recorded.Count == 0)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "未录制目标轨迹：预览实时追踪；导出 / 联动 XAT / 烘焙时按目标当前位置计算。");
        else if(c.RecordingStale(first, last))
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), c.RecordedFor != c.TargetKey
                ? $"录制已过期：角色或骨骼已更改（录制了 {c.Recorded.Count} 帧）。"
                : $"录制已过期：只覆盖第 {c.Recorded.Keys[0]}–{c.Recorded.Keys[^1]} 帧，当前轨道为第 {first}–{last} 帧。");
        else ImGui.TextColored(new Vector4(0.5f, 1, 0.5f, 1), $"已录制 {c.Recorded.Count} 帧（第 {c.Recorded.Keys[0]}–{c.Recorded.Keys[^1]} 帧），导出时使用。");
        ImGui.SameLine();
        ImGui.BeginDisabled(_recording());
        if(ImGui.SmallButton("录制此约束")) _record([c]);
        ImGui.EndDisabled();
        if(c.Recorded.Count > 0)
        {
            ImGui.SameLine();
            if(ImGui.SmallButton("清除录制")) Edit(() => { c.Recorded.Clear(); c.RecordedFor = ""; });
        }
    }

    private void DrawRecording(CamTrack track, bool gpose)
    {
        ImGui.Separator();
        var (first, last) = Range(track);
        if(_recording())
        {
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), "正在录制目标轨迹……");
            ImGui.SameLine();
            if(ImGui.Button("停止录制")) _stopRecording();
            return;
        }
        ImGui.BeginDisabled(!gpose || track.Keys.Count == 0);
        if(ImGui.Button("录制目标轨迹（全部追踪约束）")) _record(track.TrackConstraints.Where(c => c.Enabled).ToList());
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"从第 {first} 帧播放绑定的灵动时间轴 / 动画进度条到第 {last} 帧，逐帧记录骨骼的世界位置。\n导出 .xcp、联动 XAT 和烘焙使用录制结果，让镜头跟随角色的动作；预览镜头始终实时追踪。");
        ImGui.SameLine();
        ImGui.TextDisabled("骨骼随动画移动，导出前请先录制。");
    }
}
