using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class XcpMotionImportTests
{
    private static void Check(bool ok, string message)
    { if(!ok) throw new InvalidOperationException(message); }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch(T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    public static void Run()
    {
        const float rad = MathF.PI / 180;
        var frames = new XcpFrame[121];
        for(int i = 0; i < frames.Length; i++)
        {
            int t = i < 30 ? 0 : i < 54 ? i - 30 : i < 75 ? 24 : i - 51;
            var p = new Vector3(t * .1f, MathF.Sin(t * .1f), t == 24 ? 8 : t * -.02f);
            var q = Quaternion.CreateFromYawPitchRoll((178 + t*3)*rad, (60+t)*rad, t*.3f*rad);
            frames[i] = new(p, i % 2 == 0 ? q : -q, (45 + t*.4f)*rad);
        }
        var fit = XcpMotionImport.Fit(frames);
        Check(fit.NodeCount > 2 && fit.NodeCount < frames.Length && fit.CurveKeyCount > 0 && fit.Timeline.LastFrame == 120, "Complex paths use editable intermediate cameras, but not one camera per frame");
        Verify(fit, frames);
        var fittedShot = fit.Timeline.ActiveTrack.Shots.Single();
        Check(fit.Timeline.Keys.Any(k => k.Frame is >= 53 and <= 75 && k.Pos.Z == 8) &&
            fit.Timeline.Keys.Skip(1).SkipLast(1).Any(k => fittedShot.PathHandles(k) is { } handles && handles.right.LengthSquared() > 0),
            "The reconstructed route contains a real node at the bend and editable path handles");
        var leg = fittedShot.Intervals.First(i => i.End - i.Start > 3 &&
            fittedShot.PathProgress.Keys.Any(k => k.NodeId == null && k.Time > i.Start && k.Time < i.End));
        var ending = fit.Timeline.Keys.Single(k => k.Id == leg.EndNodeId);
        ending.CutIn = true;
        var cutPlayback = ShotComposer.Create(fit.Timeline, new())!;
        Check(cutPlayback.Sample(leg.End - .5f) == cutPlayback.Sample(leg.Start), "Toggling a reconstructed leg to hard cut holds its outgoing pose");
        ending.CutIn = false;
        Verify(fit, frames);
        VerifyLongMotion();
        fit.Timeline.AddSampledNode(fit.Timeline.ActiveTrack.Shots.Single(), 43);
        Verify(fit, frames);
        fit.Timeline.Undo(); Verify(fit, frames);
        VerifyEditingAndFps(Enumerable.Range(0, 120).Select(i => new XcpFrame(new Vector3(i * .1f, .003f * MathF.Sin(i * .2f), 0), Quaternion.Identity, .7f)).ToArray());

        var rapid = new[] { frames[0], frames[^1] };
        var smooth = XcpMotionImport.Fit(rapid);
        smooth.Timeline.RefreshAutoCuts();
        Check(!smooth.Timeline.Keys[1].CutIn && Vector3.Distance(ShotComposer.Create(smooth.Timeline, new())!.Sample(.5f).Pos, rapid[0].Pos) > .5, "Adjacent motion stays continuous");
        var cut = XcpMotionImport.Fit(frames, cutFrames: new HashSet<int> { 53,54 });
        var composer = ShotComposer.Create(cut.Timeline, new())!;
        Check(composer.CutFrames.SequenceEqual(new[] { 53,54 }), "Only confirmed cuts become hard cuts");
        Check(Vector3.Distance(composer.Sample(53.75f).Pos, frames[53].Pos) < .001, "Hold outgoing position until cut");
        Verify(cut, frames);

        // F63 / 30f * 30f used to land just before the cut.
        var jump = Enumerable.Range(0, 80).Select(i => i < 63 ? rapid[0] : rapid[1]).ToArray();
        var jumpFit = XcpMotionImport.Fit(jump, cutFrames: new HashSet<int>{63});
        var baked = ShotComposer.Create(jumpFit.Timeline, new())!.BakeXcp();
        Check(baked.Count == jump.Length && Vector3.Distance(baked[62].Pos, rapid[0].Pos) < .001 &&
            Vector3.Distance(baked[63].Pos, rapid[1].Pos) < .001, "XCP export samples the incoming pose exactly at F63");
        var single = XcpMotionImport.Fit([frames[0]]);
        Verify(single, [frames[0]]);
        var one = ShotComposer.Create(single.Timeline, new())!.BakeXcp();
        Check(one.Count == 2 && one[0] == one[1], "Single pose uses the existing two-identical-frame export convention");
        var stationary = Enumerable.Repeat(frames[0], 10001).ToArray();
        var still = XcpMotionImport.Fit(stationary);
        Check(still.NodeCount == 2 && still.CurveKeyCount == 0, "Long stationary recording remains compact");
        var spin = Enumerable.Range(0, 241).Select(i => new XcpFrame(Vector3.One,
            Quaternion.CreateFromYawPitchRoll(i*3*rad, .2f, -.1f), (.6f + .1f*MathF.Sin(i*.1f)))).ToArray();
        Verify(XcpMotionImport.Fit(spin), spin);
        Throws<ArgumentOutOfRangeException>(() => XcpMotionImport.Fit([]));
        Throws<ArgumentOutOfRangeException>(() => XcpMotionImport.Fit(new XcpFrame[XcpMotionImport.MaxFrames+1]));
        Throws<ArgumentOutOfRangeException>(() => XcpMotionImport.Fit(rapid, cutFrames: new HashSet<int>{0}));
        Throws<ArgumentException>(() => XcpMotionImport.Fit([frames[0] with { Rot = default }]));
        Throws<ArgumentException>(() => XcpMotionImport.Fit(rapid, angleTolerance: double.NaN));
        Throws<InvalidOperationException>(() => XcpMotionImport.Fit([frames[0] with { Fov = 179*rad }]));
        Throws<OperationCanceledException>(() => XcpMotionImport.Fit(frames, cancellationToken: new CancellationToken(true)));
    }

    private static void VerifyLongMotion()
    {
        var frames = Enumerable.Range(0, 12001).Select(i =>
        {
            float t = i / 12000f;
            return new XcpFrame(new(15 * MathF.Sin(t * 10), 5 * MathF.Sin(t * 20), 15 * MathF.Cos(t * 10)),
                Quaternion.CreateFromYawPitchRoll(t * 10, t * .15f, 0), .7f);
        }).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var fit = XcpMotionImport.Fit(frames);
        long importMs = timer.ElapsedMilliseconds;
        var composer = ShotComposer.Create(fit.Timeline, new())!;
        var display = PathDisplay.Build(fit.Timeline.ActiveTrack.Shots.Single(), composer.Fps);
        Check(fit.NodeCount > 2 && display.Edges.Count <= 1536 + fit.NodeCount, "Long path display has a bounded sample count");
        float peak = composer.DisplaySpeedPeak;
        timer.Restart();
        for(int i = 0; i < 100; i++)
            foreach(var edge in display.Edges)
            {
                Check(composer.TrackAt(edge.EndFrame) == 0 && composer.DisplaySpeedPeak == peak, "Output ownership and cached peak remain stable");
                _ = composer.DisplaySpeedAt(0, edge.StartFrame);
            }
        Console.WriteLine($"XCP long motion: {frames.Length} frames, {fit.NodeCount} cameras, {display.Edges.Count} display edges, fit {importMs} ms, 100 display lookup passes {timer.ElapsedMilliseconds} ms");
    }

    private static void VerifyEditingAndFps(XcpFrame[] frames)
    {
        var timeline = XcpMotionImport.Fit(frames).Timeline;
        var original = ShotComposer.Create(timeline, new())!;
        var probes = Enumerable.Range(0, 239).Select(i => original.Sample(i * .5f)).ToArray();
        var offset = timeline.ActiveTrack.Shots.Single().Offset.First(c => c.Keys.Count > 2);
        var key = offset.Keys[1];
        Check(key.Interpolation == CurveInterpolation.Bezier && key.Left.X < 0 && key.Right.X > 0, "Imported intermediate keys have usable handles");
        timeline.Snapshot();
        var before = offset.Evaluate((key.Time + offset.Keys[2].Time) * .5);
        offset.SetHandle(key, true, offset.Handle(key, true) + Vector2.UnitY);
        Check(Math.Abs(before - offset.Evaluate((key.Time + offset.Keys[2].Time) * .5)) > .01, "Dragging an imported handle changes the curve");
        timeline.Undo();
        timeline.SetFps(60, snapshot: true);
        Check(timeline.Length == 240 && timeline.LastFrame == 238 && timeline.Keys.Count == 2, "120 source samples occupy 240 timeline frames at 60 fps without adding nodes");
        var doubled = ShotComposer.Create(timeline, new())!;
        for(int i = 0; i < probes.Length; i++)
        {
            var p = doubled.Sample(i); var expected = probes[i];
            Check(Vector3.Distance(p.Pos, expected.Pos) < .00002f && MathF.Abs(Quaternion.Dot(p.Rot, expected.Rot)) > .999999f &&
                MathF.Abs(p.Fov - expected.Fov) < .000001f, "60 fps preserves the source and intervening poses");
        }
        timeline.Undo();
        Check(timeline.Fps == 30 && timeline.Length == 120, "Undo restores the imported frame rate and duration");
        var initial = ShotComposer.Create(ProjectCodec.Decode(ProjectCodec.Encode(timeline)), new())!;
        var oldKeys = timeline.ActiveTrack.Shots.Single().Offset.Select(c => c.Keys.Count).ToArray();
        timeline.SetFps(24, snapshot: true);
        var restored = ProjectCodec.Decode(ProjectCodec.Encode(timeline));
        var shot = restored.ActiveTrack.Shots.Single();
        Check(restored.Length == 96 && oldKeys.SequenceEqual(shot.Offset.Select(c => c.Keys.Count)) &&
            shot.Offset.SelectMany(c => c.Keys).Any(k => k.Subframe.HasValue), "Noninteger conversion and project reload preserve fractional offset keys");
        var reduced = ShotComposer.Create(restored, new())!;
        for(int i = 0; i <= reduced.LastFrame; i++)
            Check(Vector3.Distance(reduced.Sample(i).Pos, initial.Sample(i * 119f / reduced.LastFrame).Pos) < .00007f, "Offset timing stays aligned with camera anchors after rounding the endpoints");

        var lastNode = restored.Keys.Last();
        int lastFrame = restored.LastFrame;
        var positionBeforeMove = reduced.Sample(lastFrame * .375f).Pos;
        Check(restored.MoveKey(lastNode, lastFrame - 4) == lastFrame - 4, "Imported camera node can be retimed");
        var retimed = ShotComposer.Create(restored, new())!;
        Check(oldKeys.SequenceEqual(restored.ActiveTrack.Shots.Single().Offset.Select(c => c.Keys.Count)) &&
            Vector3.Distance(retimed.Sample((lastFrame - 4) * .375f).Pos, positionBeforeMove) < .00002f,
            "Moving the anchor retimes its intermediate keys without losing the motion");

        var cuts = XcpMotionImport.Fit(frames, cutFrames: new HashSet<int> { 53, 54 }).Timeline;
        var saved = ProjectCodec.Encode(cuts);
        Throws<InvalidOperationException>(() => cuts.SetFps(10, snapshot: true));
        Check(!cuts.CanUndo && saved.SequenceEqual(ProjectCodec.Encode(cuts)), "Frame rate collision leaves history and project untouched");
        cuts.SetFps(60, snapshot: true);
        Check(ShotComposer.Create(cuts, new())!.CutFrames.SequenceEqual(new[] { 106, 108 }), "Frame rate conversion preserves confirmed cuts");
    }

    private static void Verify(XcpMotionFit fit, XcpFrame[] frames)
    {
        var composer = ShotComposer.Create(fit.Timeline, new())!;
        double pos = 0, angle = 0, fov = 0;
        for(int i = 0; i < frames.Length; i++)
        {
            var f = frames[i]; var p = composer.Sample(i);
            var delta = Quaternion.Normalize(Quaternion.Conjugate(f.Rot)*p.Rot);
            angle = Math.Max(angle, 2*Math.Atan2(new Vector3(delta.X,delta.Y,delta.Z).Length(), Math.Abs(delta.W))*180/Math.PI);
            pos = Math.Max(pos, Vector3.Distance(p.Pos, f.Pos));
            fov = Math.Max(fov, Math.Abs((double)p.Fov-f.Fov)*180/Math.PI);
        }
        Check(pos <= .001 && angle <= .05 && fov <= .01, $"Playback maximum errors: {pos} m, {angle} deg, FoV {fov} deg");
        Check(Math.Abs(pos-fit.PositionError)<1e-5 && Math.Abs(angle-fit.AngleError)<.0001 && Math.Abs(fov-fit.FovError)<.0001, "Reported maxima match playback");
    }
}
