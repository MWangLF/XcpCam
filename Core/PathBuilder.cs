using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>
/// A continuous camera path built from user keys. Geometry (where the camera goes) is separated from timing
/// (how far along it is at time t), which removes the per-key stutter of per-segment easing.
///  - Shape: centripetal Catmull-Rom through the key positions, arc-length parameterised.
///  - Timed mode: one global monotone Hermite time->distance curve through (t_i, s_i), speed continuous at keys.
///  - Constant-speed mode: uniform progress along a combined distance+rotation "pace" coordinate with C2 ease ramps.
///  - Rotation: non-uniform SQUAD; FoV: Hermite on the same knots.
/// Techniques after psenxiv/vista (arc-length table + PCHIP timing) and ReplayMod (time-knotted splines).
/// </summary>
public sealed class CameraPath
{
    public const float DefaultFps = 30f;

    private readonly ArcLengthCurve _curve;
    private readonly float[] _legArc;     // arc length of each leg
    private readonly float[] _keyArc;     // arc length at each virtual key
    private readonly float[] _keyPace;    // pace coordinate at each key (constant mode)
    private readonly HermiteCurve? _timeToArc;  // timed mode
    private readonly SquadCurve _rot;
    private readonly HermiteCurve _fov;
    private readonly float _fovMin, _fovMax;
    private readonly bool _constant;
    private readonly PathSettings _settings;
    private readonly float _t0;

    /// <summary>Total duration in seconds.</summary>
    public float Duration { get; }
    /// <summary>Times (seconds from path start) of the virtual keys, for display.</summary>
    public IReadOnlyList<float> KeyTimes { get; }
    public float TotalLength => _curve.TotalLength;

    private CameraPath(IReadOnlyList<Keyframe> keys, PathSettings settings)
    {
        _settings = settings.Clone();
        _constant = settings.Mode == TimingMode.ConstantSpeed;

        // Expand holds into duplicated keys (timed mode only)
        var vk = new List<(float t, Vector3 p, Quaternion q, float f, TangentMode tan, bool holdEnd)>();
        for(int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            vk.Add((k.TimeSec, k.Pos, k.Rot, k.Fov, k.Tangent, false));
            if(!_constant && k.HoldSec > 0)
            {
                float next = i + 1 < keys.Count ? keys[i + 1].TimeSec : float.PositiveInfinity;
                float hold = MathF.Min(k.HoldSec, MathF.Max(0, next - k.TimeSec - 0.05f));
                if(hold > 1e-3f)
                    vk.Add((k.TimeSec + hold, k.Pos, k.Rot, k.Fov, k.Tangent, true));
            }
        }

        int n = vk.Count;
        _curve = new ArcLengthCurve(vk.Select(v => v.p).ToList());
        _keyArc = new float[n];
        _legArc = new float[Math.Max(0, n - 1)];
        for(int i = 0; i < n; i++) _keyArc[i] = _curve.LengthAtKey(i);
        for(int i = 0; i < n - 1; i++) _legArc[i] = _keyArc[i + 1] - _keyArc[i];

        // Align rotation hemispheres up front (also used for the pace angle)
        var rots = new Quaternion[n];
        rots[0] = Quaternion.Normalize(vk[0].q);
        for(int i = 1; i < n; i++)
        {
            var q = Quaternion.Normalize(vk[i].q);
            if(Quaternion.Dot(rots[i - 1], q) < 0) q = Quaternion.Negate(q);
            rots[i] = q;
        }

        var fovs = vk.Select(v => v.f).ToArray();
        _fovMin = fovs.Min(); _fovMax = fovs.Max();

        // Hold segments: virtual key i+1 is the end of a hold that starts at i
        var holdSeg = new bool[Math.Max(0, n - 1)];
        for(int i = 0; i < n - 1; i++) holdSeg[i] = vk[i + 1].holdEnd;

        _keyPace = new float[n];
        float[] knots;

        if(_constant)
        {
            for(int i = 1; i < n; i++)
            {
                float ang = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(rots[i - 1], rots[i])), 0f, 1f));
                float fovD = MathF.Abs(fovs[i] - fovs[i - 1]);
                _keyPace[i] = _keyPace[i - 1] + _legArc[i - 1] + settings.RotationWeight * (ang + fovD);
            }
            Duration = MathF.Max(0.1f, settings.Duration);
            _t0 = 0;
            knots = _keyPace;

            var times = new float[n];
            float total = _keyPace[^1];
            for(int i = 0; i < n; i++)
                times[i] = total > 1e-6f ? InverseProgress(_keyPace[i] / total) * Duration : Duration * i / Math.Max(1, n - 1);
            KeyTimes = times;

            _rot = new SquadCurve(knots, rots, new bool[n], null, false, false);
            _fov = new HermiteCurve(knots, fovs, monotone: false);
        }
        else
        {
            _t0 = vk[0].t;
            var t = vk.Select(v => v.t - _t0).ToArray();
            // enforce strictly increasing times
            for(int i = 1; i < n; i++) if(t[i] <= t[i - 1]) t[i] = t[i - 1] + 1e-3f;
            Duration = MathF.Max(t[^1], 1e-3f);
            KeyTimes = t;
            knots = t;

            var stop = new bool[n];
            var linear = new bool[n];
            for(int i = 0; i < n; i++)
            {
                stop[i] = vk[i].tan == TangentMode.Stop;
                linear[i] = vk[i].tan == TangentMode.Linear && !vk[i].holdEnd && !(i + 1 < n && vk[i + 1].holdEnd);
            }
            // keys adjacent to a hold come to rest
            for(int i = 0; i < n - 1; i++) if(holdSeg[i]) { stop[i] = true; stop[i + 1] = true; }

            _timeToArc = new HermiteCurve(t, _keyArc, stop, linear, settings.EaseStart, settings.EaseEnd, monotone: true);
            _rot = new SquadCurve(t, rots, stop, holdSeg, settings.EaseStart, settings.EaseEnd);
            _fov = new HermiteCurve(t, fovs, stop, null, settings.EaseStart, settings.EaseEnd, monotone: false);
        }
    }

    /// <summary>Builds a path, or returns null when fewer than two keys.</summary>
    public static CameraPath? Create(IReadOnlyList<Keyframe> keys, PathSettings settings)
    {
        if(keys.Count < 2) return null;
        var sorted = settings.Mode == TimingMode.Timed ? keys.OrderBy(k => k.TimeSec).ToList() : keys.ToList();
        return new CameraPath(sorted, settings);
    }

    /// <summary>Sample at <paramref name="time"/> seconds from the path start.</summary>
    public XcpFrame Sample(float time)
    {
        time = Math.Clamp(time, 0f, Duration);
        if(_constant)
        {
            float total = _keyPace[^1];
            float pace = total * ConstantSpeedProfile.Progress(time / Duration, _settings.EaseInFraction, _settings.EaseOutFraction);
            float arc = PaceToArc(pace);
            var rot = _rot.Evaluate(pace);
            float fov = Math.Clamp(_fov.Evaluate(pace), _fovMin, _fovMax);
            return new XcpFrame(_curve.PositionAt(arc), rot, fov);
        }
        else
        {
            float arc = _timeToArc!.Evaluate(time);
            var rot = _rot.Evaluate(time);
            float fov = Math.Clamp(_fov.Evaluate(time), _fovMin, _fovMax);
            return new XcpFrame(_curve.PositionAt(arc), rot, fov);
        }
    }

    /// <summary>Uniformly sampled frames covering the whole path (inclusive of both ends).</summary>
    public List<XcpFrame> Bake(float fps = DefaultFps)
    {
        int count = Math.Max(2, (int)MathF.Round(Duration * fps) + 1);
        var list = new List<XcpFrame>(count);
        Quaternion prev = default;
        for(int f = 0; f < count; f++)
        {
            float time = f == count - 1 ? Duration : f / fps;
            var s = Sample(time);
            var q = s.Rot;
            if(f > 0 && Quaternion.Dot(prev, q) < 0) q = Quaternion.Negate(q);
            prev = q;
            list.Add(s with { Rot = q });
        }
        return list;
    }

    private float PaceToArc(float pace)
    {
        int n = _keyPace.Length;
        if(n == 1) return 0;
        int i = 0;
        while(i < n - 2 && pace > _keyPace[i + 1]) i++;
        float span = _keyPace[i + 1] - _keyPace[i];
        float f = span > 1e-6f ? Math.Clamp((pace - _keyPace[i]) / span, 0f, 1f) : 1f;
        return _keyArc[i] + f * _legArc[i];
    }

    private float InverseProgress(float target)
    {
        float lo = 0, hi = 1;
        for(int i = 0; i < 40; i++)
        {
            float mid = (lo + hi) / 2;
            if(ConstantSpeedProfile.Progress(mid, _settings.EaseInFraction, _settings.EaseOutFraction) < target) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
}

public static class PathBuilder
{
    public const float Fps = CameraPath.DefaultFps;

    /// <summary>Backwards-compatible helper: timed mode with default settings.</summary>
    public static List<XcpFrame> Build(IReadOnlyList<Keyframe> keys) => Build(keys, new PathSettings());

    public static List<XcpFrame> Build(IReadOnlyList<Keyframe> keys, PathSettings settings)
    {
        if(keys.Count == 0) return [];
        if(keys.Count == 1) return [new XcpFrame(keys[0].Pos, Quaternion.Normalize(keys[0].Rot), keys[0].Fov)];
        return CameraPath.Create(keys, settings)!.Bake(Fps);
    }
}
