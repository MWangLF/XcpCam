using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace XcpCam.Core;

public sealed record XcpMotionFit(CamTimeline Timeline, int NodeCount, int CurveKeyCount,
    double PositionError, double AngleError, double FovError);

/// <summary>Fits world-space samples on the native 30 Hz XCP grid. Errors are maxima over the input frames.</summary>
public static class XcpMotionImport
{
    public const int MaxFrames = 100_000;
    private const double Degrees = 180 / Math.PI;
    private const int MaxFittedKeys = 100_000;

    public static XcpMotionFit Fit(IReadOnlyList<XcpFrame> worldFrames, double positionTolerance = .001,
        double angleTolerance = .05, double fovTolerance = .01, CancellationToken cancellationToken = default,
        IReadOnlySet<int>? cutFrames = null)
    {
        ArgumentNullException.ThrowIfNull(worldFrames);
        cancellationToken.ThrowIfCancellationRequested();
        int count = worldFrames.Count, last = count - 1;
        if(count < 1 || count > MaxFrames) throw new ArgumentOutOfRangeException(nameof(worldFrames), $"XCP 帧数须在 1 到 {MaxFrames} 之间。");
        foreach(double tolerance in new[] { positionTolerance, angleTolerance, fovTolerance })
            if(!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentException("误差容限须为有限非负数。");
        var anchors = new SortedSet<int> { 0, last };
        if(cutFrames != null) foreach(int cut in cutFrames)
        {
            if(cut < 1 || cut > last) throw new ArgumentOutOfRangeException(nameof(cutFrames));
            anchors.Add(cut - 1); anchors.Add(cut);
        }
        var timeline = new CamTimeline { Length = count };
        var samples = new XcpFrame[count];
        var values = new double[7][];
        for(int c = 0; c < values.Length; c++) values[c] = new double[count];
        Vector3 previous = default;
        for(int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var f = worldFrames[i];
            if(!XcpMath.IsFinite(f.Pos) || !XcpMath.IsFinite(f.Rot) || !float.IsFinite(f.Fov) || f.Rot == default)
                throw new ArgumentException($"XCP 第 {i} 帧的姿态无效。", nameof(worldFrames));
            samples[i] = f;
            previous = Euler(f.Rot, i == 0 || cutFrames?.Contains(i) == true ? Vector3.Zero : previous);
            values[3][i] = previous.X; values[4][i] = previous.Y; values[5][i] = previous.Z;
            values[6][i] = f.Fov * Degrees;
        }
        SelectNodes(samples, anchors, positionTolerance, angleTolerance, fovTolerance, cancellationToken);
        CheckWork(anchors.Count, anchors.Count);
        var nodes = timeline.Keys;
        foreach(int i in anchors)
        {
            var f = samples[i];
            nodes.Add(new Keyframe { Frame = i, Pos = f.Pos, Rot = Unit(f.Rot), Fov = f.Fov, CutIn = cutFrames?.Contains(i) == true });
        }
        timeline.ResyncGaps(); timeline.SyncSeconds();
        var shot = new ShotCurves { AnchorNodeId = nodes[0].Id };
        timeline.ActiveTrack.Shots.Add(shot);
        shot.SetEditorMode(CurveEditorMode.Advanced);
        shot.Space.PreserveIntermediateKeys = true;
        shot.Space.End = last;
        foreach(var node in nodes)
        {
            int i = node.Frame;
            shot.Space.AddAnchor(node.Id, i, i, i);
            double[] pose = [node.Pos.X, node.Pos.Y, node.Pos.Z, values[3][i], values[4][i], values[5][i], values[6][i]];
            foreach(var c in Enum.GetValues<SpatialChannel>()) shot.Space.Channels[c].Keys.Add(Key(i, pose[(int)c], node));
            shot.PathProgress.Keys.Add(Key(i, 0, node));
        }
        BuildPathHandles(shot.Space, nodes, samples);
        CurveModel.Ensure(timeline.ActiveTrack, timeline.Fps);
        FitProgress(shot, nodes, samples, positionTolerance, cancellationToken);
        // Sample the actual arc-length path first. Offset curves carry the residual in world axes.
        for(int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = shot.Sample(i).Pos;
            values[0][i] = (double)samples[i].Pos.X - p.X;
            values[1][i] = (double)samples[i].Pos.Y - p.Y;
            values[2][i] = (double)samples[i].Pos.Z - p.Z;
        }
        var residualCutKeys = new List<Guid>();
        for(int attempt = 0; ; attempt++)
        {
            int extra = shot.PathProgress.Keys.Count - nodes.Count;
            foreach(var id in residualCutKeys) shot.CutRestore.Remove(id);
            residualCutKeys.Clear();
            for(int c = 0; c < 7; c++)
            {
                var curve = c < 3 ? shot.Offset[c] : shot.Space.Channels[(SpatialChannel)c];
                double tolerance = c < 3 ? positionTolerance / Math.Sqrt(3) : c < 6 ? angleTolerance / 3 : fovTolerance;
                // Leave room for native float reconstruction. On failure retry without simplification error.
                var keys = Simplify(values[c], nodes, attempt == 0 ? tolerance * .9 : 0, cancellationToken);
                curve.Keys = keys;
                curve.MakeLinearSegmentsEditable();
                foreach(var key in keys)
                    if(key.NodeId.HasValue && cutFrames?.Contains(key.Frame + 1) == true)
                    {
                        residualCutKeys.Add(key.Id);
                        shot.CutRestore[key.Id] = key.Interpolation;
                        key.Interpolation = CurveInterpolation.Constant;
                    }
                extra += keys.Count - nodes.Count;
            }
            CheckWork(extra + nodes.Count, nodes.Count);
            shot.Space.Touch();
            // One unconstrained world-space track: this is the same sequence sampled by ShotComposer.
            var playback = ShotSequence.Create(timeline.ActiveTrack, timeline.Fps)!;
            double position = 0, angle = 0, fov = 0;
            int bad = -1;
            for(int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = playback.Sample(i).pose;
                var f = samples[i];
                double dx = (double)p.Pos.X - f.Pos.X, dy = (double)p.Pos.Y - f.Pos.Y, dz = (double)p.Pos.Z - f.Pos.Z;
                position = Math.Max(position, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                angle = Math.Max(angle, Angle(f.Rot, p.Rot));
                fov = Math.Max(fov, Math.Abs((double)p.Fov - f.Fov) * Degrees);
                if(bad < 0 && !(position <= positionTolerance && angle <= angleTolerance && fov <= fovTolerance)) bad = i;
            }
            if(bad < 0) return new(timeline, nodes.Count, extra, position, angle, fov);
            if(attempt != 0) throw new InvalidOperationException($"XCP 从第 {bad} 帧起超差。最大位置误差 {position:G6} 米，旋转 {angle:G6} 度，FoV {fov:G6} 度。");
        }
    }

    private static void SelectNodes(XcpFrame[] samples, SortedSet<int> anchors, double positionTolerance,
        double angleTolerance, double fovTolerance, CancellationToken token)
    {
        // Prefer a real node at a visible bend or pose change. Leave sample-level noise to the channel fit.
        double positionScale = Math.Max(.015, positionTolerance * 4);
        double angleScale = Math.Max(5, angleTolerance * 4);
        double fovScale = Math.Max(1, fovTolerance * 4);
        // Real stop/start boundaries make useful anchors. Do not create nodes for a wholly static clip.
        var holds = new List<(int start, int end)>();
        for(int first = 0, end = 0; end < samples.Length;)
        {
            token.ThrowIfCancellationRequested();
            while(end + 1 < samples.Length && samples[end + 1].Pos == samples[first].Pos) end++;
            if(end - first >= 6 && (first > 0 || end < samples.Length - 1)) holds.Add((first, end));
            first = ++end;
        }
        int holdBudget = Math.Max(0, (384 - anchors.Count) / 4);
        foreach(var hold in holds.OrderByDescending(h => h.end - h.start).Take(holdBudget))
        { anchors.Add(hold.start); anchors.Add(hold.end); }
        var boundaries = anchors.ToArray();
        var spans = new PriorityQueue<(int start, int end, int split), double>();
        void Inspect(int start, int end)
        {
            if(end - start < 2) return;
            var a = samples[start]; var b = samples[end];
            var chord = b.Pos - a.Pos;
            double lengthSq = chord.LengthSquared();
            double maxScore = 1, highWater = 0;
            int split = -1;
            for(int i = start + 1; i < end; i++)
            {
                token.ThrowIfCancellationRequested();
                var sample = samples[i];
                double time = (i - start) / (double)(end - start);
                double along = lengthSq > 0 ? Math.Clamp(Vector3.Dot(sample.Pos - a.Pos, chord) / lengthSq, 0, 1) : 0;
                highWater = Math.Max(highWater, along);
                double spatial = Math.Max(Vector3.Distance(sample.Pos, a.Pos + chord * (float)along), (highWater - along) * Math.Sqrt(lengthSq));
                double angular = Angle(sample.Rot, Quaternion.Slerp(Unit(a.Rot), Unit(b.Rot), (float)time));
                double lens = Math.Abs(sample.Fov - (a.Fov + (b.Fov - a.Fov) * time)) * Degrees;
                double score = Math.Max(spatial / positionScale, Math.Max(angular / angleScale, lens / fovScale));
                if(score > maxScore) { maxScore = score; split = i; }
            }
            if(split >= 0) spans.Enqueue((start, end, split), -maxScore);
        }
        for(int i = 1; i < boundaries.Length; i++) Inspect(boundaries[i - 1], boundaries[i]);
        // Keep the strongest features first, so a dense section cannot consume the rest of the clip's budget.
        int budget = Math.Max(anchors.Count, Math.Min(384, Math.Max(2, 16_000_000 / samples.Length)));
        while(anchors.Count < budget && spans.TryDequeue(out var span, out _))
        {
            anchors.Add(span.split);
            Inspect(span.start, span.split); Inspect(span.split, span.end);
        }
    }

    private static void BuildPathHandles(SpatialCurves space, List<Keyframe> nodes, XcpFrame[] samples)
    {
        var axes = new[] { SpatialChannel.PositionX, SpatialChannel.PositionY, SpatialChannel.PositionZ };
        foreach(var axis in axes) space.Channels[axis].MakeLinearSegmentsEditable();
        for(int n = 0; n < nodes.Count - 1; n++)
        {
            var a = nodes[n]; var b = nodes[n + 1];
            if(b.CutIn) continue;
            var chord = b.Pos - a.Pos;
            float length = chord.Length();
            if(length <= 1e-8f) continue;
            Vector3 Handle(Vector3 direction)
            {
                if(direction.LengthSquared() < 1e-16f) return chord / 3;
                var unit = Vector3.Normalize(direction);
                return Vector3.Dot(unit, chord / length) >= .78f ? unit * (length / 3) : chord / 3;
            }
            int window = Math.Clamp((b.Frame - a.Frame) / 4, 1, 6);
            var outgoing = Handle(samples[a.Frame + window].Pos - a.Pos);
            var incoming = Handle(b.Pos - samples[b.Frame - window].Pos);
            for(int c = 0; c < axes.Length; c++)
            {
                var keys = space.Channels[axes[c]].Keys;
                keys[n].Right.Y = outgoing[c]; keys[n + 1].Left.Y = -incoming[c];
            }
        }
        space.Touch();
    }

    private static void FitProgress(ShotCurves shot, List<Keyframe> nodes, XcpFrame[] samples, double tolerance, CancellationToken token)
    {
        if(nodes.Count < 2) return;
        var progress = shot.PathProgress;
        var values = new double[samples.Length];
        var axes = new[] { SpatialChannel.PositionX, SpatialChannel.PositionY, SpatialChannel.PositionZ };
        Vector3 Position(double parameter) => new((float)shot.Space.Channels[axes[0]].EvaluateOrdered(parameter),
            (float)shot.Space.Channels[axes[1]].EvaluateOrdered(parameter), (float)shot.Space.Channels[axes[2]].EvaluateOrdered(parameter));
        double factorTolerance = double.PositiveInfinity;
        for(int n = 1; n < nodes.Count; n++)
        {
            var interval = shot.Intervals[n - 1];
            int a = nodes[n - 1].Frame, b = nodes[n].Frame;
            double start = progress.Keys[n - 1].Value, end = progress.Keys[n].Value;
            values[a] = start; values[b] = end;
            var path = interval.Path!;
            var (p0, p1) = interval.Ranges[CameraChannel.Position];
            double distance0 = path.Distance(p0), distance = path.Distance(p1) - distance0;
            double previous = p0;
            if(!(distance > 1e-9) || nodes[n].CutIn)
            {
                for(int i = a + 1; i < b; i++) values[i] = start + (end - start) * (i - a) / (b - a);
                continue;
            }
            factorTolerance = Math.Min(factorTolerance, tolerance * .2 * (end - start) / distance);
            var chord = samples[b].Pos - samples[a].Pos;
            bool straight = chord.LengthSquared() > 1e-12f &&
                Vector3.DistanceSquared(Position((p0 + p1) * .5), (samples[a].Pos + samples[b].Pos) * .5f) < 1e-12f &&
                Vector3.DistanceSquared(Position(p0 + (p1 - p0) * .25), Vector3.Lerp(samples[a].Pos, samples[b].Pos, .25f)) < 1e-12f;
            for(int i = a + 1; i < b; i++)
            {
                token.ThrowIfCancellationRequested();
                if(samples[i].Pos == samples[i - 1].Pos) { values[i] = values[i - 1]; continue; }
                var point = samples[i].Pos;
                if(straight)
                {
                    double fraction = Math.Clamp(Vector3.Dot(point - samples[a].Pos, chord) / chord.LengthSquared(), 0, 1);
                    values[i] = Math.Max(values[i - 1], start + (end - start) * fraction);
                    continue;
                }
                double lo = previous, hi = p1;
                for(int step = 0; step < 21; step++)
                {
                    double l = (2 * lo + hi) / 3, r = (lo + 2 * hi) / 3;
                    if(Vector3.DistanceSquared(point, Position(l)) <= Vector3.DistanceSquared(point, Position(r))) hi = r; else lo = l;
                }
                previous = lo + (hi - lo) * .5;
                // Keep stationary boundaries exact and retain the monotone projection on this leg.
                if(Vector3.DistanceSquared(point, Position(p1)) < Vector3.DistanceSquared(point, Position(previous))) previous = p1;
                values[i] = start + (end - start) * Math.Clamp((path.Distance(previous) - distance0) / distance, 0, 1);
            }
        }
        progress.Keys = Simplify(values, nodes, double.IsFinite(factorTolerance) ? factorTolerance : 1e-12, token);
        progress.MakeLinearSegmentsEditable();
        var cuts = nodes.Where(n => n.CutIn).Select(n => n.Frame - 1).ToHashSet();
        foreach(var key in progress.Keys)
            if(key.NodeId.HasValue && cuts.Contains(key.Frame))
            { shot.CutRestore[key.Id] = key.Interpolation; key.Interpolation = CurveInterpolation.Constant; }
    }

    private static void CheckWork(int keys, int nodes)
    {
        if(keys > MaxFittedKeys || nodes > 1024)
            throw new InvalidOperationException("重建所需的镜头节点或曲线键过多，请分段导入或适当放宽误差范围。");
    }

    private static CurveKey Key(int frame, double value, Keyframe? node = null) => new()
    { Frame = frame, Value = value, NodeId = node?.Id, Interpolation = CurveInterpolation.Linear, Mode = HandleMode.Free, AutomaticTangent = false };

    // Each observed frame constrains the permissible slope from the last retained point. When a new
    // endpoint leaves the corridor, commit its predecessor (already known to satisfy every constraint).
    private static List<CurveKey> Simplify(double[] values, List<Keyframe> nodes, double tolerance, CancellationToken token)
    {
        var keys = new List<CurveKey> { Key(0, values[0], nodes[0]) };
        for(int n = 1; n < nodes.Count; n++)
        {
            int start = nodes[n - 1].Frame, end = nodes[n].Frame;
            double low = double.NegativeInfinity, high = double.PositiveInfinity;
            for(int i = start + 1; i <= end; i++)
            {
                token.ThrowIfCancellationRequested();
                double slope = (values[i] - values[start]) / (i - start);
                if(slope < low || slope > high)
                {
                    start = i - 1;
                    keys.Add(Key(start, values[start]));
                    low = double.NegativeInfinity; high = double.PositiveInfinity;
                    slope = values[i] - values[start];
                }
                double margin = tolerance / (i - start);
                low = Math.Max(low, slope - margin); high = Math.Min(high, slope + margin);
            }
            keys.Add(Key(end, values[end], nodes[n]));
        }
        return keys;
    }

    private static double Norm(Quaternion q) => Math.Sqrt((double)q.X*q.X + (double)q.Y*q.Y + (double)q.Z*q.Z + (double)q.W*q.W);
    private static Quaternion Unit(Quaternion q)
    {
        double n = Norm(q);
        return new((float)(q.X/n), (float)(q.Y/n), (float)(q.Z/n), (float)(q.W/n));
    }

    private static double Angle(Quaternion a, Quaternion b)
    {
        double an = Norm(a), bn = Norm(b);
        if(Quaternion.Dot(a, b) < 0) bn = -bn;
        double x = a.X/an - b.X/bn, y = a.Y/an - b.Y/bn, z = a.Z/an - b.Z/bn, w = a.W/an - b.W/bn;
        return 4 * Math.Asin(Math.Min(1, .5 * Math.Sqrt(x*x + y*y + z*z + w*w))) * Degrees;
    }

    private static Vector3 Euler(Quaternion q, Vector3 near)
    {
        double n = Norm(q), x = q.X/n, y = q.Y/n, z = q.Z/n, w = q.W/n;
        double sin = 2*(w*x - y*z), a = 2*(w*y + x*z), b = 1 - 2*(x*x + y*y);
        double cos = Math.Sqrt(a*a + b*b);
        double pitch = Math.Atan2(sin, cos) * Degrees;
        double yaw = Math.Atan2(a, b) * Degrees, roll = Math.Atan2(2*(w*z + x*y), 1 - 2*(x*x + z*z)) * Degrees;
        if(cos < 1e-7) { roll = near.Z; yaw = 2*Math.Atan2(y, w)*Degrees + Math.Sign(sin)*roll; }
        static float Wrap(double v, float near) => (float)(v + 360*Math.Round((near - v)/360));
        Vector3 Near(double y0, double p0, double r0) => new(Wrap(y0, near.X), Wrap(p0, near.Y), Wrap(r0, near.Z));
        var direct = Near(yaw, pitch, roll); var alternate = Near(yaw + 180, 180 - pitch, roll + 180);
        return Vector3.DistanceSquared(near, direct) <= Vector3.DistanceSquared(near, alternate) ? direct : alternate;
    }
}
