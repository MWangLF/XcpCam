using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>A key to write into Brio's Vivacity camera track. <see cref="Step"/> = hard cut (no interpolation into it).</summary>
public readonly record struct BakeKey(int Frame, XcpFrame Pose, bool Step);

/// <summary>A run of consecutive output frames coming from one track.</summary>
public readonly record struct OutputRun(int Start, int End, int Track);

/// <summary>
/// Combines all camera tracks into the final camera. At every frame the output is the highest track (lowest index)
/// that is active (not muted; or soloed when any track is soloed) and covers the frame. Changing track, or crossing
/// a hard cut inside a track, is a hard cut. Frames covered by no track hold the previous output; frames before the
/// first coverage use the earliest shot.
/// </summary>
public sealed class ShotComposer
{
    private readonly ShotSequence?[] _seq;   // per track index (null = no keys)
    private readonly bool[] _active;

    public int Fps { get; }
    public int FirstFrame { get; }
    public int LastFrame { get; }
    public float DurationSec => (LastFrame - FirstFrame) / (float)Fps;
    public float TotalLength { get; }
    /// <summary>Integer frames where the output shot changes (hard cuts).</summary>
    public IReadOnlyList<int> CutFrames { get; }
    public IReadOnlyList<OutputRun> Runs { get; }
    public int TrackCount => _seq.Length;

    public ShotSequence? SequenceOf(int track) => track >= 0 && track < _seq.Length ? _seq[track] : null;
    public bool IsActive(int track) => track >= 0 && track < _active.Length && _active[track];

    private ShotComposer(ShotSequence?[] seq, bool[] active, int fps)
    {
        _seq = seq; _active = active; Fps = fps;
        var act = Enumerable.Range(0, seq.Length).Where(i => active[i] && seq[i] != null).ToList();
        FirstFrame = act.Min(i => seq[i]!.Start);
        LastFrame = act.Max(i => seq[i]!.End);
        TotalLength = act.Sum(i => seq[i]!.TotalLength);

        var cuts = new List<int>();
        var runs = new List<OutputRun>();
        var prev = ShotAt(FirstFrame);
        int runStart = FirstFrame;
        for(int f = FirstFrame + 1; f <= LastFrame; f++)
        {
            var cur = ShotAt(f);
            if(cur != prev)
            {
                cuts.Add(f);
                if(cur.track != prev.track)
                {
                    runs.Add(new OutputRun(runStart, f - 1, prev.track));
                    runStart = f;
                }
            }
            prev = cur;
        }
        runs.Add(new OutputRun(runStart, LastFrame, prev.track));
        CutFrames = cuts;
        Runs = runs;
    }

    /// <summary>Builds the composer, or null when no active track has nodes.</summary>
    public static ShotComposer? Create(CamTimeline tl, PathSettings settings)
    {
        int n = tl.Tracks.Count;
        var seq = new ShotSequence?[n];
        for(int i = 0; i < n; i++) seq[i] = ShotSequence.Create(tl.Tracks[i].Keys, settings, tl.Fps);

        bool anySolo = Enumerable.Range(0, n).Any(i => tl.Tracks[i].Solo && seq[i] != null);
        var active = new bool[n];
        for(int i = 0; i < n; i++)
            active[i] = seq[i] != null && (anySolo ? tl.Tracks[i].Solo : !tl.Tracks[i].Muted);

        if(!active.Any(a => a)) return null;
        return new ShotComposer(seq, active, tl.Fps);
    }

    /// <summary>Highest active track covering the frame, or -1.</summary>
    public int TopTrackAt(float frame)
    {
        for(int i = 0; i < _seq.Length; i++)
            if(_active[i] && _seq[i] is { } s && frame >= s.Start && frame <= s.End) return i;
        return -1;
    }

    /// <summary>Output track at a frame (gaps resolve to the shot being held).</summary>
    public int TrackAt(float frame) => SampleEx(frame).track;

    private (int track, int segment) ShotAt(float frame)
    {
        var (_, t, s) = SampleEx(frame);
        return (t, s);
    }

    public XcpFrame Sample(float frame) => SampleEx(frame).pose;

    public (XcpFrame pose, int track, int segment) SampleEx(float frame)
    {
        if(frame < FirstFrame) frame = FirstFrame;
        if(frame > LastFrame) frame = LastFrame;
        int t = TopTrackAt(frame);
        if(t < 0)
        {
            // in a gap: hold what was shown at the last covered moment before it
            // (the latest end of any active track before this frame — that track was on top there,
            // otherwise the higher track would still be covering and this would not be a gap)
            float holdAt = float.NegativeInfinity;
            for(int i = 0; i < _seq.Length; i++)
                if(_active[i] && _seq[i] is { } s && s.End < frame && s.End > holdAt) holdAt = s.End;
            if(float.IsNegativeInfinity(holdAt)) holdAt = FirstFrame;
            t = TopTrackAt(holdAt);
            if(t < 0) return SampleEx(FirstFrame);
            var (hp, hs) = _seq[t]!.Sample(holdAt);
            return (hp, t, hs);
        }
        var (pose, seg) = _seq[t]!.Sample(frame);
        return (pose, t, seg);
    }

    /// <summary>Output speed (yalms/s) at a frame, null across a cut.</summary>
    public float? SpeedAt(float frame)
    {
        float d = Fps / 60f;
        var a = SampleEx(frame);
        var b = SampleEx(frame + d);
        if(a.track != b.track || a.segment != b.segment) return null;
        return Vector3.Distance(a.pose.Pos, b.pose.Pos) / (d / Fps);
    }

    /// <summary>
    /// Uniform frames for an .xcp (Brio plays .xcp at 30 fps and lerps between neighbours, so a cut shows as a
    /// single-frame blend; the samples either side of a cut are the outgoing and incoming poses).
    /// </summary>
    public List<XcpFrame> BakeXcp(float xcpFps = CameraPath.DefaultFps)
    {
        int count = Math.Max(2, (int)MathF.Round(DurationSec * xcpFps) + 1);
        var list = new List<XcpFrame>(count);
        Quaternion prev = default;
        for(int i = 0; i < count; i++)
        {
            float frame = i == count - 1 ? LastFrame : FirstFrame + i / xcpFps * Fps;
            var s = Sample(frame);
            var q = s.Rot;
            if(i > 0 && Quaternion.Dot(prev, q) < 0) q = Quaternion.Negate(q);
            prev = q;
            list.Add(s with { Rot = q });
        }
        return list;
    }

    /// <summary>
    /// Keys for a frame-based timeline running at <paramref name="targetFps"/> (Brio Vivacity): one key every
    /// <paramref name="step"/> frames, plus for every cut a key on the frame before the cut (outgoing pose) and a
    /// <see cref="BakeKey.Step"/> key on the cut frame (incoming pose), so the cut is instantaneous.
    /// Timeline frame 0 maps to target frame 0.
    /// </summary>
    public List<BakeKey> BakeForTimeline(int targetFps, int step)
    {
        step = Math.Max(1, step);
        double r = targetFps / (double)Fps;
        int b0 = (int)Math.Round(FirstFrame * r);
        int b1 = Math.Max(b0, (int)Math.Round(LastFrame * r));

        var poses = new XcpFrame[b1 - b0 + 1];
        var ids = new (int, int)[b1 - b0 + 1];
        for(int b = b0; b <= b1; b++)
        {
            var (p, t, s) = SampleEx((float)(b / r));
            poses[b - b0] = p;
            ids[b - b0] = (t, s);
        }

        var keys = new SortedDictionary<int, bool>(); // frame -> step
        for(int b = b0; b <= b1; b += step) keys[b] = false;
        keys[b1] = false;
        for(int b = b0 + 1; b <= b1; b++)
        {
            if(ids[b - b0] == ids[b - b0 - 1]) continue;
            if(!keys.ContainsKey(b - 1)) keys[b - 1] = false;
            keys[b] = true;
        }

        var result = new List<BakeKey>(keys.Count);
        foreach(var (frame, isStep) in keys)
            result.Add(new BakeKey(frame, poses[frame - b0], isStep));
        return result;
    }
}
