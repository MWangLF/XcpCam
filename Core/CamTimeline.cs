using System;
using System.Collections.Generic;
using System.Linq;

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

    public CamTrack Clone() => new()
    {
        Name = Name, Color = Color, Muted = Muted, Solo = Solo,
        Keys = Keys.Select(k => k.Clone()).ToList(),
        Shots = Shots.Select(s => s.Clone()).ToList(),
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
    private readonly List<(List<CamTrack> tracks, int active, int fps, int length)> _undo = [];
    private readonly List<(List<CamTrack> tracks, int active, int fps, int length)> _redo = [];
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
    public void SetFps(int fps, bool keepSeconds = true)
    {
        fps = Math.Clamp(fps, 1, 240);
        if(fps == Fps) return;
        if(keepSeconds)
        {
            double r = fps / (double)Fps;
            // Validate every rescale on clones before modifying anything; never silently merge keys.
            foreach(var track in Tracks)
            {
                if(track.Keys.GroupBy(k => (int)Math.Round(k.Frame * r, MidpointRounding.AwayFromZero)).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("更改帧率会使镜头节点占用同一帧，请先移动冲突节点。");
                foreach(var shot in track.Shots) shot.Clone().Rescale(r);
            }
            foreach(var track in Tracks)
                foreach(var shot in track.Shots)
                    shot.Rescale(r);
            foreach(var k in AllKeys)
            {
                k.Frame = (int)Math.Round(k.Frame * r, MidpointRounding.AwayFromZero);
                k.HoldFrames = (int)Math.Round(k.HoldFrames * r, MidpointRounding.AwayFromZero);
            }
            Length = Math.Max(1, (int)Math.Round(Length * r, MidpointRounding.AwayFromZero));
            Playhead = (float)(Playhead * r);
            foreach(var t in Tracks) ResolveCollisions(t);
        }
        Fps = fps;
        ResyncGaps();
        Touch();
    }

    // ---------------- per-track frame helpers ----------------

    private CamTrack TrackFor(Keyframe? k, CamTrack? track) => track ?? (k != null ? TrackOf(k) : null) ?? ActiveTrack;

    public bool IsFrameFree(int frame, Keyframe? except = null, CamTrack? track = null)
        => !TrackFor(except, track).Keys.Any(k => k != except && k.Frame == frame);

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
        Touch();
        return true;
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

    public void DeleteNodes(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        if(!AllKeys.Any(k => selected.Contains(k.Id))) return;
        Snapshot();
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

    private (List<CamTrack>, int, int, int) Capture() => (Tracks.Select(t => t.Clone()).ToList(), ActiveTrackIndex, Fps, Length);

    public void Snapshot()
    {
        _undo.Add(Capture());
        if(_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo() => Swap(_undo, _redo);
    public void Redo() => Swap(_redo, _undo);

    private void Swap(List<(List<CamTrack> tracks, int active, int fps, int length)> from,
        List<(List<CamTrack> tracks, int active, int fps, int length)> to)
    {
        if(from.Count == 0) return;
        to.Add(Capture());
        var (tracks, active, fps, length) = from[^1];
        from.RemoveAt(from.Count - 1);
        Tracks = tracks.Select(t => t.Clone()).ToList();
        ActiveTrackIndex = active;
        Fps = fps; Length = length;
        SelectNodes(SelectedNodes.ToArray());
        Touch();
    }
}
