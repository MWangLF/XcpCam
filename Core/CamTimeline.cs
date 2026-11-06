using System;
using System.Collections.Generic;
using System.Linq;

namespace XcpCam.Core;

/// <summary>
/// Frame-based camera timeline: nodes live on integer frames at <see cref="Fps"/> (normally Brio's
/// Vivacity playback rate). Seconds used by <see cref="CameraPath"/> are derived from frames.
/// </summary>
public sealed class CamTimeline
{
    public int Fps { get; private set; } = 30;
    public int Length = 300;
    public float Playhead;
    public readonly List<Keyframe> Keys = [];

    private readonly List<List<Keyframe>> _undo = [];
    private readonly List<List<Keyframe>> _redo = [];
    private const int MaxUndo = 64;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public IReadOnlyList<Keyframe> Sorted() => Keys.OrderBy(k => k.Frame).ToList();

    public int FirstFrame => Keys.Count == 0 ? 0 : Keys.Min(k => k.Frame);
    public int LastFrame => Keys.Count == 0 ? 0 : Keys.Max(k => k.Frame + k.HoldFrames);

    /// <summary>Path time (seconds from the first node) for a timeline frame.</summary>
    public float FrameToPathTime(float frame) => (frame - FirstFrame) / Fps;
    public float PathTimeToFrame(float t) => FirstFrame + t * Fps;

    /// <summary>Writes TimeSec/HoldSec from Frame/HoldFrames so the path builder sees seconds.</summary>
    public void SyncSeconds()
    {
        foreach(var k in Keys)
        {
            k.TimeSec = k.Frame / (float)Fps;
            k.HoldSec = k.HoldFrames / (float)Fps;
        }
    }

    /// <summary>Change the frame rate keeping every node at the same time in seconds.</summary>
    public void SetFps(int fps, bool keepSeconds = true)
    {
        fps = Math.Clamp(fps, 1, 240);
        if(fps == Fps) return;
        if(keepSeconds)
        {
            double r = fps / (double)Fps;
            foreach(var k in Keys)
            {
                k.Frame = (int)Math.Round(k.Frame * r);
                k.HoldFrames = (int)Math.Round(k.HoldFrames * r);
            }
            Length = Math.Max(1, (int)Math.Round(Length * r));
            Playhead = (float)(Playhead * r);
            ResolveCollisions();
        }
        Fps = fps;
    }

    public bool IsFrameFree(int frame, Keyframe? except = null) => !Keys.Any(k => k != except && k.Frame == frame);

    /// <summary>Nearest free frame to <paramref name="frame"/> (searching outwards), within [0, Length].</summary>
    public int NearestFreeFrame(int frame, Keyframe? except = null)
    {
        frame = Math.Clamp(frame, 0, Math.Max(0, Length));
        for(int d = 0; d <= Length + 1; d++)
        {
            if(frame + d <= Length && IsFrameFree(frame + d, except)) return frame + d;
            if(frame - d >= 0 && IsFrameFree(frame - d, except)) return frame - d;
        }
        return frame;
    }

    /// <summary>Moves a node; returns the frame it ended up on (collisions are skipped, not merged).</summary>
    public int MoveKey(Keyframe k, int frame)
    {
        frame = Math.Clamp(frame, 0, Math.Max(0, Length));
        if(IsFrameFree(frame, k)) k.Frame = frame;
        return k.Frame;
    }

    private void ResolveCollisions()
    {
        foreach(var k in Keys.OrderBy(k => k.Frame).ToList())
            if(!IsFrameFree(k.Frame, k))
                k.Frame = NearestFreeFrame(k.Frame, k);
    }

    // ---------------- undo ----------------

    public void Snapshot()
    {
        _undo.Add(Keys.Select(k => k.Clone()).ToList());
        if(_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo() => Swap(_undo, _redo);
    public void Redo() => Swap(_redo, _undo);

    private void Swap(List<List<Keyframe>> from, List<List<Keyframe>> to)
    {
        if(from.Count == 0) return;
        to.Add(Keys.Select(k => k.Clone()).ToList());
        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        Keys.Clear();
        Keys.AddRange(state.Select(k => k.Clone()));
    }
}
