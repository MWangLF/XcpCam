using System;
using System.IO;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class XcpRoundTripTests
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("xcpcam-roundtrip-");
        try
        {
            string plain = Path.Combine(directory.FullName, "camera.xcp");
            // Blender XAT layout: count includes the last authored sample, without a padding record.
            using(var writer = new BinaryWriter(File.Create(plain)))
            {
                writer.Write("XCP1"u8);
                writer.Write(121u);
                for(int i = 0; i <= 120; i++)
                {
                    float t = i / 30f;
                    var q = Quaternion.CreateFromYawPitchRoll(t, .17f * MathF.Sin(t), .02f * t);
                    foreach(float x in new[] { MathF.Sin(t), .125f * t, MathF.Cos(t), q.X, q.Y, q.Z, q.W, .75f + .03f * t }) writer.Write(x);
                }
            }
            var samples = XcpFile.Read(plain).Frames;
            var fit = XcpMotionImport.Fit(samples);
            string enhanced = Path.Combine(directory.FullName, "editable.xcp");
            var motion = ShotComposer.Create(fit.Timeline, new PathSettings())!;
            XcpFile.Write(enhanced, motion.BakeXcp(30), ProjectCodec.Encode(fit.Timeline), Vector3.Zero, Quaternion.Identity);
            var saved = XcpFile.Read(enhanced);
            var target = new CamTimeline();
            target.ReplaceProject(ProjectCodec.Decode(saved.Project!));
            var restored = ShotComposer.Create(target, new PathSettings())!;
            double worstPosition = 0, worstAngle = 0, worstFov = 0;
            for(int i = 0; i < samples.Count; i++)
            {
                var s = samples[i]; var r = restored.Sample(i);
                worstPosition = Math.Max(worstPosition, Vector3.Distance(s.Pos, r.Pos));
                var d = Quaternion.Normalize(Quaternion.Conjugate(s.Rot) * r.Rot);
                worstAngle = Math.Max(worstAngle, 2 * Math.Atan2(new Vector3(d.X, d.Y, d.Z).Length(), Math.Abs(d.W)) * 180 / Math.PI);
                worstFov = Math.Max(worstFov, Math.Abs(s.Fov - r.Fov) * 180 / Math.PI);
            }
            check(samples.Count == saved.Frames.Count && target.AllKeys.Count() == fit.NodeCount && fit.NodeCount > 2 && fit.CurveKeyCount > 0,
                $"XCP plain → fit → enhanced → editable: {samples.Count} samples, {target.AllKeys.Count()} camera nodes, {fit.CurveKeyCount} intermediate channel keys");
            check(worstPosition <= .001 && worstAngle <= .05 && worstFov <= .01,
                $"XCP restored playback max errors: {worstPosition:G5} m, {worstAngle:G5} deg, {worstFov:G5} deg FoV");
            target.Undo(); check(!target.AllKeys.Any(), "Undo XCP timeline replacement");
            target.Redo(); check(target.AllKeys.Count() == fit.NodeCount, "Redo XCP timeline replacement");

            var constraint = new TrackConstraint { TargetName = "Actor", Bone = "j_kao" };
            target.ActiveTrack.TrackConstraints.Add(constraint);
            var bone = new Vector3(-8, 1, -8);
            var liveSource = new FuncTargetSource((c, _) => c.TargetName == "Actor" && c.Bone == "j_kao" ? bone : null);
            var baked = ShotComposer.Create(target, new PathSettings(), liveSource)!;
            XcpFile.Write(enhanced, baked.BakeXcp(30), ProjectCodec.Encode(target), Vector3.Zero, Quaternion.Identity);
            var restoredTracking = ShotComposer.Create(ProjectCodec.Decode(XcpFile.Read(enhanced).Project!), new PathSettings(), liveSource)!;
            var first = restoredTracking.Sample(30);
            bone.X += 16;
            var second = restoredTracking.Sample(30);
            check(first.Pos == second.Pos && MathF.Abs(Quaternion.Dot(first.Rot, second.Rot)) < .99f &&
                Vector3.Dot(Vector3.Transform(-Vector3.UnitZ, second.Rot), Vector3.Normalize(bone - second.Pos)) > .9999f,
                "Restored enhanced XCP tracks the live bone after it moves at the same timeline frame");
        }
        finally { directory.Delete(recursive: true); }
    }
}
