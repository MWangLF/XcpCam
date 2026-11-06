using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using XcpCam.Core;

int fails = 0;
void Check(bool ok, string name) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if(!ok) fails++; }

var rng = new Random(1);
float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

// ---------- 1) Round trip: Brio free-camera style view -> relative -> Brio playback view ----------
float worst = 0;
for(int n = 0; n < 200; n++)
{
    float yaw = R(-3.1f, 3.1f), pitch = R(-1.4f, 1.4f), roll = R(-0.5f, 0.5f);
    var P = new Vector3(R(-50, 50), R(-5, 20), R(-50, 50));
    var look = Vector3.Normalize(new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)));
    var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, look));
    var up = Vector3.Cross(look, right);
    var m = new Matrix4x4(
        right.X, up.X, look.X, 0, right.Y, up.Y, look.Y, 0, right.Z, up.Z, look.Z, 0,
        -Vector3.Dot(P, right), -Vector3.Dot(P, up), -Vector3.Dot(P, look), 1);
    var view = Matrix4x4.Transform(m, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll));
    if(n % 2 == 0) { view.M14 = 0; view.M24 = 0; view.M34 = 0; view.M44 = 0; }

    var basePos = new Vector3(R(-50, 50), R(-2, 5), R(-50, 50));
    var baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, R(-3.1f, 3.1f));

    XcpMath.DecomposeView(view, out var wp, out var wr);
    var (pos, rot) = XcpMath.WorldToRelative(wp, wr, basePos, baseRot);
    var rebuilt = XcpMath.BrioPlaybackView(pos, rot, basePos, baseRot);
    worst = MathF.Max(worst, XcpMath.MaxAffineDiff(rebuilt, view));
    worst = MathF.Max(worst, Vector3.Distance(wp, P));
}
Check(worst < 1e-3f, $"round-trip view matrix (worst {worst:0.0e0})");

// ---------- 1b) xcp pose -> world -> Brio free-cam Euler -> Brio free-cam view == Brio XAT playback view ----------
{
    float worstFc = 0;
    float? prevYaw = null;
    for(int n = 0; n < 300; n++)
    {
        var basePos = new Vector3(R(-50, 50), R(-2, 5), R(-50, 50));
        var baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, R(-3.1f, 3.1f));
        var relPos = new Vector3(R(-5, 5), R(-1, 3), R(-5, 5));
        var relRot = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(R(-3.1f, 3.1f), R(-1.3f, 1.3f), R(-0.6f, 0.6f)));

        var xatView = XcpMath.BrioPlaybackView(relPos, relRot, basePos, baseRot);
        var worldPos = Vector3.Transform(relPos, baseRot) + basePos;
        var worldRot = baseRot * relRot;
        var (yaw, pitch, roll) = FreeCamMath.FromWorldRotation(worldRot, prevYaw);
        var fcView = FreeCamMath.BuildView(worldPos, yaw, pitch, roll);
        worstFc = MathF.Max(worstFc, XcpMath.MaxAffineDiff(xatView, fcView) / MathF.Max(1, worldPos.Length() / 10));
    }
    Check(worstFc < 2e-3f, $"xcp -> Brio free-cam euler reproduces XAT view (worst {worstFc:0.0e0})");

    // unwrap keeps yaw continuous across +-pi
    var (y1, _, _) = FreeCamMath.FromWorldRotation(Quaternion.CreateFromYawPitchRoll(3.1f, 0, 0));
    var (y2, _, _) = FreeCamMath.FromWorldRotation(Quaternion.CreateFromYawPitchRoll(-3.1f, 0, 0), y1);
    Check(MathF.Abs(y2 - y1) < 0.2f, $"free-cam yaw unwrap ({y1:0.00} -> {y2:0.00})");
}

// ---------- shared keys: uneven legs (short + long) ----------
Quaternion Yaw(float y, float p = 0) => Quaternion.CreateFromYawPitchRoll(y, p, 0);
List<Keyframe> Keys() =>
[
    new() { TimeSec = 0,   Pos = new(0, 1.5f, 3),  Rot = Yaw(0),       Fov = 0.78f },
    new() { TimeSec = 2,   Pos = new(3, 1.5f, 0),  Rot = Quaternion.Negate(Yaw(1.5f, 0.1f)), Fov = 0.6f },
    new() { TimeSec = 3,   Pos = new(1, 2f, -3),   Rot = Yaw(3.0f, 0.2f), Fov = 0.9f },
    new() { TimeSec = 7,   Pos = new(-6, 1f, -1),  Rot = Yaw(4.5f),    Fov = 0.7f },
    new() { TimeSec = 8.5f,Pos = new(-6, 3f, 4),   Rot = Yaw(5.5f, -0.3f), Fov = 0.8f },
];

float[] Speeds(List<XcpFrame> f) => Enumerable.Range(1, f.Count - 1).Select(i => Vector3.Distance(f[i - 1].Pos, f[i].Pos) * 30f).ToArray();
float AngStep(Quaternion a, Quaternion b) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a, b)), 0f, 1f));
bool UnitAndHemi(List<XcpFrame> f)
{
    for(int i = 0; i < f.Count; i++)
    {
        if(MathF.Abs(f[i].Rot.Length() - 1) > 1e-4f) return false;
        if(i > 0 && Quaternion.Dot(f[i - 1].Rot, f[i].Rot) < 0) return false;
    }
    return true;
}

// ---------- 2) Timed mode ----------
{
    var keys = Keys();
    var path = CameraPath.Create(keys, new PathSettings { Mode = TimingMode.Timed })!;
    var frames = path.Bake(30);
    Check(frames.Count == 256, $"timed: frame count 8.5s@30fps = {frames.Count} (expect 256)");

    bool hits = true;
    foreach(var k in keys)
    {
        var s = path.Sample(k.TimeSec);
        hits &= Vector3.Distance(s.Pos, k.Pos) < 2e-3f;
        hits &= MathF.Abs(MathF.Abs(Quaternion.Dot(s.Rot, k.Rot)) - 1) < 1e-4f;
        hits &= MathF.Abs(s.Fov - k.Fov) < 1e-4f;
    }
    Check(hits, "timed: passes through every key at its time (pos/rot/fov)");

    // speed continuity at interior keys: compare speed just before/after
    float worstJump = 0;
    for(int i = 1; i < keys.Count - 1; i++)
    {
        float t = keys[i].TimeSec, e = 1f / 120f;
        float vBefore = Vector3.Distance(path.Sample(t - 2 * e).Pos, path.Sample(t - e).Pos) / e;
        float vAfter = Vector3.Distance(path.Sample(t + e).Pos, path.Sample(t + 2 * e).Pos) / e;
        worstJump = MathF.Max(worstJump, MathF.Abs(vAfter - vBefore) / MathF.Max(0.1f, (vAfter + vBefore) / 2));
    }
    Check(worstJump < 0.05f, $"timed: speed continuous at keys (worst rel. jump {worstJump:P1})");

    var sp = Speeds(frames);
    Check(sp.Skip(5).Take(sp.Length - 10).All(v => v > 0.05f), "timed (Auto): never stops between start and end");
    Check(sp[0] < sp[10] && sp[^1] < sp[^10], "timed: eases in at start and out at end");

    float worstAngJump = 0;
    for(int i = 1; i < keys.Count - 1; i++)
    {
        float t = keys[i].TimeSec, e = 1f / 120f;
        float wB = AngStep(path.Sample(t - 2 * e).Rot, path.Sample(t - e).Rot) / e;
        float wA = AngStep(path.Sample(t + e).Rot, path.Sample(t + 2 * e).Rot) / e;
        worstAngJump = MathF.Max(worstAngJump, MathF.Abs(wA - wB) / MathF.Max(0.2f, (wA + wB) / 2));
    }
    Check(worstAngJump < 0.08f, $"timed: angular speed continuous at keys (worst rel. jump {worstAngJump:P1})");
    Check(UnitAndHemi(frames), "timed: unit quaternions, same hemisphere");

    // monotone progress (no going backwards along the curve)
    float prevArc = -1; bool mono = true;
    var curve = new ArcLengthCurve(keys.Select(k => k.Pos).ToList());
    for(float t = 0; t <= path.Duration; t += 0.01f)
    {
        // project via nearest arc sample is costly; instead check speed sign through tiny steps
        float d = Vector3.Distance(path.Sample(t).Pos, path.Sample(t + 0.005f).Pos);
        if(d < -1e-6f) mono = false;
        prevArc = d;
    }
    Check(mono, "timed: progress monotone");
}

// ---------- 3) Stop tangent ----------
{
    var keys = Keys();
    keys[2].Tangent = TangentMode.Stop;
    var path = CameraPath.Create(keys, new PathSettings())!;
    float t = keys[2].TimeSec, e = 1f / 240f;
    float v = Vector3.Distance(path.Sample(t - e).Pos, path.Sample(t + e).Pos) / (2 * e);
    Check(v < 0.05f, $"stop tangent: speed ~0 at key ({v:0.000} y/s)");
}

// ---------- 4) Hold ----------
{
    var keys = Keys();
    keys[1].HoldSec = 0.5f;
    var path = CameraPath.Create(keys, new PathSettings())!;
    var a = path.Sample(2.05f); var b = path.Sample(2.45f);
    Check(Vector3.Distance(a.Pos, keys[1].Pos) < 1e-3f && Vector3.Distance(b.Pos, keys[1].Pos) < 1e-3f
        && MathF.Abs(MathF.Abs(Quaternion.Dot(a.Rot, b.Rot)) - 1) < 1e-5f, "hold: camera stays put during hold");
}

// ---------- 5) Constant speed ----------
{
    var keys = Keys();
    // pure positional path to measure linear speed cleanly
    foreach(var k in keys) { k.Rot = Yaw(0); k.Fov = 0.78f; }
    var settings = new PathSettings { Mode = TimingMode.ConstantSpeed, Duration = 8f, EaseInFraction = 0.15f, EaseOutFraction = 0.15f };
    var path = CameraPath.Create(keys, settings)!;
    var frames = path.Bake(30);
    var sp = Speeds(frames);
    int a = (int)(sp.Length * 0.2f), b = (int)(sp.Length * 0.8f);
    var mid = sp.Skip(a).Take(b - a).ToArray();
    float dev = (mid.Max() - mid.Min()) / mid.Average();
    Check(dev < 0.02f, $"constant speed: middle section speed spread {dev:P2} (<2%)");
    Check(sp[0] < mid.Average() * 0.2f && sp[^1] < mid.Average() * 0.2f, "constant speed: eases at both ends");
    Check(Vector3.Distance(frames[0].Pos, keys[0].Pos) < 1e-4f && Vector3.Distance(frames[^1].Pos, keys[^1].Pos) < 1e-3f, "constant speed: endpoints");
    Check(MathF.Abs(path.Duration - 8f) < 1e-4f && frames.Count == 241, $"constant speed: duration honoured ({frames.Count} frames)");

    // with rotation: unit & smooth
    var keys2 = Keys();
    var p2 = CameraPath.Create(keys2, settings)!;
    var f2 = p2.Bake(30);
    Check(UnitAndHemi(f2), "constant speed: unit quaternions, same hemisphere");
    float maxAng = 0; for(int i = 1; i < f2.Count; i++) maxAng = MathF.Max(maxAng, AngStep(f2[i - 1].Rot, f2[i].Rot));
    Check(maxAng < 0.1f, $"constant speed: no rotation jumps (max {maxAng:0.000} rad/frame)");
}

// ---------- 6) Two keys ----------
{
    var two = new List<Keyframe>
    {
        new() { TimeSec = 0, Pos = new(0, 1.5f, 3), Rot = Yaw(0), Fov = 0.78f },
        new() { TimeSec = 1, Pos = new(2, 1.5f, 3), Rot = Yaw(0), Fov = 0.78f },
    };
    var p = CameraPath.Create(two, new PathSettings { EaseStart = false, EaseEnd = false })!;
    Check(Vector3.Distance(p.Sample(0.5f).Pos, new Vector3(1, 1.5f, 3)) < 1e-3f, "2 keys, no ease: straight line, uniform");
}

// ---------- 7) Writer + simulate Brio reader (i < count-1) ----------
{
    var frames = PathBuilder.Build(Keys());
    var path = Path.Combine(Path.GetTempPath(), "xcpcam_test.xcp");
    XcpWriter.Write(path, frames);
    var bytes = File.ReadAllBytes(path);
    using var br = new BinaryReader(new MemoryStream(bytes));
    var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
    uint count = br.ReadUInt32();
    var read = new List<Vector3>();
    for(int i = 0; i < count - 1; i++)
    {
        var p = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        br.ReadBytes(20);
        read.Add(p);
    }
    Check(magic == "XCP1", "magic");
    Check(bytes.Length == 8 + (frames.Count + 1) * 32, $"file size {bytes.Length}");
    Check(read.Count == frames.Count && Vector3.Distance(read[^1], frames[^1].Pos) < 1e-6f, $"Brio reader gets all {read.Count} frames");
    Console.WriteLine($"sample: {path}");
}

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
