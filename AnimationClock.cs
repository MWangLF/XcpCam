using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;

using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XcpCam;

/// <summary>
/// The GPose target's current animation playhead — the same value Brio's "Animation Scruber" shows and edits
/// (Brio/UI/Controls/Editors/ActionTimelineEditor.cs DrawFirstScrub):
///   CharacterBase.Skeleton -> PartialSkeletons[0] -> GetHavokAnimatedSkeleton(0) -> AnimationControls[0]
///   -> hkaAnimationControl.LocalTime (seconds) / Binding.Animation.Duration (seconds).
/// Read and written directly from game memory; does not depend on Brio internals.
/// </summary>
public sealed unsafe class AnimationClock
{
    private readonly ITargetManager _targets;

    public bool Available { get; private set; }
    /// <summary>Animation local time in seconds.</summary>
    public float Time { get; private set; }
    /// <summary>Animation length in seconds.</summary>
    public float Duration { get; private set; }
    /// <summary>True when the local time advanced since the previous <see cref="Update"/>.</summary>
    public bool IsAdvancing { get; private set; }
    /// <summary>Debounced "animation is playing": advanced within the last 0.3 s (avoids flapping between ticks).</summary>
    public bool IsPlaying => Available && (DateTime.UtcNow - _lastAdvanceUtc).TotalSeconds < 0.3;
    private DateTime _lastAdvanceUtc = DateTime.MinValue;
    /// <summary>True when the local time jumped backwards (loop wrap or user scrub back).</summary>
    public bool Wrapped { get; private set; }
    public string Status { get; private set; } = "";

    private float _lastTime = float.NaN;

    public AnimationClock(ITargetManager targets) { _targets = targets; }

    private FFXIVClientStructs.Havok.Animation.Playback.Control.hkaAnimationControl* GetControl(out float duration, out string error)
    {
        duration = 0; error = "";
        var go = _targets.GPoseTarget;
        if(go == null) { error = "没有 GPose 目标"; return null; }
        var native = (GameObjectStruct*)go.Address;
        if(native == null || native->DrawObject == null) { error = "目标没有模型"; return null; }
        if(native->DrawObject->Object.GetObjectType() != ObjectType.CharacterBase) { error = "目标不是角色"; return null; }

        var chara = (CharacterBase*)native->DrawObject;
        var skel = chara->Skeleton;
        if(skel == null || skel->PartialSkeletonCount <= 0) { error = "目标没有骨骼"; return null; }

        var partial = &skel->PartialSkeletons[0];
        var animated = partial->GetHavokAnimatedSkeleton(0);
        if(animated == null || animated->AnimationControls.Length <= 0) { error = "目标当前没有播放动画"; return null; }

        var control = animated->AnimationControls[0].Value;
        if(control == null) { error = "目标当前没有播放动画"; return null; }

        var binding = control->hkaAnimationControl.Binding;
        if(binding.ptr == null || binding.ptr->Animation.ptr == null) { error = "目标当前没有播放动画"; return null; }

        duration = binding.ptr->Animation.ptr->Duration;
        return &control->hkaAnimationControl;
    }

    /// <summary>Call once per framework tick.</summary>
    public void Update()
    {
        Wrapped = false;
        IsAdvancing = false;
        try
        {
            var c = GetControl(out var dur, out var err);
            if(c == null)
            {
                Available = false;
                Status = err;
                _lastTime = float.NaN;
                return;
            }
            Available = true;
            Duration = dur;
            Time = c->LocalTime;
            if(!float.IsNaN(_lastTime))
            {
                if(Time > _lastTime + 1e-5f) { IsAdvancing = true; _lastAdvanceUtc = DateTime.UtcNow; }
                else if(Time + 0.05f < _lastTime) Wrapped = true;
            }
            _lastTime = Time;
            Status = $"{Time:0.00}s / {Duration:0.00}s";
        }
        catch(Exception ex)
        {
            Available = false;
            Status = $"读取动画失败：{ex.Message}";
        }
    }

    /// <summary>Scrubs the animation to <paramref name="seconds"/> (like dragging Brio's scrubber).</summary>
    public bool Seek(float seconds)
    {
        var c = GetControl(out var dur, out _);
        if(c == null) return false;
        c->LocalTime = Math.Clamp(seconds, 0f, MathF.Max(0f, dur));
        _lastTime = c->LocalTime;
        Time = c->LocalTime;
        _lastAdvanceUtc = DateTime.MinValue; // our own seek is not "playing"
        return true;
    }
}
