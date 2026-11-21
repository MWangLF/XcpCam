using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XcpCam.Core;

namespace XcpCam;

public sealed partial class MainWindow
{
    private sealed record ImportPreview(string Path, IReadOnlyList<XcpFrame> Frames, string? Warning,
        bool HasProject, CamTimeline? Timeline, string Details, bool PaddingRemoved = false);

    private bool _embedProject = true, _syncLengthWithSource = true;
    private bool _brioFrameCount;
    private float _positionTolerance = 0.001f, _angleTolerance = 0.05f, _fovTolerance = 0.01f;
    private int[] _cutCandidates = [];
    private readonly HashSet<int> _importCuts = [];
    private int _cutPage;
    private Task<ImportPreview>? _importWork;
    private CancellationTokenSource? _importCancel;
    private ImportPreview? _importPreview;

    private void OpenImportDialog()
    {
        FileDialog.OpenFileDialog("导入 XCP###xcpcam_import", "XAT Camera File{.xcp}", (ok, paths) =>
        {
            if(!ok || paths.Count == 0) return;
            var path = paths[0];
            _brioFrameCount = false;
            _cutCandidates = []; _importCuts.Clear(); _cutPage = 0;
            _importPreview = null;
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            StartImportWork(token =>
            {
                var file = XcpFile.Read(path);
                token.ThrowIfCancellationRequested();
                string? warning = file.Warning;
                if(file.Project != null)
                {
                    try
                    {
                        var timeline = ProjectCodec.Decode(file.Project);
                        foreach(var track in timeline.Tracks)
                            foreach(var constraint in track.TrackConstraints) constraint.TargetIndex = -1;
                        return new ImportPreview(path, file.Frames, warning, true, timeline,
                            $"可编辑时间轴 · {timeline.Fps} fps · 总长 {timeline.Length} 帧 · {timeline.Tracks.Count} 条轨道 · {timeline.AllKeys.Count()} 个镜头节点");
                    }
                    catch(Exception ex) when(ex is not OutOfMemoryException && ex is not OperationCanceledException)
                    { warning = $"工程数据无法恢复，可使用播放帧重建。{ex.Message}"; }
                }
                return new ImportPreview(path, file.Frames, warning, false, null, "", PaddingRemoved: file.Project != null);
            });
        }, 1, _lastDir, true);
    }

    private void StartImportWork(Func<CancellationToken, ImportPreview> build)
    {
        _importCancel?.Dispose();
        _importCancel = new CancellationTokenSource();
        var token = _importCancel.Token;
        _importWork = Task.Run(() => build(token), token);
    }

    private void DrawImportControls()
    {
        if(_importWork is { IsCompleted: true } work)
        {
            try
            {
                var result = work.GetAwaiter().GetResult();
                if(_importCancel?.IsCancellationRequested != true)
                {
                    _importPreview = result;
                    if(!result.HasProject && result.Timeline == null && result.Frames.Count <= XcpMotionImport.MaxFrames + 1)
                        _cutCandidates = FindCutCandidates(result.Frames);
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { SetStatus($"导入失败：{ex.Message}", true); }
            _importWork = null;
            _importCancel?.Dispose(); _importCancel = null;
        }
        if(_importWork != null)
        {
            ImGui.TextDisabled("正在生成导入预览…");
            ImGui.SameLine();
            if(ImGui.SmallButton("取消导入")) { _importCancel?.Cancel(); _importPreview = null; }
            return;
        }
        if(ImGui.Button("导入 .xcp")) OpenImportDialog();
        if(_importPreview is not { } preview) return;
        ImGui.Separator();
        ImGui.TextUnformatted($"导入预览 · {Path.GetFileName(preview.Path)}");
        if(!string.IsNullOrEmpty(preview.Warning)) ImGui.TextWrapped(preview.Warning);
        if(!preview.HasProject)
        {
            if(!preview.PaddingRemoved && ImGui.Checkbox("匹配 Brio 播放长度（忽略最后一条记录）", ref _brioFrameCount))
                _importPreview = preview = preview with { Timeline = null, Details = "" };
            int count = preview.Frames.Count - (_brioFrameCount ? 1 : 0);
            ImGui.TextUnformatted($"{count} 个采样 · 30 fps · {Math.Max(count - 1, 0) / 30.0:0.###} 秒");
            bool hasBase = CameraCapture.TryGetBase(_targets, out var pos, out var rot, out var name, out var error);
            ImGui.TextWrapped(hasBase ? $"重建坐标基准：{name} ({pos.X:0.##}, {pos.Y:0.##}, {pos.Z:0.##})" : error);
            if(DrawFitOptions(count)) _importPreview = preview = preview with { Timeline = null, Details = "" };
            if(count > XcpMotionImport.MaxFrames) ImGui.TextWrapped($"单次重建最多支持 {XcpMotionImport.MaxFrames} 帧，请先分段。");
            ImGui.BeginDisabled(!hasBase || count <= 0 || count > XcpMotionImport.MaxFrames);
            if(ImGui.Button("按此基准重建预览"))
            {
                _importPreview = preview = preview with { Timeline = null, Details = "" };
                var cuts = _importCuts.Where(frame => frame < count).ToHashSet();
                var tolerance = (_positionTolerance, _angleTolerance, _fovTolerance);
                StartImportWork(token => FitPreview(preview, count, pos, rot, name, tolerance, cuts, token));
            }
            ImGui.EndDisabled();
        }
        if(preview.Timeline != null)
        {
            ImGui.TextWrapped(preview.Details);
            if(preview.HasProject) ImGui.TextDisabled("按制作时的世界坐标恢复；骨骼约束将在当前场景中重新绑定。");
            ImGui.BeginDisabled(_recording != null);
            if(ImGui.Button("替换当前时间轴（可撤销）")) Try(() => ApplyImport(preview));
            ImGui.EndDisabled();
            ImGui.SameLine();
        }
        if(ImGui.Button("关闭导入预览")) _importPreview = null;
    }

    private bool DrawFitOptions(int count)
    {
        if(!ImGui.TreeNode("重建精度与疑似切点")) return false;
        bool changed = ImGui.DragFloat("最大位置误差（米）", ref _positionTolerance, 0.0001f, 0.00001f, 1f, "%.5f");
        changed |= ImGui.DragFloat("最大朝向误差（度）", ref _angleTolerance, 0.005f, 0.001f, 10f, "%.3f");
        changed |= ImGui.DragFloat("最大 FoV 误差（度）", ref _fovTolerance, 0.001f, 0.001f, 10f, "%.3f");
        if(_cutCandidates.Length > 0)
        {
            ImGui.TextWrapped("这些帧附近出现孤立的姿态突变。勾选后将从前一帧硬切到该帧。");
            const int pageSize = 8;
            if(_cutPage > 0 && ImGui.SmallButton("上一页切点")) _cutPage--;
            if((_cutPage + 1) * pageSize < _cutCandidates.Length && ImGui.SmallButton("下一页切点")) _cutPage++;
            foreach(int frame in _cutCandidates.Skip(_cutPage * pageSize).Take(pageSize).Where(f => f < count))
            {
                bool cut = _importCuts.Contains(frame);
                if(!ImGui.Checkbox($"第 {frame} 帧硬切", ref cut)) continue;
                if(cut) _importCuts.Add(frame); else _importCuts.Remove(frame);
                changed = true;
            }
        }
        ImGui.TreePop();
        return changed;
    }

    private static int[] FindCutCandidates(IReadOnlyList<XcpFrame> frames)
    {
        // Suggest isolated jumps compared with both neighbouring steps; leave the source samples untouched.
        static Vector3 Step(XcpFrame a, XcpFrame b) => new(Vector3.Distance(a.Pos, b.Pos),
            2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a.Rot, b.Rot)), 0, 1)), MathF.Abs(a.Fov - b.Fov));
        var result = new List<int>();
        var previous = Vector3.Zero;
        var current = frames.Count > 1 ? Step(frames[0], frames[1]) : Vector3.Zero;
        for(int frame = 1; frame < frames.Count; frame++)
        {
            var next = frame + 1 < frames.Count ? Step(frames[frame], frames[frame + 1]) : Vector3.Zero;
            var limit = Vector3.Max(new Vector3(0.1f, 0.08f, 0.04f), 8 * Vector3.Max(previous, next));
            if(current.X > limit.X || current.Y > limit.Y || current.Z > limit.Z) result.Add(frame);
            previous = current; current = next;
        }
        return result.ToArray();
    }

    private static ImportPreview FitPreview(ImportPreview file, int count, Vector3 origin, Quaternion basis, string name,
        (float pos, float angle, float fov) tolerance, HashSet<int> cuts, CancellationToken token)
    {
        var world = new XcpFrame[count];
        for(int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var frame = file.Frames[i];
            world[i] = new XcpFrame(Vector3.Transform(frame.Pos, basis) + origin, Quaternion.Normalize(basis * frame.Rot), frame.Fov);
        }
        var fit = XcpMotionImport.Fit(world, tolerance.pos, tolerance.angle, tolerance.fov, token, cuts);
        return file with { Timeline = fit.Timeline, Details = $"基准 {name} · {count} 个采样 → {fit.NodeCount} 个镜头节点、{fit.CurveKeyCount} 个中间通道键。最大位置误差 {fit.PositionError:G4} 米，朝向 {fit.AngleError:G4}°，FoV {fit.FovError:G4}°。" };
    }

    private void ApplyImport(ImportPreview preview)
    {
        var project = preview.Timeline!;
        bool vivacity = _source == BindSource.Vivacity;
        bool alignFps = vivacity && SourceAvailable && (_syncFpsWithBrio || _sync.Bound);
        if(alignFps && project.Fps != _brio.TimelineFps)
        {
            // Convert a copy before replacing the current timeline. A collision leaves both timelines intact.
            project = ProjectCodec.Decode(ProjectCodec.Encode(project));
            project.SetFps(_brio.TimelineFps);
        }
        var unresolved = new HashSet<string>();
        foreach(var constraint in project.Tracks.SelectMany(track => track.TrackConstraints))
        {
            var actor = _bones.Resolve(constraint);
            if(actor == null || _bones.BoneWorld(actor, constraint.Bone, out _) == null)
                unresolved.Add($"{(constraint.TargetName.Length > 0 ? constraint.TargetName : "当前 GPose 目标")} / {constraint.Bone}");
        }
        _tl.ReplaceProject(project);
        _importedVivacityPending = vivacity && _syncLengthWithSource;
        SyncImportedVivacityLength();
        _lookSynced = null; _clockEpoch++;
        Selected = null; // the restored timeline already owns its node selection
        _curveTool.ClearSelection(); _curveTool.CurveId = null; _curveTool.PointTrack = null;
        _graphShot = 0; _graphNode = Guid.Empty;
        _comp = null; _pathHash = ~ComputeHash();
        RebuildPathIfNeeded();
        _tlw.FitAll(_tl, ImGui.GetContentRegionAvail().X);
        _importPreview = null;
        var message = $"已导入 {Path.GetFileName(preview.Path)}（{_tl.Fps} fps，总长 {_tl.Length} 帧）。";
        if(LengthFromVivacity) message += $"灵动时间轴总长已同步为 {_brio.FrameMax} 帧。";
        if(unresolved.Count > 0) message += "请在约束页重新选择角色或骨骼：" + string.Join("、", unresolved);
        SetStatus(message, unresolved.Count > 0);
    }
}
