using Dalamud.Plugin.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.BrioInterop;

/// <summary>
/// Mode 2: bake the composed camera (all tracks, hard cuts included) into a Brio free camera's Vivacity timeline
/// track, so the timeline itself drives the camera (scrub / pause / loop all stay in sync). Cuts become
/// InterpolationMode.Step keys with a key on the frame before, so they are instantaneous in Brio.
/// The relative path is placed in the world using the current GPose target, then converted to Brio free-cam
/// yaw / pitch / roll (see <see cref="FreeCamMath"/>).
/// </summary>
public sealed class TimelineBake
{
    private readonly BrioBridge _brio;
    private readonly IPluginLog _log;

    public int StepFrames = 1;
    public bool ClearExisting = true;
    /// <summary>Base vertical FoV the free camera's FoV offset is added to (Brio default 0.78).</summary>
    public float BaseFov = 0.78f;

    public string Status { get; private set; } = "";

    private BrioBridge.FreeCam? _undoCam;
    private IList? _undoKeys;
    private int _undoFrameMax;
    public bool CanUndo => _undoCam != null && _undoKeys != null;

    public TimelineBake(BrioBridge brio, IPluginLog log) { _brio = brio; _log = log; }

    /// <summary>Converts composed bake keys (relative poses) into Brio free-cam keys in world space.</summary>
    public List<BrioBridge.FreeCamKey> ToFreeCamKeys(IReadOnlyList<BakeKey> keys, Vector3 basePos, Quaternion baseRot, out float maxPitch)
    {
        var list = new List<BrioBridge.FreeCamKey>(keys.Count);
        float? prevYaw = null;
        maxPitch = 0;
        foreach(var k in keys)
        {
            var worldPos = Vector3.Transform(k.Pose.Pos, baseRot) + basePos;
            var worldRot = Quaternion.Normalize(baseRot * k.Pose.Rot);
            // never unwrap across a cut: the new shot takes the short way from its own heading
            var (yaw, pitch, roll) = FreeCamMath.FromWorldRotation(worldRot, k.Step ? null : prevYaw);
            prevYaw = yaw;
            maxPitch = MathF.Max(maxPitch, MathF.Abs(pitch));
            list.Add(new BrioBridge.FreeCamKey(k.Frame, worldPos, yaw, pitch, roll, k.Pose.Fov - BaseFov, k.Step));
        }
        return list;
    }

    public void Bake(BrioBridge.FreeCam cam, ShotComposer comp, Vector3 basePos, Quaternion baseRot)
    {
        int fps = _brio.TimelineFps;
        var bakeKeys = comp.BakeForTimeline(fps, StepFrames);
        var keys = ToFreeCamKeys(bakeKeys, basePos, baseRot, out float maxPitch);
        int firstFrame = keys.Count > 0 ? keys[0].Frame : 0;
        int lastFrame = keys.Count > 0 ? keys[^1].Frame : 0;
        int cuts = keys.Count(k => k.Step);

        // undo snapshot
        var current = _brio.GetTrackKeyframes(cam);
        _undoCam = cam;
        _undoKeys = current == null ? null : _brio.CopyList(current);
        _undoFrameMax = _brio.FrameMax;

        if(ClearExisting) _brio.ClearTrack(cam);
        _brio.AddCameraKeys(cam, keys);

        if(_brio.FrameMax < lastFrame) _brio.FrameMax = lastFrame;

        string pitchNote = "";
        if(maxPitch > 1.5f && !_brio.GetDelimitAngle(cam))
        {
            _brio.SetDelimitAngle(cam, true);
            pitchNote = "；俯仰角超过 ±86°，已为该相机开启 Brio 的「解除角度限制」";
        }

        _brio.EnsureInTimeline(cam);
        _brio.ApplyTimelineFrame();

        Status = $"已烘焙 {keys.Count} 个关键帧到「{cam.Name}」（第 {firstFrame}–{lastFrame} 帧 @ {fps}fps，{cuts} 处硬切）{pitchNote}";
        _log.Info($"TimelineBake: {keys.Count} keys into {cam.Name}, frames {firstFrame}-{lastFrame} @ {fps}fps, cuts {cuts}, baseFov {BaseFov}");
    }

    public void Undo()
    {
        if(_undoCam == null || _undoKeys == null) return;
        _brio.RestoreTrackKeyframes(_undoCam, _undoKeys);
        _brio.FrameMax = _undoFrameMax;
        _brio.ApplyTimelineFrame();
        Status = $"已撤销对「{_undoCam.Name}」的烘焙";
        _undoCam = null;
        _undoKeys = null;
    }
}
