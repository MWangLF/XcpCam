using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// Samples user keyframes into a 30fps frame list for Brio's XAT player.
/// Position: centripetal Catmull-Rom (2 keys degenerate to a straight line).
/// Rotation: per-segment slerp with hemisphere alignment. FoV: Catmull-Rom (uniform), clamped.
/// Per-segment easing remaps the segment parameter.
/// </summary>
public static class PathBuilder
{
    public const float Fps = 30f;

    public static List<XcpFrame> Build(IReadOnlyList<Keyframe> input)
    {
        var keys = input.OrderBy(k => k.TimeSec).ToList();
        var result = new List<XcpFrame>();
        if(keys.Count == 0)
            return result;

        // Align quaternion hemispheres between consecutive keys
        var rots = new Quaternion[keys.Count];
        rots[0] = Quaternion.Normalize(keys[0].Rot);
        for(int i = 1; i < keys.Count; i++)
        {
            var q = Quaternion.Normalize(keys[i].Rot);
            if(Quaternion.Dot(rots[i - 1], q) < 0) q = Quaternion.Negate(q);
            rots[i] = q;
        }

        if(keys.Count == 1)
        {
            result.Add(new XcpFrame(keys[0].Pos, rots[0], keys[0].Fov));
            return result;
        }

        float t0 = keys[0].TimeSec;
        float duration = keys[^1].TimeSec - t0;
        int frameCount = Math.Max(2, (int)MathF.Round(duration * Fps) + 1);

        float minFov = keys.Min(k => k.Fov), maxFov = keys.Max(k => k.Fov);

        Quaternion prev = rots[0];
        int seg = 0;
        for(int f = 0; f < frameCount; f++)
        {
            float time = t0 + f / Fps;
            if(f == frameCount - 1) time = keys[^1].TimeSec;

            while(seg < keys.Count - 2 && time > keys[seg + 1].TimeSec)
                seg++;

            var a = keys[seg];
            var b = keys[seg + 1];
            float len = b.TimeSec - a.TimeSec;
            float u = len <= 1e-6f ? 1f : Math.Clamp((time - a.TimeSec) / len, 0f, 1f);
            float e = ApplyEase(u, a.Ease);

            var p0 = keys[Math.Max(seg - 1, 0)].Pos;
            var p1 = a.Pos;
            var p2 = b.Pos;
            var p3 = keys[Math.Min(seg + 2, keys.Count - 1)].Pos;
            var pos = CentripetalCatmullRom(p0, p1, p2, p3, e);

            var rot = Quaternion.Normalize(Quaternion.Slerp(rots[seg], rots[seg + 1], e));
            if(Quaternion.Dot(prev, rot) < 0) rot = Quaternion.Negate(rot);
            prev = rot;

            float f0 = keys[Math.Max(seg - 1, 0)].Fov;
            float f3 = keys[Math.Min(seg + 2, keys.Count - 1)].Fov;
            float fov = Math.Clamp(CatmullRom1(f0, a.Fov, b.Fov, f3, e), minFov, maxFov);

            result.Add(new XcpFrame(pos, rot, fov));
        }

        return result;
    }

    public static float ApplyEase(float t, Ease ease) => ease switch
    {
        Ease.Linear => t,
        Ease.In => t * t,
        Ease.Out => 1f - (1f - t) * (1f - t),
        _ => t * t * (3f - 2f * t),
    };

    static float CatmullRom1(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// <summary>Centripetal (alpha=0.5) Catmull-Rom between p1 and p2 (Barry-Goldman). Duplicated endpoints are handled.</summary>
    static Vector3 CentripetalCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        const float eps = 1e-4f;
        float d12 = Vector3.Distance(p1, p2);
        if(d12 < eps)
            return Vector3.Lerp(p1, p2, t);

        // Mirror duplicated end points so the curve stays well defined
        if(Vector3.Distance(p0, p1) < eps) p0 = p1 - (p2 - p1);
        if(Vector3.Distance(p3, p2) < eps) p3 = p2 + (p2 - p1);

        float k0 = 0f;
        float k1 = k0 + MathF.Sqrt(Vector3.Distance(p0, p1));
        float k2 = k1 + MathF.Sqrt(d12);
        float k3 = k2 + MathF.Sqrt(Vector3.Distance(p2, p3));
        float s = k1 + (k2 - k1) * t;

        var a1 = (k1 - s) / (k1 - k0) * p0 + (s - k0) / (k1 - k0) * p1;
        var a2 = (k2 - s) / (k2 - k1) * p1 + (s - k1) / (k2 - k1) * p2;
        var a3 = (k3 - s) / (k3 - k2) * p2 + (s - k2) / (k3 - k2) * p3;
        var b1 = (k2 - s) / (k2 - k0) * a1 + (s - k0) / (k2 - k0) * a2;
        var b2 = (k3 - s) / (k3 - k1) * a2 + (s - k1) / (k3 - k1) * a3;
        return (k2 - s) / (k2 - k1) * b1 + (s - k1) / (k2 - k1) * b2;
    }
}
