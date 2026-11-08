using System;
using XcpCam.Core;

namespace XcpCam.BrioInterop;

/// <summary>Owns only the speed changes we made, on the original actor, not whichever actor is selected later.</summary>
public sealed class AnimationSpeedLease
{
    private readonly BrioBridge _brio;
    private BrioBridge.AnimationActor? _actor;
    private float? _originalOverride;
    private float _originalEffective;
    private float? _lastOverride;
    private float _lastEffective;
    public bool Active => _actor != null;
    public nint ActorAddress => _actor?.Address ?? 0;
    public bool Frozen => Active && _lastOverride == 0f;

    public AnimationSpeedLease(BrioBridge brio) { _brio = brio; }

    public bool OwnsCurrentValue()
    {
        if(_actor == null) return false;
        if(!_brio.TryAnimationSpeed(_actor, out var ov, out var actual)
            || ov != _lastOverride || MathF.Abs(actual - _lastEffective) > 1e-4f)
        {
            // External edits take precedence. Do not restore over the user's new setting.
            _actor = null;
            return false;
        }
        return true;
    }

    private bool Acquire(nint address)
    {
        if(_actor != null && _actor.Address == address && OwnsCurrentValue()) return true;
        Release();
        var actor = _brio.GetAnimationActor(address);
        if(actor == null || !_brio.TryAnimationSpeed(actor, out var ov, out var effective)) return false;
        _actor = actor;
        _originalOverride = _lastOverride = ov;
        _originalEffective = _lastEffective = effective;
        return true;
    }

    public bool Pause(nint address) => Write(address, 0);

    public bool Play(nint address)
    {
        if(!Acquire(address)) return false;
        return Write(address, TransportMath.ResumeSpeed(_originalOverride, _originalEffective));
    }

    private bool Write(nint address, float speed)
    {
        if(!Acquire(address) || _actor == null) return false;
        if(!_brio.WriteAnimationSpeed(_actor, speed, speed)) { _actor = null; return false; }
        _lastOverride = speed; _lastEffective = speed;
        return true;
    }

    public void Release()
    {
        if(_actor == null) return;
        if(OwnsCurrentValue() && _actor != null)
            _brio.WriteAnimationSpeed(_actor, _originalOverride, _originalEffective);
        _actor = null;
    }
}
