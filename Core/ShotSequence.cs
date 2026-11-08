using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>A continuous shot between hard cuts, sampled from independent scalar F-curves.</summary>
public sealed class ShotSegment
{
    public required IReadOnlyList<Keyframe> Keys { get; init; }
    public required ShotCurves Curves { get; init; }
    public int Start => Curves.Start;
    public int End => Curves.End;
    public int Fps { get; init; }
    // Keeps existing drawing consumers simple; the path is now the authored curve bundle, not arc-length pacing.
    public ShotCurves? Path => Keys.Count >= 2 ? Curves : null;
    public XcpFrame SampleAt(float frame) => Curves.Sample(frame);
}

public sealed class ShotSequence
{
    public IReadOnlyList<ShotSegment> Segments { get; }
    public int Start => Segments[0].Start;
    public int End => Segments[^1].End;
    public int Fps { get; }
    public IEnumerable<int> CutFrames => Segments.SelectMany(s => s.Keys.Where(k => k.CutIn).Select(k => k.Frame));
    public float TotalLength { get; }

    private ShotSequence(List<ShotSegment> segments, int fps)
    {
        Segments = segments; Fps = fps;
        float length = 0;
        foreach(var s in segments)
        {
            for(int i = 0; i < s.Curves.Intervals.Count; i++)
            {
                var prev = s.Curves.SampleGeometry(i, 0).Pos;
                for(int n = 1; n <= 96; n++)
                {
                    var p = s.Curves.SampleGeometry(i, n / 96.0).Pos;
                    length += Vector3.Distance(prev, p); prev = p;
                }
            }
        }
        TotalLength = length;
    }

    public static ShotSequence? Create(CamTrack track, int fps)
    {
        CurveModel.Ensure(track, fps);
        if(track.Keys.Count == 0) return null;
        var groups = CurveModel.Groups(track);
        return new ShotSequence(track.Shots.Select((s, i) => new ShotSegment
        { Keys = groups[i], Curves = s, Fps = fps }).ToList(), fps);
    }

    // Legacy callers/tests can seed a fresh curve track from captured nodes; old pacing is intentionally ignored.
    public static ShotSequence? Create(IEnumerable<Keyframe> keys, PathSettings settings, int fps)
        => Create(new CamTrack { Keys = keys.ToList() }, fps);

    public int SegmentAt(float frame)
    {
        int i = 0;
        for(int j = 1; j < Segments.Count; j++)
            if(frame >= Segments[j].Start) i = j; else break;
        return i;
    }

    public (XcpFrame pose, int segment) Sample(float frame)
    {
        int i = SegmentAt(frame);
        return (Segments[i].SampleAt(frame), i);
    }

    public float? SpeedAt(float frame)
    {
        if(frame < Start || frame > End) return null;
        float d = Fps / 60f;
        var (a, sa) = Sample(frame); var (b, sb) = Sample(frame + d);
        return sa != sb || CutFrames.Any(f => f > frame && f <= frame + d) ? null : Vector3.Distance(a.Pos, b.Pos) / (d / Fps);
    }
}
