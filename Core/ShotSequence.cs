using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>A run of nodes between two hard cuts; moves continuously, built from one <see cref="CameraPath"/>.</summary>
public sealed class ShotSegment
{
    public required IReadOnlyList<Keyframe> Keys { get; init; }
    /// <summary>First node frame.</summary>
    public int Start { get; init; }
    /// <summary>Last node frame plus its hold.</summary>
    public int End { get; init; }
    /// <summary>Null when the segment has a single node (static shot).</summary>
    public CameraPath? Path { get; init; }
    public int Fps { get; init; }

    public XcpFrame SampleAt(float frame)
    {
        if(Path == null)
        {
            var k = Keys[0];
            return new XcpFrame(k.Pos, Quaternion.Normalize(k.Rot), k.Fov);
        }
        float t = Math.Clamp((frame - Start) / Fps, 0f, Path.Duration);
        return Path.Sample(t);
    }
}

/// <summary>
/// One track's nodes split into segments at hard cuts (<see cref="Keyframe.CutIn"/>). Each segment gets its own
/// path, so curves, tangents and constant-speed pacing never leak across a cut. Between a segment's end and the
/// next cut the camera holds the segment's last pose.
/// </summary>
public sealed class ShotSequence
{
    public IReadOnlyList<ShotSegment> Segments { get; }
    public int Start => Segments[0].Start;
    public int End => Segments[^1].End;
    public int Fps { get; }

    /// <summary>Frames where a segment begins with a hard cut (all segments except the first).</summary>
    public IEnumerable<int> CutFrames => Segments.Skip(1).Select(s => s.Start);
    public float TotalLength => Segments.Sum(s => s.Path?.TotalLength ?? 0f);

    private ShotSequence(List<ShotSegment> segs, int fps) { Segments = segs; Fps = fps; }

    /// <summary>Builds the sequence for one track's nodes, or null when there are none.</summary>
    public static ShotSequence? Create(IEnumerable<Keyframe> keys, PathSettings settings, int fps)
    {
        var sorted = keys.OrderBy(k => k.Frame).ToList();
        if(sorted.Count == 0) return null;
        foreach(var k in sorted)
        {
            k.TimeSec = k.Frame / (float)fps;
            k.HoldSec = k.HoldFrames / (float)fps;
        }

        // split at hard cuts (the first node never cuts)
        var groups = new List<List<Keyframe>>();
        foreach(var k in sorted)
        {
            if(groups.Count == 0 || k.CutIn) groups.Add([]);
            groups[^1].Add(k);
        }

        var segs = new List<ShotSegment>(groups.Count);
        for(int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            int start = g[0].Frame;
            int end = g.Max(k => k.Frame + k.HoldFrames);

            // ease only at the track's real start / end, not at cuts (a cut usually happens mid-motion)
            var s = settings.Clone();
            bool first = i == 0, last = i == groups.Count - 1;
            s.EaseStart = settings.EaseStart && first;
            s.EaseEnd = settings.EaseEnd && last;
            if(!first) s.EaseInFraction = 0;
            if(!last) s.EaseOutFraction = 0;
            if(s.Mode == TimingMode.ConstantSpeed)
                s.Duration = MathF.Max(0.1f, (g[^1].Frame - start) / (float)fps);

            var path = g.Count >= 2 ? CameraPath.Create(g, s) : null;
            segs.Add(new ShotSegment { Keys = g, Start = start, End = end, Path = path, Fps = fps });
        }
        return new ShotSequence(segs, fps);
    }

    public int SegmentAt(float frame)
    {
        int idx = 0;
        for(int i = 1; i < Segments.Count; i++)
            if(frame >= Segments[i].Start) idx = i; else break;
        return idx;
    }

    public (XcpFrame pose, int segment) Sample(float frame)
    {
        int i = SegmentAt(frame);
        return (Segments[i].SampleAt(frame), i);
    }

    /// <summary>Speed (yalms/s) at a frame, or null outside the track.</summary>
    public float? SpeedAt(float frame)
    {
        if(frame < Start || frame > End) return null;
        float d = Fps / 60f;
        var (a, sa) = Sample(frame);
        var (b, sb) = Sample(frame + d);
        if(sa != sb) return null;
        return Vector3.Distance(a.Pos, b.Pos) / (d / Fps);
    }
}
