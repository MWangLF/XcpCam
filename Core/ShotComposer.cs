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
        var boundaries = new SortedSet<int>();
        foreach(int track in act)
        {
            var sequence = seq[track]!;
            foreach(var segment in sequence.Segments) boundaries.Add(segment.Start);
            if(sequence.End < LastFrame) boundaries.Add(sequence.End + 1);
            boundaries.UnionWith(sequence.CutFrames);
        }
        foreach(int f in boundaries.GetViewBetween(FirstFrame, LastFrame))
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

    /// <summary>Any active track has tracking constraints.</summary>
    public bool Tracking => Enumerable.Range(0, _seq.Length).Any(i => _active[i] && _seq[i]?.Tracking == true);

    /// <summary>Builds the composer, or null when no active track has nodes. Tracking constraints are only
    /// evaluated with a <paramref name="targets"/> source.</summary>
    public static ShotComposer? Create(CamTimeline tl, PathSettings settings, ITrackTargetSource? targets = null)
    {
        int n = tl.Tracks.Count;
        tl.ResolveConstraints();
        var seq = new ShotSequence?[n];
        for(int i = 0; i < n; i++) seq[i] = ShotSequence.Create(tl.Tracks[i], tl.Fps, targets);

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
    public int TrackAt(float frame) => ResolveTrack(frame).track;

    private (int track, int segment, int cuts) ShotAt(float frame)
    {
        var (track, at) = ResolveTrack(frame);
        var sequence = _seq[track]!;
        return (track, sequence.SegmentAt(at), sequence.CutsThrough(frame));
    }

    private (float step, float?[] samples)[]? _displaySpeeds;
    private float _displaySpeedPeak;
    public float DisplaySpeedPeak { get { EnsureDisplaySpeeds(); return _displaySpeedPeak; } }

    public float? DisplaySpeedAt(int track, float frame)
    {
        if(SequenceOf(track) is not { } seq || frame < seq.Start || frame >= seq.End) return null;
        EnsureDisplaySpeeds();
        var (step, samples) = _displaySpeeds![track];
        if(samples.Length == 0 || seq.CutsThrough(frame) != seq.CutsThrough(frame + Fps / 60f)) return null;
        return samples[Math.Clamp((int)((frame - seq.Start) / step), 0, samples.Length - 1)];
    }

    private void EnsureDisplaySpeeds()
    {
        if(_displaySpeeds != null) return;
        var speeds = new (float, float?[])[_seq.Length];
        float peak = .001f;
        for(int track = 0; track < _seq.Length; track++)
        {
            if(_seq[track] is not { } seq) continue;
            float step = MathF.Max(MathF.Max(1, Fps / 30f), (seq.End - seq.Start) / 1024f);
            var samples = new float?[Math.Min(1024, (int)MathF.Ceiling((seq.End - seq.Start) / step))];
            for(int i = 0; i < samples.Length; i++)
            {
                samples[i] = seq.SpeedAt(seq.Start + i * step);
                if(samples[i] is float speed) peak = Math.Max(peak, speed);
            }
            speeds[track] = (step, samples);
        }
        _displaySpeedPeak = peak;
        _displaySpeeds = speeds;
    }

    public XcpFrame Sample(float frame) => SampleEx(frame).pose;

    /// <param name="constrained">Apply tracking constraints (rotation / FoV); positions never depend on them.</param>
    public (XcpFrame pose, int track, int segment) SampleEx(float frame, bool constrained = true)
    {
        var r = SampleRaw(frame);
        return constrained && _seq[r.track] is { Tracking: true } seq ? (seq.Constrain(r.pose, r.at), r.track, r.segment) : (r.pose, r.track, r.segment);
    }

    private (XcpFrame pose, int track, int segment, float at) SampleRaw(float frame)
    {
        var (t, at) = ResolveTrack(frame);
        var (pose, segment) = _seq[t]!.Sample(at);
        return (pose, t, segment, at);
    }

    private (int track, float at) ResolveTrack(float frame)
    {
        frame = Math.Clamp(frame, FirstFrame, LastFrame);
        int track = TopTrackAt(frame);
        if(track >= 0) return (track, frame);
        // Gaps hold the exact last covered moment, including the highest track present at that boundary.
        float held = FirstFrame;
        for(int i = 0; i < _seq.Length; i++)
            if(_active[i] && _seq[i] is { } seq && seq.End < frame) held = Math.Max(held, seq.End);
        return (TopTrackAt(held), held);
    }

    /// <summary>Output speed (yalms/s) at a frame, null across a cut.</summary>
    public float? SpeedAt(float frame)
    {
        float d = Fps / 60f;
        var a = SampleEx(frame, false);
        var b = SampleEx(frame + d, false);
        if(a.track != b.track || a.segment != b.segment || CutFrames.Any(f => f > frame && f <= frame + d)) return null;
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
            float frame = i == count - 1 ? LastFrame : FirstFrame + (xcpFps == Fps ? i : (float)(i * (double)Fps / xcpFps));
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
        var ids = new (int track, int segment, int cuts)[b1 - b0 + 1];
        for(int b = b0; b <= b1; b++)
        {
            var (p, t, s) = SampleEx((float)(b / r));
            poses[b - b0] = p;
            ids[b - b0] = ShotAt((float)(b / r));
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
