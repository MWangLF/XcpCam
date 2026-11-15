using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Time-aware scalar tangents over consecutive node runs, bounded by cuts/holds/selection.</summary>
public static class SpatialTangents
{
    private static readonly SpatialChannel[] PositionChannels = [SpatialChannel.PositionX, SpatialChannel.PositionY, SpatialChannel.PositionZ];

    public static void Apply(SpatialCurves space, IReadOnlyList<Keyframe> nodes, SpatialChannel? channel = null,
        ISet<Guid>? selected = null, bool explicitEdit = false)
    {
        bool changed = false;
        // Routine synchronization only refits dirty nodes, so an edit reshapes just the legs
        // touching the edited node (plus the configured influence range), never the whole run.
        bool routine = selected == null && !explicitEdit;
        if(routine && space.DirtyTangents.Count == 0) return;
        var channels = Enum.GetValues<SpatialChannel>().Where(c => channel == null || c == channel).ToList();
        // The position path is a 3D Bezier curve: its automatic handles use Blender's spatial formula
        // over the joint XYZ points instead of three independent scalar tangents.
        if(channels.Contains(SpatialChannel.PositionX) || channels.Contains(SpatialChannel.PositionY) || channels.Contains(SpatialChannel.PositionZ))
        {
            changed |= ApplyPosition(space, nodes, selected, explicitEdit, routine);
            channels.RemoveAll(PositionChannels.Contains);
        }
        foreach(var c in channels)
        {
            var curve = space.Channels[c];
            foreach(var run in Runs(curve, nodes, selected).Where(r => r.Count > 0))
                for(int i = 0; i < run.Count; i++)
                {
                    var (node,key) = run[i];
                    if(routine && !space.DirtyTangents.Contains(node.Id)) continue;
                    if(!explicitEdit && !key.AutomaticTangent || key.Mode is HandleMode.Free or HandleMode.Aligned) continue;
                    if(run.Count == 1) continue;
                    var previous = run[Math.Max(0,i-1)]; var next = run[Math.Min(run.Count-1,i+1)];
                    double h0 = i > 0 ? node.Frame-previous.node.Frame : next.node.Frame-node.Frame;
                    double h1 = i+1 < run.Count ? next.node.Frame-node.Frame : h0;
                    if(h0 <= 0 || h1 <= 0) continue;
                    double d0 = i > 0 ? (key.Value-previous.key.Value)/h0 : (next.key.Value-key.Value)/h1;
                    double d1 = i+1 < run.Count ? (next.key.Value-key.Value)/h1 : d0;
                    double tangent = (h1*d0+h0*d1)/(h0+h1);
                    if(key.Mode == HandleMode.AutoClamped)
                    {
                        if(d0*d1 <= 0) tangent=0;
                        else { double a=2*h1+h0,b=h1+2*h0; tangent=(a+b)/(a/d0+b/d1); }
                    }
                    if(node.HoldFrames > 0) tangent=0;
                    double leftSpan = i>0 ? key.Frame-previous.key.Frame : next.key.Frame-key.Frame;
                    double rightSpan = i+1<run.Count ? next.key.Frame-key.Frame : leftSpan;
                    var l=new Vector2((float)(-leftSpan/3),(float)(-h0/3*(key.Mode==HandleMode.Vector?d0:tangent)));
                    var r=new Vector2((float)(rightSpan/3),(float)(h1/3*(key.Mode==HandleMode.Vector?d1:tangent)));
                    if(key.Left!=l || key.Right!=r) { key.Left=l; key.Right=r; changed=true; }
                    if(explicitEdit) key.AutomaticTangent=true;
                }
        }
        if(routine) space.DirtyTangents.Clear();
        if(changed) space.Touch();
    }

    private static List<List<(Keyframe node, CurveKey key)>> Runs(FCurve curve, IReadOnlyList<Keyframe> nodes, ISet<Guid>? selected)
    {
        var runs = new List<List<(Keyframe node, CurveKey key)>>();
        foreach(var node in nodes.OrderBy(n => n.Frame))
        {
            var key = curve.Keys.FirstOrDefault(k => k.NodeId == node.Id);
            if(key == null || selected != null && !selected.Contains(node.Id)) { if(runs.Count > 0 && runs[^1].Count > 0) runs.Add([]); continue; }
            if(runs.Count == 0 || runs[^1].Count > 0 && (node.CutIn || runs[^1][^1].node.HoldFrames > 0 || runs[^1][^1].key.Interpolation == CurveInterpolation.Constant)) runs.Add([]);
            runs[^1].Add((node,key));
        }
        return runs;
    }

    /// <summary>
    /// Blender calchandleNurb_intern over the XYZ control points. The spatial parameter of every
    /// position key stays linear in the Bezier parameter (handle X = ±span/3), so the three scalar
    /// curves are exactly the 3D Bezier spline edited in the game view.
    /// </summary>
    private static bool ApplyPosition(SpatialCurves space, IReadOnlyList<Keyframe> nodes, ISet<Guid>? selected, bool explicitEdit, bool routine)
    {
        bool changed = false;
        var x = space.Channels[SpatialChannel.PositionX]; var y = space.Channels[SpatialChannel.PositionY]; var z = space.Channels[SpatialChannel.PositionZ];
        foreach(var run in Runs(x, nodes, selected).Where(r => r.Count > 1))
        {
            var keys = run.Select(r => (r.node, kx: r.key, ky: y.Keys.FirstOrDefault(k => k.NodeId == r.node.Id), kz: z.Keys.FirstOrDefault(k => k.NodeId == r.node.Id))).ToList();
            if(keys.Any(k => k.ky == null || k.kz == null)) continue;
            Vector3 Co(int i) => new((float)keys[i].kx.Value, (float)keys[i].ky!.Value, (float)keys[i].kz!.Value);
            for(int i = 0; i < keys.Count; i++)
            {
                var (node, kx, ky, kz) = keys[i];
                if(routine && !space.DirtyTangents.Contains(node.Id)) continue;
                if(!explicitEdit && !kx.AutomaticTangent || kx.Mode is HandleMode.Free or HandleMode.Aligned) continue;
                var type = kx.Mode == HandleMode.Vector ? CurveHandle.Vector : CurveHandle.Auto;
                // Where the direction is undefined (path folds straight back, or coincident points) Blender keeps
                // the previous handles, which makes the result depend on edit order; use zero-length handles instead.
                var point = new CurvePoint { Co = Co(i), LeftType = type, RightType = type, Left = Co(i), Right = Co(i) };
                CurvePoint? prev = i > 0 ? new CurvePoint { Co = Co(i - 1) } : null;
                CurvePoint? next = i + 1 < keys.Count ? new CurvePoint { Co = Co(i + 1) } : null;
                Spline.CalcHandle(point, prev, next, false);
                var left = point.Left - point.Co; var right = point.Right - point.Co;
                if(node.HoldFrames > 0) { left = Vector3.Zero; right = Vector3.Zero; } // a hold stops on the node
                double leftSpan = i > 0 ? kx.Frame - keys[i - 1].kx.Frame : keys[i + 1].kx.Frame - kx.Frame;
                double rightSpan = i + 1 < keys.Count ? keys[i + 1].kx.Frame - kx.Frame : leftSpan;
                float lx = (float)(-leftSpan / 3), rx = (float)(rightSpan / 3);
                foreach(var (key, l, r) in new[] { (kx, left.X, right.X), (ky!, left.Y, right.Y), (kz!, left.Z, right.Z) })
                {
                    var nl = new Vector2(lx, l); var nr = new Vector2(rx, r);
                    if(key.Left != nl || key.Right != nr) { key.Left = nl; key.Right = nr; changed = true; }
                    if(explicitEdit) key.AutomaticTangent = true;
                }
            }
        }
        return changed;
    }
}
