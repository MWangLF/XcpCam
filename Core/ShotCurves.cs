using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

public enum CameraChannel { Position, Rotation, Fov }
public enum CurveEditorMode { Simple, Advanced }

/// <summary>One node-to-node motion. Curves describe bounded progress, not pose coordinates.</summary>
public sealed class MotionInterval
{
    public Guid Id = Guid.NewGuid();
    public Guid StartNodeId, EndNodeId;
    public int Start, End;
    public int HoldFrames;
    public bool Linked = true;
    public bool AutoTiming = true;
    internal Dictionary<CameraChannel, FCurve>? AutoSnapshot;
    public void MarkManual() { AutoTiming = false; AutoSnapshot = null; }
    public Dictionary<CameraChannel, FCurve> Channels = new();
    // Persist the channel-to-curve mapping explicitly so shared edits survive a round trip.
    internal sealed record ChannelTable(FCurve[] Curves, Dictionary<CameraChannel, int> Indices);
    internal ChannelTable ProjectChannels
    {
        get
        {
            var curves = Channels.Values.Distinct().ToArray();
            return new(curves, Channels.ToDictionary(p => p.Key, p => Array.IndexOf(curves, p.Value)));
        }
        set
        {
            if(value?.Curves == null || value.Indices == null || value.Curves.Length is < 1 or > 3 ||
                value.Indices.Count != 3 || value.Indices.Values.Any(i => (uint)i >= value.Curves.Length) ||
                value.Indices.Values.Distinct().Count() != value.Curves.Length)
                throw new System.IO.InvalidDataException(I18n.T("core.invalid_channel_map"));
            Channels = value.Indices.ToDictionary(p => p.Key, p => value.Curves[p.Value]);
        }
    }
    internal PosePath? Path;
    /// <summary>Leg length along a Follow Path target curve (overrides the node path distance for timing).</summary>
    internal double? FollowDistance;
    internal Dictionary<CameraChannel, (double start, double end)> Ranges = new();
    private readonly Dictionary<FCurve, (FCurve original, FCurve effective, int start, int end)> evaluationCache = new();
    // Holds can place a subdivision at fractional authored time. Keep the immutable timing
    // mapping until an actual curve/hold edit, rather than rounding that mapping to integer keys.
    internal MotionInterval? splitTiming;
    internal Dictionary<CameraChannel, FCurve>? splitCurves;
    internal Dictionary<CameraChannel, (double start, double end)>? splitValues;
    internal int splitHoldFrames;
    internal double splitTimeStart, splitTimeEnd;

    public MotionInterval() { }
    public MotionInterval(Guid startNodeId, Guid endNodeId, int start, int end)
    {
        StartNodeId = startNodeId; EndNodeId = endNodeId; Start = start; End = end;
        var curve = ProgressCurve.Linear(start, end);
        curve.Keys[0].NodeId = startNodeId; curve.Keys[^1].NodeId = endNodeId;
        foreach(var c in Enum.GetValues<CameraChannel>()) Channels[c] = curve;
    }

    public FCurve Curve(CameraChannel channel)
    {
        if(!Channels.TryGetValue(channel, out var curve))
        {
            curve = Linked && Channels.Count > 0 ? Channels.Values.First() : ProgressCurve.Linear(Start, End);
            Channels[channel] = curve;
        }
        return curve;
    }

    public void Unlink()
    {
        if(!Linked) return;
        foreach(var c in Enum.GetValues<CameraChannel>()) Channels[c] = Curve(c).Clone();
        Linked = false;
    }

    public void Link(CameraChannel chosen)
    {
        var curve = Curve(chosen);
        foreach(var c in Enum.GetValues<CameraChannel>()) Channels[c] = curve;
        Linked = true;
    }

    public MotionInterval Clone()
    {
        var copy = new MotionInterval { Id = Id, StartNodeId = StartNodeId, EndNodeId = EndNodeId,
            Start = Start, End = End, HoldFrames = HoldFrames, Linked = Linked, Path = Path, FollowDistance = FollowDistance,
            AutoTiming = AutoTiming, AutoSnapshot = AutoSnapshot?.ToDictionary(p => p.Key, p => p.Value.Clone()),
            Ranges = new(Ranges), splitTiming = splitTiming?.Clone(),
            splitCurves = splitCurves?.ToDictionary(p => p.Key, p => p.Value.Clone()),
            splitValues = splitValues == null ? null : new(splitValues), splitHoldFrames = splitHoldFrames,
            splitTimeStart = splitTimeStart, splitTimeEnd = splitTimeEnd };
        var clones = new Dictionary<FCurve, FCurve>();
        foreach(var pair in Channels)
        {
            if(!clones.TryGetValue(pair.Value, out var curve)) clones[pair.Value] = curve = pair.Value.Clone();
            copy.Channels[pair.Key] = curve;
        }
        return copy;
    }

    public void Validate()
    {
        if(End <= Start) throw new InvalidOperationException("Motion interval must have positive duration.");
        foreach(var c in Enum.GetValues<CameraChannel>())
        {
            var curve = Curve(c);
            if(curve.Keys.Any(k => k.Frame < Start || k.Frame > End || !double.IsFinite(k.Value)) ||
                curve.Keys.GroupBy(k => k.Frame).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Invalid progress curve keys.");
        }
        Constrain();
    }

    public void Constrain()
    {
        foreach(var c in Enum.GetValues<CameraChannel>()) _ = Curve(c);
        if(Linked) Link(CameraChannel.Position);
        foreach(var curve in Channels.Values.Distinct()) ProgressCurve.Constrain(curve, Start, End);
    }

    public double Evaluate(CameraChannel channel, double frame)
    {
        if(double.IsNaN(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        if(frame >= End) return 1;
        if(frame <= Start) return 0;
        if(splitTiming != null && splitCurves != null && splitValues != null)
        {
            bool unchanged = HoldFrames == splitHoldFrames && Enum.GetValues<CameraChannel>().All(c => ProgressCurve.Same(Curve(c), splitCurves[c]));
            if(unchanged)
            {
                var range = splitValues[channel];
                if(range.end - range.start <= 1e-14) return Math.Clamp((frame - Start) / ((double)End - Start), 0, 1);
                double sourceTime = splitTimeStart + (splitTimeEnd - splitTimeStart) * ((frame - Start) / ((double)End - Start));
                return Math.Clamp((splitTiming.Evaluate(channel, sourceTime) - range.start) / (range.end - range.start), 0, 1);
            }
            splitTiming = null; splitCurves = null; splitValues = null;
        }
        double release = Math.Min((double)End, (double)Start + Math.Max(0, HoldFrames));
        if(frame <= release) return 0;
        double time = Start + (frame - release) * ((double)End - Start) / (End - release);
        var curve = Curve(channel);
        if(!evaluationCache.TryGetValue(curve, out var cached) || cached.start != Start || cached.end != End || !ProgressCurve.Same(curve, cached.original))
        {
            var original = curve.Clone(); var effective = curve.Clone();
            ProgressCurve.Constrain(effective, Start, End);
            cached = (original, effective, Start, End); evaluationCache[curve] = cached;
        }
        return Math.Clamp(cached.effective.Evaluate(time), 0, 1);
    }

    /// <summary>Affine retiming with atomic collision/overflow validation. Curve identities are retained.</summary>
    public void Retime(int start, int end)
    {
        if(end <= start || End <= Start) throw new ArgumentOutOfRangeException(nameof(end));
        if(start == Start && end == End) return;
        bool keepSplit = splitTiming != null && splitCurves != null && HoldFrames == splitHoldFrames &&
            Enum.GetValues<CameraChannel>().All(c => ProgressCurve.Same(Curve(c), splitCurves[c]));
        double scale = ((double)end - start) / ((double)End - Start);
        var updates = new List<(FCurve curve, FCurve replacement)>();
        foreach(var curve in Channels.Values.Distinct())
        {
            var copy = curve.Clone(); var seen = new HashSet<int>();
            foreach(var key in copy.Keys)
            {
                key.Frame = ProgressCurve.Round(start + ((double)key.Frame - Start) * scale);
                if(!seen.Add(key.Frame)) throw new InvalidOperationException("Retiming would create duplicate progress key frames.");
                double l = key.Left.X * scale, r = key.Right.X * scale;
                if(!double.IsFinite(l) || !double.IsFinite(r) || Math.Abs(l) > float.MaxValue || Math.Abs(r) > float.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(end));
                key.Left = new Vector2((float)l, key.Left.Y); key.Right = new Vector2((float)r, key.Right.Y);
            }
            updates.Add((curve, copy));
        }
        foreach(var (curve, copy) in updates)
            for(int i = 0; i < curve.Keys.Count; i++)
            {
                curve.Keys[i].Frame = copy.Keys[i].Frame;
                curve.Keys[i].Left = copy.Keys[i].Left; curve.Keys[i].Right = copy.Keys[i].Right;
            }
        Start = start; End = end;
        if(AutoTiming && AutoSnapshot != null) AutoSnapshot = Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        if(keepSplit) splitCurves = Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        else { splitTiming = null; splitCurves = null; splitValues = null; }
    }

    internal void RefreshSplitAfterRescale(double factor)
    {
        if(splitTiming == null || splitCurves == null) return;
        foreach(var curve in splitCurves.Values) curve.Rescale(factor);
        if(!Enum.GetValues<CameraChannel>().All(c => ProgressCurve.Same(Curve(c), splitCurves[c])))
        { splitTiming = null; splitCurves = null; splitValues = null; return; }
        splitHoldFrames = HoldFrames;
    }

    internal XcpFrame Geometry(double position, double rotation, double fov)
    {
        if(Path == null) throw new InvalidOperationException("Geometry has not been synchronized.");
        return Path.Sample(Parameter(CameraChannel.Position, position), Parameter(CameraChannel.Rotation, rotation), Parameter(CameraChannel.Fov, fov));
    }

    internal double Parameter(CameraChannel channel, double progress)
    {
        if(Path == null) throw new InvalidOperationException("Geometry has not been synchronized.");
        var range = Ranges[channel];
        return Path.Parameter(channel, range.start, range.end, progress);
    }
}

/// <summary>Fixed node-defined target-local geometry with independent per-interval progress timing.</summary>
public sealed class ShotCurves
{
    public Guid Id = Guid.NewGuid();
    public Guid AnchorNodeId;
    public int Start, End;
    public string Name = I18n.T("core.shot_default");
    public List<MotionInterval> Intervals = [];
    internal Dictionary<Guid, CurveInterpolation> CutRestore = new();
    internal Dictionary<Guid, bool> CutObserved = new();
    public HashSet<Guid> SeededNodes = [];
    public List<(int start, int end)> HoldRegions = [];
    public IReadOnlyList<(int start, int end)> Holds => HoldRegions;
    private List<Keyframe> nodes = [];
    private Dictionary<Guid, XcpFrame> poses = new();
    internal void ValidateProjectNodes(HashSet<Guid> owned)
    {
        if(poses == null || CutObserved == null || CutRestore == null || ManualFactors == null || Space.DirtyTangents == null ||
            nodes.Any(n => !owned.Contains(n.Id)) || nodes.Select(n => n.Id).Distinct().Count() != nodes.Count ||
            !SeededNodes.SetEquals(nodes.Select(n => n.Id)))
            throw new System.IO.InvalidDataException(I18n.T("core.invalid_shot_internal"));
    }

    internal void RebindProjectNodes(Dictionary<Guid, Keyframe> keys)
    {
        for(int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            // A cloned shot can carry an older bound-node snapshot. Keep it until the next Ensure.
            if(keys.TryGetValue(n.Id, out var k) && n.Frame == k.Frame && n.HoldFrames == k.HoldFrames &&
                n.CutIn == k.CutIn && n.CutAuto == k.CutAuto && n.LastGap == k.LastGap && n.TimeSec == k.TimeSec &&
                n.Pos == k.Pos && n.Rot == k.Rot && n.Fov == k.Fov && n.Tangent == k.Tangent && n.HoldSec == k.HoldSec &&
                n.SelfCheckError == k.SelfCheckError && n.TargetName == k.TargetName && n.BasePos == k.BasePos && n.BaseRot == k.BaseRot)
                nodes[i] = k;
        }
    }
    public SpatialCurves Space = new();
    /// <summary>
    /// Advanced-mode position timing: frame → length factor (0..1) along this shot's path, keyed at every node
    /// (Blender Follow Path "fixed position" offset factor animated per node). For the node path the key
    /// values are the nodes' own positions on the path, so only the timing between nodes is edited.
    /// </summary>
    public FCurve PathProgress = new();
    /// <summary>Location offset X/Y/Z on top of the path point, world axes (Blender: the object's own location under Follow Path).</summary>
    public FCurve[] Offset = [new(), new(), new()];
    /// <summary>Follow Path target, set by <see cref="CurveModel.Ensure"/> from the track constraint.</summary>
    internal FollowBinding? Follow;
    /// <summary>Curve the path-progress values were initialised for; a new target re-projects the nodes.</summary>
    private Guid followCurve;
    public bool IsFollowing => Follow != null;
    internal void ClearFollow() { Follow = null; followCurve = Guid.Empty; }
    public CurveEditorMode EditorMode { get; private set; }
    private bool simpleVisited;
    private bool neutralSimple;
    private bool parameterSimple;

    public void SetEditorMode(CurveEditorMode mode)
    {
        if(mode == EditorMode) return;
        if(mode == CurveEditorMode.Simple && !simpleVisited)
        {
            foreach(var interval in Intervals)
            {
                if(interval.AutoTiming)
                {
                    if(interval.Linked)
                    {
                        var curve = ProgressCurve.Linear(interval.Start, interval.End);
                        curve.Keys[0].NodeId = interval.StartNodeId; curve.Keys[^1].NodeId = interval.EndNodeId;
                        foreach(var c in Enum.GetValues<CameraChannel>()) interval.Channels[c] = curve;
                    }
                    else foreach(var c in Enum.GetValues<CameraChannel>())
                    {
                        var curve = ProgressCurve.Linear(interval.Start, interval.End);
                        curve.Keys[0].NodeId = interval.StartNodeId; curve.Keys[^1].NodeId = interval.EndNodeId;
                        interval.Channels[c] = curve;
                    }
                    interval.MarkManual();
                }
            }
            neutralSimple = true; parameterSimple = true; simpleVisited = true;
        }
        EditorMode = mode;
    }

    public void ActivateSimpleTiming() { neutralSimple = false; simpleVisited = true; }

    private double DirectParameter(CameraChannel channel, double frame, MotionInterval interval)
    {
        var range = interval.Ranges[channel];
        double release = Math.Min(interval.End, (double)interval.Start + interval.HoldFrames);
        double progress = frame >= interval.End ? 1 : frame <= release ? 0 : (frame - release) / (interval.End - release);
        return range.start + (range.end - range.start) * Math.Clamp(progress, 0, 1);
    }
    private PosePath? spatialPath;
    private long spatialRevision = -1;

    public bool CanEditAt(int frame) => frame >= Start && frame <= End && !HoldRegions.Any(h => frame > h.start && frame < h.end);

    public ShotCurves Clone() => new()
    {
        Id = Id, AnchorNodeId = AnchorNodeId, Start = Start, End = End, Name = Name,
        EditorMode = EditorMode, simpleVisited = simpleVisited, neutralSimple = neutralSimple, parameterSimple = parameterSimple,
        Intervals = Intervals.Select(i => i.Clone()).ToList(), SeededNodes = new(SeededNodes),
        CutRestore = new(CutRestore), CutObserved = new(CutObserved),
        HoldRegions = [.. HoldRegions], nodes = nodes.Select(n => n.Clone()).ToList(), poses = new(poses), Space = Space.Clone(),
        PathProgress = PathProgress.Clone(), Offset = Offset.Select(c => c.Clone()).ToArray(),
        Follow = Follow, followCurve = followCurve, ManualFactors = [.. ManualFactors],
    };

    private CurveKey? ProgressKey(Guid node) => PathProgress.Keys.FirstOrDefault(k => k.NodeId == node);

    /// <summary>Length factor along the Follow Path target at a frame.</summary>
    private double FollowFactor(MotionInterval interval, double frame)
    {
        // a curve anchored to the timeline owns the timing: the camera is on every anchored point at its frame
        if(Follow?.Constraint.EditorTiming != true && TimeAnchorPoints() is { Count: >= 2 } anchors) return AnchorFactorAt(anchors, frame);
        double a = ProgressKey(interval.StartNodeId)?.Value ?? 0, b = ProgressKey(interval.EndNodeId)?.Value ?? a;
        if(EditorMode == CurveEditorMode.Advanced || neutralSimple) return PathProgress.Evaluate(TimingFrame(interval, frame));
        return a + (b - a) * interval.Evaluate(CameraChannel.Position, frame);
    }

    private XcpFrame FollowPose(double factor, Vector3 offset, XcpFrame own) =>
        Follow!.Constraint.Apply(Follow.Path.Sample(factor), offset, own.Rot, own.Fov);

    /// <summary>Time inside an interval with its node hold removed (the hold freezes every channel).</summary>
    private static double TimingFrame(MotionInterval interval, double frame)
    {
        double release = Math.Min(interval.End, (double)interval.Start + Math.Max(0, interval.HoldFrames));
        if(frame <= release) return interval.Start;
        if(frame >= interval.End) return interval.End;
        return interval.Start + (frame - release) * ((double)interval.End - interval.Start) / (interval.End - release);
    }

    public Vector3 OffsetAt(double frame) => Offset[0].Keys.Count == 0 ? Vector3.Zero :
        new Vector3((float)Offset[0].EvaluateOrdered(frame), (float)Offset[1].EvaluateOrdered(frame), (float)Offset[2].EvaluateOrdered(frame));

    private Vector3 OffsetAt(MotionInterval? interval, double frame) => OffsetAt(interval == null ? frame : TimingFrame(interval, frame));

    /// <summary>
    /// Advanced position parameter: the path progress (a length factor) is mapped by arc length inside the
    /// current node-to-node leg, so cuts (zero-length constant legs) and holds stay exact. Progress that
    /// overshoots the leg falls back to the whole-path arc-length mapping.
    /// </summary>
    private double AdvancedPosition(MotionInterval interval, double frame)
    {
        if(PathProgress.Keys.Count == 0 || interval.Path == null) return DirectParameter(CameraChannel.Position, frame, interval);
        double a = PathProgress.EvaluateOrdered(interval.Start), b = PathProgress.EvaluateOrdered(interval.End);
        double u = PathProgress.EvaluateOrdered(TimingFrame(interval, frame));
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        if(hi - lo <= 1e-12) return DirectParameter(CameraChannel.Position, frame, interval);
        var range = interval.Ranges[CameraChannel.Position];
        if(u >= lo - 1e-12 && u <= hi + 1e-12)
            return interval.Path.Parameter(CameraChannel.Position, range.start, range.end, (u - a) / (b - a));
        return interval.Path.Parameter(CameraChannel.Position, Space.Start, Space.End, Math.Clamp(u, 0, 1));
    }

    /// <summary>
    /// Keeps one path-progress key and one offset key per node at the node frame. Progress values follow the
    /// nodes' positions on the path (length factors); new offset keys take the current offset curve value, so
    /// inserting a node does not change the motion. Automatic handles refit like Blender F-curves
    /// (Auto Clamped, flat at the first and last key).
    /// </summary>
    /// <summary>
    /// Follow Path factors: the first node is 0 %, the last 100 %; nodes set by hand (dragged along the curve,
    /// typed in) keep their value, the others are spread by frame between their neighbouring fixed nodes.
    /// Speed between nodes is then shaped by the timing curves (simple editor) as usual.
    /// </summary>
    /// <remarks>
    /// Curve points anchored to the timeline add (frame, factor) references: with two or more of them every
    /// node's factor comes from them (extrapolated outside, the first/last node are no longer 0 % / 100 %);
    /// a single one acts like a hand-set node.
    /// </remarks>
    private double[] AutoFactors()
    {
        int n = nodes.Count;
        var result = new double[n];
        if(n == 0) return result;
        var timeAnchors = Follow?.Constraint.EditorTiming == true ? [] : TimeAnchorPoints();
        if(timeAnchors.Count >= 2)
        {
            for(int i = 0; i < n; i++) result[i] = AnchorFactorAt(timeAnchors, nodes[i].Frame);
            return result;
        }
        var fixedAt = new SortedDictionary<double, double>();
        for(int i = 0; i < n; i++)
            if(ManualFactors.Contains(nodes[i].Id) && ProgressKey(nodes[i].Id) is { } key) fixedAt[nodes[i].Frame] = key.Value;
        if(!fixedAt.ContainsKey(nodes[0].Frame)) fixedAt[nodes[0].Frame] = 0;
        if(n > 1 && !fixedAt.ContainsKey(nodes[^1].Frame)) fixedAt[nodes[^1].Frame] = 1;
        foreach(var (frame, factor) in timeAnchors)
        {
            // a closed curve passes the point once per turn: take the turn the default spread is on at that frame
            double value = factor;
            if(Follow!.Path.Cyclic)
                value += Math.Round(Interpolate(fixedAt.Select(p => (p.Key, p.Value)).ToList(), frame, false) - factor);
            fixedAt[frame] = value;
        }
        var points = fixedAt.Select(p => (p.Key, p.Value)).ToList();
        for(int i = 0; i < n; i++) result[i] = Interpolate(points, nodes[i].Frame, false);
        return result;
    }

    /// <summary>Piecewise linear through (frame, value) points sorted by frame; outside: extrapolate or hold.</summary>
    private static double Interpolate(List<(double frame, double value)> points, double frame, bool extrapolate)
    {
        if(points.Count == 1) return points[0].value;
        int seg = 0;
        while(seg < points.Count - 2 && frame > points[seg + 1].frame) seg++;
        var (f0, v0) = points[seg]; var (f1, v1) = points[seg + 1];
        if(!extrapolate && frame <= f0) return v0;
        if(!extrapolate && frame >= points[^1].frame) return points[^1].value;
        return f1 > f0 ? v0 + (v1 - v0) * (frame - f0) / (f1 - f0) : v0;
    }

    /// <summary>
    /// Curve points anchored to the timeline as (frame, length factor), sorted by frame, one per frame. On a
    /// closed curve the camera travels forward (as the default 0 → 100 % does): each later anchor is the next
    /// forward continuation of the previous one, and the loop's return to the start point
    /// (<see cref="FollowPathConstraint.LoopEndFrame"/>) is one more anchor, at 100 %.
    /// </summary>
    internal List<(double frame, double factor)> TimeAnchorPoints() => Anchors().points;

    private (List<(double frame, double factor)> points, double[] tangents) Anchors()
    {
        if(Follow == null || Follow.Constraint.TimeAnchors.Count == 0) return ([], []);
        // cached: the composer samples this for every frame
        var c = Follow.Constraint;
        int signature = HashCode.Combine(c.TimeAnchors.Count, c.LoopEndFrame, Follow.Path,
            nodes.Count > 0 ? nodes[0].Frame : 0, nodes.Count > 0 ? nodes[^1].Frame : 0);
        foreach(var (id, f) in c.TimeAnchors) signature = HashCode.Combine(signature, id, f);
        if(anchorCache is { } hit && hit.signature == signature && ReferenceEquals(hit.follow, Follow)) return (hit.points, hit.tangents);

        var result = new List<(double frame, double factor)>();
        var points = Follow.Path.Spline.Points;
        var factors = Follow.Path.PointFactors();
        for(int i = 0; i < points.Count; i++)
            if(c.TimeAnchors.TryGetValue(points[i].Id, out double frame)) result.Add((frame, factors[i]));
        if(Follow.Path.Cyclic && c.LoopEndFrame is double end && points.Count > 0 && c.TimeAnchors.ContainsKey(points[0].Id))
            result.Add((end, factors[0]));
        result.Sort((a, b) => a.frame.CompareTo(b.frame));
        // two points dragged onto one frame: the first one keeps it
        for(int i = result.Count - 1; i > 0; i--) if(result[i].frame <= result[i - 1].frame) result.RemoveAt(i);
        if(Follow.Path.Cyclic)
        {
            for(int i = 1; i < result.Count; i++)
            {
                double f = result[i].factor, prev = result[i - 1].factor;
                double step = f - prev - Math.Floor(f - prev); // forward distance, 0..1
                if(step < 1e-9) step = 1; // the same point again (P1 and the loop end): one full turn
                result[i] = (result[i].frame, prev + step);
            }
            if(result.Count >= 2) result = ExtendLoop(result, factors);
        }
        var tangents = MonotoneTangents(result);
        anchorCache = (Follow, signature, result, tangents);
        return (result, tangents);
    }
    private (FollowBinding follow, int signature, List<(double frame, double factor)> points, double[] tangents)? anchorCache;

    /// <summary>
    /// Closed curve: beyond the anchored frames the camera keeps looping with the anchored rhythm. Every arc of
    /// the loop (Pk → Pk+1) takes the duration it has between the anchors (the latest occurrence ahead, the
    /// earliest one behind; arcs never anchored use the average speed), and those durations repeat turn after
    /// turn as extra anchors until the camera nodes' range is covered. Retiming P2 / P3 therefore also retimes
    /// P2 / P3 in every later turn.
    /// </summary>
    private List<(double frame, double factor)> ExtendLoop(List<(double frame, double factor)> anchors, double[] u)
    {
        int n = u.Length;
        if(n < 2 || nodes.Count == 0) return anchors;
        static double Frac(double x) => x - Math.Floor(x);
        var length = new double[n];
        for(int k = 0; k < n; k++) length[k] = Frac(u[(k + 1) % n] - u[k]);
        if(length.Any(l => l < 1e-9) || Math.Abs(length.Sum() - 1) > 1e-6) return anchors; // points not in loop order
        int PointAt(double factor)
        {
            for(int k = 0; k < n; k++) { double d = Frac(factor - u[k]); if(d < 1e-6 || d > 1 - 1e-6) return k; }
            return -1;
        }

        // arc durations from consecutive anchors (each pair spans whole arcs: anchors sit on control points)
        var ahead = new double?[n]; var behind = new double?[n];
        double totalFrames = 0, totalFactor = 0;
        for(int i = 0; i + 1 < anchors.Count; i++)
        {
            var (fa, va) = anchors[i]; var (fb, vb) = anchors[i + 1];
            int k = PointAt(va);
            if(k < 0 || vb <= va) return anchors;
            double speed = (vb - va) / (fb - fa);
            totalFrames += fb - fa; totalFactor += vb - va;
            for(double v = va; v < vb - 1e-9; v += length[k], k = (k + 1) % n)
            {
                ahead[k] = length[k] / speed; // later pairs win ahead
                behind[k] ??= length[k] / speed; // the first pair wins behind
            }
        }
        double average = totalFrames / totalFactor;
        double Duration(double?[] d, int k) => d[k] ?? length[k] * average;

        double lo = nodes[0].Frame, hi = nodes[^1].Frame;
        var result = new List<(double frame, double factor)>(anchors);
        var (frame, factor) = anchors[^1];
        for(int k = PointAt(factor), guard = 0; k >= 0 && guard < 4096; guard++)
        {
            bool covered = frame > hi; // one extra arc supplies the tangent beyond the sampled range
            frame += Duration(ahead, k); factor += length[k]; k = (k + 1) % n;
            result.Add((frame, factor));
            if(covered) break;
        }
        (frame, factor) = anchors[0];
        var before = new List<(double frame, double factor)>();
        for(int k = PointAt(factor), guard = 0; k >= 0 && guard < 4096; guard++)
        {
            bool covered = frame < lo;
            k = (k + n - 1) % n;
            frame -= Duration(behind, k); factor -= length[k];
            before.Add((frame, factor));
            if(covered) break;
        }
        before.Reverse();
        before.AddRange(result);
        return before;
    }

    /// <summary>Fritsch–Carlson tangents: a C1 cubic through every anchor that never overshoots between them.</summary>
    private static double[] MonotoneTangents(List<(double frame, double factor)> p)
    {
        int n = p.Count;
        var m = new double[n];
        if(n < 2) return m;
        var d = new double[n - 1];
        for(int i = 0; i + 1 < n; i++) d[i] = (p[i + 1].factor - p[i].factor) / (p[i + 1].frame - p[i].frame);
        m[0] = d[0]; m[n - 1] = d[n - 2];
        for(int i = 1; i + 1 < n; i++)
        {
            if(d[i - 1] * d[i] <= 0) { m[i] = 0; continue; }
            double h0 = p[i].frame - p[i - 1].frame, h1 = p[i + 1].frame - p[i].frame;
            m[i] = 3 * (h0 + h1) / ((2 * h1 + h0) / d[i - 1] + (h1 + 2 * h0) / d[i]);
        }
        return m;
    }

    private static double Hermite((double frame, double factor) a, (double frame, double factor) b, double ma, double mb, double x)
    {
        double h = b.frame - a.frame, t = (x - a.frame) / h, t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * a.factor + (t3 - 2 * t2 + t) * h * ma + (-2 * t3 + 3 * t2) * b.factor + (t3 - t2) * h * mb;
    }

    /// <summary>True when two or more curve points anchored to the timeline drive the path progress.</summary>
    public bool UsesTimeAnchors => Follow?.Constraint.EditorTiming == true || TimeAnchorPoints().Count >= 2;

    /// <summary>
    /// Path progress of a timeline-anchored curve at a frame: exactly each anchor's factor at its frame, smooth
    /// and without overshoot in between. Closed curves repeat the anchored arc durations outside; open curves
    /// continue at the end slopes and stop at their ends.
    /// </summary>
    private double AnchorFactorAt(List<(double frame, double factor)> anchors, double frame)
    {
        var m = Anchors().tangents;
        if(m.Length != anchors.Count) m = MonotoneTangents(anchors);
        double f;
        if(frame <= anchors[0].frame) f = anchors[0].factor + m[0] * (frame - anchors[0].frame);
        else if(frame >= anchors[^1].frame) f = anchors[^1].factor + m[^1] * (frame - anchors[^1].frame);
        else
        {
            int k = 0;
            while(frame > anchors[k + 1].frame) k++;
            f = Hermite(anchors[k], anchors[k + 1], m[k], m[k + 1], frame);
        }
        return Follow!.Path.Cyclic ? f : Math.Clamp(f, 0, 1);
    }

    /// <summary>Frame at which the timeline anchors reach a length factor (inverse of the anchor mapping).
    /// On a closed curve the factor is reached once per turn: the frame closest to <paramref name="near"/> wins
    /// (without it, the turn closest to the anchored range).</summary>
    public double? AnchorFrameOf(double factor, double? near = null)
    {
        if(Follow?.Constraint.EditorTiming == true) return EditorFrameOf(factor, near);
        var (anchors, m) = Anchors();
        if(anchors.Count < 2 || !double.IsFinite(factor)) return null;
        if(!Follow!.Path.Cyclic) return AnchorFrameOf(anchors, m, factor);
        var frames = Follow.Constraint.TimeAnchors.Values.Select(f => (double)f).ToList();
        if(Follow.Constraint.LoopEndFrame is double end) frames.Add(end);
        double mid = near is double at && double.IsFinite(at) ? AnchorFactorAt(anchors, at) :
            (AnchorFactorAt(anchors, frames.Min()) + AnchorFactorAt(anchors, frames.Max())) / 2;
        double centre = factor + Math.Round(mid - factor);
        if(near is not double target || !double.IsFinite(target)) return AnchorFrameOf(anchors, m, centre);
        double? best = null;
        for(int k = -2; k <= 2; k++)
            if(AnchorFrameOf(anchors, m, centre + k) is double f && (best == null || Math.Abs(f - target) < Math.Abs(best.Value - target))) best = f;
        return best;
    }

    /// <summary>Finds the requested node position on the current timing before editing that node's value.
    /// Editor timing must retain the requested factor after the node moves; anchor timing derives it anew.</summary>
    public double? SetAnchoredNodeFactor(Keyframe node, double factor)
    {
        if(!UsesTimeAnchors) return null;
        double? frame = AnchorFrameOf(factor, node.Frame);
        if(frame != null && Follow?.Constraint.EditorTiming == true) SetNodeFactor(node, factor);
        return frame;
    }

    private static double? AnchorFrameOf(List<(double frame, double factor)> anchors, double[] m, double factor)
    {
        // the first anchor segment containing the factor (each segment is monotone), else extrapolate beyond the end
        for(int i = 0; i + 1 < anchors.Count; i++)
        {
            var a = anchors[i]; var b = anchors[i + 1];
            if(Math.Abs(b.factor - a.factor) < 1e-12 || (factor - a.factor) * (factor - b.factor) > 0) continue;
            double lo = a.frame, hi = b.frame; bool rising = b.factor > a.factor;
            for(int it = 0; it < 60; it++)
            {
                double x = (lo + hi) / 2;
                if(Hermite(a, b, m[i], m[i + 1], x) < factor == rising) lo = x; else hi = x;
            }
            return (lo + hi) / 2;
        }
        double direction = Math.Sign(anchors[^1].factor - anchors[0].factor);
        bool beforeStart = (factor - anchors[0].factor) * direction < 0;
        var (end, slope) = beforeStart ? (anchors[0], m[0]) : (anchors[^1], m[^1]);
        if(Math.Abs(slope) < 1e-12) return end.frame;
        return end.frame + (factor - end.factor) / slope;
    }

    private double CrossingFrame(double t0, double v0, double t1, double v1, double factor)
    {
        if(Math.Abs(factor - v0) < 1e-12 || Math.Abs(v1 - v0) < 1e-12) return t0;
        if(Math.Abs(factor - v1) < 1e-12) return t1;
        double left = t0, right = t1;
        for(int it = 0; it < 40; it++)
        {
            double mid = (left + right) / 2;
            if((FollowFactorAt(mid)!.Value < factor) == (v1 > v0)) left = mid; else right = mid;
        }
        return (left + right) / 2;
    }

    private double? EditorFrameOf(double factor, double? near)
    {
        if(!double.IsFinite(factor) || nodes.Count < 2 || Follow == null) return null;
        double target = near is double n && double.IsFinite(n) ? n : Follow.Constraint.TimeAnchors.Count > 0
            ? (Follow.Constraint.TimeAnchors.Values.Min() + Follow.Constraint.TimeAnchors.Values.Max()) / 2 : nodes[0].Frame;
        double? best = null;
        int start = nodes[0].Frame, end = nodes[^1].Frame, step = Math.Max(1, (end - start) / 2000);
        double t0 = start, v0 = FollowFactorAt(start)!.Value;
        for(long time = (long)start + step; t0 < end; time += step)
        {
            double t1 = Math.Min(time, end), v1 = FollowFactorAt(t1)!.Value;
            double lo = Math.Min(v0, v1), hi = Math.Max(v0, v1);
            double k0 = Follow.Path.Cyclic ? Math.Ceiling(lo - factor - 1e-9) : 0;
            double k1 = Follow.Path.Cyclic ? Math.Floor(hi - factor + 1e-9) : 0;
            for(double k = k0; k <= k1; k++)
            {
                double value = factor + k;
                if(value < lo - 1e-9 || value > hi + 1e-9) continue;
                double frame = CrossingFrame(t0, v0, t1, v1, value);
                if(Math.Abs(FollowFactorAt(frame)!.Value - value) > 1e-6) continue;
                if(best == null || Math.Abs(frame - target) < Math.Abs(best.Value - target)) best = frame;
            }
            t0 = t1; v0 = v1;
        }
        return best;
    }

    /// <summary>Follow Path length factor at a frame (null when not following or without nodes).</summary>
    public double? FollowFactorAt(double frame)
    {
        if(Follow == null || nodes.Count == 0) return null;
        EnsureGeometry();
        var interval = At(frame);
        if(interval == null) return ProgressKey(nodes[0].Id)?.Value;
        return FollowFactor(interval, frame);
    }

    /// <summary>
    /// When every curve control point is on the timeline: timeline-anchored points at their anchor frame,
    /// camera-anchored points at the first frame the camera reaches them (null when it never does).
    /// Needs a followed curve and at least two camera nodes.
    /// </summary>
    public List<CurvePointTime> CurvePointTimes(bool useAnchors = true)
    {
        var result = new List<CurvePointTime>();
        if(Follow == null || nodes.Count < 2) return result;
        var points = Follow.Path.Spline.Points;
        var factors = Follow.Path.PointFactors();
        int a = nodes[0].Frame, b = nodes[^1].Frame;
        int step = Math.Max(1, (b - a) / 2000);
        var samples = new List<(double frame, double factor)>();
        for(long t = a; t < b; t += step) samples.Add((t, FollowFactorAt(t) ?? 0));
        samples.Add((b, FollowFactorAt(b) ?? 0));
        bool cyclic = Follow.Path.Cyclic;
        for(int i = 0; i < points.Count; i++)
        {
            // every frame the camera passes the point (a closed curve passes its start at 0 % and 100 %)
            var passes = new List<double>();
            for(int s = 0; s + 1 < samples.Count; s++)
            {
                var (t0, v0) = samples[s]; var (t1, v1) = samples[s + 1];
                double lo = Math.Min(v0, v1), hi = Math.Max(v0, v1);
                // a cyclic curve passes the point at every whole turn: factor + k
                double k0 = cyclic ? Math.Ceiling(lo - factors[i] - 1e-9) : 0, k1 = cyclic ? Math.Floor(hi - factors[i] + 1e-9) : 0;
                for(double k = k0; k <= k1; k++)
                {
                    double x = factors[i] + k;
                    if(x < lo - 1e-9 || x > hi + 1e-9) continue;
                    if(s > 0 && Math.Abs(x - v0) < 1e-9 && Math.Abs(samples[s - 1].factor - x) < 1e-9) continue;
                    double frame = CrossingFrame(t0, v0, t1, v1, x);
                    // A constant segment or cut can jump over a point without actually visiting it.
                    if(Math.Abs(FollowFactorAt(frame)!.Value - x) > 1e-6) continue;
                    if(passes.Count == 0 || frame - passes[^1] > 1e-6) passes.Add(frame);
                }
            }
            if(useAnchors && Follow.Constraint.TimeAnchors.TryGetValue(points[i].Id, out double anchored))
            {
                result.Add(new CurvePointTime(i, points[i].Id, factors[i], anchored, true));
                double? loopEnd = i == 0 && cyclic ? Follow.Constraint.LoopEndFrame : null;
                if(loopEnd is double end) result.Add(new CurvePointTime(i, points[i].Id, factors[i], end, true, true));
                foreach(double pass in passes.Where(p => Math.Abs(p - anchored) > 1 && (loopEnd is not double e || Math.Abs(p - e) > 1)))
                    result.Add(new CurvePointTime(i, points[i].Id, factors[i], pass, false, true));
                continue;
            }
            result.Add(new CurvePointTime(i, points[i].Id, factors[i], passes.Count > 0 ? passes[0] : null, false));
            foreach(double pass in passes.Skip(1))
                result.Add(new CurvePointTime(i, points[i].Id, factors[i], pass, false, true));
        }
        return result;
    }

    /// <summary>Following nodes whose factor was set by hand (kept instead of the automatic time distribution).</summary>
    internal HashSet<Guid> ManualFactors = [];

    /// <summary>Sets a following node's position on the curve (0..1, cyclic curves wrap) and keeps it.</summary>
    public bool SetNodeFactor(Keyframe node, double factor)
    {
        if(Follow == null || ProgressKey(node.Id) is not { } key || !double.IsFinite(factor)) return false;
        key.Value = Follow.Path.Cyclic ? factor : Math.Clamp(factor, 0, 1);
        ManualFactors.Add(node.Id);
        Space.Touch();
        return true;
    }

    /// <summary>Marks a factor edited elsewhere (advanced editor) as hand-set.</summary>
    public void MarkFactorManual(Guid node) { if(Follow != null) ManualFactors.Add(node); }

    /// <summary>Only active path timing participates: selection, view changes, rotation and offsets do not
    /// switch an anchored path to editor timing. Linked channels naturally share the position curve.</summary>
    internal int FollowTimingSignature()
    {
        var hash = new HashCode();
        hash.Add(EditorMode); hash.Add(neutralSimple);
        foreach(var node in nodes) { hash.Add(node.Id); hash.Add(node.Frame); hash.Add(node.HoldFrames); }
        bool direct = EditorMode == CurveEditorMode.Advanced || neutralSimple;
        void Add(FCurve curve)
        {
            foreach(var key in curve.Keys)
            {
                hash.Add(key.Frame); hash.Add(key.Value); hash.Add(key.Interpolation);
                hash.Add(key.Left); hash.Add(key.Right); hash.Add(key.Mode);
            }
        }
        if(direct) Add(PathProgress);
        else
        {
            foreach(var key in PathProgress.Keys) { hash.Add(key.Frame); hash.Add(key.Value); }
            foreach(var interval in Intervals)
            {
                hash.Add(interval.Start); hash.Add(interval.End); hash.Add(interval.HoldFrames);
                Add(interval.Curve(CameraChannel.Position));
            }
        }
        return hash.ToHashCode();
    }

    private int? editorAnchorSignature;
    internal void UpdateEditorAnchors()
    {
        if(Follow?.Constraint is not { EditorTiming: true } constraint) { editorAnchorSignature = null; return; }
        var hash = new HashCode(); hash.Add(FollowTimingSignature());
        foreach(double factor in Follow.Path.PointFactors()) hash.Add(factor);
        int signature = hash.ToHashCode();
        if(editorAnchorSignature == signature) return;
        var old = constraint.TimeAnchors;
        var times = CurvePointTimes(useAnchors: false).Where(t => t.Frame.HasValue).ToList();
        var anchors = new Dictionary<Guid, double>();
        foreach(var group in times.GroupBy(t => t.Id))
        {
            double near = old.TryGetValue(group.Key, out double frame) ? frame : nodes[0].Frame;
            anchors[group.Key] = group.OrderBy(t => Math.Abs(t.Frame!.Value - near)).First().Frame!.Value;
        }
        double? loopEnd = null;
        if(Follow.Path.Cyclic && Follow.Path.Spline.Points.Count > 0 &&
            anchors.TryGetValue(Follow.Path.Spline.Points[0].Id, out double start))
            loopEnd = times.Where(t => t.Index == 0 && t.Frame > start + 1e-6)
                .OrderBy(t => Math.Abs(t.Frame!.Value - (constraint.LoopEndFrame ?? start)))
                .Select(t => t.Frame).FirstOrDefault();
        constraint.TimeAnchors = anchors; constraint.LoopEndFrame = loopEnd;
        editorAnchorSignature = signature;
    }

    /// <summary>Returns every following node to the automatic 0–100 % time distribution.</summary>
    public void ResetFactors() { ManualFactors.Clear(); Space.Touch(); }

    // Preserve the fractional times of imported offset keys between their bounding camera nodes.
    // The two sides of a bound key may belong to intervals with different stretch factors.
    private static void RetimeTimeCurve(FCurve curve, Func<CurveKey, double> target)
    {
        curve.Sort();
        var anchors = curve.Keys.Where(k => k.NodeId.HasValue).ToArray();
        if(anchors.Length == 0) return;
        var old = anchors.Select(k => k.Time).ToArray();
        var frames = anchors.Select(target).ToArray();
        if(old.SequenceEqual(frames)) return;
        int span = 0;
        foreach(var key in curve.Keys)
        {
            double time = key.Time;
            while(span + 1 < old.Length - 1 && time >= old[span + 1]) span++;
            double Scale(int i) => old.Length == 1 ? 1 : (frames[i + 1] - frames[i]) / (old[i + 1] - old[i]);
            double scale = Scale(span);
            double left = span > 0 && time == old[span] ? Scale(span - 1) : scale;
            key.SetTime(key.NodeId.HasValue ? target(key) : frames[span] + (time - old[span]) * scale);
            key.Left.X *= (float)left;
            key.Right.X *= (float)scale;
        }
    }

    private static void RemapProgressValues(List<CurveKey> keys, FCurve previous)
    {
        int start = 0;
        for(int end = 1; end < keys.Count; end++)
        {
            if(keys[end].NodeId == null) continue;
            var a = keys[start]; var b = keys[end];
            double oldA = previous.EvaluateOrdered(a.Time), oldB = previous.EvaluateOrdered(b.Time);
            if(a.Value != oldA || b.Value != oldB)
            {
                double scale = oldB > oldA + 1e-12 ? (b.Value - a.Value) / (oldB - oldA) : 0;
                a.Right.Y *= (float)scale; b.Left.Y *= (float)scale;
                for(int i = start + 1; i < end; i++)
                {
                    var k = keys[i];
                    k.Value = a.Value + (k.Value - oldA) * scale;
                    k.Left.Y *= (float)scale; k.Right.Y *= (float)scale;
                }
            }
            start = end;
        }
    }

    private void SyncPositionTiming()
    {
        var path = spatialPath;
        double start = path?.Distance(Space.Start) ?? 0, total = (path?.Distance(Space.End) ?? 0) - start;
        // A new Follow Path target resets every factor to the automatic time distribution.
        if(Follow != null && followCurve != Follow.CurveId) ManualFactors.Clear();
        var factors = Follow != null ? AutoFactors() : null;
        // Imported timing uses a stable global parameter for the anchors, then arc length inside
        // each leg. A hold or cut must not collapse the domain of its editable progress keys.
        bool importedProgress = Space.PreserveIntermediateKeys && Follow == null && Space.End > Space.Start;
        bool anchored = Follow != null && !Follow.Constraint.EditorTiming && UsesTimeAnchors;
        var nodeFrames = Space.PreserveIntermediateKeys ? nodes.ToDictionary(n => n.Id, n => n.Frame) : null;
        foreach(var (curve, progress) in Offset.Select(c => (c, false)).Prepend((PathProgress, true)))
        {
            bool preserve = nodeFrames != null && (!progress || Follow == null);
            if(preserve && curve.Keys.Where(k => k.NodeId.HasValue).All(k => nodeFrames!.ContainsKey(k.NodeId!.Value)))
                RetimeTimeCurve(curve, key => nodeFrames![key.NodeId!.Value]);
            var old = curve.Clone();
            var keys = new List<CurveKey>();
            for(int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var key = curve.Keys.FirstOrDefault(k => k.NodeId == node.Id);
                if(key == null && Space.PreserveIntermediateKeys && curve.Keys.Count > 0)
                    key = curve.InsertPreserving(node.Frame, node.Id);
                key ??= new CurveKey { NodeId = node.Id, Mode = HandleMode.AutoClamped, Value = old.Keys.Count > 0 ? old.Evaluate(node.Frame) : 0 };
                key.SetTime(node.Frame);
                if(progress && factors != null)
                {
                    // following a curve: the player camera position is ignored; the node is a factor on the curve
                    // (with timeline anchors every factor comes from them, hand-set values do not apply)
                    if(anchored || !ManualFactors.Contains(node.Id)) key.Value = factors[i];
                }
                else if(progress && importedProgress && Space.Anchors.TryGetValue(node.Id, out var importedAnchor))
                    key.Value = (importedAnchor[CameraChannel.Position] - Space.Start) / (Space.End - Space.Start);
                else if(progress)
                    key.Value = path != null && total > 1e-12 && Space.Anchors.TryGetValue(node.Id, out var anchor)
                        ? Math.Clamp((path.Distance(anchor[CameraChannel.Position]) - start) / total, 0, 1)
                        : nodes.Count > 1 ? i / (double)(nodes.Count - 1) : 0;
                keys.Add(key);
            }
            if(preserve && keys.Count != 0 && curve.Keys.Any(k => !k.NodeId.HasValue))
            {
                var occupied = keys.Select(k => k.Time).ToHashSet();
                double first = keys[0].Time, last = keys[^1].Time;
                keys.AddRange(curve.Keys.Where(k => !k.NodeId.HasValue && k.Time > first && k.Time < last && occupied.Add(k.Time)));
                keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                if(progress) RemapProgressValues(keys, old);
            }
            curve.Keys = keys;
            if(keys.Count == 0) continue;
            curve.RecalculateHandles();
            foreach(var end in new[] { keys[0], keys[^1] })
                if(end.Mode == HandleMode.AutoClamped && keys.Count > 1) { end.Left = new Vector2(end.Left.X, 0); end.Right = new Vector2(end.Right.X, 0); }
        }
        followCurve = Follow?.CurveId ?? Guid.Empty;
        foreach(var interval in Intervals)
            interval.FollowDistance = Follow == null ? null :
                Math.Abs((ProgressKey(interval.EndNodeId)?.Value ?? 0) - (ProgressKey(interval.StartNodeId)?.Value ?? 0)) * Follow.Path.Length;
    }

    private MotionInterval? At(double frame)
    {
        int lo = 0, hi = Intervals.Count;
        while(lo < hi)
        {
            int mid = (lo + hi) / 2;
            if(frame < Intervals[mid].End) hi = mid; else lo = mid + 1;
        }
        return Intervals.Count == 0 ? null : Intervals[Math.Min(lo, Intervals.Count - 1)];
    }
    public double Evaluate(CameraChannel channel, double frame) => At(frame)?.Evaluate(channel, frame) ?? 0;

    public XcpFrame Sample(float frame)
    {
        EnsureGeometry();
        var interval = At(frame);
        if(interval == null)
        {
            var n = nodes.FirstOrDefault();
            return n == null ? new XcpFrame(Vector3.Zero, Quaternion.Identity, MathF.PI / 4) : NodePose(n);
        }
        // Hidden channel keys remain editable and are recovered when an imported leg's cut is cleared.
        if(Space.PreserveIntermediateKeys && frame < interval.End && CutObserved.GetValueOrDefault(interval.EndNodeId))
            frame = interval.Start;
        XcpFrame pose;
        if(EditorMode == CurveEditorMode.Advanced || neutralSimple)
            pose = Space.SampleOrdered(AdvancedPosition(interval, frame), DirectParameter(CameraChannel.Rotation, frame, interval), DirectParameter(CameraChannel.Fov, frame, interval));
        else if(parameterSimple)
        {
            double P(CameraChannel c) { var r = interval.Ranges[c]; return r.start + (r.end - r.start) * interval.Evaluate(c, frame); }
            pose = Space.SampleOrdered(P(CameraChannel.Position), P(CameraChannel.Rotation), P(CameraChannel.Fov));
        }
        else pose = interval.Geometry(interval.Evaluate(CameraChannel.Position, frame), interval.Evaluate(CameraChannel.Rotation, frame), interval.Evaluate(CameraChannel.Fov, frame));
        if(Follow != null) return FollowPose(FollowFactor(interval, frame), OffsetAt(interval, frame), pose);
        return pose with { Pos = pose.Pos + OffsetAt(interval, frame) };
    }

    public XcpFrame SampleGeometry(int intervalIndex, double progress)
    {
        EnsureGeometry();
        return Intervals[intervalIndex].Geometry(progress, progress, progress);
    }

    public double ParameterAt(CameraChannel channel, double frame)
    {
        EnsureGeometry();
        var interval = At(frame);
        if(interval != null)
        {
            if(Space.PreserveIntermediateKeys && frame < interval.End && CutObserved.GetValueOrDefault(interval.EndNodeId))
                frame = interval.Start;
            if(EditorMode == CurveEditorMode.Advanced || neutralSimple)
                return channel == CameraChannel.Position ? AdvancedPosition(interval, frame) : DirectParameter(channel, frame, interval);
            if(parameterSimple) { var r = interval.Ranges[channel]; return r.start + (r.end - r.start) * interval.Evaluate(channel, frame); }
            return interval.Parameter(channel, interval.Evaluate(channel, frame));
        }
        var node = nodes.FirstOrDefault();
        return node != null && Space.Anchors.TryGetValue(node.Id, out var anchor) ? anchor[channel] : Space.Start;
    }

    public XcpFrame NodePose(Keyframe node)
    {
        if(!Space.Anchors.TryGetValue(node.Id, out var anchor)) return new XcpFrame(node.Pos, node.Rot, node.Fov);
        var pose = Space.SampleOrdered(anchor[CameraChannel.Position], anchor[CameraChannel.Rotation], anchor[CameraChannel.Fov]);
        if(Follow != null && ProgressKey(node.Id) is { } key) return FollowPose(key.Value, OffsetAt(node.Frame), pose);
        return pose with { Pos = pose.Pos + OffsetAt(node.Frame) };
    }

    /// <summary>The node's point on the path (node path or Follow Path target), without the location offset.</summary>
    public Vector3 NodePathPoint(Keyframe node)
    {
        if(Follow != null && ProgressKey(node.Id) is { } key) return Follow.Path.Position(key.Value);
        return NodePose(node).Pos - OffsetAt(node.Frame);
    }

    /// <summary>Length factor of a node along the Follow Path target (null when not following).</summary>
    public double? NodeFactor(Keyframe node) => Follow != null ? ProgressKey(node.Id)?.Value : null;

    // ---------------- node path as a 3D Bezier curve (handles edited in the game view) ----------------

    private (CurveKey x, CurveKey y, CurveKey z)? PositionKeys(Keyframe node)
    {
        var x = Space.Channels[SpatialChannel.PositionX].Keys.FirstOrDefault(k => k.NodeId == node.Id);
        var y = Space.Channels[SpatialChannel.PositionY].Keys.FirstOrDefault(k => k.NodeId == node.Id);
        var z = Space.Channels[SpatialChannel.PositionZ].Keys.FirstOrDefault(k => k.NodeId == node.Id);
        return x != null && y != null && z != null ? (x, y, z) : null;
    }

    public static CurveHandle HandleType(HandleMode mode) => mode switch
    {
        HandleMode.Vector => CurveHandle.Vector, HandleMode.Aligned => CurveHandle.Aligned, HandleMode.Free => CurveHandle.Free, _ => CurveHandle.Auto,
    };
    private static HandleMode Mode(CurveHandle type) => type switch
    {
        CurveHandle.Vector => HandleMode.Vector, CurveHandle.Aligned => HandleMode.Aligned, CurveHandle.Free => HandleMode.Free, _ => HandleMode.Auto,
    };

    /// <summary>The node's path handles as offsets from its path point, and their type (null when not on the node path).</summary>
    public (Vector3 left, Vector3 right, CurveHandle type)? PathHandles(Keyframe node)
    {
        if(Follow != null || PositionKeys(node) is not { } k) return null;
        return (new Vector3(k.x.Left.Y, k.y.Left.Y, k.z.Left.Y), new Vector3(k.x.Right.Y, k.y.Right.Y, k.z.Right.Y), HandleType(k.x.Mode));
    }

    /// <summary>
    /// Drags one handle of the node path (Blender handle rules): an Auto handle turns the pair Aligned, a Vector
    /// handle becomes Free; an Aligned partner keeps its length and points the opposite way. Handles stay
    /// at a third of the parameter span horizontally, so the path remains an exact 3D Bezier curve.
    /// </summary>
    public bool SetPathHandle(Keyframe node, bool left, Vector3 offset)
    {
        if(Follow != null || PositionKeys(node) is not { } k || !XcpMath.IsFinite(offset)) return false;
        var type = HandleType(k.x.Mode);
        if(type == CurveHandle.Auto) type = CurveHandle.Aligned;
        else if(type == CurveHandle.Vector) type = CurveHandle.Free;
        var l = new Vector3(k.x.Left.Y, k.y.Left.Y, k.z.Left.Y); var r = new Vector3(k.x.Right.Y, k.y.Right.Y, k.z.Right.Y);
        if(left) l = offset; else r = offset;
        if(type == CurveHandle.Aligned)
        {
            var moved = left ? l : r; float other = (left ? r : l).Length();
            if(moved.LengthSquared() > 1e-12f) { var opposite = -Vector3.Normalize(moved) * other; if(left) r = opposite; else l = opposite; }
        }
        foreach(var (key, lv, rv) in new[] { (k.x, l.X, r.X), (k.y, l.Y, r.Y), (k.z, l.Z, r.Z) })
        {
            key.Mode = Mode(type); key.AutomaticTangent = false;
            key.Left = new Vector2(key.Left.X, lv); key.Right = new Vector2(key.Right.X, rv);
        }
        Space.Touch();
        return true;
    }

    /// <summary>Sets the node path handle type (Blender V menu). Automatic types refit on the next sync.</summary>
    public bool SetPathHandleType(Keyframe node, CurveHandle type)
    {
        if(Follow != null || PositionKeys(node) is not { } k) return false;
        foreach(var key in new[] { k.x, k.y, k.z }) { key.Mode = Mode(type); key.AutomaticTangent = type is CurveHandle.Auto or CurveHandle.Vector; }
        if(type == CurveHandle.Aligned)
        {
            // Blender: with nothing grabbed the left handle follows the right one
            var r = new Vector3(k.x.Right.Y, k.y.Right.Y, k.z.Right.Y); var l = new Vector3(k.x.Left.Y, k.y.Left.Y, k.z.Left.Y);
            if(r.LengthSquared() > 1e-12f)
            {
                l = -Vector3.Normalize(r) * l.Length();
                k.x.Left = new Vector2(k.x.Left.X, l.X); k.y.Left = new Vector2(k.y.Left.X, l.Y); k.z.Left = new Vector2(k.z.Left.X, l.Z);
            }
        }
        Space.MarkTangentsDirty(node.Id, 0);
        Space.Touch();
        return true;
    }

    public Vector3 NodeEuler(Keyframe node)
    {
        if(!Space.Anchors.TryGetValue(node.Id, out var anchor)) return Euler(node.Rot);
        double p = anchor[CameraChannel.Rotation];
        return new Vector3((float)Space.Channels[SpatialChannel.Yaw].Evaluate(p),
            (float)Space.Channels[SpatialChannel.Pitch].Evaluate(p), (float)Space.Channels[SpatialChannel.Roll].Evaluate(p));
    }

    public void WriteNodeEuler(Keyframe node, Vector3 degrees, int influence = 0)
    {
        var pose = NodePose(node);
        Space.WritePose(node, pose, false, true, false, degrees, influence);
        const float rad = MathF.PI / 180;
        node.Rot = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(degrees.X * rad, degrees.Y * rad, degrees.Z * rad));
    }

    public bool GeometryJump(int intervalIndex, double from, double to)
    {
        EnsureGeometry(); var interval = Intervals[intervalIndex];
        return spatialPath!.HasJump(interval.Parameter(CameraChannel.Position, from), interval.Parameter(CameraChannel.Position, to));
    }

    /// <summary>
    /// Set by <see cref="WritePose"/> when timeline anchors drive the path progress: the node was moved along
    /// the curve, and the progress there belongs to this frame. The caller moves the node in time.
    /// </summary>
    public int? RequestedFrame { get; private set; }

    public void WritePose(Keyframe node, XcpFrame pose, bool position = true, bool rotation = true, bool fov = true, int influence = 0)
    {
        RequestedFrame = null;
        if(Follow != null && ProgressKey(node.Id) is { } key)
        {
            // Following a curve: the node slides along it (nearest point to the requested position);
            // rotation is stored in the path frame so the final camera rotation is what was asked for.
            var point = Follow.Path.Sample(key.Value);
            var q = Follow.Constraint.Orientation(point);
            if(position)
            {
                var offset = OffsetAt(node.Frame);
                if(Follow.Constraint.UseRadius) offset *= point.Radius;
                double old = key.Value;
                double factor = Follow.Path.Project(pose.Pos - Vector3.Transform(offset, q));
                // a closed curve: stay on the same turn (no jump across the start point)
                if(Follow.Path.Cyclic) factor += Math.Round(old - factor);
                // Invert the existing timing before changing the node's progress key.
                if(SetAnchoredNodeFactor(node, factor) is double frame) RequestedFrame = (int)Math.Round(frame);
                else ManualFactors.Add(node.Id); // dragged along the curve by hand
                key.Value = factor;
                point = Follow.Path.Sample(key.Value);
                q = Follow.Constraint.Orientation(point);
                // what the curve cannot reach (e.g. height above a flat circle) becomes the node's location offset,
                // so the node ends up exactly where it was put
                var residual = Vector3.Transform(pose.Pos - point.Position, Quaternion.Inverse(q));
                if(Follow.Constraint.UseRadius && MathF.Abs(point.Radius) > 1e-6f) residual /= point.Radius;
                SetOffsetKey(node, residual);
            }
            var own = pose with { Rot = Quaternion.Normalize(Quaternion.Inverse(q) * pose.Rot) };
            if(rotation || fov) Space.WritePose(node, own, false, rotation, fov, null, influence);
            else Space.Touch();
        }
        // the path point is the node position minus its location offset
        else Space.WritePose(node, pose with { Pos = pose.Pos - OffsetAt(node.Frame) }, position, rotation, fov, null, influence);
        if(position) node.Pos = Follow != null ? NodePose(node).Pos : pose.Pos;
        if(rotation) node.Rot = pose.Rot;
        if(fov) node.Fov = pose.Fov;
    }

    /// <summary>Sets the location offset keys of one node (one key per node on each offset curve).</summary>
    private void SetOffsetKey(Keyframe node, Vector3 offset)
    {
        for(int i = 0; i < 3; i++)
        {
            var curve = Offset[i];
            var key = curve.Keys.FirstOrDefault(k => k.NodeId == node.Id);
            if(key == null) continue;
            key.Value = i == 0 ? offset.X : i == 1 ? offset.Y : offset.Z;
            curve.RecalculateHandles();
            var keys = curve.Keys;
            foreach(var end in new[] { keys[0], keys[^1] })
                if(end.Mode == HandleMode.AutoClamped && keys.Count > 1) { end.Left = new Vector2(end.Left.X, 0); end.Right = new Vector2(end.Right.X, 0); }
        }
    }

    internal void Bind(List<Keyframe> current)
    {
        nodes = current;
        bool regenerate = Space.IsDefaultPath;
        bool added = false;
        var seeded = new List<Guid>();
        foreach(var node in current)
        {
            if(Space.Anchors.ContainsKey(node.Id)) continue;
            int index = current.IndexOf(node);
            var before = current.Take(index).LastOrDefault(n => Space.Anchors.ContainsKey(n.Id));
            var after = current.Skip(index + 1).FirstOrDefault(n => Space.Anchors.ContainsKey(n.Id));
            int parameter;
            if(before != null && after != null)
            {
                double a = Space.Anchors[before.Id][CameraChannel.Position], b = Space.Anchors[after.Id][CameraChannel.Position];
                parameter = checked((int)Math.Round((a + b) * .5));
                if(parameter <= a || parameter >= b) throw new InvalidOperationException("No integer spatial parameter remains between these nodes; insert from the path instead.");
            }
            else if(after != null) parameter = checked(Space.Start - 1000);
            else parameter = Space.Anchors.Count == 0 ? 0 : checked(Space.End + 1000);
            var seed = node.Clone(); seed.Pos -= OffsetAt(node.Frame); // the path point excludes the location offset
            Space.Seed(seed, parameter); added = true; seeded.Add(node.Id);
        }
        // A new node changes its neighbours' Auto tangents too (an end node becomes interior, a lone
        // node gains a neighbour), so the default path is a true Bezier Auto fit without editing first.
        foreach(var id in seeded) Space.MarkTangentsDirty(id, 1);
        if(added && regenerate) Space.LegacyInitialized = false;
        if(Space.SynchronizeNodes(current))
        {
            foreach(var interval in Intervals) interval.Ranges.Clear();
        }
        SeededNodes = current.Select(n => n.Id).ToHashSet();
        EnsureGeometry(rebind: true);
        SyncPositionTiming();
    }

    private void EnsureGeometry(bool rebind = false)
    {
        if(nodes.Count == 0) return;
        if(spatialPath == null || spatialRevision != Space.Revision)
        {
            foreach(var curve in Space.Channels.Values) curve.Sort();
            spatialPath = new PosePath(Space); spatialRevision = Space.Revision;
            rebind = true;
        }
        if(!rebind) return;
        foreach(var interval in Intervals)
        {
            interval.Path = spatialPath;
            foreach(var c in Enum.GetValues<CameraChannel>())
                if(!interval.Ranges.ContainsKey(c)) interval.Ranges[c] =
                    (Space.Anchors[interval.StartNodeId][c], Space.Anchors[interval.EndNodeId][c]);
        }
    }

    /// <summary>Exact subdivision: normalized progress polygons and immutable geometry subranges.
    /// Call before synchronization; node pose is replaced with the sampled authoritative pose.</summary>
    public void InsertNode(Keyframe node)
    {
        var source = Intervals.FirstOrDefault(i => node.Frame > i.Start && node.Frame < i.End);
        if(source == null) return;
        EnsureGeometry();
        bool importedAdvanced = Space.PreserveIntermediateKeys && EditorMode == CurveEditorMode.Advanced;
        var sampled = importedAdvanced ? Space.SampleOrdered(ParameterAt(CameraChannel.Position, node.Frame),
            ParameterAt(CameraChannel.Rotation, node.Frame), ParameterAt(CameraChannel.Fov, node.Frame)) :
            source.Geometry(source.Evaluate(CameraChannel.Position, node.Frame), source.Evaluate(CameraChannel.Rotation, node.Frame), source.Evaluate(CameraChannel.Fov, node.Frame));
        // following a curve: the new node keeps the current factor along it, so the motion is unchanged
        double? followFactor = Follow != null ? FollowFactor(source, node.Frame) : null;
        var followed = followFactor is double ff ? FollowPose(ff, OffsetAt(source, node.Frame), Sample(node.Frame) with { Rot = sampled.Rot }) : (XcpFrame?)null;
        var left = source.Clone(); var right = source.Clone();
        left.MarkManual(); right.MarkManual();
        right.Id = Guid.NewGuid(); left.EndNodeId = node.Id; right.StartNodeId = node.Id;
        left.End = node.Frame; right.Start = node.Frame;
        var timingSnapshot = source.Clone();
        left.splitTiming = timingSnapshot; right.splitTiming = timingSnapshot;
        left.splitValues = new(); right.splitValues = new();
        left.splitTimeStart = source.Start; left.splitTimeEnd = node.Frame;
        right.splitTimeStart = node.Frame; right.splitTimeEnd = source.End;
        var baked = new Dictionary<FCurve, FCurve>();
        foreach(var channel in Enum.GetValues<CameraChannel>())
        {
            var original = source.Curve(channel);
            if(!baked.TryGetValue(original, out var timing))
            {
                timing = original.Clone(); ProgressCurve.Constrain(timing, source.Start, source.End);
                baked[original] = timing;
            }
            double progress = source.Evaluate(channel, node.Frame);
            left.splitValues[channel] = (0, progress); right.splitValues[channel] = (progress, 1);
            var range = source.Ranges[channel];
            double middle = importedAdvanced ? ParameterAt(channel, node.Frame) : source.Parameter(channel, progress);
            left.Ranges[channel] = (range.start, middle); right.Ranges[channel] = (middle, range.end);
            left.Channels[channel] = ProgressCurve.Slice(timing, source.Start, node.Frame, source.Start, source.End);
            right.Channels[channel] = ProgressCurve.Slice(timing, node.Frame, source.End, source.Start, source.End);
        }
        foreach(var curve in left.Channels.Values) { curve.Keys[0].NodeId = left.StartNodeId; curve.Keys[^1].NodeId = node.Id; }
        foreach(var curve in right.Channels.Values) { curve.Keys[0].NodeId = node.Id; curve.Keys[^1].NodeId = right.EndNodeId; }
        if(source.Linked) { left.Link(CameraChannel.Position); right.Link(CameraChannel.Position); }
        left.HoldFrames = left.splitHoldFrames = Math.Min(Math.Max(0, source.HoldFrames), node.Frame - source.Start);
        right.HoldFrames = right.splitHoldFrames = Math.Max(0, source.HoldFrames - (node.Frame - source.Start));
        left.splitCurves = left.Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        right.splitCurves = right.Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        if(source.HoldFrames == 0 && source.splitTiming == null) { left.splitTiming = null; right.splitTiming = null; }
        Space.AddAnchor(node.Id, left.Ranges[CameraChannel.Position].end, left.Ranges[CameraChannel.Rotation].end, left.Ranges[CameraChannel.Fov].end);
        node.Pos = sampled.Pos + OffsetAt(node.Frame); node.Rot = sampled.Rot; node.Fov = sampled.Fov;
        if(followFactor is double factor && followed is XcpFrame f)
        {
            node.Pos = f.Pos; node.Rot = f.Rot;
            // Keep existing nodes at their sampled factors too: pinning only the inserted node would
            // re-spread its automatic neighbours between the new fixed points on the next synchronization.
            foreach(var key in PathProgress.Keys) if(key.NodeId is Guid id) ManualFactors.Add(id);
            PathProgress.Keys.Add(new CurveKey { NodeId = node.Id, Frame = node.Frame, Value = factor, Mode = HandleMode.AutoClamped });
            PathProgress.Sort();
            ManualFactors.Add(node.Id); // keep the sampled position so inserting does not change the motion
        }
        // Holds are node-authoritative. Commit their subdivision together with timing so
        // the next Ensure cannot replace the remaining hold with the new node's default zero.
        node.HoldFrames = right.HoldFrames;
        var startNode = nodes.FirstOrDefault(n => n.Id == source.StartNodeId);
        if(startNode != null) startNode.HoldFrames = left.HoldFrames;
        foreach(var existing in Intervals) existing.MarkManual();
        int index = Intervals.IndexOf(source); Intervals[index] = left; Intervals.Insert(index + 1, right);
        nodes = nodes.Where(n => n.Id != node.Id).Append(node).OrderBy(n => n.Frame).ToList();
        SeededNodes.Add(node.Id); poses[node.Id] = sampled;
    }

    public void Rescale(double factor)
    {
        if(!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        int start = ProgressCurve.Round(Start * factor), end = ProgressCurve.Round(End * factor);
        var copies = Intervals.Select(i => i.Clone()).ToList();
        foreach(var i in copies)
        {
            foreach(var curve in i.Channels.Values.Distinct()) curve.Rescale(factor);
            i.Start = ProgressCurve.Round(i.Start * factor); i.End = ProgressCurve.Round(i.End * factor);
            if(i.End <= i.Start) throw new InvalidOperationException("Scaling would collapse a motion interval.");
            i.HoldFrames = ProgressCurve.Round(i.HoldFrames * factor);
            i.RefreshSplitAfterRescale(factor);
        }
        var holds = HoldRegions.Select(h => (ProgressCurve.Round(h.start * factor), ProgressCurve.Round(h.end * factor))).ToList();
        var progress = PathProgress.Clone();
        if(Space.PreserveIntermediateKeys && Follow == null) RetimeTimeCurve(progress, key => ProgressCurve.Round(key.Time * factor));
        else progress.Rescale(factor);
        var offsets = Offset.Select(curve => curve.Clone()).ToArray();
        foreach(var curve in offsets)
        {
            if(Space.PreserveIntermediateKeys) RetimeTimeCurve(curve, key => ProgressCurve.Round(key.Time * factor));
            else curve.Rescale(factor);
        }
        // Commit only after all curves, intervals, and holds have passed prevalidation.
        for(int n = 0; n < Intervals.Count; n++)
        {
            var i = Intervals[n]; var copy = copies[n];
            foreach(var c in Enum.GetValues<CameraChannel>().Select(i.Curve).Distinct()) c.Rescale(factor);
            i.Start = copy.Start; i.End = copy.End; i.HoldFrames = copy.HoldFrames;
            i.RefreshSplitAfterRescale(factor);
            if(i.AutoTiming && i.AutoSnapshot != null) i.AutoSnapshot = i.Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        }
        Start = start; End = end; HoldRegions = holds;
        PathProgress.Keys = progress.Keys;
        for(int i = 0; i < Offset.Length; i++) Offset[i].Keys = offsets[i].Keys;
    }

    public static Vector3 Euler(Quaternion q, Vector3? near = null)
    {
        q = Quaternion.Normalize(q);
        float pitch = MathF.Asin(Math.Clamp(2 * (q.W * q.X - q.Y * q.Z), -1, 1));
        float yaw = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z));
        var e = new Vector3(yaw, pitch, roll) * (180 / MathF.PI);
        if(near is Vector3 n)
        {
            float U(float a, float b) => a + 360 * MathF.Round((b - a) / 360);
            e = new Vector3(U(e.X, n.X), U(e.Y, n.Y), U(e.Z, n.Z));
        }
        return e;
    }
}

public static class CurveModel
{
    public static List<List<Keyframe>> Groups(CamTrack track)
    {
        var groups = new List<List<Keyframe>>();
        foreach(var key in track.Keys.OrderBy(k => k.Frame))
        {
            if(groups.Count == 0) groups.Add([]);
            groups[^1].Add(key);
        }
        return groups;
    }

    public static void Ensure(CamTrack track, int fps = 30)
    {
        // anchors of deleted curve points are dropped
        if(track.FollowPath is { TimeAnchors.Count: > 0 } follow && track.FollowCurve?.PathSpline is { } spline)
            foreach(var id in follow.TimeAnchors.Keys.Where(id => !spline.Points.Any(p => p.Id == id)).ToList())
                follow.TimeAnchors.Remove(id);
        if(track.FollowPath is { } fp && (fp.TimeAnchors.Count == 0 || track.FollowCurve?.PathSpline is not { Cyclic: true }))
            fp.LoopEndFrame = null;
        var groups = Groups(track);
        var old = track.Shots;
        var snapshots = old.Select(s => s.Clone()).ToList();
        var allIntervals = old.SelectMany(s => s.Intervals).ToList();
        var result = new List<ShotCurves>();
        for(int g = 0; g < groups.Count; g++)
        {
            var nodes = groups[g]; var ids = nodes.Select(n => n.Id).ToHashSet();
            var shot = old.FirstOrDefault(s => s.AnchorNodeId == nodes[0].Id && !result.Contains(s)) ??
                old.FirstOrDefault(s => !result.Contains(s) && !track.Keys.Any(n => n.Id == s.AnchorNodeId) && s.SeededNodes.Overlaps(ids)) ??
                new ShotCurves();
            if(shot.Space.Anchors.Count == 0)
            {
                var parent = snapshots.FirstOrDefault(s => s.SeededNodes.Contains(nodes[0].Id));
                if(parent != null) shot.Space = parent.Space.Clone();
            }
            var offsets = new Dictionary<Guid, double>();
            foreach(var source in snapshots.Where(s => s.Id != shot.Id && s.SeededNodes.Overlaps(ids)))
            {
                var owned = source.SeededNodes.Where(ids.Contains).ToHashSet();
                // A cut splits one spatial snapshot without trimming/refitting any authored polygon.
                // Rejoining independently edited shots appends a separate canonical domain.
                if(shot.Space.Anchors.Count > 0 && owned.Any(id => !shot.SeededNodes.Contains(id)) &&
                    !source.SeededNodes.Contains(nodes[0].Id))
                {
                    double offset = shot.Space.Import(source.Space, owned);
                    foreach(var id in owned) offsets[id] = offset;
                }
            }
            var intervals = new List<MotionInterval>();
            for(int n = 0; n + 1 < nodes.Count; n++)
            {
                var a = nodes[n]; var b = nodes[n + 1];
                var interval = allIntervals.FirstOrDefault(i => i.StartNodeId == a.Id && i.EndNodeId == b.Id);
                if(interval == null) interval = new MotionInterval(a.Id, b.Id, a.Frame, b.Frame);
                else
                {
                    interval.Retime(a.Frame, b.Frame);
                    if(offsets.TryGetValue(a.Id, out double offset) && offsets.ContainsKey(b.Id))
                        foreach(var c in Enum.GetValues<CameraChannel>())
                            if(interval.Ranges.TryGetValue(c, out var range)) interval.Ranges[c] = (range.start + offset, range.end + offset);
                }
                interval.HoldFrames = Math.Clamp(a.HoldFrames, 0, b.Frame - a.Frame);
                foreach(var curve in interval.Channels.Values)
                { curve.Keys[0].NodeId = a.Id; curve.Keys[^1].NodeId = b.Id; }
                intervals.Add(interval);
            }
            shot.Intervals = intervals; shot.AnchorNodeId = nodes[0].Id; shot.Start = nodes[0].Frame;
            // Follow Path: bind to the evaluated target curve (rebuilt every time, so curve edits apply)
            if(track.FollowPath is { Enabled: true } constraint && track.FollowCurve is { } target && CurvePath.Create(target) is { } targetPath)
                shot.Follow = new FollowBinding(targetPath, constraint, target.Id);
            else if(shot.Follow != null) shot.ClearFollow();
            int next = g + 1 < groups.Count ? groups[g + 1][0].Frame : int.MaxValue;
            long end = Math.Min((long)next - 1, (long)nodes[^1].Frame + Math.Max(0, nodes[^1].HoldFrames));
            shot.End = (int)Math.Clamp(end, shot.Start, int.MaxValue);
            shot.Name = I18n.T("core.shot_name", g + 1, shot.Start, shot.End);
            shot.HoldRegions = nodes.Where(n => n.HoldFrames > 0).Select(n =>
                (n.Frame, (int)Math.Min((long)n.Frame + n.HoldFrames, nodes.Where(k => k.Frame > n.Frame).Select(k => k.Frame).DefaultIfEmpty(shot.End).Min()))).ToList();
            shot.Bind(nodes);
            ConstantCuts.Synchronize(shot, nodes);
            SpatialTangents.Apply(shot.Space, nodes);
            shot.Bind(nodes);
            SmoothTiming.Apply(shot);
            shot.UpdateEditorAnchors();
            result.Add(shot);
        }
        track.Shots = result;
    }
}
