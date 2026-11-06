using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using XcpCam.Core;

int fails = 0;
void Check(bool ok, string name) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if(!ok) fails++; }

var rng = new Random(1);
float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);

// 1) Round trip: Brio free-camera style view -> relative -> Brio playback view
float worst = 0;
for(int n = 0; n < 200; n++)
{
    // free camera view per Brio VirtualCameraManager.UpdateMatrix
    float yaw = R(-3.1f, 3.1f), pitch = R(-1.4f, 1.4f), roll = R(-0.5f, 0.5f);
    var P = new Vector3(R(-50, 50), R(-5, 20), R(-50, 50));
    var look = Vector3.Normalize(new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)));
    var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, look));
    var up = Vector3.Cross(look, right);
    var m = new Matrix4x4(
        right.X, up.X, look.X, 0, right.Y, up.Y, look.Y, 0, right.Z, up.Z, look.Z, 0,
        -Vector3.Dot(P, right), -Vector3.Dot(P, up), -Vector3.Dot(P, look), 1);
    var view = Matrix4x4.Transform(m, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll));

    var basePos = new Vector3(R(-50, 50), R(-2, 5), R(-50, 50));
    var baseRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, R(-3.1f, 3.1f));

    // Game leaves junk / zeros in the 4th column -> full 4x4 inverse would fail
    if(n % 2 == 0) { view.M14 = 0; view.M24 = 0; view.M34 = 0; view.M44 = 0; }

    XcpMath.DecomposeView(view, out var wp, out var wr);
    var (pos, rot) = XcpMath.WorldToRelative(wp, wr, basePos, baseRot);
    var rebuilt = XcpMath.BrioPlaybackView(pos, rot, basePos, baseRot);
    worst = MathF.Max(worst, XcpMath.MaxAffineDiff(rebuilt, view));
    worst = MathF.Max(worst, Vector3.Distance(wp, P));
}
Check(worst < 1e-3f, $"round-trip view matrix (worst {worst:0.0e0})");

// 2) PathBuilder hits keys
var keys = new List<Keyframe>
{
    new() { TimeSec = 0, Pos = new(0, 1.5f, 3), Rot = Quaternion.CreateFromYawPitchRoll(0, 0, 0), Fov = 0.78f, Ease = Ease.Smooth },
    new() { TimeSec = 2, Pos = new(3, 1.5f, 0), Rot = Quaternion.Negate(Quaternion.CreateFromYawPitchRoll(1.5f, 0.1f, 0)), Fov = 0.6f, Ease = Ease.Linear },
    new() { TimeSec = 3.5f, Pos = new(0, 2f, -3), Rot = Quaternion.CreateFromYawPitchRoll(3.0f, 0.2f, 0), Fov = 0.9f, Ease = Ease.Out },
    new() { TimeSec = 5, Pos = new(-2, 1f, 0), Rot = Quaternion.CreateFromYawPitchRoll(4.5f, 0, 0), Fov = 0.7f },
};
var frames = PathBuilder.Build(keys);
Check(frames.Count == 151, $"frame count 5s@30fps = {frames.Count} (expect 151)");
Check(Vector3.Distance(frames[0].Pos, keys[0].Pos) < 1e-4f && Vector3.Distance(frames[^1].Pos, keys[^1].Pos) < 1e-4f, "endpoints match");
Check(Vector3.Distance(frames[60].Pos, keys[1].Pos) < 1e-3f, "passes through key 2 at t=2s");
Check(MathF.Abs(MathF.Abs(Quaternion.Dot(frames[60].Rot, keys[1].Rot)) - 1) < 1e-4f, "rotation at key 2");
float minDot = 1, maxStep = 0;
for(int i = 1; i < frames.Count; i++)
{
    minDot = MathF.Min(minDot, Quaternion.Dot(frames[i - 1].Rot, frames[i].Rot));
    maxStep = MathF.Max(maxStep, Vector3.Distance(frames[i - 1].Pos, frames[i].Pos));
}
Check(minDot > 0.9f, $"adjacent quats same hemisphere & smooth (min dot {minDot:0.0000})");
Check(maxStep < 0.3f, $"no position jumps (max step {maxStep:0.000})");
bool normOk = true; foreach(var f in frames) normOk &= MathF.Abs(f.Rot.Length() - 1) < 1e-4f;
Check(normOk, "unit quaternions");

// Two keys -> straight line
var two = PathBuilder.Build([keys[0], new Keyframe { TimeSec = 1, Pos = new(2, 1.5f, 3), Rot = keys[0].Rot, Fov = 0.78f, Ease = Ease.Linear }]);
two = PathBuilder.Build([new Keyframe { TimeSec = 0, Pos = new(0, 1.5f, 3), Rot = keys[0].Rot, Fov = 0.78f, Ease = Ease.Linear }, new Keyframe { TimeSec = 1, Pos = new(2, 1.5f, 3), Rot = keys[0].Rot, Fov = 0.78f }]);
Check(Vector3.Distance(two[15].Pos, new Vector3(1, 1.5f, 3)) < 1e-3f, "2 keys linear midpoint on straight line");

// 3) Writer + simulate Brio reader (i < count-1)
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

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
