using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XcpCam.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XcpCam;

/// <summary>
/// Live tracking targets: a character's bone in world space, read from its Havok model-space pose and the
/// skeleton transform. Pointers are re-resolved on every read (like <see cref="AnimationClock"/>); results are
/// cached for one framework tick, so sampling many frames in one tick reads the game once per target.
/// </summary>
public sealed unsafe class BoneSource : ITrackTargetSource
{
    private readonly IObjectTable _objects;
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly Dictionary<string, Vector3?> _tick = new();
    private readonly Dictionary<nint, Dictionary<string, int>> _boneIndex = new();

    /// <summary>Common bones listed first in the picker.</summary>
    public static readonly (string Bone, string Label)[] Common =
    [
        ("j_kao", "头"), ("j_kubi", "颈"), ("j_sebo_c", "胸"), ("j_sebo_b", "背"), ("j_kosi", "腰"),
        ("j_te_l", "左手"), ("j_te_r", "右手"), ("j_asi_e_l", "左脚"), ("j_asi_e_r", "右脚"), ("n_hara", "根骨骼"),
    ];

    public BoneSource(IObjectTable objects, ITargetManager targets, IClientState clientState)
    {
        _objects = objects; _targets = targets; _clientState = clientState;
    }

    public float Aspect
    {
        get
        {
            var size = ImGui.GetIO().DisplaySize;
            return size.X > 1 && size.Y > 1 ? size.X / size.Y : TrackSolver.DefaultAspect;
        }
    }

    /// <summary>Call once per framework tick.</summary>
    public void NewTick()
    {
        _tick.Clear();
        if(_boneIndex.Count > 64) _boneIndex.Clear();
    }

    public Vector3? Position(TrackConstraint c, float frame)
    {
        string key = $"{c.TargetName}|{c.TargetIndex}|{c.Bone}";
        if(_tick.TryGetValue(key, out var cached)) return cached;
        Vector3? result = null;
        try { if(Resolve(c) is { } character) result = BoneWorld(character, c.Bone, out _); }
        catch { result = null; }
        _tick[key] = result;
        return result;
    }

    /// <summary>Characters that can be tracked (in GPose: the GPose actors, including Brio's).</summary>
    public List<(int Index, string Name)> Characters()
    {
        var list = new List<(int, string)>();
        bool gpose = _clientState.IsGPosing;
        foreach(var o in _objects)
        {
            if(o is not ICharacter ch || o.Address == 0) continue;
            if(gpose && o.ObjectIndex < 200) continue; // GPose actors live at 200+
            var name = ch.Name.TextValue;
            if(!string.IsNullOrEmpty(name)) list.Add((o.ObjectIndex, name));
        }
        return list;
    }

    /// <summary>The character of a constraint: the GPose target for an empty name, else by index hint, else by name.</summary>
    public IGameObject? Resolve(TrackConstraint c) => Resolve(c.TargetName, ref c.TargetIndex);

    /// <summary>A character by name with an object-index hint (updated when it moved); empty name = the GPose target.</summary>
    public IGameObject? Resolve(string name, ref int index)
    {
        if(string.IsNullOrEmpty(name)) return _targets.GPoseTarget;
        if(index >= 0 && index < _objects.Length && _objects[index] is ICharacter hinted && hinted.Name.TextValue == name)
            return hinted;
        var match = Characters().Where(x => x.Name == name).Select(x => (int?)x.Index).FirstOrDefault();
        if(match is int found) { index = found; return _objects[found]; }
        return null;
    }

    private static CharacterBase* CharacterBaseOf(IGameObject go)
    {
        var native = (GameObjectStruct*)go.Address;
        if(native == null || native->DrawObject == null) return null;
        if(native->DrawObject->Object.GetObjectType() != ObjectType.CharacterBase) return null;
        var cb = (CharacterBase*)native->DrawObject;
        return cb->Skeleton == null || cb->Skeleton->PartialSkeletonCount <= 0 ? null : cb;
    }

    /// <summary>Bone names of a character, in skeleton order (all partial skeletons).</summary>
    public List<string> BoneNames(IGameObject go)
    {
        var names = new List<string>();
        var cb = CharacterBaseOf(go);
        if(cb == null) return names;
        var skel = cb->Skeleton;
        for(int p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var pose = skel->PartialSkeletons[p].GetHavokPose(0);
            if(pose == null || pose->Skeleton == null) continue;
            var bones = pose->Skeleton->Bones;
            for(int i = 0; i < bones.Length; i++)
                if(bones[i].Name.String is { Length: > 0 } n && !names.Contains(n)) names.Add(n);
        }
        return names;
    }

    /// <summary>World position of a named bone, or null (no model / bone).</summary>
    public Vector3? BoneWorld(IGameObject go, string bone, out string error)
    {
        error = "";
        var cb = CharacterBaseOf(go);
        if(cb == null) { error = "角色没有模型或骨骼"; return null; }
        var skel = cb->Skeleton;
        for(int p = 0; p < skel->PartialSkeletonCount; p++)
        {
            var pose = skel->PartialSkeletons[p].GetHavokPose(0);
            if(pose == null || pose->Skeleton == null) continue;
            var sk = pose->Skeleton;
            if(!_boneIndex.TryGetValue((nint)sk, out var index))
            {
                index = new Dictionary<string, int>();
                for(int i = 0; i < sk->Bones.Length; i++)
                    if(sk->Bones[i].Name.String is { Length: > 0 } n) index.TryAdd(n, i);
                _boneIndex[(nint)sk] = index;
            }
            if(!index.TryGetValue(bone, out int b)) continue;
            var model = pose->ModelPose;
            if(b >= model.Length) continue;
            var tr = model[b].Translation;
            var t = skel->Transform;
            Vector3 position = t.Position, scale = t.Scale;
            Quaternion rotation = t.Rotation;
            var world = position + Vector3.Transform(new Vector3(tr.X, tr.Y, tr.Z) * scale, rotation);
            return XcpMath.IsFinite(world) ? world : null;
        }
        error = $"找不到骨骼 {bone}";
        return null;
    }
}
