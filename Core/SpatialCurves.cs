using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XcpCam.Core;

public enum SpatialChannel { PositionX, PositionY, PositionZ, Yaw, Pitch, Roll, Fov }

/// <summary>Persistent target-local pose curves. Integer key parameters are independent of timeline frames.</summary>
public sealed class SpatialCurves
{
    public Dictionary<SpatialChannel, FCurve> Channels = Enum.GetValues<SpatialChannel>().ToDictionary(c => c, _ => new FCurve());
    public int Start, End;
    public long Revision { get; private set; }
    internal Dictionary<Guid, Dictionary<CameraChannel, double>> Anchors = new();
    internal bool LegacyInitialized;
    internal long GeneratedRevision = -1;
    public bool IsDefaultPath => LegacyInitialized && GeneratedRevision == Revision;
    public void Touch() => Revision++;
    public void MarkDirty() => Touch();
    public SpatialCurves Clone() => new()
    {
        Channels = Channels.ToDictionary(p => p.Key, p => p.Value.Clone()), Start = Start, End = End, Revision = Revision,
        LegacyInitialized = LegacyInitialized, GeneratedRevision = GeneratedRevision,
        Anchors = Anchors.ToDictionary(p => p.Key, p => new Dictionary<CameraChannel, double>(p.Value)),
    };
    public XcpFrame Sample(double parameter) => Sample(parameter, parameter, parameter);
    public XcpFrame Sample(double posParam, double rotParam, double fovParam)
    {
        double V(SpatialChannel c, double p) => Channels[c].Evaluate(p);
        var pos = new Vector3((float)V(SpatialChannel.PositionX, posParam), (float)V(SpatialChannel.PositionY, posParam), (float)V(SpatialChannel.PositionZ, posParam));
        const float rad = MathF.PI / 180;
        var rot = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll((float)V(SpatialChannel.Yaw, rotParam) * rad,
            (float)V(SpatialChannel.Pitch, rotParam) * rad, (float)V(SpatialChannel.Roll, rotParam) * rad));
        float fov = (float)V(SpatialChannel.Fov, fovParam);
        return new XcpFrame(pos, rot, (float.IsFinite(fov) ? Math.Clamp(fov, 1, 175) : 45) * rad);
    }
    internal void AddAnchor(Guid id, double position, double rotation, double fov) => Anchors[id] = new()
    { [CameraChannel.Position] = position, [CameraChannel.Rotation] = rotation, [CameraChannel.Fov] = fov };

    internal void Seed(Keyframe node, int parameter)
    {
        AddAnchor(node.Id, parameter, parameter, parameter);
        var near = new Vector3((float)Channels[SpatialChannel.Yaw].Evaluate(parameter),
            (float)Channels[SpatialChannel.Pitch].Evaluate(parameter), (float)Channels[SpatialChannel.Roll].Evaluate(parameter));
        var e = ShotCurves.Euler(node.Rot, near);
        double[] values = [node.Pos.X, node.Pos.Y, node.Pos.Z, e.X, e.Y, e.Z, node.Fov * 180 / MathF.PI];
        foreach(var c in Enum.GetValues<SpatialChannel>())
        {
            var curve = Channels[c];
            var key = curve.Keys.FirstOrDefault(k => k.Frame == parameter);
            if(key == null) { key = new CurveKey { Frame = parameter, NodeId = node.Id, Mode = HandleMode.Auto }; curve.Keys.Add(key); }
            key.Value = values[(int)c]; curve.Sort(); curve.RecalculateHandles();
        }
        var frames = Channels.Values.SelectMany(c => c.Keys).Select(k => k.Frame).ToArray();
        Start = frames.Min(); End = frames.Max(); Touch();
    }

    internal bool SynchronizeNodes(IReadOnlyList<Keyframe> nodes)
    {
        var ids = nodes.Select(n => n.Id).ToHashSet();
        if(Channels.Values.All(c => c.Keys.Count == nodes.Count && c.Keys.All(k => k.NodeId.HasValue && ids.Contains(k.NodeId.Value)) && c.Keys.Select(k => k.NodeId).Distinct().Count() == nodes.Count))
            return false;
        var old = Clone();
        foreach(var channel in Enum.GetValues<SpatialChannel>())
        {
            var group = SpatialFrameMap.Group(channel); var source = old.Channels[channel].Clone();
            foreach(var node in nodes)
            {
                int p = (int)Math.Round(old.Anchors[node.Id][group]);
                if(!source.Keys.Any(k => k.NodeId == node.Id) && !source.Keys.Any(k => k.Frame == p))
                    source.InsertPreserving(p, node.Id);
            }
            var curve = new FCurve();
            for(int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i]; double parameter = old.Anchors[node.Id][group];
                var existing = source.Keys.FirstOrDefault(k => k.NodeId == node.Id);
                var key = existing?.Clone() ?? new CurveKey { Mode = HandleMode.Free };
                key.NodeId = node.Id; key.Frame = i * 1000; key.Value = source.Evaluate(parameter);
                if(existing != null)
                {
                    double leftSpan = i > 0 ? parameter - old.Anchors[nodes[i - 1].Id][group] : 1000;
                    double rightSpan = i + 1 < nodes.Count ? old.Anchors[nodes[i + 1].Id][group] - parameter : 1000;
                    key.Left = new Vector2(leftSpan > 1e-9 ? (float)(key.Left.X * 1000 / leftSpan) : -1000f / 3, key.Left.Y);
                    key.Right = new Vector2(rightSpan > 1e-9 ? (float)(key.Right.X * 1000 / rightSpan) : 1000f / 3, key.Right.Y);
                }
                else
                {
                    key.Left = new Vector2(-1000f / 3, 0); key.Right = new Vector2(1000f / 3, 0);
                }
                curve.Keys.Add(key);
            }
            Channels[channel] = curve;
        }
        Anchors.Clear();
        for(int i = 0; i < nodes.Count; i++) AddAnchor(nodes[i].Id, i * 1000, i * 1000, i * 1000);
        Start = 0; End = Math.Max(0, (nodes.Count - 1) * 1000); Touch();
        return true;
    }

    /// <summary>Write selected pose groups at their independent anchors. Fractional anchors are
    /// represented by visible integer keys; a linear influence solve avoids rounding the pose anchor.</summary>
    public void WritePose(Keyframe node, XcpFrame pose, bool position = true, bool rotation = true, bool fov = true, Vector3? rawEuler = null)
    {
        if(!Anchors.TryGetValue(node.Id, out var anchors)) throw new ArgumentException("Node has no spatial anchor.", nameof(node));
        var copies = Channels.ToDictionary(p => p.Key, p => p.Value.Clone());
        var near = new Vector3((float)Channels[SpatialChannel.Yaw].Evaluate(anchors[CameraChannel.Rotation]),
            (float)Channels[SpatialChannel.Pitch].Evaluate(anchors[CameraChannel.Rotation]), (float)Channels[SpatialChannel.Roll].Evaluate(anchors[CameraChannel.Rotation]));
        var e = rawEuler ?? ShotCurves.Euler(pose.Rot, near);
        double[] values = [pose.Pos.X, pose.Pos.Y, pose.Pos.Z, e.X, e.Y, e.Z, pose.Fov * 180 / MathF.PI];
        foreach(var c in Enum.GetValues<SpatialChannel>())
        {
            var group = (int)c < 3 ? CameraChannel.Position : (int)c < 6 ? CameraChannel.Rotation : CameraChannel.Fov;
            if(group == CameraChannel.Position && !position || group == CameraChannel.Rotation && !rotation || group == CameraChannel.Fov && !fov) continue;
            var curve = copies[c]; double parameter = anchors[group];
            int at = checked((int)Math.Round(parameter, MidpointRounding.AwayFromZero));
            var key = curve.InsertPreserving(at, node.Id);
            double before = curve.Evaluate(parameter); key.Value += 1;
            double weight = curve.Evaluate(parameter) - before; key.Value -= 1;
            if(Math.Abs(weight) < 1e-10)
            {
                at = checked((int)Math.Floor(parameter)); key = curve.InsertPreserving(at, node.Id);
                before = curve.Evaluate(parameter); key.Value += 1; weight = curve.Evaluate(parameter) - before; key.Value -= 1;
            }
            if(Math.Abs(weight) < 1e-10) throw new InvalidOperationException("Spatial anchor cannot be edited with the current constant segment.");
            key.Value += (values[(int)c] - before) / weight;
        }
        foreach(var c in Enum.GetValues<SpatialChannel>())
        {
            var group = (int)c < 3 ? CameraChannel.Position : (int)c < 6 ? CameraChannel.Rotation : CameraChannel.Fov;
            if(group == CameraChannel.Position && position || group == CameraChannel.Rotation && rotation || group == CameraChannel.Fov && fov)
                Channels[c].Keys = copies[c].Keys;
        }
        Touch();
    }

    // Import authored polygons without refitting. A constant bridge keeps old cut boundaries discontinuous.
    internal double Import(SpatialCurves other, HashSet<Guid> ownedNodes)
    {
        int offset = checked(End + 1000 - other.Start);
        foreach(var c in Enum.GetValues<SpatialChannel>())
        {
            var destination = Channels[c];
            if(destination.Keys.Count > 0) destination.Keys[^1].Interpolation = CurveInterpolation.Constant;
            foreach(var key in other.Channels[c].Keys)
            {
                var copy = key.Clone(); copy.Frame = checked(copy.Frame + offset); destination.Keys.Add(copy);
            }
            destination.Sort();
        }
        foreach(var a in other.Anchors)
            if(ownedNodes.Contains(a.Key)) Anchors[a.Key] = a.Value.ToDictionary(p => p.Key, p => p.Value + offset);
        End = checked(other.End + offset); Touch(); return offset;
    }
}
