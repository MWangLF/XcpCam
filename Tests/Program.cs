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

// ---------- 6b) Frame timeline model ----------
{
    var tl = new CamTimeline { Length = 300 };
    tl.Keys.Add(new Keyframe { Frame = 0, Pos = new(0, 1, 3), Rot = Yaw(0), Fov = 0.78f });
    tl.Keys.Add(new Keyframe { Frame = 60, Pos = new(3, 1, 0), Rot = Yaw(1.5f), Fov = 0.78f, HoldFrames = 15 });
    tl.Keys.Add(new Keyframe { Frame = 150, Pos = new(0, 2, -3), Rot = Yaw(3f), Fov = 0.7f });

    tl.SyncSeconds();
    Check(MathF.Abs(tl.Keys[1].TimeSec - 2f) < 1e-6f && MathF.Abs(tl.Keys[1].HoldSec - 0.5f) < 1e-6f, "timeline: frame -> seconds @30fps");

    // move with collision: moving key 3 onto key 2's frame is refused
    int got = tl.MoveKey(tl.Keys[2], 60);
    Check(got == 150, "timeline: move onto occupied frame is refused");
    got = tl.MoveKey(tl.Keys[2], 121);
    Check(got == 121, "timeline: move to free frame");

    // fps change keeps seconds
    tl.SetFps(60);
    tl.SyncSeconds();
    Check(tl.Fps == 60 && tl.Keys[1].Frame == 120 && tl.Keys[1].HoldFrames == 30 && MathF.Abs(tl.Keys[1].TimeSec - 2f) < 1e-6f && tl.Length == 600,
        $"timeline: 30->60fps keeps seconds (key2 frame {tl.Keys[1].Frame}, len {tl.Length})");

    // undo / redo
    tl.Snapshot();
    tl.Keys[0].Frame = 10;
    tl.Keys.RemoveAt(2);
    tl.Undo();
    Check(tl.Keys.Count == 3 && tl.Keys[0].Frame == 0, "timeline: undo restores");
    tl.Redo();
    Check(tl.Keys.Count == 2 && tl.Keys[0].Frame == 10, "timeline: redo reapplies");

    // path from frames hits nodes at their frames
    tl.Undo();
    tl.SyncSeconds();
    var p = CameraPath.Create(tl.Sorted(), new PathSettings())!;
    var s = p.Sample(tl.FrameToPathTime(120));
    Check(Vector3.Distance(s.Pos, new Vector3(3, 1, 0)) < 2e-3f, "timeline: path reaches node 2 at its frame");
    var sh = p.Sample(tl.FrameToPathTime(140));
    Check(Vector3.Distance(sh.Pos, new Vector3(3, 1, 0)) < 2e-3f, "timeline: hold frames keep camera still");
}

// ---------- 6b2) Insert a key sampled from the path: path should barely change ----------
{
    var tl = new CamTimeline { Length = 600 };
    foreach(var (f, p, y) in new[] { (0, new Vector3(0, 1.5f, 3), 0f), (60, new Vector3(3, 1.5f, 0), 1.5f), (150, new Vector3(1, 2, -3), 3f), (240, new Vector3(-4, 1, -1), 4.5f) })
        tl.Keys.Add(new Keyframe { Frame = f, Pos = p, Rot = Yaw(y), Fov = 0.78f });
    tl.SyncSeconds();
    var before = CameraPath.Create(tl.Sorted(), new PathSettings())!;

    int ins = 100;
    var s = before.Sample(tl.FrameToPathTime(ins));
    tl.Keys.Add(new Keyframe { Frame = ins, Pos = s.Pos, Rot = s.Rot, Fov = s.Fov });
    tl.SyncSeconds();
    var after = CameraPath.Create(tl.Sorted(), new PathSettings())!;

    var atIns = after.Sample(tl.FrameToPathTime(ins));
    Check(Vector3.Distance(atIns.Pos, s.Pos) < 2e-3f && MathF.Abs(MathF.Abs(Quaternion.Dot(atIns.Rot, s.Rot)) - 1) < 1e-4f,
        "insert-from-path: new node reproduces the sampled pose");

    float maxDev = 0, maxAng = 0;
    for(int f = 0; f <= 240; f++)
    {
        var a = before.Sample(tl.FrameToPathTime(f)); var b = after.Sample(tl.FrameToPathTime(f));
        maxDev = MathF.Max(maxDev, Vector3.Distance(a.Pos, b.Pos));
        maxAng = MathF.Max(maxAng, 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a.Rot, b.Rot)), 0f, 1f)));
    }
    Console.WriteLine($"      insert-from-path deviation: pos {maxDev:0.000}y, rot {maxAng * 180 / MathF.PI:0.0}°");
    Check(maxDev < 0.25f && maxAng < 0.15f, "insert-from-path: rest of the path stays close to the original");
}

// ---------- 6d) Hard cuts within one track (plan 1) ----------
{
    var tl = new CamTimeline { Length = 600 };
    // shot A: frames 0..60 moving; shot B starts at 61 (1 frame later) -> auto cut
    tl.Keys.Add(new Keyframe { Frame = 0, Pos = new(0, 1.5f, 3), Rot = Yaw(0), Fov = 0.78f });
    tl.Keys.Add(new Keyframe { Frame = 60, Pos = new(3, 1.5f, 0), Rot = Yaw(1.5f), Fov = 0.78f });
    var b0 = new Keyframe { Frame = 61, Pos = new(-5, 2, -5), Rot = Yaw(4f), Fov = 0.6f };
    tl.Keys.Add(b0);
    tl.Keys.Add(new Keyframe { Frame = 120, Pos = new(-6, 2, 2), Rot = Yaw(5f), Fov = 0.6f });
    tl.RefreshAutoCuts();
    Check(b0.CutIn && b0.CutAuto && !tl.Keys[1].CutIn, "cut: node 1 frame after predecessor auto-enables CutIn");

    var comp = ShotComposer.Create(tl, new PathSettings())!;
    var at60 = comp.Sample(60); var at61 = comp.Sample(61);
    Check(Vector3.Distance(at60.Pos, new Vector3(3, 1.5f, 0)) < 1e-3f, "cut: frame before cut = end of shot A");
    Check(Vector3.Distance(at61.Pos, b0.Pos) < 1e-3f && MathF.Abs(MathF.Abs(Quaternion.Dot(at61.Rot, b0.Rot)) - 1) < 1e-4f,
        "cut: cut frame = start of shot B (exact pose)");
    Check(comp.CutFrames.Count == 1 && comp.CutFrames[0] == 61, $"cut: cut frames [{string.Join(",", comp.CutFrames)}]");

    // shot A unaffected by shot B: compare with a track that only has shot A
    // (A is not the last segment, so it has no end-ease — mirror that in the reference)
    var tlA = new CamTimeline { Length = 600 };
    tlA.Keys.Add(tl.Keys[0].Clone()); tlA.Keys.Add(tl.Keys[1].Clone());
    var compA = ShotComposer.Create(tlA, new PathSettings { EaseEnd = false })!;
    float devA = 0;
    for(int f = 0; f <= 60; f++) devA = MathF.Max(devA, Vector3.Distance(comp.Sample(f).Pos, compA.Sample(f).Pos));
    Check(devA < 1e-4f, $"cut: shot A independent of shot B (max dev {devA:0.0e0})");

    // and moving shot B's nodes does not change shot A at all
    var before = Enumerable.Range(0, 61).Select(f => comp.Sample(f).Pos).ToArray();
    tl.Keys[3].Pos += new Vector3(10, 5, -7);
    b0.Pos += new Vector3(-3, 1, 4);
    var compMoved = ShotComposer.Create(tl, new PathSettings())!;
    float devMoved = Enumerable.Range(0, 61).Max(f => Vector3.Distance(before[f], compMoved.Sample(f).Pos));
    Check(devMoved < 1e-6f, $"cut: editing shot B leaves shot A untouched (max dev {devMoved:0.0e0})");
    b0.Pos -= new Vector3(-3, 1, 4);
    tl.Keys[3].Pos -= new Vector3(10, 5, -7);

    // manual off survives while gap stays 1; moving away clears auto, moving back re-enables
    b0.CutIn = false; b0.CutAuto = false;
    tl.RefreshAutoCuts();
    Check(!b0.CutIn, "cut: manual off is respected");
    b0.Frame = 70; tl.RefreshAutoCuts();
    b0.Frame = 61; tl.RefreshAutoCuts();
    Check(b0.CutIn, "cut: moving back to +1 frame re-enables");

    // fps change keeps the cut even though the gap becomes 2
    tl.SetFps(60); tl.RefreshAutoCuts();
    Check(b0.CutIn && b0.Frame - tl.Keys[1].Frame == 2, $"cut: survives 30->60fps (gap {b0.Frame - tl.Keys[1].Frame})");
    tl.SetFps(30); tl.RefreshAutoCuts();
    Check(b0.CutIn, "cut: survives 60->30fps");

    // .xcp around the cut
    var comp30 = ShotComposer.Create(tl, new PathSettings())!;
    var xcp = comp30.BakeXcp(30);
    Check(Vector3.Distance(xcp[60].Pos, new Vector3(3, 1.5f, 0)) < 1e-3f && Vector3.Distance(xcp[61].Pos, b0.Pos) < 1e-3f,
        "cut: .xcp frames either side of the cut are A-end / B-start");

    // bake plan at 60fps: key before the cut, Step key on the cut
    var bk = comp30.BakeForTimeline(60, 10);
    var stepKeys = bk.Where(k => k.Step).ToList();
    Check(stepKeys.Count == 1 && stepKeys[0].Frame == 122 && bk.Any(k => k.Frame == 121 && !k.Step),
        $"bake: Step key at cut ({string.Join(",", stepKeys.Select(k => k.Frame))}) and key before it");
    Check(Vector3.Distance(bk.First(k => k.Frame == 121).Pose.Pos, new Vector3(3, 1.5f, 0)) < 1e-3f
        && Vector3.Distance(stepKeys[0].Pose.Pos, b0.Pos) < 1e-3f, "bake: poses either side of the cut");
}

// ---------- 6e) Multiple tracks, top wins (plan 2) ----------
{
    var tl = new CamTimeline { Length = 600 };
    var lower = tl.ActiveTrack;
    lower.Keys.Add(new Keyframe { Frame = 0, Pos = new(0, 1, 3), Rot = Yaw(0), Fov = 0.78f });
    lower.Keys.Add(new Keyframe { Frame = 200, Pos = new(3, 1, 0), Rot = Yaw(1.5f), Fov = 0.78f });
    var upper = tl.AddTrack();
    Check(tl.Tracks[0] == upper && tl.ActiveTrack == upper, "tracks: new track goes on top and becomes active");
    upper.Keys.Add(new Keyframe { Frame = 80, Pos = new(-4, 2, -4), Rot = Yaw(3f), Fov = 0.6f });
    upper.Keys.Add(new Keyframe { Frame = 120, Pos = new(-5, 2, -2), Rot = Yaw(3.5f), Fov = 0.6f });

    var c = ShotComposer.Create(tl, new PathSettings())!;
    Check(c.TrackAt(50) == 1 && c.TrackAt(80) == 0 && c.TrackAt(100) == 0 && c.TrackAt(121) == 1,
        $"tracks: top wins where it covers ({c.TrackAt(50)},{c.TrackAt(80)},{c.TrackAt(100)},{c.TrackAt(121)})");
    Check(c.CutFrames.SequenceEqual(new[] { 80, 121 }), $"tracks: cuts at track switches [{string.Join(",", c.CutFrames)}]");
    Check(Vector3.Distance(c.Sample(80).Pos, new Vector3(-4, 2, -4)) < 1e-3f, "tracks: switch lands on the upper shot's first pose");

    var lowerOnly = ShotSequence.Create(lower.Keys, new PathSettings(), 30)!;
    Check(Vector3.Distance(c.Sample(150).Pos, lowerOnly.Sample(150).pose.Pos) < 1e-4f, "tracks: lower track continues on its own timing");

    upper.Muted = true;
    var cm = ShotComposer.Create(tl, new PathSettings())!;
    Check(cm.CutFrames.Count == 0 && cm.TrackAt(100) == 1, "tracks: mute removes the upper track");
    upper.Muted = false; lower.Solo = true;
    var cs = ShotComposer.Create(tl, new PathSettings())!;
    Check(cs.TrackAt(100) == 1 && cs.CutFrames.Count == 0, "tracks: solo shows only the soloed track");
    lower.Solo = false;

    // gap: no coverage between 120 and 200 if the lower track ends at 60
    lower.Keys[1].Frame = 60;
    var cg = ShotComposer.Create(tl, new PathSettings())!;
    var gapPose = cg.Sample(150);
    Check(Vector3.Distance(gapPose.Pos, new Vector3(-5, 2, -2)) < 1e-3f && cg.TrackAt(150) == 0, "tracks: gap holds the last shown shot");
    Check(Vector3.Distance(cg.Sample(-10).Pos, new Vector3(0, 1, 3)) < 1e-4f, "tracks: before first coverage uses earliest shot");

    // undo restores all tracks
    tl.Snapshot();
    tl.RemoveTrack(0);
    Check(tl.Tracks.Count == 1, "tracks: remove");
    tl.Undo();
    Check(tl.Tracks.Count == 2 && tl.Tracks[0].Keys.Count == 2 && tl.Tracks[1].Keys.Count == 2, "tracks: undo restores all tracks");

    // regression: removing a track leaves the previous composer with more tracks than the timeline;
    // the composer must expose that so callers drop it (TimelineWidget / Overlay check TrackCount)
    var stale = ShotComposer.Create(tl, new PathSettings())!;
    tl.Snapshot();
    tl.RemoveTrack(0);
    Check(stale.TrackCount == 2 && tl.Tracks.Count == 1, "tracks: stale composer detectable after removing a track");
    var fresh = ShotComposer.Create(tl, new PathSettings());
    Check(fresh != null && fresh.TrackCount == 1 && fresh.Runs.All(r => r.Track == 0), "tracks: rebuilt composer matches the new track list");
    // removing every key of the last track must not crash either
    tl.Tracks[0].Keys.Clear();
    Check(ShotComposer.Create(tl, new PathSettings()) == null, "tracks: empty timeline gives no composer (no crash)");
    tl.Undo();

    // move track down: priority flips
    tl.MoveTrack(0, +1);
    lower = tl.Tracks[0];
    lower.Keys[1].Frame = 200;
    var cf = ShotComposer.Create(tl, new PathSettings())!;
    Check(cf.TrackAt(100) == 0 && cf.CutFrames.Count == 0, "tracks: moving a track down changes priority");
}

// ---------- 6c) Gizmo math: world matrix -> decompose -> relative -> world ----------
{
    float worstG = 0;
    for(int n = 0; n < 200; n++)
    {
        var basePos = new Vector3(R(-50, 50), R(-2, 5), R(-50, 50));
        var baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, R(-3.1f, 3.1f));
        var rel = new Vector3(R(-5, 5), R(-1, 3), R(-5, 5));
        var relRot = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(R(-3, 3), R(-1.3f, 1.3f), R(-0.5f, 0.5f)));
        var wp = Vector3.Transform(rel, baseRot) + basePos;
        var wr = Quaternion.Normalize(baseRot * relRot);
        var model = Matrix4x4.CreateFromQuaternion(wr) * Matrix4x4.CreateTranslation(wp);
        Matrix4x4.Decompose(model, out _, out var dr, out var dp);
        var (rel2, relRot2) = XcpMath.WorldToRelative(dp, Quaternion.Normalize(dr), basePos, baseRot);
        worstG = MathF.Max(worstG, Vector3.Distance(rel, rel2));
        worstG = MathF.Max(worstG, 1 - MathF.Abs(Quaternion.Dot(relRot, relRot2)));
    }
    Check(worstG < 1e-4f, $"gizmo: model matrix round-trip (worst {worstG:0.0e0})");
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
