using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using XcpCam.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using AnimationControl = FFXIVClientStructs.Havok.Animation.Playback.Control.Default.hkaDefaultAnimationControl;

namespace XcpCam;

/// <summary>
/// Reads the same first skeleton/control as Brio's Animation Scruber. Native loop counters distinguish an
/// actual completed cycle from a backward scrub, including short clips or multiple cycles between updates.
/// Pointers are re-resolved for every observation/seek, never retained as dereferenceable pointers.
/// </summary>
public sealed unsafe class AnimationClock
{
    /// <summary>The bound character (default: the GPose target), resolved again for every read.</summary>
    private readonly Func<IGameObject?> _actor;
    private readonly TransportState _transport = new();
    public bool Available { get; private set; }
    public float Time => (float)Sample.Time;
    public float Duration => (float)Sample.Duration;
    public AnimationSample Sample { get; private set; }
    public bool IsPlaying => Available && _transport.IsPlaying;
    public bool Ended => _transport.State == PlaybackState.Ended;
    public bool Wrapped => _transport.Cycle;
    public bool Discontinuity => _transport.Discontinuity;
    public long Generation => _transport.Generation;
    public bool MustFreeze { get; private set; }
    public bool IdentityChanged { get; private set; }
    public string Status { get; private set; } = "";

    public AnimationClock(Func<IGameObject?> actor) { _actor = actor; }

    private AnimationControl* GetControl(out AnimationSample sample, out string error)
    {
        sample = default; error = "";
        var go = _actor();
        if(go == null) { error = "找不到绑定的角色（没有 GPose 目标，或所选角色已不在场景中）"; return null; }
        var native = (GameObjectStruct*)go.Address;
        if(native == null || native->DrawObject == null) { error = "目标没有模型"; return null; }
        if(native->DrawObject->Object.GetObjectType() != ObjectType.CharacterBase) { error = "目标不是角色"; return null; }
        var skel = ((CharacterBase*)native->DrawObject)->Skeleton;
        if(skel == null || skel->PartialSkeletonCount <= 0) { error = "目标没有骨骼"; return null; }
        var animated = skel->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if(animated == null || animated->AnimationControls.Length <= 0) { error = "目标没有动画控制器"; return null; }
        var control = animated->AnimationControls[0].Value;
        if(control == null) { error = "目标当前没有播放动画"; return null; }
        var binding = control->Binding.ptr;
        if(binding == null || binding->Animation.ptr == null) { error = "目标当前没有播放动画"; return null; }
        var anim = binding->Animation.ptr;
        if(!float.IsFinite(anim->Duration) || anim->Duration <= 0 || !float.IsFinite(control->LocalTime))
        { error = "动画时长或进度无效"; return null; }
        sample = new AnimationSample(new AnimationIdentity(go.Address, go.GameObjectId, (nint)control, (nint)binding, (nint)anim),
            control->LocalTime, anim->Duration, control->OverflowCount, control->UnderflowCount);
        return control;
    }

    public void Reset()
    {
        Available = false;
        Sample = default;
        MustFreeze = IdentityChanged = false;
        _transport.Reset();
    }

    public void Update(float dt, bool loop, bool speedAllowsAdvance)
    {
        MustFreeze = IdentityChanged = false;
        try
        {
            var control = GetControl(out var sample, out var error);
            if(control == null)
            {
                if(Available) Reset();
                Status = error;
                return;
            }
            IdentityChanged = !Available || sample.Identity != Sample.Identity;
            bool externalSeekAfterEnd = _transport.State == PlaybackState.Ended && !IdentityChanged
                && sample.OverflowCount == Sample.OverflowCount && sample.Time < Sample.Time - 1e-6;
            Available = true;
            Sample = sample;
            if(externalSeekAfterEnd) _transport.Seek(sample); // respect a scrub in Brio after completion
            MustFreeze = _transport.Observe(sample, dt, loop, speedAllowsAdvance);
            Status = $"{Time:0.00}s / {Duration:0.00}s";
        }
        catch(Exception ex)
        {
            Reset();
            Status = $"读取动画失败：{ex.Message}";
        }
    }

    /// <summary>Reject a command issued after the target/control changed but before the next framework observation.</summary>
    public bool MatchesCurrent()
    {
        var control = GetControl(out var sample, out _);
        return Available && control != null && sample.Identity == Sample.Identity;
    }

    public bool Seek(float seconds)
    {
        var control = GetControl(out var sample, out _);
        if(!Available || control == null || sample.Identity != Sample.Identity) return false;
        control->LocalTime = Math.Clamp(seconds, 0, (float)sample.Duration);
        Sample = sample with { Time = control->LocalTime };
        _transport.Seek(Sample);
        MustFreeze = false;
        return true;
    }

    public void Play() => _transport.Play(Sample);
    public void Pause() => _transport.Pause();
    public void Stop() => _transport.Stop();

    /// <summary>Correct a native wrap back to the exact endpoint, without clearing the latched Ended state.</summary>
    public bool FreezeAtEnd()
    {
        var control = GetControl(out var sample, out _);
        if(control == null || sample.Identity != Sample.Identity) return false;
        control->LocalTime = (float)sample.Duration;
        Sample = sample with { Time = sample.Duration };
        MustFreeze = false;
        return true;
    }
}
