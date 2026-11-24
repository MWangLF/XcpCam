using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Editable approximation of the 0.0.4.0 Catmull-Rom/SQUAD/Hermite reference.</summary>
public static class LegacySpatialPath
{
    public const double PositionTolerance = .0002;
    public const double AngleToleranceDegrees = .015;
    public const double FovToleranceDegrees = .005;
    public static void Generate(ShotCurves shot, IReadOnlyList<Keyframe> nodes, int fps, IEnumerable<SpatialChannel>? selected = null)
    {
        if(nodes.Count < 2) { shot.Space.LegacyInitialized = true; shot.Space.GeneratedRevision = shot.Space.Revision; return; }
        var keys = nodes.Select(n =>
        {
            var key = n.Clone(); var pose = shot.NodePose(n);
            key.Pos = pose.Pos; key.Rot = pose.Rot; key.Fov = pose.Fov;
            return key;
        }).ToList();
        foreach(var k in keys) { k.TimeSec = k.Frame / (float)fps; k.HoldSec = k.HoldFrames / (float)fps; k.Tangent = TangentMode.Auto; }
        var reference = CameraPath.Create(keys, new PathSettings())!;
        double firstTime = keys[0].TimeSec;
        var output = Enum.GetValues<SpatialChannel>().ToDictionary(c => c, _ => new FCurve());
        var anchors = new Dictionary<Guid, Dictionary<CameraChannel,double>>();
        const int unit = 65536;
        for(int index = 0; index < nodes.Count; index++)
            anchors[nodes[index].Id] = Enum.GetValues<CameraChannel>().ToDictionary(c => c, _ => (double)checked(index * unit));
        for(int index = 0; index + 1 < nodes.Count; index++)
        {
            int start = index * unit, end = (index + 1) * unit;
            var leg = shot.Intervals[index];
            float t0 = (float)(keys[index].TimeSec - firstTime), t1 = (float)(keys[index + 1].TimeSec - firstTime);
            float d0 = reference.DistanceAtTime(t0), d1 = reference.DistanceAtTime(t1);
            double[] Values(int parameter)
            {
                double u = (parameter - start) / (double)unit;
                double lo = leg.Start, hi = leg.End;
                for(int n = 0; n < 45; n++)
                { double middle = (lo + hi) * .5; if(leg.Evaluate(CameraChannel.Rotation, middle) < u) lo = middle; else hi = middle; }
                float time = (float)(((lo + hi) * .5 - nodes[0].Frame) / fps);
                if(parameter == start) time = t0;
                if(parameter == end) time = t1;
                var pose = reference.SampleAtDistance(d0 + (d1 - d0) * (float)u, time);
                var near = Vector3.Lerp(ShotCurves.Euler(keys[index].Rot), ShotCurves.Euler(keys[index + 1].Rot, ShotCurves.Euler(keys[index].Rot)), (float)u);
                var euler = ShotCurves.Euler(pose.Rot, near);
                return [pose.Pos.X, pose.Pos.Y, pose.Pos.Z, euler.X, euler.Y, euler.Z, pose.Fov * 180 / Math.PI];
            }
            var samples = new SortedDictionary<int,double[]>();
            var a = Values(start); var b = Values(end); samples[start] = a; samples[end] = b;
            void Fit(int left, double[] av, int right, double[] bv)
            {
                if(right - left <= 1) return;
                bool split = false;
                foreach(double fraction in new[] { .25, .5, .75 })
                {
                    int at = left + (int)((right - left) * fraction); var v = Values(at);
                    double f = (at - left) / (double)(right - left);
                    for(int c = 0; c < 7; c++)
                    {
                        double tolerance = c < 3 ? PositionTolerance / 2 : c < 6 ? AngleToleranceDegrees / 4 : FovToleranceDegrees / 2;
                        if(Math.Abs(v[c] - (av[c] + (bv[c] - av[c]) * f)) > tolerance) split = true;
                    }
                }
                if(!split) return;
                int middle = (left + right) / 2; var mv = Values(middle); samples[middle] = mv;
                Fit(left, av, middle, mv); Fit(middle, mv, right, bv);
            }
            Fit(start, a, end, b);
            foreach(var pair in samples)
                foreach(var c in Enum.GetValues<SpatialChannel>())
                {
                    var curve = output[c];
                    if(curve.Keys.Any(k => k.Frame == pair.Key)) continue;
                    curve.Keys.Add(new CurveKey { Frame = pair.Key, Value = pair.Value[(int)c], Mode = HandleMode.Free,
                        Interpolation = CurveInterpolation.Bezier,
                        NodeId = pair.Key == start ? nodes[index].Id : pair.Key == end ? nodes[index + 1].Id : null });
                }
        }
        foreach(var curve in output.Values)
        {
            curve.Sort();
            for(int n = 0; n + 1 < curve.Keys.Count; n++)
            {
                var a = curve.Keys[n]; var b = curve.Keys[n + 1];
                a.Right = new Vector2((b.Frame - a.Frame) / 3f, (float)((b.Value - a.Value) / 3));
                b.Left = -a.Right;
            }
        }
        var wanted = selected?.ToHashSet() ?? Enum.GetValues<SpatialChannel>().ToHashSet();
        // Resets use the same canonical domain; first initialization installs all anchors.
        bool keepDomain = shot.Space.LegacyInitialized;
        foreach(var c in wanted)
        {
            if(keepDomain)
            {
                var group = SpatialFrameMap.Group(c);
                double Map(double p)
                {
                    int segment = Math.Clamp((int)(p / unit), 0, nodes.Count - 2);
                    double a = shot.Space.Anchors[nodes[segment].Id][group], b = shot.Space.Anchors[nodes[segment + 1].Id][group];
                    return a + (b - a) * ((p - segment * unit) / unit);
                }
                foreach(var key in output[c].Keys)
                {
                    double p = key.Frame, mapped = Map(p);
                    double l = Map(p + key.Left.X), r = Map(p + key.Right.X);
                    key.Frame = ProgressCurve.Round(mapped);
                    key.Left = new Vector2((float)(l - mapped), key.Left.Y);
                    key.Right = new Vector2((float)(r - mapped), key.Right.Y);
                }
                if(output[c].Keys.GroupBy(k => k.Frame).Any(g => g.Count() > 1))
                    throw new InvalidOperationException(I18n.T("core.legacy_precision"));
            }
        }
        foreach(var c in wanted) shot.Space.Channels[c] = output[c];
        if(!keepDomain)
        {
            shot.Space.Anchors = anchors; shot.Space.Start = 0; shot.Space.End = (nodes.Count - 1) * unit;
            foreach(var leg in shot.Intervals)
                foreach(var c in Enum.GetValues<CameraChannel>()) leg.Ranges[c] = (anchors[leg.StartNodeId][c], anchors[leg.EndNodeId][c]);
        }
        shot.Space.Touch(); shot.Space.LegacyInitialized = true;
        if(wanted.Count == 7) shot.Space.GeneratedRevision = shot.Space.Revision;
    }
}
