using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// The Constraints page: a camera track's constraint stack, modelled on Blender's Object Constraints.
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
    private readonly HashSet<Guid> _collapsed = [];
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

    /// <summary>Compact summary card content rendered inside the right N-Panel.</summary>
    public void DrawSummary(CamTrack track, bool brioTheme = true, bool windowOpen = false, Action<bool?>? setWindowOpen = null)
    {
        int total = (track.FollowPath != null ? 1 : 0) + track.TrackConstraints.Count(c => c.Enabled);
        if(UiWidgets.SectionHeaderAction(total > 0 ? I18n.T("constraint.summary_title_count", total) : I18n.T("constraint.summary_title"),
            FontAwesomeIcon.ExternalLinkAlt, "goto_constraints",
            windowOpen ? I18n.T("constraint.win_close_tip") : I18n.T("constraint.win_open_tip"), windowOpen, brioTheme, FontAwesomeIcon.Link))
            setWindowOpen?.Invoke(null);

        if(total == 0)
        {
            ImGui.TextDisabled(I18n.T("constraint.summary_none"));
            return;
        }

        if(track.FollowPath != null)
        {
            bool anchored = CamTimeline.CurveAnchoredToTimeline(track);
            if(UiWidgets.Chip("sum_fp", I18n.T("constraint.summary_follow_chip", track.FollowCurve?.Name ?? "?"),
                XcpStyle.Info, I18n.T("constraint.summary_follow_tip", anchored ? I18n.T("anchor.timeline") : I18n.T("anchor.camera"))))
                setWindowOpen?.Invoke(true);
        }
        foreach(var c in track.TrackConstraints.Where(x => x.Enabled))
        {
            var (first, last) = Range(track);
            bool fresh = c.Recorded.Count > 0 && !c.RecordingStale(first, last);
            var tint = fresh ? XcpStyle.Success : XcpStyle.Warning;
            if(UiWidgets.Chip($"sum_tc_{c.Id}", $"{TrackConstraint.TypeName(c.Type)} · {BoneShortLabel(c.Bone)}",
                tint, I18n.T("constraint.summary_track_tip", TargetLabel(c), BoneLabel(c.Bone),
                    fresh ? I18n.T("constraint.baked_short") : I18n.T("constraint.unbaked_short"))))
                setWindowOpen?.Invoke(true);
        }
    }

    public void Draw(bool gpose, bool brioTheme = true)
    {
        float s = XcpStyle.Scale;

        // ---- top bar ----
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(I18n.T("common.track"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f * s);
        int trackIndex = _tl.ActiveTrackIndex;
        var names = _tl.Tracks.Select(t => t.Name).ToArray();
        if(ImGui.Combo("##c_track", ref trackIndex, names, names.Length)) _tl.ActiveTrackIndex = trackIndex;
        var track = _tl.ActiveTrack;

        ImGui.SameLine();
        if(UiWidgets.IconTextButton("add_tc_btn", FontAwesomeIcon.Plus, I18n.T("constraint.btn_add"),
            I18n.T("constraint.btn_add_tip"), brioTheme: brioTheme, dropdownCaret: true))
            ImGui.OpenPopup("##add_tc_popup");

        if(ImGui.BeginPopup("##add_tc_popup"))
        {
            void Option(TrackType type, string tip)
            {
                if(ImGui.MenuItem(TrackConstraint.TypeName(type)))
                {
                    var c = new TrackConstraint { Type = type };
                    Edit(() => track.TrackConstraints.Add(c));
                    _status(I18n.T("constraint.status_added", track.Name, TrackConstraint.TypeName(c.Type)), false);
                }
                UiWidgets.Tooltip(tip);
            }
            Option(TrackType.Damped, I18n.T("constraint.type.damped_tip"));
            Option(TrackType.Locked, I18n.T("constraint.type.locked_tip"));
            Option(TrackType.TrackTo, I18n.T("constraint.type.track_to_tip"));
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        UiWidgets.HelpMarker(I18n.T("constraint.panel_help"));

        ImGui.Spacing();

        float footerH = track.TrackConstraints.Count > 0 ? 40f * s : 0f;
        float scrollH = MathF.Max(120f * s, ImGui.GetContentRegionAvail().Y - footerH);
        if(ImGui.BeginChild("##constraints_scroll", new Vector2(0, scrollH), false))
        {
            DrawFollowPathCard(track, brioTheme);
            ImGui.Spacing();

            if(track.TrackConstraints.Count == 0)
            {
                using var empty = UiWidgets.Card("tc_empty", brioTheme);
                ImGui.TextDisabled(I18n.T("constraint.empty_hint"));
            }
            else
            {
                for(int i = 0; i < track.TrackConstraints.Count; i++)
                {
                    if(!DrawTrackCard(track, track.TrackConstraints[i], i, brioTheme)) break;
                    ImGui.Spacing();
                }
            }
        }
        ImGui.EndChild();

        if(track.TrackConstraints.Count > 0) DrawRecordingFooter(track, gpose, brioTheme);
    }

    // ---------------------------------------------------------------- follow path

    private void DrawFollowPathCard(CamTrack track, bool brioTheme)
    {
        float s = XcpStyle.Scale;
        var target = track.FollowPath is { } fp ? _tl.Curves.FirstOrDefault(c => c.Id == fp.CurveId) : null;

        using var card = UiWidgets.Card("card_follow_path", brioTheme);
        UiWidgets.SectionHeader(I18n.T("constraint.fp.title"), brioTheme, FontAwesomeIcon.Route,
            I18n.T("constraint.fp.tip"));

        ImGui.SetNextItemWidth(220f * s);
        string noneLabel = I18n.T("constraint.fp.none");
        if(ImGui.BeginCombo($"{I18n.T("constraint.fp.path_label")}##follow", target?.Name ?? noneLabel))
        {
            if(ImGui.Selectable($"{noneLabel}##fp_none", target == null) && track.FollowPath != null)
            { _tl.ClearFollowPath(track); _status(I18n.T("constraint.fp.removed"), false); }
            foreach(var curve in _tl.Curves)
                if(ImGui.Selectable($"{curve.Name}##{curve.Id}", curve == target) && curve != target)
                {
                    if(track.FollowPath != null) _tl.ClearFollowPath(track);
                    _tl.SetFollowPath(track, curve);
                    _status(I18n.T("constraint.fp.set", track.Name, curve.Name), false);
                }
            ImGui.EndCombo();
        }
        if(_tl.Curves.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(I18n.T("constraint.fp.no_curves_hint"));
        }
        if(track.FollowPath is not { } c) return;

        ImGui.SameLine();
        if(UiWidgets.TogglePill("fp_rot", I18n.T("constraint.fp.follow_curve"), c.FollowCurve,
            I18n.T("constraint.fp.follow_curve_tip"), brioTheme: brioTheme))
            Edit(() => c.FollowCurve = !c.FollowCurve);

        ImGui.SameLine();
        if(UiWidgets.TogglePill("fp_rad", I18n.T("constraint.fp.use_radius"), c.UseRadius,
            I18n.T("constraint.fp.use_radius_tip"), brioTheme: brioTheme))
            Edit(() => c.UseRadius = !c.UseRadius);

        ImGui.SameLine();
        if(UiWidgets.IconTextButton("fp_reset", FontAwesomeIcon.Sync, I18n.T("constraint.fp.reset"),
            I18n.T("constraint.fp.reset_tip"), brioTheme: brioTheme))
        {
            Edit(() => { foreach(var shot in track.Shots) shot.ResetFactors(); CurveModel.Ensure(track, _tl.Fps); });
            _status(I18n.T("constraint.fp.reset_status"), false);
        }

        if(target?.PathSpline != null)
        {
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(I18n.T("constraint.fp.anchor_label"));
            ImGui.SameLine();
            bool anchored = CamTimeline.CurveAnchoredToTimeline(track);
            int mode = anchored ? 1 : 0;
            string camLbl = I18n.T("anchor.camera"), tlLbl = I18n.T("anchor.timeline");
            float segW = MathF.Max(200f * s, MathF.Max(ImGui.CalcTextSize(camLbl).X, ImGui.CalcTextSize(tlLbl).X) * 2f + 28f * s);
            if(UiWidgets.Segmented("fp_anchor", ref mode, [camLbl, tlLbl], brioTheme, segW,
                [I18n.T("constraint.fp.anchor_camera_tip"),
                 I18n.T("constraint.fp.anchor_timeline_tip", CamTimeline.TimelineAnchorNotice)]))
            {
                bool toTimeline = mode == 1;
                if(toTimeline && !anchored)
                    _status(_tl.AnchorCurve(track, true)
                        ? I18n.T("constraint.fp.anchor_ok_timeline", CamTimeline.TimelineAnchorNotice)
                        : I18n.T("constraint.fp.anchor_err_no_visit"), !CamTimeline.CurveAnchoredToTimeline(track));
                else if(!toTimeline && anchored && _tl.AnchorCurve(track, false))
                    _status(I18n.T("constraint.fp.anchor_ok_camera"), false);
            }
        }

        if(c.FollowCurve)
        {
            ImGui.Spacing();
            int forward = (int)c.Forward, up = (int)c.Up;
            ImGui.SetNextItemWidth(65f * s);
            if(ImGui.Combo($"{I18n.T("constraint.fp.forward_axis")}##fp_fwd", ref forward, Axes)) Edit(() => c.Forward = (PathAxis)forward);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(65f * s);
            if(ImGui.Combo($"{I18n.T("constraint.fp.up_axis")}##fp_up", ref up, Axes)) Edit(() => c.Up = (PathAxis)up);
        }
    }

    // ---------------------------------------------------------------- tracking

    private static string BoneLabel(string bone)
    {
        foreach(var (b, label) in BoneSource.Common) if(b == bone) return $"{label} ({bone})";
        return bone;
    }

    private static string BoneShortLabel(string bone)
    {
        foreach(var (b, label) in BoneSource.Common) if(b == bone) return label;
        return bone;
    }

    private static string TargetLabel(TrackConstraint c) => string.IsNullOrEmpty(c.TargetName) ? I18n.T("brio.actor_current_gpose") : c.TargetName;

    /// <summary>Draws one constraint card; returns false when the list changed.</summary>
    private bool DrawTrackCard(CamTrack track, TrackConstraint c, int index, bool brioTheme)
    {
        float s = XcpStyle.Scale;
        using var card = UiWidgets.Card($"tc_{c.Id}", brioTheme);

        bool open = !_collapsed.Contains(c.Id);
        var (first, last) = Range(track);
        bool fresh = c.Recorded.Count > 0 && !c.RecordingStale(first, last);

        // ---- card header row ----
        float btnH = ImGui.GetFrameHeight();
        var sq = new Vector2(btnH, btnH);
        if(UiWidgets.IconButton("fold", open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight,
            open ? I18n.T("constraint.tc.fold_tip") : I18n.T("constraint.tc.unfold_tip"), sq, subtle: true, brioTheme: brioTheme))
        {
            if(open) _collapsed.Add(c.Id); else _collapsed.Remove(c.Id);
        }

        ImGui.SameLine(0, 4f * s);
        var dotCol = !c.Enabled ? new Vector4(0.5f, 0.5f, 0.55f, 1f) : fresh ? XcpStyle.Success : XcpStyle.Warning;
        UiWidgets.StatusDot(dotCol, $"{index + 1}. {TrackConstraint.TypeName(c.Type)} · {TargetLabel(c)} / {BoneShortLabel(c.Bone)}",
            !c.Enabled ? I18n.T("constraint.tc.disabled_tip") : fresh ? I18n.T("constraint.tc.baked_tip") : I18n.T("constraint.tc.unbaked_tip"));

        string enLabel = c.Enabled ? I18n.T("common.enabled") : I18n.T("common.disabled");
        float enW = MathF.Max(54f * s, ImGui.CalcTextSize(enLabel).X + 18f * s);
        float rightToolsW = enW + (btnH + 3f * s) * 3f + 4f * s;
        float rightX = MathF.Max(ImGui.GetCursorPosX() + 6f * s, ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - UiWidgets.CurrentRightPad - rightToolsW);
        ImGui.SameLine();
        ImGui.SetCursorPosX(rightX);

        if(UiWidgets.TogglePill("en", enLabel, c.Enabled, I18n.T("constraint.tc.toggle_tip"), size: new Vector2(enW, btnH), brioTheme: brioTheme))
            Edit(() => c.Enabled = !c.Enabled);

        ImGui.SameLine(0, 3f * s);
        bool moveUp, moveDown;
        ImGui.BeginDisabled(index == 0);
        moveUp = UiWidgets.IconButton("up", FontAwesomeIcon.ChevronUp, I18n.T("constraint.tc.up_tip"), sq, subtle: true, brioTheme: brioTheme);
        ImGui.EndDisabled();
        if(moveUp) { Edit(() => (track.TrackConstraints[index - 1], track.TrackConstraints[index]) = (c, track.TrackConstraints[index - 1])); return false; }

        ImGui.SameLine(0, 3f * s);
        ImGui.BeginDisabled(index == track.TrackConstraints.Count - 1);
        moveDown = UiWidgets.IconButton("dn", FontAwesomeIcon.ChevronDown, I18n.T("constraint.tc.down_tip"), sq, subtle: true, brioTheme: brioTheme);
        ImGui.EndDisabled();
        if(moveDown) { Edit(() => (track.TrackConstraints[index + 1], track.TrackConstraints[index]) = (c, track.TrackConstraints[index + 1])); return false; }

        ImGui.SameLine(0, 3f * s);
        if(UiWidgets.IconButton("rm", FontAwesomeIcon.TrashAlt, I18n.T("constraint.tc.del_tip"), sq, subtle: true, brioTheme: brioTheme))
        { Edit(() => track.TrackConstraints.Remove(c)); _status(I18n.T("constraint.tc.del_status"), false); return false; }

        if(!open) return true;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- Row 1: Type + Influence + KeepFraming ----
        int type = (int)c.Type;
        ImGui.SetNextItemWidth(128f * s);
        if(ImGui.Combo($"{I18n.T("constraint.tc.type_label")}##tc_type", ref type,
            I18n.Combo("constraint.type.damped", "constraint.type.locked", "constraint.type.track_to")))
            Edit(() => c.Type = (TrackType)type);

        ImGui.SameLine();
        float influence = c.Influence;
        ImGui.SetNextItemWidth(110f * s);
        if(ImGui.SliderFloat($"{I18n.T("constraint.tc.influence_label")}##tc_inf", ref influence, 0, 1, "%.2f")) { c.Influence = influence; _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        UiWidgets.Tooltip(I18n.T("constraint.tc.influence_tip"));

        ImGui.SameLine();
        if(UiWidgets.TogglePill("keep_fov", I18n.T("constraint.tc.keep_framing"), c.KeepFraming,
            I18n.T("constraint.tc.keep_framing_tip"), brioTheme: brioTheme))
            Edit(() => c.KeepFraming = !c.KeepFraming);

        // ---- Row 2: Target Actor & Bone ----
        DrawTarget(c, s);

        // ---- Row 3: Offsets ----
        var offset = c.TargetOffset;
        ImGui.SetNextItemWidth(185f * s);
        if(ImGui.DragFloat3($"{I18n.T("constraint.tc.target_offset")}##tc_toff", ref offset, .005f)) { c.TargetOffset = offset; _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        UiWidgets.Tooltip(I18n.T("constraint.tc.target_offset_tip"));

        ImGui.SameLine();
        var screen = c.ScreenOffset * 100;
        ImGui.SetNextItemWidth(135f * s);
        if(ImGui.DragFloat2($"{I18n.T("constraint.tc.screen_offset")}##tc_soff", ref screen, .2f, -100, 100, "%.1f"))
        { c.ScreenOffset = Vector2.Clamp(screen / 100, -Vector2.One, Vector2.One); _tl.Touch(); }
        if(ImGui.IsItemActivated()) _tl.Snapshot();
        UiWidgets.Tooltip(I18n.T("constraint.tc.screen_offset_tip"));

        // ---- Row 4: Axes ----
        int trackAxis = (int)c.TrackAxis;
        ImGui.SetNextItemWidth(62f * s);
        if(ImGui.Combo($"{I18n.T("constraint.tc.track_axis")}##tc_ta", ref trackAxis, Axes)) Edit(() => c.TrackAxis = (PathAxis)trackAxis);
        UiWidgets.Tooltip(I18n.T("constraint.tc.track_axis_tip"));

        if(c.Type == TrackType.TrackTo)
        {
            ImGui.SameLine();
            int up = (int)c.UpAxis;
            ImGui.SetNextItemWidth(62f * s);
            if(ImGui.Combo($"{I18n.T("constraint.tc.up_axis")}##tc_ua", ref up, Axes)) Edit(() => c.UpAxis = (PathAxis)up);
        }
        else if(c.Type == TrackType.Locked)
        {
            ImGui.SameLine();
            int lockAxis = (int)c.LockAxis;
            ImGui.SetNextItemWidth(62f * s);
            if(ImGui.Combo($"{I18n.T("constraint.tc.lock_axis")}##tc_la", ref lockAxis, Axes)) Edit(() => c.LockAxis = (PathAxis)lockAxis);
            UiWidgets.Tooltip(I18n.T("constraint.tc.lock_axis_tip"));
        }

        // ---- Row 5: Trajectory bake status ----
        ImGui.Spacing();
        DrawRecordState(track, c, brioTheme);
        return true;
    }

    private void DrawTarget(TrackConstraint c, float s)
    {
        ImGui.SetNextItemWidth(175f * s);
        if(ImGui.BeginCombo($"{I18n.T("constraint.tc.actor_label")}##tc_act", TargetLabel(c)))
        {
            if(ImGui.Selectable($"{I18n.T("brio.actor_current_gpose")}##gpose_tgt", string.IsNullOrEmpty(c.TargetName))) Edit(() => { c.TargetName = ""; c.TargetIndex = -1; });
            foreach(var (index, name) in _bones.Characters())
                if(ImGui.Selectable($"{name}##{index}", c.TargetName == name && c.TargetIndex == index))
                    Edit(() => { c.TargetName = name; c.TargetIndex = index; });
            ImGui.EndCombo();
        }
        UiWidgets.Tooltip(I18n.T("constraint.tc.actor_tip"));

        ImGui.SameLine();
        ImGui.SetNextItemWidth(175f * s);
        if(ImGui.BeginCombo($"{I18n.T("constraint.tc.bone_label")}##tc_bone", BoneLabel(c.Bone), ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##bonefilter", I18n.T("constraint.tc.bone_search_hint"), ref _boneFilter, 64);
            var character = _bones.Resolve(c);
            var all = character != null ? _bones.BoneNames(character) : [];
            foreach(var (bone, label) in BoneSource.Common)
                if(Matches(bone, label) && (all.Count == 0 || all.Contains(bone)) && ImGui.Selectable($"{label}  {bone}##c{bone}", c.Bone == bone))
                    Edit(() => c.Bone = bone);
            if(all.Count > 0) ImGui.Separator();
            else ImGui.TextDisabled(I18n.T("constraint.tc.bone_unavailable_hint"));
            foreach(var bone in all)
                if(Matches(bone, "") && ImGui.Selectable(bone, c.Bone == bone)) Edit(() => c.Bone = bone);
            ImGui.EndCombo();
        }

        var who = _bones.Resolve(c);
        string error = I18n.T("constraint.tc.actor_missing");
        var live = who != null ? _bones.BoneWorld(who, c.Bone, out error) : null;
        ImGui.SameLine();
        if(live is { } p) ImGui.TextDisabled($"({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})");
        else ImGui.TextColored(XcpStyle.Warning, I18n.T("constraint.tc.unavailable", error));
    }

    private bool Matches(string bone, string label)
        => _boneFilter.Length == 0 || bone.Contains(_boneFilter, StringComparison.OrdinalIgnoreCase) || label.Contains(_boneFilter, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- recording

    private static (int first, int last) Range(CamTrack track)
        => track.Keys.Count == 0 ? (0, 0) : (track.Keys.Min(k => k.Frame), CamTimeline.TrackLastFrame(track));

    private void DrawRecordState(CamTrack track, TrackConstraint c, bool brioTheme)
    {
        var (first, last) = Range(track);
        if(c.Recorded.Count == 0)
            UiWidgets.StatusDot(XcpStyle.Warning, I18n.T("constraint.rec.unbaked"));
        else if(c.RecordingStale(first, last))
            UiWidgets.StatusDot(XcpStyle.Warning, c.RecordedFor != c.TargetKey
                ? I18n.T("constraint.rec.stale_target", c.Recorded.Count)
                : I18n.T("constraint.rec.stale_range", c.Recorded.Keys[0], c.Recorded.Keys[^1], first, last));
        else
            UiWidgets.StatusDot(XcpStyle.Success, I18n.T("constraint.rec.baked", c.Recorded.Count, c.Recorded.Keys[0], c.Recorded.Keys[^1]));

        ImGui.SameLine();
        ImGui.BeginDisabled(_recording());
        if(UiWidgets.IconTextButton("rec_one", FontAwesomeIcon.DotCircle, I18n.T("constraint.rec.btn_one"),
            I18n.T("constraint.rec.btn_one_tip"), brioTheme: brioTheme))
            _record([c]);
        ImGui.EndDisabled();

        if(c.Recorded.Count > 0)
        {
            ImGui.SameLine();
            if(UiWidgets.IconTextButton("clr_one", FontAwesomeIcon.Times, I18n.T("common.clear"), I18n.T("constraint.rec.clear_tip"), brioTheme: brioTheme))
                Edit(() => { c.Recorded.Clear(); c.RecordedFor = ""; });
        }
    }

    private void DrawRecordingFooter(CamTrack track, bool gpose, bool brioTheme)
    {
        ImGui.Separator();
        ImGui.Spacing();
        var (first, last) = Range(track);
        if(_recording())
        {
            UiWidgets.StatusDot(XcpStyle.Info, I18n.T("constraint.rec.recording"));
            ImGui.SameLine();
            if(UiWidgets.DangerButton("stop_rec", FontAwesomeIcon.Stop, I18n.T("constraint.rec.stop"), I18n.T("constraint.rec.stop_tip"), brioTheme: brioTheme))
                _stopRecording();
            return;
        }
        ImGui.BeginDisabled(!gpose || track.Keys.Count == 0);
        if(UiWidgets.IconTextButton("rec_all", FontAwesomeIcon.Fire, I18n.T("constraint.rec.btn_all"),
            I18n.T("constraint.rec.btn_all_tip", first, last),
            active: true, brioTheme: brioTheme))
            _record(track.TrackConstraints.Where(c => c.Enabled).ToList());
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(I18n.T("constraint.rec.footer_hint", first, last));
    }
}
