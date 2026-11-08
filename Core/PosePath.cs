using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Cached joint XYZ distance table over the actual authored spatial curves.
/// Discontinuities are zero-width parameter events, never interpolated world-space bridges.</summary>
public sealed class PosePath
{
    private readonly SpatialCurves space;
    private readonly double[] parameters, lengths;
    private readonly List<double> jumps = new();
    public bool HasJump(double from, double to) => jumps.Any(p => p > Math.Min(from, to) && p <= Math.Max(from, to));
    public PosePath(SpatialCurves source)
    {
        space = source.Clone();
        var boundaries = new SortedSet<double> { space.Start, space.End };
        foreach(var c in new[] { SpatialChannel.PositionX, SpatialChannel.PositionY, SpatialChannel.PositionZ })
            foreach(var k in space.Channels[c].Keys) boundaries.Add(k.Frame);
        var ps = new List<double>(); var ds = new List<double>();
        double total = 0; Vector3 previous = default; bool first = true;
        void Add(double p, bool jump)
        {
            var value = space.Sample(p).Pos;
            if(!first && !jump) total += Vector3.Distance(previous, value);
            ps.Add(p); ds.Add(total); previous = value; first = false;
        }
        var knots = boundaries.ToArray(); Add(knots[0], false);
        for(int i = 0; i + 1 < knots.Length; i++)
        {
            double a = knots[i], b = knots[i + 1];
            // All XYZ key boundaries are included, so no sample spans a constant-channel jump.
            bool jump = new[] { SpatialChannel.PositionX, SpatialChannel.PositionY, SpatialChannel.PositionZ }.Any(c =>
            {
                var curve = space.Channels[c];
                var key = curve.Keys.LastOrDefault(k => k.Frame < b);
                return key?.Interpolation == CurveInterpolation.Constant && curve.Evaluate(Math.BitDecrement(b)) != curve.Evaluate(b);
            });
            int count = space.IsDefaultPath ? 2 : 128;
            for(int j = 1; j <= count; j++)
            {
                double p = a + (b - a) * j / count;
                if(j == count && jump) { Add(Math.BitDecrement(b), false); Add(b, true); jumps.Add(b); }
                else Add(p, false);
            }
        }
        parameters = ps.ToArray(); lengths = ds.ToArray();
    }
    public PosePath(IReadOnlyList<Keyframe> nodes) : this(FromNodes(nodes)) { }
    private static SpatialCurves FromNodes(IReadOnlyList<Keyframe> nodes)
    {
        if(nodes.Count == 0) throw new ArgumentException("A path requires a node.", nameof(nodes));
        var result = new SpatialCurves();
        for(int i = 0; i < nodes.Count; i++) result.Seed(nodes[i], checked(i * 1000));
        return result;
    }
    public double Distance(double p)
    {
        p = Math.Clamp(p, parameters[0], parameters[^1]);
        int index = Array.BinarySearch(parameters, p);
        if(index >= 0) return lengths[index];
        int hi = ~index, lo = hi - 1;
        if(lo < 0) return lengths[0]; if(hi >= parameters.Length) return lengths[^1];
        return lengths[lo] + (lengths[hi] - lengths[lo]) * ((p - parameters[lo]) / (parameters[hi] - parameters[lo]));
    }
    public double Parameter(CameraChannel channel, double start, double end, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        if(progress <= 0) return start; if(progress >= 1) return end;
        if(channel != CameraChannel.Position) return start + (end - start) * progress;
        double a = Distance(start), b = Distance(end);
        if(b - a <= 1e-12) return start + (end - start) * progress;
        double target = a + (b - a) * progress;
        int lo = 0, hi = lengths.Length - 1;
        while(hi - lo > 1) { int mid = (lo + hi) / 2; if(lengths[mid] <= target) lo = mid; else hi = mid; }
        // Never interpolate through the zero-distance jump pair.
        double span = lengths[hi] - lengths[lo];
        return Math.Clamp(span > 1e-12 ? parameters[lo] + (parameters[hi] - parameters[lo]) * ((target - lengths[lo]) / span) : parameters[hi], start, end);
    }
    public XcpFrame Sample(double positionParameter, double rotationParameter, double fovParameter) => space.Sample(positionParameter, rotationParameter, fovParameter);
}
