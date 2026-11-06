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

    /// <param name="selected">Node to highlight (reference compare).</param>
    public void Draw(CameraPath? path, IReadOnlyList<Keyframe> keys, float? externalPlayhead, Keyframe? selected = null)
    {
        if(!Enabled || !_clientState.IsGPosing) return;
        if(!CameraCapture.TryGetBase(_targets, out var basePos, out var baseRot, out _, out _)) return;
        if(selected != null) SelectedKey = -1;

        var dl = ImGui.GetBackgroundDrawList();
        Vector3 ToWorld(Vector3 rel) => Vector3.Transform(rel, baseRot) + basePos;

        // ---- path polyline ----
        if(ShowPath && path != null)
        {
            const float fps = 30f;
            int n = Math.Max(2, (int)(path.Duration * fps) + 1);
            var pts = new Vector3[n];
            for(int i = 0; i < n; i++) pts[i] = ToWorld(path.Sample(MathF.Min(i / fps, path.Duration)).Pos);

            float maxSpeed = 1e-3f;
            var speeds = new float[n];
            for(int i = 1; i < n; i++) { speeds[i] = Vector3.Distance(pts[i - 1], pts[i]) * fps; maxSpeed = MathF.Max(maxSpeed, speeds[i]); }

            for(int i = 1; i < n; i++)
            {
                uint col = SpeedColors ? SpeedColor(speeds[i] / maxSpeed) : 0xFF33DDFF;
                Line(dl, pts[i - 1], pts[i], col, 2.5f);
            }

            // tick marks every second
            for(int sec = 1; sec < path.Duration; sec++)
                Dot(dl, ToWorld(path.Sample(sec).Pos), 0xFFFFFFFF, 2.5f);
        }

        // ---- key cameras ----
        if(ShowFrustums)
        {
            for(int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                bool sel = i == SelectedKey || (selected != null && ReferenceEquals(k, selected));
                uint col = sel ? 0xFF00FFFF : 0xFFFFAA33;
                DrawFrustum(dl, ToWorld(k.Pos), Quaternion.Normalize(baseRot * k.Rot), k.Fov, col, sel ? 2.5f : 1.5f);
                if(ShowLabels && _gui.WorldToScreen(ToWorld(k.Pos), out var sp))
                {
                    var label = $"#{i + 1}  F{k.Frame}";
                    dl.AddText(sp + new Vector2(8, -18), 0xFF000000, label);
                    dl.AddText(sp + new Vector2(7, -19), col, label);
                }
            }
        }

        // ---- playheads ----
        if(path != null)
        {
            if(PreviewTime is float pt)
            {
                var s = path.Sample(pt);
                DrawFrustum(dl, ToWorld(s.Pos), Quaternion.Normalize(baseRot * s.Rot), s.Fov, 0xFF55FF55, 3f);
            }
            if(externalPlayhead is float et)
            {
                var s = path.Sample(et);
                DrawFrustum(dl, ToWorld(s.Pos), Quaternion.Normalize(baseRot * s.Rot), s.Fov, 0xFFFF55FF, 2f);
            }
        }
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
