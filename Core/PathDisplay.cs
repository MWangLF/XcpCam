using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

public readonly record struct DisplayEdge(float StartFrame, float EndFrame, XcpFrame Start, XcpFrame End, bool Dashed);
public readonly record struct DisplayJump(float Frame, XcpFrame Before, XcpFrame After);

/// <summary>Read-only render data sampled from the active camera evaluator, with explicit discontinuities.</summary>
public sealed class PathDisplay
{
    public List<DisplayEdge> Edges { get; } = [];
    public List<DisplayJump> Jumps { get; } = [];

    public static PathDisplay Build(ShotCurves shot, int fps)
    {
        var result = new PathDisplay();
        if(shot.End <= shot.Start) return result;
        var events = new SortedSet<float>();
        var spatialJumps = shot.Space.Channels.ToDictionary(pair => pair.Key, pair =>
        {
            pair.Value.Sort();
            var keys = pair.Value.Keys;
            return Enumerable.Range(1, Math.Max(0, keys.Count - 1)).Where(n =>
                keys[n - 1].Interpolation == CurveInterpolation.Constant && keys[n - 1].Value != keys[n].Value)
                .Select(n => keys[n].Time).ToArray();
        });
        foreach(var interval in shot.Intervals)
        {
            events.Add(interval.Start); events.Add(interval.End);
            foreach(var channel in Enum.GetValues<CameraChannel>())
            {
                var curve = interval.Curve(channel);
                if(shot.EditorMode == CurveEditorMode.Simple)
                    for(int n = 1; n < curve.Keys.Count; n++)
                        if(curve.Keys[n - 1].Interpolation == CurveInterpolation.Constant)
                        {
                            double release = Math.Min(interval.End, (double)interval.Start + interval.HoldFrames);
                            double f = release + (curve.Keys[n].Frame - interval.Start) * (interval.End - release) / (interval.End - interval.Start);
                            events.Add((float)f);
                        }
                var spatialChannels = channel == CameraChannel.Position
                    ? new[] { SpatialChannel.PositionX, SpatialChannel.PositionY, SpatialChannel.PositionZ }
                    : channel == CameraChannel.Rotation ? new[] { SpatialChannel.Yaw, SpatialChannel.Pitch, SpatialChannel.Roll } : new[] { SpatialChannel.Fov };
                foreach(var c in spatialChannels)
                {
                    var parameters = spatialJumps[c];
                    var range = interval.Ranges[channel];
                    int n = Array.BinarySearch(parameters, range.start);
                    for(n = n >= 0 ? n + 1 : ~n; n < parameters.Length && parameters[n] <= range.end; n++)
                    {
                        double parameter = parameters[n];
                        // ParameterAt uses exactly the active mode, including holds and simple timing.
                        double lo = interval.Start, hi = interval.End;
                        for(int j = 0; j < 52; j++)
                        {
                            double middle = (lo + hi) * .5;
                            if(shot.ParameterAt(channel, middle) < parameter) lo = middle; else hi = middle;
                        }
                        events.Add((float)hi);
                    }
                }
            }
        }
        foreach(var hold in shot.Holds) { events.Add(hold.start); events.Add(hold.end); }
        var times = new SortedSet<float> { shot.Start, shot.End };
        int samples = Math.Clamp((int)Math.Ceiling((shot.End - shot.Start) * 30.0 / Math.Max(1, fps)), 2, 1536);
        for(int n = 1; n < samples; n++) times.Add(shot.Start + (shot.End - shot.Start) * n / (float)samples);
        foreach(float frame in events.Where(f => f > shot.Start && f <= shot.End))
        {
            float beforeFrame = MathF.BitDecrement(frame);
            var before = shot.Sample(beforeFrame); var after = shot.Sample(frame);
            // Candidate boundaries come from interpolation data, not a distance threshold.
            bool jump = Vector3.DistanceSquared(before.Pos, after.Pos) > 1e-8f ||
                1 - Math.Abs(Quaternion.Dot(before.Rot, after.Rot)) > 1e-7f || Math.Abs(before.Fov - after.Fov) > 1e-5f;
            if(jump) { result.Jumps.Add(new DisplayJump(frame, before, after)); times.Add(beforeFrame); }
            times.Add(frame);
        }
        var jumpFrames = result.Jumps.Select(j => j.Frame).ToHashSet();
        float previousFrame = times.Min; var previous = shot.Sample(previousFrame);
        foreach(float frame in times.Skip(1))
        {
            var pose = shot.Sample(frame);
            bool dashed = previousFrame == MathF.BitDecrement(frame) && jumpFrames.Contains(frame);
            result.Edges.Add(new DisplayEdge(previousFrame, frame, previous, pose, dashed));
            previousFrame = frame; previous = pose;
        }
        return result;
    }
}
