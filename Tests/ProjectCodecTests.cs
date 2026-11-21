using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class ProjectCodecTests
{
    private static void Check(bool ok, string message)
    { if(!ok) throw new InvalidOperationException(message); }

    private static void Reject(Action action)
    {
        try { action(); }
        catch(InvalidDataException) { return; }
        throw new InvalidOperationException("损坏的工程通过了校验。");
    }

    public static void Run()
    {
        var source = new CamTimeline { Playhead = 19.25f, Length = 400, PathInfluence = 3 };
        source.SetFps(48, false);
        var track = source.ActiveTrack;
        source.AddTrack().Muted = true; source.ActiveTrackIndex = 1;
        track.Name = "跟随镜头"; track.Solo = true;
        foreach(int frame in new[] { 0, 40, 100 })
            track.Keys.Add(new Keyframe { Frame = frame, Pos = new(frame * .1f, 2, 3), Rot = Quaternion.Identity,
                Fov = .6f, TargetName = "目标", HoldFrames = frame == 0 ? 8 : 0 });
        var spline = new Spline { Type = SplineType.Bezier, Cyclic = true, Points = [new(), new() { Co = new(4, 2, 1) }, new() { Co = new(9, 0, 5) }] };
        var curve = new CurveObject { Splines = [spline], Name = "路径", Location = new(1, 0, 0) }; curve.Touch();
        source.Curves.Add(curve); source.SetFollowPath(track, curve);
        var shot = track.Shots.Single();
        shot.SetEditorMode(CurveEditorMode.Advanced); shot.SetEditorMode(CurveEditorMode.Simple); shot.ActivateSimpleTiming();
        var split = source.AddSampledNode(shot, 16); shot.SetEditorMode(CurveEditorMode.Advanced);
        shot.MarkFactorManual(split.Id);
        shot.Offset[0].Keys.Add(new CurveKey { Frame = 18, Value = 2.75, Mode = HandleMode.Free, Left = new(-2, 3), Right = new(3, -4) });
        shot.Space.MarkTangentsDirty(split.Id, 1);
        shot.Space.PreserveIntermediateKeys = true;
        shot.Intervals[^1].Unlink();
        track.FollowPath!.TimeAnchors[spline.Points[1].Id] = 18.25;
        track.FollowPath.LoopEndFrame = 99.875;
        track.FollowPath.EditorTiming = true;
        track.TrackConstraints.Add(new TrackConstraint { Bone = "j_kao", TargetName = "目标", KeepFraming = true,
            RecordedFor = "目标|j_kao", Recorded = { [3] = new(2, 4, 6), [9] = new(4, 6, 8) } });
        source.SelectNodes([split.Id]);
        var samples = Enumerable.Range(0, 100).Select(i => shot.Sample(i + .375f)).ToArray();
        var bytes = ProjectCodec.Encode(source);
        var restored = ProjectCodec.Decode(bytes);
        var copy = restored.ActiveTrack.Shots.Single();
        Check(bytes.SequenceEqual(ProjectCodec.Encode(restored)), "往返改变了工程状态。");
        Check(copy.Space.PreserveIntermediateKeys, "拟合中间键保形标志丢失。");
        var old = JsonNode.Parse(bytes)!;
        old["Tracks"]![1]!["Shots"]![0]!["Space"]!.AsObject().Remove("PreserveIntermediateKeys");
        Check(!ProjectCodec.Decode(Encoding.UTF8.GetBytes(old.ToJsonString())).ActiveTrack.Shots[0].Space.PreserveIntermediateKeys,
            "旧工程缺省保形标志应为 false。");
        Check(!restored.CanUndo && !restored.CanRedo && restored.SelectedNodes.SetEquals([split.Id]), "新工程历史或选区错误。");
        Check(ReferenceEquals(restored.ActiveTrack.FollowCurve, restored.Curves[0]) && copy.IsFollowing, "跟随目标绑定错误。");
        Check(ReferenceEquals(copy.Intervals[0].Channels[CameraChannel.Position], copy.Intervals[0].Channels[CameraChannel.Rotation]) &&
            copy.Intervals[^1].Channels.Values.Distinct().Count() == 3, "联动或独立通道丢失。");
        Check(copy.Intervals[0].splitTiming != null && copy.Space.DirtyTangents.SetEquals(shot.Space.DirtyTangents) && copy.ManualFactors.Contains(split.Id), "内部编辑状态丢失。");
        Check(Enumerable.Range(0, 100).All(i => samples[i] == copy.Sample(i + .375f)), "恢复后首次采样发生变化。");
        CurveModel.Ensure(restored.ActiveTrack, restored.Fps); CurveModel.Ensure(track, source.Fps);
        Check(ProjectCodec.Encode(restored).SequenceEqual(ProjectCodec.Encode(source)), "恢复后的编辑同步行为不同。");

        var live = new CamTimeline { Playhead = 3.5f, PathInfluence = 2 };
        live.Keys.Add(new Keyframe { Frame = 3, Rot = Quaternion.Identity }); live.SelectNodes([live.Keys[0].Id]);
        var before = ProjectCodec.Encode(live); var after = ProjectCodec.Encode(source);
        live.ReplaceProject(source); source.Curves[0].Name = "外部修改"; source.ActiveTrack.Keys.Clear();
        Check(ProjectCodec.Encode(live).SequenceEqual(after), "替换与来源共享了状态。");
        live.Undo(); Check(ProjectCodec.Encode(live).SequenceEqual(before) && !live.CanUndo && live.CanRedo, "替换未作为一步撤销。");
        Reject(() => live.ReplaceProject(source));
        Check(ProjectCodec.Encode(live).SequenceEqual(before) && live.CanRedo, "替换失败破坏了工程或历史。");
        live.Redo(); Check(ProjectCodec.Encode(live).SequenceEqual(after), "重做未恢复完整工程。");

        Reject(() => ProjectCodec.Decode(bytes[..^1]));
        Reject(() => ProjectCodec.Decode([0xff]));
        void Damage(Action<JsonNode> edit)
        {
            var json = JsonNode.Parse(bytes)!; edit(json);
            Reject(() => ProjectCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }
        Reject(() => ProjectCodec.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"Playhead\":19.25", "\"Playhead\":1e99"))));
        Reject(() => ProjectCodec.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"Version\":1", "\"Version\":1,\"Version\":1"))));
        Damage(j => j["Tracks"]![1]!["Shots"]![0]!["CutRestore"] = null);
        Damage(j => j["Version"] = 99);
        Damage(j => j["Fps"] = 0);
        Damage(j => j["Tracks"]![1]!["Keys"]![1]!["Id"] = j["Tracks"]![1]!["Keys"]![0]!["Id"]!.DeepClone());
        Damage(j => j["Tracks"]![1]!["FollowPath"]!["CurveId"] = Guid.NewGuid());
        Damage(j => j["Tracks"]![1]!["Shots"]![0]!["Offset"] = new JsonArray());
        Damage(j => j["Tracks"]![1]!["Shots"]![0]!["PathProgress"]!["Keys"]![0]!["NodeId"] = Guid.NewGuid());
        Damage(j => j["Tracks"]![1]!["Shots"]![0]!["Intervals"]![0]!["ProjectChannels"]!["Indices"]!["Fov"] = 999);
        Damage(j => j["Tracks"]![1]!["Shots"]![0]!["Space"]!["Channels"]!.AsObject().Remove("Yaw"));
        Damage(j => j["Tracks"]![1]!["TrackConstraints"]![0]!["Type"] = "unknown");
        Damage(j => j["Tracks"]![1]!["Keys"]![0] = null);
        Damage(j => j["Tracks"]![1]!.AsObject().Remove("Shots"));
    }
}
