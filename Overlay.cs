using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
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

    public bool Enabled = true;
    public bool ShowPath = true;
    public bool ShowFrustums = true;
    public bool ShowLabels = true;
    public bool SpeedColors = true;
    public float FrustumSize = 0.35f;
    public float Aspect = 16f / 9f;

    public int SelectedKey = -1;
    public float? PreviewTime;

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
        if(!Enabled || !_clientState.IsGPosing) return;
        if(!CameraCapture.TryGetBase(_targets, out var basePos, out var baseRot, out _, out _)) return;
        // a composer built for a different track list (track just added / removed) is stale for this frame
        if(comp != null && comp.TrackCount != tl.Tracks.Count) comp = null;

        var dl = ImGui.GetBackgroundDrawList();
        Vector3 ToWorld(Vector3 rel) => Vector3.Transform(rel, baseRot) + basePos;

        // ---- paths, per track / per segment ----
        if(ShowPath && comp != null)
        {
            float step = MathF.Max(1f, tl.Fps / 30f); // ~30 samples per second
            float maxSpeed = 1e-3f;
            for(int t = 0; t < comp.TrackCount; t++)
                if(comp.SequenceOf(t) is { } seq)
                    for(float f = seq.Start; f < seq.End; f += step)
                        if(seq.SpeedAt(f) is float v) maxSpeed = MathF.Max(maxSpeed, v);

            for(int t = comp.TrackCount - 1; t >= 0; t--) // lower tracks first, top track drawn last
            {
                if(comp.SequenceOf(t) is not { } seq) continue;
                bool trackActive = comp.IsActive(t);
                foreach(var seg in seq.Segments)
                {
                    if(seg.Path == null) continue;
                    Vector3? prev = null;
                    for(float f = seg.Start; ; f += step)
                    {
                        bool last = f >= seg.End;
                        if(last) f = seg.End;
                        var p = ToWorld(seg.SampleAt(f).Pos);
                        if(prev is Vector3 pv)
                        {
                            bool shown = trackActive && comp.TrackAt(f - step * 0.5f) == t;
                            float speed = Vector3.Distance(pv, p) / (step / tl.Fps);
                            uint col = SpeedColors ? SpeedColor(speed / maxSpeed) : tl.Tracks[t].Color;
                            if(!shown) col = Dim(col);
                            Line(dl, pv, p, col, shown ? 2.5f : 1.2f);
                        }
                        prev = p;
                        if(last) break;
                    }
                }
            }

            // a marker at every cut: outgoing pose -> incoming pose
            foreach(int cf in comp.CutFrames)
            {
                var a = ToWorld(comp.Sample(cf - 1).Pos);
                var b = ToWorld(comp.Sample(cf).Pos);
                DashedLine(dl, a, b, 0xC0FFFFFF);
                if(_gui.WorldToScreen(b, out var sp) && IsSane(sp))
                {
                    dl.AddCircle(sp, 7, 0xFFFFFFFF, 12, 2f);
                    dl.AddText(sp + new Vector2(9, 2), 0xFFFFFFFF, $"切 F{cf}");
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
                    bool sel = selected != null && ReferenceEquals(k, selected);
                    uint col = sel ? 0xFF00FFFF : active ? track.Color : Dim(track.Color);
                    DrawFrustum(dl, ToWorld(k.Pos), Quaternion.Normalize(baseRot * k.Rot), k.Fov, col, sel ? 2.5f : 1.5f);
                    if(ShowLabels && _gui.WorldToScreen(ToWorld(k.Pos), out var sp) && IsSane(sp))
                    {
                        var label = tl.Tracks.Count > 1 ? $"{track.Name} #{i + 1}  F{k.Frame}" : $"#{i + 1}  F{k.Frame}";
                        if(k.CutIn) label += " ✂";
                        dl.AddText(sp + new Vector2(8, -18), 0xFF000000, label);
                        dl.AddText(sp + new Vector2(7, -19), col, label);
                    }
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
        const int n = 12;
        for(int i = 0; i < n; i += 2)
            Line(dl, Vector3.Lerp(a, b, i / (float)n), Vector3.Lerp(a, b, (i + 1) / (float)n), col, 1.5f);
    }

    private void DrawFrustum(ImDrawListPtr dl, Vector3 eye, Quaternion worldRot, float fov, uint col, float thick)
    {
        // camera looks along local +Z (matches XcpMath / Brio view convention), up = +Y
        float d = FrustumSize;
        float hy = MathF.Tan(MathF.Max(0.05f, fov) / 2f) * d;
        float hx = hy * Aspect;
        Vector3 L(float x, float y, float z) => eye + Vector3.Transform(new Vector3(x, y, z), worldRot);
        var c0 = L(-hx, hy, d); var c1 = L(hx, hy, d); var c2 = L(hx, -hy, d); var c3 = L(-hx, -hy, d);
        Line(dl, eye, c0, col, thick); Line(dl, eye, c1, col, thick); Line(dl, eye, c2, col, thick); Line(dl, eye, c3, col, thick);
        Line(dl, c0, c1, col, thick); Line(dl, c1, c2, col, thick); Line(dl, c2, c3, col, thick); Line(dl, c3, c0, col, thick);
        // "up" notch so roll/orientation is visible
        Line(dl, c0, L(0, hy * 1.4f, d), col, thick); Line(dl, c1, L(0, hy * 1.4f, d), col, thick);
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
