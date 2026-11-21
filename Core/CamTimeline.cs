using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>One camera track (layer). Tracks earlier in the list are higher and win where they overlap.</summary>
public sealed class CamTrack
{
    public string Name = "镜头 1";
    public uint Color = 0xFFFFAA33;
    public bool Muted;
    public bool Solo;
    public List<Keyframe> Keys = [];
    public List<ShotCurves> Shots = [];
    /// <summary>Follow Path constraint (null = the track follows its own node path).</summary>
    public FollowPathConstraint? FollowPath;
    /// <summary>Target curve resolved from <see cref="CamTimeline.Curves"/> (runtime reference).</summary>
    public CurveObject? FollowCurve;
    /// <summary>Tracking constraints, evaluated after Follow Path in list order.</summary>
    public List<TrackConstraint> TrackConstraints = [];

    /// <summary>An enabled tracking constraint drives the rotation (node rotation is locked).</summary>
    public bool RotationDriven => TrackConstraints.Any(c => c.Enabled);
    /// <summary>An enabled tracking constraint keeps the framing (node FoV is locked).</summary>
    public bool FovDriven => TrackConstraints.Any(c => c.DrivesFov);

    public CamTrack Clone() => new()
    {
        Name = Name, Color = Color, Muted = Muted, Solo = Solo,
        Keys = Keys.Select(k => k.Clone()).ToList(),
        Shots = Shots.Select(s => s.Clone()).ToList(),
        FollowPath = FollowPath?.Clone(), FollowCurve = FollowCurve,
        TrackConstraints = TrackConstraints.Select(c => c.Clone()).ToList(),
    };
}

/// <summary>
/// Frame-based camera timeline with one or more tracks. Nodes live on integer frames at <see cref="Fps"/>
/// (normally Brio's Vivacity playback rate). Seconds used by <see cref="CameraPath"/> are derived from frames.
/// <see cref="Keys"/> / <see cref="Sorted"/> refer to the active track.
/// </summary>
public sealed class CamTimeline
{
    /// <summary>Track colours (ABGR).</summary>
    public static readonly uint[] Palette = [0xFFFFAA33, 0xFF3399FF, 0xFF55DD55, 0xFFDD66DD, 0xFFDDDD33, 0xFF6666FF];

    public int Fps { get; private set; } = 30;
    public int Length = 300;
    public float Playhead;
    public List<CamTrack> Tracks { get; private set; } = [new CamTrack()];

    private int _active;
    public int ActiveTrackIndex
    {
        get => Math.Clamp(_active, 0, Tracks.Count - 1);
        set => _active = Math.Clamp(value, 0, Tracks.Count - 1);
    }
    public CamTrack ActiveTrack => Tracks[ActiveTrackIndex];

    /// <summary>Keys of the active track.</summary>
    public List<Keyframe> Keys => ActiveTrack.Keys;
    public IEnumerable<Keyframe> AllKeys => Tracks.SelectMany(t => t.Keys);

    /// <summary>Automatic-tangent influence: 0 refits only the edited node, N also refits N neighbours each side.</summary>
    public int PathInfluence;
    public HashSet<Guid> SelectedNodes { get; } = [];
    public long SelectionRevision { get; private set; }
    public void SelectNodes(IEnumerable<Guid> ids, bool additive = false)
    {
        if(!additive) SelectedNodes.Clear();
        SelectedNodes.UnionWith(ids);
        SelectedNodes.IntersectWith(AllKeys.Select(k => k.Id));
        SelectionRevision++;
    }
    public void ToggleNode(Guid id)
    {
        if(!SelectedNodes.Remove(id)) SelectedNodes.Add(id);
        SelectionRevision++;
    }

    public long Revision { get; private set; }
    public void Touch() => Revision++;

    /// <summary>User-placed curve objects (world space), targets for Follow Path constraints.</summary>
    public List<CurveObject> Curves { get; private set; } = [];

    /// <summary>Points every track's Follow Path constraint at its curve object (after undo, deletion, ...).</summary>
    public void ResolveConstraints()
    {
        foreach(var track in Tracks)
            track.FollowCurve = track.FollowPath is { } c ? Curves.FirstOrDefault(x => x.Id == c.CurveId) : null;
    }

    /// <summary>Adds a Follow Path constraint; node positions start at their nearest points on the curve.</summary>
    public void SetFollowPath(CamTrack track, CurveObject curve)
    {
        Snapshot();
        track.FollowPath = new FollowPathConstraint { CurveId = curve.Id };
        ResolveConstraints();
        CurveModel.Ensure(track, Fps);
        Touch();
    }

    /// <summary>Removes the constraint and bakes the followed node positions back into the node path.</summary>
    public void ClearFollowPath(CamTrack track)
    {
        if(track.FollowPath == null) return;
        Snapshot();
        var poses = track.Shots.SelectMany(s => track.Keys.Where(k => s.SeededNodes.Contains(k.Id)).Select(k => (s, k, pose: s.NodePose(k)))).ToList();
        track.FollowPath = null; track.FollowCurve = null;
        foreach(var shot in track.Shots) shot.ClearFollow();
        foreach(var (shot, node, pose) in poses) shot.WritePose(node, pose, true, true, false);
        CurveModel.Ensure(track, Fps);
        Touch();
    }

    public void RemoveCurve(CurveObject curve)
    {
        Snapshot();
        foreach(var track in Tracks.Where(t => t.FollowPath?.CurveId == curve.Id).ToList())
        {
            var poses = track.Shots.SelectMany(s => track.Keys.Where(k => s.SeededNodes.Contains(k.Id)).Select(k => (s, k, pose: s.NodePose(k)))).ToList();
            track.FollowPath = null; track.FollowCurve = null;
            foreach(var shot in track.Shots) shot.ClearFollow();
            foreach(var (shot, node, pose) in poses) shot.WritePose(node, pose, true, true, false);
            CurveModel.Ensure(track, Fps);
        }
        Curves.Remove(curve);
        ResolveConstraints();
        Touch();
    }

    private sealed record HistoryEntry(ProjectState State, bool RestoreEditor);
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private const int MaxUndo = 64;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Active track's keys sorted by frame.</summary>
    public IReadOnlyList<Keyframe> Sorted() => Keys.OrderBy(k => k.Frame).ToList();

    public CamTrack? TrackOf(Keyframe k) => Tracks.FirstOrDefault(t => t.Keys.Contains(k));

    /// <summary>First node frame over all tracks.</summary>
    public int FirstFrame => !AllKeys.Any() ? 0 : AllKeys.Min(k => k.Frame);
    /// <summary>Last frame over all tracks, including holds.</summary>
    public int LastFrame => !AllKeys.Any() ? 0 : AllKeys.Max(k => k.Frame + k.HoldFrames);
    public static int TrackLastFrame(CamTrack t) => t.Keys.Count == 0 ? 0 : t.Keys.Max(k => k.Frame + k.HoldFrames);

    /// <summary>Path time (seconds from the first node) for a timeline frame.</summary>
    public float FrameToPathTime(float frame) => (frame - FirstFrame) / Fps;
    public float PathTimeToFrame(float t) => FirstFrame + t * Fps;

    /// <summary>Writes TimeSec/HoldSec from Frame/HoldFrames so the path builder sees seconds.</summary>
    public void SyncSeconds()
    {
        foreach(var k in AllKeys)
        {
            k.TimeSec = k.Frame / (float)Fps;
            k.HoldSec = k.HoldFrames / (float)Fps;
        }
    }

    /// <summary>Change the frame rate keeping every node at the same time in seconds. Hard cuts are kept.</summary>
    public void SetFps(int fps, bool keepSeconds = true, bool snapshot = false)
    {
        fps = Math.Clamp(fps, 1, 240);
        if(fps == Fps) return;
        if(keepSeconds)
        {
            double r = fps / (double)Fps;
            // Validate every rescale on clones before modifying anything; never silently merge keys.
            foreach(var track in Tracks)
            {
                var collision = track.Keys.GroupBy(k => ProgressCurve.Round(k.Frame * r)).FirstOrDefault(g => g.Count() > 1);
                if(collision != null)
                    throw new InvalidOperationException($"轨道「{track.Name}」的第 {string.Join("、", collision.Select(k => k.Frame))} 帧换算后都落在第 {collision.Key} 帧。");
                foreach(var shot in track.Shots) shot.Clone().Rescale(r);
            }
            int length = Math.Max(1, ProgressCurve.Round(Length * r));
            var recordings = new Dictionary<TrackConstraint, SortedList<int, Vector3>>();
            foreach(var constraint in Tracks.SelectMany(track => track.TrackConstraints).Where(c => c.Recorded.Count > 0))
            {
                var recording = new SortedList<int, Vector3>();
                foreach(int frame in constraint.Recorded.Keys.Select(k => ProgressCurve.Round(k * r)).Distinct())
                    recording.Add(frame, constraint.RecordedAt((float)(frame / r))!.Value);
                recordings.Add(constraint, recording);
            }
            if(snapshot) SaveHistory(true);
            foreach(var track in Tracks)
            {
                foreach(var shot in track.Shots) shot.Rescale(r);
                if(track.FollowPath is { } follow)
                {
                    foreach(var point in follow.TimeAnchors.Keys.ToArray()) follow.TimeAnchors[point] *= r;
                    follow.LoopEndFrame *= r;
                }
                foreach(var constraint in track.TrackConstraints)
                {
                    if(recordings.TryGetValue(constraint, out var recording)) constraint.Recorded = recording;
                }
            }
            foreach(var k in AllKeys)
            {
                k.Frame = (int)Math.Round(k.Frame * r, MidpointRounding.AwayFromZero);
                k.HoldFrames = (int)Math.Round(k.HoldFrames * r, MidpointRounding.AwayFromZero);
            }
            Length = length;
            Playhead = (float)(Playhead * r);
            foreach(var t in Tracks) ResolveCollisions(t);
        }
        if(!keepSeconds && snapshot) Snapshot();
        Fps = fps;
        SyncSeconds();
        ResyncGaps();
        Touch();
    }

    // ---------------- per-track frame helpers ----------------

    private CamTrack TrackFor(Keyframe? k, CamTrack? track) => track ?? (k != null ? TrackOf(k) : null) ?? ActiveTrack;

    public bool IsFrameFree(int frame, Keyframe? except = null, CamTrack? track = null)
        => !TrackFor(except, track).Keys.Any(k => k != except && k.Frame == frame);

    public const string TimelineAnchorNotice = "锚点保留小数帧。编辑速度曲线会同步曲线节点的经过帧；拖动曲线节点则按新锚点重新定时。切换锚定或编辑时间会改变速度，部分镜头位置可能变化；可撤销。";

    /// <summary>Whether the followed curve of a track is anchored to the timeline (otherwise to the camera).</summary>
    public static bool CurveAnchoredToTimeline(CamTrack track) => track.FollowPath is { } follow && (follow.EditorTiming || follow.TimeAnchors.Count > 0);

    /// <summary>
    /// Anchors the whole followed curve of a track: to the timeline (every point at the frame the camera
    /// currently reaches it, plus a closed loop's return to its start point; points it never reaches stay
    /// unanchored) or back to the camera. Anchoring is per curve, never per point. Returns false when nothing
    /// changed; undoable.
    /// </summary>
    public bool AnchorCurve(CamTrack track, bool timeline, bool snapshot = true)
    {
        if(track.FollowPath is not { } follow || track.FollowCurve?.PathSpline is not { } spline) return false;
        if(CurveAnchoredToTimeline(track) == timeline) return false;
        var anchors = new Dictionary<Guid, double>();
        double? loopEnd = null;
        double Clamp(double frame) => Math.Clamp(frame, 0, Math.Max(0, Length));
        if(timeline)
        {
            var times = track.Shots.FirstOrDefault()?.CurvePointTimes() ?? [];
            foreach(var t in times)
                if(!t.Repeat && t.Frame is double frame) anchors[t.Id] = Clamp(frame);
            if(anchors.Count == 0) return false;
            if(spline.Cyclic && times.FirstOrDefault(t => t.Repeat && t.Index == 0) is { Frame: double end })
                loopEnd = Clamp(end);
        }
        if(snapshot) Snapshot();
        follow.EditorTiming = false;
        follow.TimeAnchors = anchors;
        follow.LoopEndFrame = loopEnd;
        Touch();
        return true;
    }

    /// <summary>Sets one curve point's anchor frame (<paramref name="loopEnd"/>: a closed curve's return to
    /// its start point); a camera-anchored curve is anchored to the timeline as a whole first.</summary>
    public void SetCurvePointAnchor(CamTrack track, Guid point, double frame, bool snapshot = true, bool loopEnd = false)
    {
        if(track.FollowPath is not { } follow || !double.IsFinite(frame)) return;
        frame = Math.Clamp(frame, 0, Math.Max(0, Length));
        if(loopEnd ? follow.LoopEndFrame == frame : follow.TimeAnchors.TryGetValue(point, out double old) && old == frame) return;
        if(snapshot) Snapshot();
        if(!CurveAnchoredToTimeline(track)) AnchorCurve(track, true, snapshot: false);
        follow.EditorTiming = false;
        if(loopEnd) follow.LoopEndFrame = frame; else follow.TimeAnchors[point] = frame;
        Touch();
    }

    /// <summary>Commits a graph edit in the caller's existing undo snapshot. Keep the curve timeline-anchored,
    /// but let the edited timing drive its crossing frames instead of feeding old anchors back into it.</summary>
    public bool SyncCurveAnchorsFromEditor(ShotCurves shot)
    {
        var track = Tracks.FirstOrDefault(t => t.Shots.Contains(shot));
        if(track?.FollowPath is not { } follow || !CurveAnchoredToTimeline(track)) return false;
        follow.EditorTiming = true;
        foreach(var key in shot.PathProgress.Keys) if(key.NodeId is Guid id) shot.MarkFactorManual(id);
        CurveModel.Ensure(track, Fps);
        Touch();
        return true;
    }

    /// <summary>Nearest free frame to <paramref name="frame"/> in the track (searching outwards), within [0, Length].</summary>
    public int NearestFreeFrame(int frame, Keyframe? except = null, CamTrack? track = null)
    {
        var t = TrackFor(except, track);
        frame = Math.Clamp(frame, 0, Math.Max(0, Length));
        for(int d = 0; d <= Length + 1; d++)
        {
            if(frame + d <= Length && IsFrameFree(frame + d, except, t)) return frame + d;
            if(frame - d >= 0 && IsFrameFree(frame - d, except, t)) return frame - d;
        }
        return frame;
    }

    /// <summary>Moves a node within its own track; returns the frame it ended up on (collisions are refused).</summary>
    public int MoveKey(Keyframe k, int frame)
    {
        frame = Math.Clamp(frame, 0, Math.Max(0, Length));
        if(!IsFrameFree(frame, k)) return k.Frame;
        var owner = TrackOf(k);
        if(owner != null)
        {
            // A time edit must not reorder geometry controls or reshape the route.
            int previous = owner.Keys.Where(n => n.Frame < k.Frame).Select(n => n.Frame).DefaultIfEmpty(-1).Max();
            int next = owner.Keys.Where(n => n.Frame > k.Frame).Select(n => n.Frame).DefaultIfEmpty(int.MaxValue).Min();
            if(frame <= previous || frame >= next) return k.Frame;
            var affected = owner.Shots.SelectMany(s => s.Intervals)
                .Where(i => i.StartNodeId == k.Id || i.EndNodeId == k.Id).ToList();
            try
            {
                foreach(var interval in affected)
                    interval.Clone().Retime(interval.StartNodeId == k.Id ? frame : interval.Start,
                        interval.EndNodeId == k.Id ? frame : interval.End);
            }
            catch(InvalidOperationException) { return k.Frame; }
            foreach(var interval in affected)
                interval.Retime(interval.StartNodeId == k.Id ? frame : interval.Start,
                    interval.EndNodeId == k.Id ? frame : interval.End);
        }
        k.Frame = frame;
        Touch();
        return k.Frame;
    }

    public bool MoveNodes(IReadOnlyDictionary<Guid, int> targets)
    {
        var nodes = AllKeys.Where(k => targets.ContainsKey(k.Id)).ToList();
        if(nodes.Count != targets.Count || nodes.Count == 0) return false;
        foreach(var track in Tracks)
        {
            int previous = -1;
            foreach(var node in track.Keys.OrderBy(k => k.Frame))
            {
                int frame = targets.TryGetValue(node.Id, out int value) ? value : node.Frame;
                if(frame < 0 || frame > Length || frame <= previous) return false;
                previous = frame;
            }
        }
        var affected = Tracks.SelectMany(t => t.Shots).SelectMany(s => s.Intervals)
            .Where(i => targets.ContainsKey(i.StartNodeId) || targets.ContainsKey(i.EndNodeId)).ToList();
        try
        {
            foreach(var interval in affected)
                interval.Clone().Retime(targets.TryGetValue(interval.StartNodeId, out int a) ? a : interval.Start,
                    targets.TryGetValue(interval.EndNodeId, out int b) ? b : interval.End);
        }
        catch(ArgumentException) { return false; }
        catch(InvalidOperationException) { return false; }
        foreach(var interval in affected)
            interval.Retime(targets.TryGetValue(interval.StartNodeId, out int a) ? a : interval.Start,
                targets.TryGetValue(interval.EndNodeId, out int b) ? b : interval.End);
        foreach(var node in nodes) node.Frame = targets[node.Id];
        foreach(var shot in Tracks.SelectMany(t => t.Shots))
            foreach(var node in nodes) shot.Space.MarkTangentsDirty(node.Id, PathInfluence);
        Touch();
        return true;
    }

    /// <summary>Adds a captured camera. Inside a followed shot, split the existing motion first so the new
    /// node inherits its current path progress and offset; the capture still supplies rotation and FoV.</summary>
    public void AddCapturedNode(Keyframe node, bool snapshot = true)
    {
        var track = ActiveTrack;
        if(track.Keys.Any(k => k.Frame == node.Frame)) throw new InvalidOperationException("这一帧已经有镜头节点。");
        CurveModel.Ensure(track, Fps);
        if(snapshot) Snapshot();
        if(track.FollowPath != null && !node.CutIn && track.Shots.FirstOrDefault(s =>
            s.Intervals.Any(i => node.Frame > i.Start && node.Frame < i.End)) is { } shot)
        {
            var rotation = node.Rot; float fov = node.Fov;
            shot.InsertNode(node);
            node.Rot = rotation; node.Fov = fov;
        }
        track.Keys.Add(node);
        CurveModel.Ensure(track, Fps);
        Touch();
    }

    public Keyframe AddSampledNode(ShotCurves shot, int frame)
    {
        var owner = Tracks.FirstOrDefault(t => t.Shots.Contains(shot)) ?? throw new InvalidOperationException("找不到镜头轨道。");
        var existing = owner.Keys.FirstOrDefault(k => k.Frame == frame);
        if(existing != null) { SelectNodes([existing.Id]); return existing; }
        if(!shot.Intervals.Any(i => frame > i.Start && frame < i.End)) throw new InvalidOperationException("请在两个镜头节点之间添加关键帧。");
        var source = owner.Keys.Where(k => shot.SeededNodes.Contains(k.Id)).OrderBy(k => k.Frame).LastOrDefault(k => k.Frame <= frame);
        var pose = shot.Sample(frame);
        var node = new Keyframe { Frame = frame, Pos = pose.Pos, Rot = pose.Rot, Fov = pose.Fov,
            TargetName = source?.TargetName ?? "", BasePos = source?.BasePos ?? default, BaseRot = source?.BaseRot ?? System.Numerics.Quaternion.Identity };
        Snapshot();
        shot.InsertNode(node); owner.Keys.Add(node);
        CurveModel.Ensure(owner, Fps); SelectNodes([node.Id]); Touch();
        return node;
    }

    public void DeleteNodes(IEnumerable<Guid> ids, bool snapshot = true)
    {
        var selected = ids.ToHashSet();
        if(!AllKeys.Any(k => selected.Contains(k.Id))) return;
        if(snapshot) Snapshot();
        foreach(var track in Tracks)
        {
            track.Keys.RemoveAll(k => selected.Contains(k.Id));
            CurveModel.Ensure(track, Fps);
        }
        SelectNodes([]); Touch();
    }

    private void ResolveCollisions(CamTrack t)
    {
        foreach(var k in t.Keys.OrderBy(k => k.Frame).ToList())
            if(!IsFrameFree(k.Frame, k, t))
                k.Frame = NearestFreeFrame(k.Frame, k, t);
    }

    // ---------------- hard-cut auto rule ----------------

    /// <summary>
    /// Auto hard-cut rule, applied only when a node's gap to its predecessor changes:
    /// gap becomes 1 frame -> CutIn on (marked automatic); gap leaves 1 frame -> an automatic CutIn goes off.
    /// A manual choice (CutAuto = false) is never overridden while the gap stays the same.
    /// </summary>
    public void RefreshAutoCuts()
    {
        foreach(var t in Tracks)
        {
            Keyframe? prev = null;
            foreach(var k in t.Keys.OrderBy(k => k.Frame))
            {
                int gap = prev == null ? -1 : k.Frame - prev.Frame;
                if(gap != k.LastGap)
                {
                    if(gap == 1 && !k.CutIn) { k.CutIn = true; k.CutAuto = true; }
                    else if(gap != 1 && k.CutAuto) { k.CutIn = false; k.CutAuto = false; }
                    k.LastGap = gap;
                }
                prev = k;
            }
        }
    }

    /// <summary>Records the current gaps without applying the auto rule (used after fps changes).</summary>
    public void ResyncGaps()
    {
        foreach(var t in Tracks)
        {
            Keyframe? prev = null;
            foreach(var k in t.Keys.OrderBy(k => k.Frame))
            {
                k.LastGap = prev == null ? -1 : k.Frame - prev.Frame;
                prev = k;
            }
        }
    }

    // ---------------- tracks ----------------

    /// <summary>Adds a new empty track on top (highest priority) and makes it active.</summary>
    public CamTrack AddTrack()
    {
        int n = Tracks.Count + 1;
        string name;
        do name = $"镜头 {n++}"; while(Tracks.Any(t => t.Name == name));
        var used = Tracks.Select(t => t.Color).ToHashSet();
        var color = Palette.FirstOrDefault(c => !used.Contains(c), Palette[Tracks.Count % Palette.Length]);
        var track = new CamTrack { Name = name, Color = color };
        Tracks.Insert(0, track);
        ActiveTrackIndex = 0;
        return track;
    }

    public void RemoveTrack(int index)
    {
        if(Tracks.Count <= 1 || index < 0 || index >= Tracks.Count) return;
        Tracks.RemoveAt(index);
        ActiveTrackIndex = Math.Min(ActiveTrackIndex, Tracks.Count - 1);
    }

    /// <summary>Moves a track up (dir = -1, higher priority) or down (dir = +1).</summary>
    public void MoveTrack(int index, int dir)
    {
        int j = index + dir;
        if(index < 0 || index >= Tracks.Count || j < 0 || j >= Tracks.Count) return;
        (Tracks[index], Tracks[j]) = (Tracks[j], Tracks[index]);
        if(ActiveTrackIndex == index) ActiveTrackIndex = j;
        else if(ActiveTrackIndex == j) ActiveTrackIndex = index;
    }

    // ---------------- undo ----------------

    internal sealed record ProjectState(int Version, int Fps, int Length, float Playhead, int ActiveTrackIndex,
        int PathInfluence, Guid[] SelectedNodes, List<CamTrack> Tracks, List<CurveObject> Curves);

    internal ProjectState Capture(bool clone) => new(1, Fps, Length, Playhead, _active, PathInfluence,
        SelectedNodes.ToArray(), clone ? Tracks.Select(t => t.Clone()).ToList() : Tracks,
        clone ? Curves.Select(c => c.Clone()).ToList() : Curves);

    public void ReplaceProject(CamTimeline project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var prepared = ProjectCodec.Decode(ProjectCodec.Encode(project)).Capture(false);
        SaveHistory(true);
        Apply(prepared, true);
    }

    public void Snapshot() => SaveHistory(false);

    private void SaveHistory(bool restoreEditor)
    {
        _undo.Add(new(Capture(true), restoreEditor));
        if(_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo() => Swap(_undo, _redo);
    public void Redo() => Swap(_redo, _undo);

    private void Swap(List<HistoryEntry> from, List<HistoryEntry> to)
    {
        if(from.Count == 0) return;
        var entry = from[^1];
        to.Add(new(Capture(true), entry.RestoreEditor));
        from.RemoveAt(from.Count - 1);
        Restore(entry.State, entry.RestoreEditor);
    }

    internal void Restore(ProjectState state, bool restoreEditor)
    {
        var paths = new Dictionary<Guid, CurvePath?>();
        var curves = state.Curves.ToDictionary(c => c.Id);
        foreach(var track in state.Tracks)
        {
            track.FollowCurve = track.FollowPath is { } follow ? curves.GetValueOrDefault(follow.CurveId) : null;
            FollowBinding? binding = null;
            if(track.Shots.Count > 0 && track.FollowPath is { Enabled: true } constraint && track.FollowCurve is { } curve)
            {
                if(!paths.TryGetValue(curve.Id, out var path)) paths.Add(curve.Id, path = CurvePath.Create(curve));
                if(path != null) binding = new(path, constraint, curve.Id);
            }
            var keys = track.Keys.ToDictionary(k => k.Id);
            foreach(var shot in track.Shots)
            {
                shot.Follow = binding;
                shot.RebindProjectNodes(keys);
            }
        }
        Apply(state, restoreEditor);
    }

    private void Apply(ProjectState state, bool restoreEditor)
    {
        Tracks = state.Tracks; Curves = state.Curves;
        Fps = state.Fps; Length = state.Length; ActiveTrackIndex = state.ActiveTrackIndex;
        if(restoreEditor) { Playhead = state.Playhead; PathInfluence = state.PathInfluence; }
        SelectNodes(restoreEditor ? state.SelectedNodes : SelectedNodes.ToArray());
        Touch();
    }
}
