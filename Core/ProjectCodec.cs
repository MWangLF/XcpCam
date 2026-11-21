using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace XcpCam.Core;

/// <summary>Versioned UTF-8 project payload. Only the listed model members belong to the wire format.</summary>
public static class ProjectCodec
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxCollection = 200_000;
    private const int MaxDepth = 64;
    private static readonly Dictionary<Type, string> Members = new()
    {
        [typeof(CamTimeline.ProjectState)] = "Version Fps Length Playhead ActiveTrackIndex PathInfluence SelectedNodes Tracks Curves",
        [typeof(CamTrack)] = "Name Color Muted Solo Keys Shots FollowPath TrackConstraints",
        [typeof(Keyframe)] = "Id Frame HoldFrames CutIn CutAuto LastGap TimeSec Pos Rot Fov Tangent HoldSec SelfCheckError TargetName BasePos BaseRot",
        [typeof(ShotCurves)] = "Id AnchorNodeId Start End Name Intervals CutRestore CutObserved SeededNodes HoldRegions nodes poses Space PathProgress Offset followCurve EditorMode simpleVisited neutralSimple parameterSimple ManualFactors",
        [typeof(MotionInterval)] = "Id StartNodeId EndNodeId Start End HoldFrames Linked AutoTiming AutoSnapshot ProjectChannels Ranges FollowDistance splitTiming splitCurves splitValues splitHoldFrames splitTimeStart splitTimeEnd",
        [typeof(MotionInterval.ChannelTable)] = "Curves Indices",
        [typeof(SpatialCurves)] = "Channels Start End Revision Anchors LegacyInitialized GeneratedRevision DirtyTangents PreserveIntermediateKeys",
        [typeof(FCurve)] = "Keys",
        [typeof(CurveKey)] = "Id NodeId Frame Subframe Value Interpolation Mode AutomaticTangent Left Right",
        [typeof(CurveObject)] = "Id Name Location Rotation Scale Splines OwnerTrack Revision",
        [typeof(Spline)] = "Type Cyclic Resolution Order Endpoint Points",
        [typeof(CurvePoint)] = "Id Co Left Right LeftType RightType W Tilt Radius",
        [typeof(FollowPathConstraint)] = "CurveId Enabled FollowCurve Forward Up UseRadius TimeAnchors EditorTiming LoopEndFrame",
        [typeof(TrackConstraint)] = "Id Type Enabled Influence TargetName TargetIndex Bone TargetOffset ScreenOffset TrackAxis UpAxis LockAxis KeepFraming Recorded RecordedFor",
        [typeof(XcpFrame)] = "Pos Rot Fov",
        [typeof(Vector2)] = "X Y", [typeof(Vector3)] = "X Y Z", [typeof(Quaternion)] = "X Y Z W",
        [typeof((double, double))] = "Item1 Item2", [typeof((int, int))] = "Item1 Item2",
    };

    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if(info.Kind != JsonTypeInfoKind.Object) return;
            var existing = info.Properties.ToDictionary(p => p.Name);
            info.Properties.Clear();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach(string name in Members[info.Type].Split(' '))
            {
                if(!existing.TryGetValue(name, out var member))
                {
                    if(info.Type.GetField(name, flags) is { } field)
                    {
                        member = info.CreateJsonPropertyInfo(field.FieldType, name);
                        member.Get = field.GetValue; member.Set = field.SetValue;
                    }
                    else
                    {
                        var prop = info.Type.GetProperty(name, flags)!;
                        member = info.CreateJsonPropertyInfo(prop.PropertyType, name);
                        member.Get = prop.GetValue; member.Set = prop.SetValue;
                    }
                }
                else if(member.Set == null && info.Type.GetProperty(name, flags) is { CanWrite: true } property)
                    member.Set = property.SetValue;
                member.IsRequired = name is not ("Subframe" or "PreserveIntermediateKeys");
                info.Properties.Add(member);
            }
        });
        return new JsonSerializerOptions
        {
            TypeInfoResolver = resolver, IncludeFields = true, MaxDepth = MaxDepth,
            AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new FiniteFloat(), new FiniteDouble() },
        };
    }

    private sealed class FiniteFloat : JsonConverter<float>
    {
        public override float Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        { float v = r.GetSingle(); Require(float.IsFinite(v), "浮点数溢出"); return v; }
        public override void Write(Utf8JsonWriter w, float v, JsonSerializerOptions o) => w.WriteNumberValue(v);
    }
    private sealed class FiniteDouble : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        { double v = r.GetDouble(); Require(double.IsFinite(v), "浮点数溢出"); return v; }
        public override void Write(Utf8JsonWriter w, double v, JsonSerializerOptions o) => w.WriteNumberValue(v);
    }

    public static byte[] Encode(CamTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return Guard(() =>
        {
            var state = timeline.Capture(false);
            Validate(state);
            using var stream = new PayloadStream();
            JsonSerializer.Serialize(stream, state, Options);
            var bytes = stream.ToArray();
            CheckSize(bytes);
            return bytes;
        });
    }

    public static CamTimeline Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Guard(() =>
        {
            CheckSize(bytes);
            var state = JsonSerializer.Deserialize<CamTimeline.ProjectState>(bytes, Options);
            if(state == null) throw new InvalidDataException("工程为空。");
            Validate(state);
            var timeline = new CamTimeline();
            timeline.Restore(state, true);
            return timeline;
        });
    }

    private sealed class PayloadStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Require(Length + count <= MaxBytes, "工程长度超限。");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Require(Length + buffer.Length <= MaxBytes, "工程长度超限。");
            base.Write(buffer);
        }
    }

    private static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch(Exception ex) when(ex is JsonException or ArgumentException or NullReferenceException or OverflowException or FormatException)
        { throw new InvalidDataException("工程内容无效。", ex); }
        catch(TargetInvocationException ex) when(ex.InnerException is InvalidDataException error)
        { throw new InvalidDataException(error.Message, error); }
    }

    private static void Require(bool ok, string reason)
    { if(!ok) throw new InvalidDataException(reason); }

    // Bound token and container counts before constructing a model (even tiny {} entries allocate objects).
    private static void CheckSize(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= MaxBytes, "工程长度无效。");
        var r = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxDepth });
        Span<int> counts = stackalloc int[MaxDepth + 1]; counts.Clear();
        int tokens = 0;
        while(r.Read())
        {
            Require(++tokens <= 4_000_000, "工程条目过多。");
            if(r.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject) continue;
            Require(++counts[r.CurrentDepth] <= MaxCollection * 2, "工程集合过大。");
            if(r.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) counts[r.CurrentDepth + 1] = 0;
            Require(r.ValueSpan.Length <= 1_048_576, "工程字段过长。");
        }
    }

    private static void Unique(Guid id, HashSet<Guid> ids) => Require(id != Guid.Empty && ids.Add(id), "工程标识为空或重复。");
    private static void Validate(CamTimeline.ProjectState state)
    {
        Require(state.Version == 1, "不支持的工程版本。");
        Require(state.Fps is >= 1 and <= 240 && state.Length > 0 && state.Length < int.MaxValue - 1 &&
            state.Tracks.Count > 0 && (uint)state.ActiveTrackIndex < state.Tracks.Count && state.PathInfluence >= 0, "时间线参数无效。");
        var objects = new Dictionary<Guid, CurveObject>();
        var ids = new HashSet<Guid>();
        foreach(var curve in state.Curves)
        {
            Unique(curve.Id, ids); objects.Add(curve.Id, curve);
            foreach(var spline in curve.Splines)
            {
                Require(spline.Resolution is >= 1 and <= 1024 && spline.Order is >= 2 and <= 64, "样条参数无效。");
                foreach(var point in spline.Points) Unique(point.Id, ids);
            }
        }
        var nodeIds = new HashSet<Guid>();
        foreach(var track in state.Tracks)
        {
            var frames = new HashSet<int>(); var owned = new HashSet<Guid>();
            foreach(var node in track.Keys)
            {
                Unique(node.Id, nodeIds); owned.Add(node.Id);
                Require(node.Frame >= 0 && node.HoldFrames >= 0 && (long)node.Frame + node.HoldFrames < int.MaxValue && frames.Add(node.Frame), "节点帧冲突或越界。");
            }
            if(track.FollowPath is { } follow)
            {
                Require(objects.TryGetValue(follow.CurveId, out var target), "跟随路径引用了不存在的曲线。");
                var points = target!.Splines.SelectMany(s => s.Points).Select(p => p.Id).ToHashSet();
                Require(follow.TimeAnchors.Keys.All(points.Contains), "时间锚点引用了不存在的曲线点。");
            }
            foreach(var constraint in track.TrackConstraints)
            {
                Unique(constraint.Id, ids);
                Require(constraint.Recorded.Keys.All(f => f >= 0), "追踪记录帧无效。");
            }
            foreach(var shot in track.Shots)
            {
                Unique(shot.Id, ids);
                Require(owned.Contains(shot.AnchorNodeId) && shot.SeededNodes.IsSubsetOf(owned) && shot.Start <= shot.End && shot.Offset.Length == 3, "镜头段引用或范围无效。");
                Require(shot.HoldRegions.All(h => h.start <= h.end), "停留范围无效。");
                Complete(shot.Space.Channels);
                Require(shot.Space.Start <= shot.Space.End, "空间曲线范围无效。");
                foreach(var curve in shot.Space.Channels.Values.Concat(shot.Offset).Append(shot.PathProgress)) ValidateCurve(curve, owned);
                foreach(var anchor in shot.Space.Anchors.Values) Complete(anchor);
                foreach(var interval in shot.Intervals)
                {
                    Unique(interval.Id, ids);
                    Require(owned.Contains(interval.StartNodeId) && owned.Contains(interval.EndNodeId) &&
                        shot.Space.Anchors.ContainsKey(interval.StartNodeId) && shot.Space.Anchors.ContainsKey(interval.EndNodeId), "区间引用了不存在的节点或空间锚点。");
                    ValidateInterval(interval, 0);
                }
                shot.ValidateProjectNodes(owned);
            }
        }
        Require(state.SelectedNodes.All(nodeIds.Contains) && state.SelectedNodes.Distinct().Count() == state.SelectedNodes.Length, "选区节点无效。");
    }

    private static void Complete<TEnum,TValue>(Dictionary<TEnum,TValue> map) where TEnum : struct, Enum
    {
        var channels = Enum.GetValues<TEnum>();
        Require(map.Count == channels.Length && channels.All(map.ContainsKey), "通道缺失或无效。");
    }
    private static void ValidateCurve(FCurve curve, HashSet<Guid>? owned = null)
    {
        var frames = new HashSet<double>(); var ids = new HashSet<Guid>();
        foreach(var key in curve.Keys)
        {
            Unique(key.Id, ids);
            Require(owned == null || key.NodeId == null || owned.Contains(key.NodeId.Value), "曲线关键帧引用了不存在的节点。");
            Require((key.Subframe == null || key.NodeId == null && Math.Abs(key.Time - key.Frame) <= .5) && frames.Add(key.Time), "曲线关键帧重复或小数帧无效。");
        }
    }
    private static void ValidateInterval(MotionInterval i, int depth)
    {
        Require(depth < MaxDepth && i.End > i.Start && i.StartNodeId != i.EndNodeId && i.HoldFrames >= 0, "运动区间无效。");
        Complete(i.Channels);
        Require(!i.Linked || i.Channels.Values.Distinct().Count() == 1, "联动通道未共享曲线。");
        foreach(var curve in i.Channels.Values.Distinct())
        {
            ValidateCurve(curve);
            Require(curve.Keys.Count >= 2 && curve.Keys[0].Frame == i.Start && curve.Keys[^1].Frame == i.End &&
                curve.Keys.All(k => k.Frame >= i.Start && k.Frame <= i.End), "运动曲线端点无效。");
        }
        foreach(var snapshot in new[] { i.AutoSnapshot, i.splitCurves })
            if(snapshot != null) { Complete(snapshot); foreach(var curve in snapshot.Values) ValidateCurve(curve); }
        Require(i.Ranges.Keys.All(Enum.IsDefined), "空间参数通道无效。");
        if(i.splitValues != null) Complete(i.splitValues);
        if(i.splitTiming != null)
        {
            Require(i.splitValues != null && i.splitCurves != null, "细分快照不完整。");
            ValidateInterval(i.splitTiming, depth + 1);
        }
    }
}
