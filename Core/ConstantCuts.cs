using System;
using System.Collections.Generic;
using System.Linq;

namespace XcpCam.Core;

/// <summary>A cut is an all-channel constant outgoing interval, never an editing-domain boundary.</summary>
public static class ConstantCuts
{
    public static bool IsCut(ShotCurves shot, MotionInterval interval)
    {
        if(shot.EditorMode == CurveEditorMode.Advanced)
            return Enum.GetValues<SpatialChannel>().All(c => shot.Space.Channels[c].Keys
                .FirstOrDefault(k => k.NodeId == interval.StartNodeId)?.Interpolation == CurveInterpolation.Constant);
        return Enum.GetValues<CameraChannel>().All(c => interval.Curve(c).Keys.Take(Math.Max(0, interval.Curve(c).Keys.Count - 1))
            .All(k => k.Interpolation == CurveInterpolation.Constant));
    }

    public static void Set(ShotCurves shot, Guid endNode, bool enabled)
    {
        var interval = shot.Intervals.FirstOrDefault(i => i.EndNodeId == endNode);
        if(interval == null) return;
        var keys = Enum.GetValues<SpatialChannel>().Select(c => shot.Space.Channels[c].Keys.FirstOrDefault(k => k.NodeId == interval.StartNodeId))
            .Concat(shot.Offset.Prepend(shot.PathProgress).Select(c => c.Keys.FirstOrDefault(k => k.NodeId == interval.StartNodeId)))
            .Where(k => k != null).Select(k => k!)
            .Concat(interval.Channels.Values.Distinct().SelectMany(c => c.Keys.Take(Math.Max(0, c.Keys.Count - 1)))).ToList();
        foreach(var key in keys)
        {
            if(enabled)
            {
                if(!shot.CutRestore.ContainsKey(key.Id)) shot.CutRestore[key.Id] = key.Interpolation;
                key.Interpolation = CurveInterpolation.Constant;
            }
            else
            {
                if(shot.CutRestore.Remove(key.Id, out var previous)) key.Interpolation = previous;
                else if(key.Interpolation == CurveInterpolation.Constant) key.Interpolation = CurveInterpolation.Bezier;
            }
        }
        interval.MarkManual(); shot.Space.Touch(); shot.ActivateSimpleTiming();
    }

    public static void Synchronize(ShotCurves shot, IReadOnlyList<Keyframe> nodes)
    {
        foreach(var interval in shot.Intervals)
        {
            var node = nodes.First(k => k.Id == interval.EndNodeId);
            bool derived = IsCut(shot, interval);
            bool observed = shot.CutObserved.TryGetValue(node.Id, out bool previous);
            if(!observed && node.CutIn || observed && node.CutIn != previous)
            {
                Set(shot, node.Id, node.CutIn);
                derived = IsCut(shot, interval);
            }
            node.CutIn = derived;
            shot.CutObserved[node.Id] = derived;
        }
        if(nodes.Count > 0) nodes[0].CutIn = false;
    }
}
