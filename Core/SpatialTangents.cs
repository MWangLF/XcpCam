using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Time-aware scalar tangents over consecutive node runs, bounded by cuts/holds/selection.</summary>
public static class SpatialTangents
{
    public static void Apply(SpatialCurves space, IReadOnlyList<Keyframe> nodes, SpatialChannel? channel = null,
        ISet<Guid>? selected = null, bool explicitEdit = false)
    {
        bool changed = false;
        // Routine synchronization only refits dirty nodes, so an edit reshapes just the legs
        // touching the edited node (plus the configured influence range), never the whole run.
        bool routine = selected == null && !explicitEdit;
        if(routine && space.DirtyTangents.Count == 0) return;
        foreach(var c in Enum.GetValues<SpatialChannel>().Where(c => channel == null || c == channel))
        {
            var curve = space.Channels[c];
            var runs = new List<List<(Keyframe node, CurveKey key)>>();
            foreach(var node in nodes.OrderBy(n => n.Frame))
            {
                var key = curve.Keys.FirstOrDefault(k => k.NodeId == node.Id);
                if(key == null || selected != null && !selected.Contains(node.Id)) { if(runs.Count > 0 && runs[^1].Count > 0) runs.Add([]); continue; }
                if(runs.Count == 0 || runs[^1].Count > 0 && (node.CutIn || runs[^1][^1].node.HoldFrames > 0 || runs[^1][^1].key.Interpolation == CurveInterpolation.Constant)) runs.Add([]);
                runs[^1].Add((node,key));
            }
            foreach(var run in runs.Where(r => r.Count > 0))
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
}
