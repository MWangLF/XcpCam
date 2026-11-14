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
    internal PosePath? Path;
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
            Start = Start, End = End, HoldFrames = HoldFrames, Linked = Linked, Path = Path,
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
    public string Name = "镜头段";
    public List<MotionInterval> Intervals = [];
    internal Dictionary<Guid, CurveInterpolation> CutRestore = new();
    internal Dictionary<Guid, bool> CutObserved = new();
    public HashSet<Guid> SeededNodes = [];
    public List<(int start, int end)> HoldRegions = [];
    public IReadOnlyList<(int start, int end)> Holds => HoldRegions;
    private List<Keyframe> nodes = [];
    private Dictionary<Guid, XcpFrame> poses = new();
    public SpatialCurves Space = new();
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
    };

    private MotionInterval? At(double frame) => Intervals.FirstOrDefault(i => frame < i.End) ?? Intervals.LastOrDefault();
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
        if(EditorMode == CurveEditorMode.Advanced || neutralSimple)
            return Space.Sample(DirectParameter(CameraChannel.Position, frame, interval), DirectParameter(CameraChannel.Rotation, frame, interval), DirectParameter(CameraChannel.Fov, frame, interval));
        if(parameterSimple)
        {
            double P(CameraChannel c) { var r = interval.Ranges[c]; return r.start + (r.end - r.start) * interval.Evaluate(c, frame); }
            return Space.Sample(P(CameraChannel.Position), P(CameraChannel.Rotation), P(CameraChannel.Fov));
        }
        return interval.Geometry(interval.Evaluate(CameraChannel.Position, frame), interval.Evaluate(CameraChannel.Rotation, frame), interval.Evaluate(CameraChannel.Fov, frame));
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
            if(EditorMode == CurveEditorMode.Advanced || neutralSimple) return DirectParameter(channel, frame, interval);
            if(parameterSimple) { var r = interval.Ranges[channel]; return r.start + (r.end - r.start) * interval.Evaluate(channel, frame); }
            return interval.Parameter(channel, interval.Evaluate(channel, frame));
        }
        var node = nodes.FirstOrDefault();
        return node != null && Space.Anchors.TryGetValue(node.Id, out var anchor) ? anchor[channel] : Space.Start;
    }

    public XcpFrame NodePose(Keyframe node)
    {
        if(!Space.Anchors.TryGetValue(node.Id, out var anchor)) return new XcpFrame(node.Pos, node.Rot, node.Fov);
        return Space.Sample(anchor[CameraChannel.Position], anchor[CameraChannel.Rotation], anchor[CameraChannel.Fov]);
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

    public void WritePose(Keyframe node, XcpFrame pose, bool position = true, bool rotation = true, bool fov = true, int influence = 0)
    {
        Space.WritePose(node, pose, position, rotation, fov, null, influence);
        if(position) node.Pos = pose.Pos;
        if(rotation) node.Rot = pose.Rot;
        if(fov) node.Fov = pose.Fov;
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
            Space.Seed(node, parameter); added = true; seeded.Add(node.Id);
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
        EnsureGeometry();
    }

    private void EnsureGeometry()
    {
        if(nodes.Count == 0) return;
        if(spatialPath == null || spatialRevision != Space.Revision)
        {
            spatialPath = new PosePath(Space); spatialRevision = Space.Revision;
        }
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
        var sampled = source.Geometry(source.Evaluate(CameraChannel.Position, node.Frame), source.Evaluate(CameraChannel.Rotation, node.Frame), source.Evaluate(CameraChannel.Fov, node.Frame));
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
            double middle = source.Parameter(channel, progress);
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
        node.Pos = sampled.Pos; node.Rot = sampled.Rot; node.Fov = sampled.Fov;
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
            int next = g + 1 < groups.Count ? groups[g + 1][0].Frame : int.MaxValue;
            long end = Math.Min((long)next - 1, (long)nodes[^1].Frame + Math.Max(0, nodes[^1].HoldFrames));
            shot.End = (int)Math.Clamp(end, shot.Start, int.MaxValue);
            shot.Name = $"镜头段 {g + 1}（F{shot.Start}–F{shot.End}）";
            shot.HoldRegions = nodes.Where(n => n.HoldFrames > 0).Select(n =>
                (n.Frame, (int)Math.Min((long)n.Frame + n.HoldFrames, nodes.Where(k => k.Frame > n.Frame).Select(k => k.Frame).DefaultIfEmpty(shot.End).Min()))).ToList();
            shot.Bind(nodes);
            ConstantCuts.Synchronize(shot, nodes);
            SpatialTangents.Apply(shot.Space, nodes);
            shot.Bind(nodes);
            SmoothTiming.Apply(shot);
            result.Add(shot);
        }
        track.Shots = result;
    }
}
