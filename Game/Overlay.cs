using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// Draws the camera path, key cameras (frustums) and a preview playhead in the game world,
/// using the current GPose target as the base (same space Brio's XAT player uses).
/// Draw-only: never takes over the game camera.
/// </summary>
public sealed class Overlay
{
    private readonly IGameGui _gui;
    private readonly IClientState _clientState;
    private readonly ITargetManager _targets;
    private readonly Dictionary<ShotCurves, PathDisplay> _pathCache = new();
    private ShotComposer? _cachedComposer;
    private long _cachedRevision = -1;
    private PathDisplay Display(ShotCurves shot, CamTimeline timeline, ShotComposer composer)
    {
        if(!ReferenceEquals(composer, _cachedComposer) || timeline.Revision != _cachedRevision)
        { _pathCache.Clear(); _cachedComposer = composer; _cachedRevision = timeline.Revision; }
        if(!_pathCache.TryGetValue(shot, out var display)) _pathCache[shot] = display = PathDisplay.Build(shot, timeline.Fps);
        return display;
    }

    public bool Enabled = true;
    public bool ShowPath = true;
    public bool ShowFrustums = true;
    public bool ShowLabels = true;
    public bool SpeedColors = true;
    public float FrustumSize = 0.35f;
    public float Aspect = 16f / 9f;

    public int SelectedKey = -1;
    public float? PreviewTime;
    /// <summary>Screen position of every node camera drawn this frame, for click picking.</summary>
    public readonly List<(Keyframe Key, Vector2 Screen)> NodeScreens = new();

    public Overlay(IGameGui gui, IClientState clientState, ITargetManager targets)
    {
        _gui = gui; _clientState = clientState; _targets = targets;
    }

    /// <summary>
    /// Draws every track's segments (the parts that are actually output are bright, hidden parts are dimmed),
    /// a marker at every hard cut, each track's node frustums, and the playheads.
    /// </summary>
    /// <param name="previewFrame">Our playhead (timeline frame), or null.</param>
    /// <param name="externalFrame">XAT-sync playhead (timeline frame), or null.</param>
    /// <param name="selected">Node to highlight (reference compare).</param>
    public void Draw(ShotComposer? comp, CamTimeline tl, float? previewFrame, float? externalFrame, Keyframe? selected = null)
    {
        NodeScreens.Clear();
        if(!Enabled || !_clientState.IsGPosing) return;
        // a composer built for a different track list (track just added / removed) is stale for this frame
        if(comp != null && comp.TrackCount != tl.Tracks.Count) comp = null;

        var dl = ImGui.GetBackgroundDrawList();
        // node poses and curves are world coordinates
        var baseRot = Quaternion.Identity;
        static Vector3 ToWorld(Vector3 world) => world;

        // ---- paths, per track / per segment ----
        if(ShowPath && comp != null)
        {
            float maxSpeed = SpeedColors ? comp.DisplaySpeedPeak : 1;

            for(int t = comp.TrackCount - 1; t >= 0; t--) // lower tracks first, top track drawn last
            {
                if(comp.SequenceOf(t) is not { } seq) continue;
                bool trackActive = comp.IsActive(t);
                foreach(var seg in seq.Segments)
                {
                    foreach(var edge in Display(seg.Curves, tl, comp).Edges)
                    {
                        bool shown = trackActive && comp.TrackAt(edge.EndFrame) == t;
                        float duration = (edge.EndFrame - edge.StartFrame) / tl.Fps;
                        float speed = duration > 1e-6f && !edge.Dashed ? Vector3.Distance(edge.Start.Pos, edge.End.Pos) / duration : 0;
                        uint col = edge.Dashed ? tl.Tracks[t].Color : SpeedColors ? SpeedColor(speed / maxSpeed) : tl.Tracks[t].Color;
                        if(!shown) col = Dim(col);
                        if(edge.Dashed) DashedLine(dl, ToWorld(edge.Start.Pos), ToWorld(edge.End.Pos), col);
                        else Line(dl, ToWorld(edge.Start.Pos), ToWorld(edge.End.Pos), col, shown ? 2.5f : 1.2f);
                    }
                }
            }

            // a marker at every cut: outgoing pose -> incoming pose
            foreach(int cf in comp.CutFrames)
            {
                var a = ToWorld(comp.Sample(cf - 1).Pos);
                var b = ToWorld(comp.Sample(cf).Pos);
                int outputTrack = comp.TrackAt(cf);
                bool alreadyDrawn = comp.SequenceOf(outputTrack)?.Segments.Any(s => Display(s.Curves, tl, comp).Jumps.Any(j => Math.Abs(j.Frame - cf) < .001f)) == true;
                if(!alreadyDrawn) DashedLine(dl, a, b, 0xC0FFFFFF);
                if(_gui.WorldToScreen(b, out var sp) && IsSane(sp))
                {
                    dl.AddCircle(sp, 7, 0xFFFFFFFF, 12, 2f);
                    dl.AddText(sp + new Vector2(9, 2), 0xFFFFFFFF, I18n.T("overlay.cut_label", cf));
                }
            }
        }

        // ---- key cameras, per track ----
        if(ShowFrustums)
        {
            for(int t = 0; t < tl.Tracks.Count; t++)
            {
                var track = tl.Tracks[t];
                bool active = comp?.IsActive(t) ?? true;
                for(int i = 0; i < track.Keys.Count; i++)
                {
                    var k = track.Keys[i];
                    bool sel = selected != null && ReferenceEquals(k, selected) || tl.SelectedNodes.Contains(k.Id);
                    uint col = sel ? 0xFF00FFFF : active ? track.Color : Dim(track.Color);
                    var seq = comp?.SequenceOf(t);
                    var pose = seq == null ? new XcpFrame(k.Pos, k.Rot, k.Fov)
                        : seq.Constrain(seq.Segments[seq.SegmentAt(k.Frame)].Curves.NodePose(k), k.Frame);
                    DrawFrustum(dl, ToWorld(pose.Pos), Quaternion.Normalize(baseRot * pose.Rot), pose.Fov, col, sel ? 2.5f : 1.5f);
                    if(_gui.WorldToScreen(ToWorld(pose.Pos), out var eye) && IsSane(eye)) NodeScreens.Add((k, eye));
                    if(ShowLabels && _gui.WorldToScreen(ToWorld(pose.Pos), out var sp) && IsSane(sp))
                    {
                        var label = tl.Tracks.Count > 1 ? $"{track.Name} #{i + 1}  F{k.Frame}" : $"#{i + 1}  F{k.Frame}";
                        if(k.CutIn) label += " ✂";
                        dl.AddText(sp + new Vector2(8, -18), 0xFF000000, label);
                        dl.AddText(sp + new Vector2(7, -19), col, label);
                    }
                }
                if(comp?.SequenceOf(t) is { } sequence)
                {
                    var drawn = new List<XcpFrame>();
                    foreach(var segment in sequence.Segments)
                        foreach(var jump in Display(segment.Curves, tl, comp).Jumps)
                            foreach(var item in new[] { (pose: jump.Before, text: I18n.T("overlay.jump_before")), (pose: jump.After, text: I18n.T("overlay.jump_after")) })
                            {
                                var pose = item.pose;
                                bool Same(XcpFrame a) => Vector3.DistanceSquared(a.Pos, pose.Pos) < 1e-7f &&
                                    Math.Abs(Quaternion.Dot(a.Rot, pose.Rot)) > 1 - 1e-6f && Math.Abs(a.Fov - pose.Fov) < 1e-5f;
                                if(drawn.Count >= 128 || drawn.Exists(Same) || track.Keys.Exists(k => segment.Curves.SeededNodes.Contains(k.Id) && Same(segment.Curves.NodePose(k)))) continue;
                                drawn.Add(pose);
                                uint color = active && comp.TrackAt(jump.Frame) == t ? track.Color : Dim(track.Color);
                                DrawFrustum(dl, ToWorld(pose.Pos), Quaternion.Normalize(baseRot * pose.Rot), pose.Fov, color, 1.5f, true);
                                if(ShowLabels && _gui.WorldToScreen(ToWorld(pose.Pos), out var point) && IsSane(point))
                                    dl.AddText(point + new Vector2(7, 5), color, $"{item.text} F{jump.Frame:0.##}");
                            }
                }
            }
        }

        // ---- tracking targets: a marker at every target, a dashed aim line from the playhead camera ----
        if(comp != null)
        {
            float frame = previewFrame ?? tl.Playhead;
            for(int t = 0; t < comp.TrackCount; t++)
            {
                if(comp.SequenceOf(t) is not { Tracking: true } seq) continue;
                bool shown = comp.IsActive(t) && comp.TrackAt(frame) == t;
                var eye = shown ? comp.Sample(frame).Pos : (Vector3?)null;
                foreach(var c in seq.Constraints)
                {
                    if(seq.Targets!.Position(c, frame) is not { } bone) continue;
                    var target = bone + c.TargetOffset;
                    uint col = shown ? 0xFF40C0FF : Dim(0xFF40C0FF);
                    if(_gui.WorldToScreen(target, out var sp) && IsSane(sp))
                    {
                        dl.AddCircle(sp, 7, col, 16, 2f);
                        dl.AddLine(sp - new Vector2(10, 0), sp + new Vector2(10, 0), col, 1.5f);
                        dl.AddLine(sp - new Vector2(0, 10), sp + new Vector2(0, 10), col, 1.5f);
                        if(ShowLabels) dl.AddText(sp + new Vector2(9, 4), col, $"{TrackConstraint.TypeName(c.Type)} · {c.Bone}");
                    }
                    if(eye is { } e) DashedLine(dl, e, target, col);
                }
            }
        }

        // ---- playheads ----
        if(comp != null)
        {
            if(previewFrame is float pf)
            {
                var s = comp.Sample(pf);
                DrawFrustum(dl, ToWorld(s.Pos), Quaternion.Normalize(baseRot * s.Rot), s.Fov, 0xFF55FF55, 3f);
            }
            if(externalFrame is float ef)
            {
                var s = comp.Sample(ef);
                DrawFrustum(dl, ToWorld(s.Pos), Quaternion.Normalize(baseRot * s.Rot), s.Fov, 0xFFFF55FF, 2f);
            }
        }
    }

    private static uint Dim(uint abgr) => (abgr & 0x00FFFFFF) | 0x50000000;

    private void DashedLine(ImDrawListPtr dl, Vector3 a, Vector3 b, uint col)
    {
        if(Vector3.DistanceSquared(a, b) < 1e-10f) return;
        const int n = 12;
        for(int i = 0; i < n; i += 2)
            Line(dl, Vector3.Lerp(a, b, i / (float)n), Vector3.Lerp(a, b, (i + 1) / (float)n), col, 1.5f);
    }

    private void DrawFrustum(ImDrawListPtr dl, Vector3 eye, Quaternion worldRot, float fov, uint col, float thick, bool dashed = false)
    {
        // The view basis uses +Z as backward; draw the visible frustum along local -Z.
        float d = FrustumSize;
        float hy = MathF.Tan(MathF.Max(0.05f, fov) / 2f) * d;
        float hx = hy * Aspect;
        Vector3 L(float x, float y, float z) => FrustumGeometry.Point(eye, worldRot, x, y, z);
        var c0 = L(-hx, hy, d); var c1 = L(hx, hy, d); var c2 = L(hx, -hy, d); var c3 = L(-hx, -hy, d);
        void Edge(Vector3 a, Vector3 b) { if(dashed) DashedLine(dl, a, b, col); else Line(dl, a, b, col, thick); }
        Edge(eye, c0); Edge(eye, c1); Edge(eye, c2); Edge(eye, c3);
        Edge(c0, c1); Edge(c1, c2); Edge(c2, c3); Edge(c3, c0);
        // "up" notch so roll/orientation is visible
        Edge(c0, L(0, hy * 1.4f, d)); Edge(c1, L(0, hy * 1.4f, d));
        Dot(dl, eye, col, 3.5f);
    }

    private void Line(ImDrawListPtr dl, Vector3 a, Vector3 b, uint col, float thick)
    {
        bool okA = _gui.WorldToScreen(a, out var sa, out bool inA);
        bool okB = _gui.WorldToScreen(b, out var sb, out bool inB);
        // WorldToScreen returns false for points behind the camera; skip those segments instead of drawing garbage
        if(!okA && !inA || !okB && !inB) return;
        if(!IsSane(sa) || !IsSane(sb)) return;
        dl.AddLine(sa, sb, col, thick);
    }

    private void Dot(ImDrawListPtr dl, Vector3 p, uint col, float r)
    {
        if(_gui.WorldToScreen(p, out var s) && IsSane(s)) dl.AddCircleFilled(s, r, col);
    }

    private static bool IsSane(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && MathF.Abs(v.X) < 20000 && MathF.Abs(v.Y) < 20000;

    // ---------------- helpers for the curve tool (world space, background draw list) ----------------

    public bool ToScreen(Vector3 world, out Vector2 screen) => _gui.WorldToScreen(world, out screen) && IsSane(screen);

    public void DrawLine(Vector3 a, Vector3 b, uint col, float thick = 1.5f, bool dashed = false)
    {
        var dl = ImGui.GetBackgroundDrawList();
        if(dashed) DashedLine(dl, a, b, col); else Line(dl, a, b, col, thick);
    }

    public void DrawPolyline(IReadOnlyList<Vector3> points, uint col, float thick)
    {
        var dl = ImGui.GetBackgroundDrawList();
        for(int i = 0; i + 1 < points.Count; i++) Line(dl, points[i], points[i + 1], col, thick);
    }

    /// <summary>Square control point / round handle markers like Blender's edit mode.</summary>
    public void DrawMarker(Vector3 world, uint col, bool square, bool selected)
    {
        if(!ToScreen(world, out var s)) return;
        var dl = ImGui.GetBackgroundDrawList();
        float r = selected ? 5 : 4;
        if(square) { dl.AddRectFilled(s - new Vector2(r), s + new Vector2(r), col); dl.AddRect(s - new Vector2(r + 1), s + new Vector2(r + 1), 0xFF000000); }
        else { dl.AddCircleFilled(s, r, col); dl.AddCircle(s, r + 1, 0xFF000000); }
    }

    public void DrawLabel(Vector3 world, string text, uint col)
    {
        if(!ToScreen(world, out var s)) return;
        var dl = ImGui.GetBackgroundDrawList();
        dl.AddText(s + new Vector2(8, -18), 0xFF000000, text);
        dl.AddText(s + new Vector2(7, -19), col, text);
    }

    /// <summary>Blue (slow) -> green -> red (fast), ABGR.</summary>
    private static uint SpeedColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float r, g, b;
        if(t < 0.5f) { float u = t / 0.5f; r = 0.2f; g = 0.4f + 0.6f * u; b = 1f - 0.8f * u; }
        else { float u = (t - 0.5f) / 0.5f; r = 0.2f + 0.8f * u; g = 1f - 0.7f * u; b = 0.2f; }
        return 0xFF000000u | ((uint)(b * 255) << 16) | ((uint)(g * 255) << 8) | (uint)(r * 255);
    }
}
