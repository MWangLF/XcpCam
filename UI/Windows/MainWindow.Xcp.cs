using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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

    private static string ImportModalId => $"{I18n.T("xcp.import_modal.title")}###xcpcam_import_modal";

    private bool _embedProject = true, _syncLengthWithSource = true;
    private bool _brioFrameCount;
    private float _positionTolerance = 0.001f, _angleTolerance = 0.05f, _fovTolerance = 0.01f;
    private int[] _cutCandidates = [];
    private readonly HashSet<int> _importCuts = [];
    private int _cutPage;
    private Task<ImportPreview>? _importWork;
    private CancellationTokenSource? _importCancel;
    private ImportPreview? _importPreview;
    private bool _openImportModal;

    private void OpenImportDialog()
    {
        FileDialog.OpenFileDialog($"{I18n.T("xcp.dialog.import")}###xcpcam_import", "XAT Camera File{.xcp}", (ok, paths) =>
        {
            if(!ok || paths.Count == 0) return;
            var path = paths[0];
            _brioFrameCount = false;
            _cutCandidates = []; _importCuts.Clear(); _cutPage = 0;
            _importPreview = null;
            _openImportModal = true;
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
                            I18n.T("xcp.import.project_details", timeline.Fps, timeline.Length, timeline.Tracks.Count, timeline.AllKeys.Count()));
                    }
                    catch(Exception ex) when(ex is not OutOfMemoryException && ex is not OperationCanceledException)
                    { warning = I18n.T("xcp.import.project_corrupt_warn", ex.Message); }
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

    /// <summary>File dropdown entries shown inside the top header's [File▾] popup.</summary>
    private void DrawFileMenuItems()
    {
        if(ImGui.MenuItem(I18n.T("xcp.menu.import"))) OpenImportDialog();
        UiWidgets.Tooltip(I18n.T("xcp.menu.import_tip"));

        bool canExport = _comp != null;
        if(ImGui.MenuItem(I18n.T("xcp.menu.export"), "", false, canExport)) OpenSaveDialog();
        if(canExport && _comp!.CutFrames.Count > 0)
            UiWidgets.Tooltip(I18n.T("xcp.menu.export_cut_tip"));
        else if(!canExport)
            UiWidgets.Tooltip(I18n.T("xcp.menu.export_disabled_tip"));

        ImGui.Separator();
        if(ImGui.MenuItem(I18n.T("xcp.menu.embed_project"), "", _embedProject)) _embedProject = !_embedProject;
        UiWidgets.Tooltip(I18n.T("xcp.menu.embed_project_tip"));

        if(_importPreview != null || _importWork != null)
        {
            ImGui.Separator();
            if(ImGui.MenuItem(I18n.T("xcp.menu.open_preview"))) _openImportModal = true;
        }
    }

    /// <summary>Polls background import work and renders the standalone Brio-styled import preview modal.</summary>
    private void DrawImportModal(bool brioTheme)
    {
        if(_importWork is { IsCompleted: true } work)
        {
            try
            {
                var result = work.GetAwaiter().GetResult();
                if(_importCancel?.IsCancellationRequested != true)
                {
                    _importPreview = result;
                    _openImportModal = true;
                    if(!result.HasProject && result.Timeline == null && result.Frames.Count <= XcpMotionImport.MaxFrames + 1)
                        _cutCandidates = FindCutCandidates(result.Frames);
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { SetStatus(I18n.T("xcp.import.failed", ex.Message), true); _openImportModal = false; }
            _importWork = null;
            _importCancel?.Dispose(); _importCancel = null;
        }

        string modalId = ImportModalId;
        if(_openImportModal && (_importWork != null || _importPreview != null))
        {
            ImGui.OpenPopup(modalId);
            _openImportModal = false;
        }

        float s = XcpStyle.Scale;
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(460f * s, 0), new Vector2(640f * s, 680f * s));

        bool modalOpen = true;
        if(!ImGui.BeginPopupModal(modalId, ref modalOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if(!modalOpen)
        {
            _importCancel?.Cancel();
            _importPreview = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        if(_importWork != null)
        {
            UiWidgets.StatusDot(XcpStyle.Info, I18n.T("xcp.import.parsing"));
            ImGui.Spacing();
            if(UiWidgets.IconTextButton("cancel_imp", FontAwesomeIcon.Times, I18n.T("xcp.import.cancel"), brioTheme: brioTheme))
            {
                _importCancel?.Cancel();
                _importPreview = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        if(_importPreview is not { } preview)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        UiWidgets.SectionHeader(Path.GetFileName(preview.Path), brioTheme, FontAwesomeIcon.FileImport);
        if(!string.IsNullOrEmpty(preview.Warning))
        {
            ImGui.TextColored(XcpStyle.Warning, preview.Warning);
            ImGui.Spacing();
        }

        if(!preview.HasProject)
        {
            using(UiWidgets.Card("imp_fit_card", brioTheme))
            {
                if(!preview.PaddingRemoved && ImGui.Checkbox($"{I18n.T("xcp.import.match_brio_len")}##brio_len", ref _brioFrameCount))
                    _importPreview = preview = preview with { Timeline = null, Details = "" };
                int count = preview.Frames.Count - (_brioFrameCount ? 1 : 0);
                ImGui.TextDisabled(I18n.T("xcp.import.samples_info", count, $"{Math.Max(count - 1, 0) / 30.0:0.###}"));

                bool hasBase = CameraCapture.TryGetBase(_targets, out var pos, out var rot, out var name, out var error);
                if(hasBase)
                    UiWidgets.StatusDot(XcpStyle.Success, I18n.T("xcp.import.base_ok", name, $"{pos.X:0.##}", $"{pos.Y:0.##}", $"{pos.Z:0.##}"));
                else
                    UiWidgets.StatusDot(XcpStyle.Warning, error);

                ImGui.Spacing();
                if(DrawFitOptions(count)) _importPreview = preview = preview with { Timeline = null, Details = "" };
                if(count > XcpMotionImport.MaxFrames)
                    ImGui.TextColored(XcpStyle.Danger, I18n.T("xcp.import.too_many_frames", XcpMotionImport.MaxFrames));

                ImGui.Spacing();
                ImGui.BeginDisabled(!hasBase || count <= 0 || count > XcpMotionImport.MaxFrames);
                if(UiWidgets.IconTextButton("fit_btn", FontAwesomeIcon.Magic, I18n.T("xcp.import.fit_btn"),
                    I18n.T("xcp.import.fit_btn_tip"), active: preview.Timeline == null, brioTheme: brioTheme))
                {
                    _importPreview = preview = preview with { Timeline = null, Details = "" };
                    var cuts = _importCuts.Where(frame => frame < count).ToHashSet();
                    var tolerance = (_positionTolerance, _angleTolerance, _fovTolerance);
                    StartImportWork(token => FitPreview(preview, count, pos, rot, name, tolerance, cuts, token));
                }
                ImGui.EndDisabled();
            }
            ImGui.Spacing();
        }

        if(preview.Timeline != null)
        {
            using(UiWidgets.Card("imp_ready_card", brioTheme))
            {
                UiWidgets.StatusDot(XcpStyle.Success, I18n.T("xcp.import.ready"));
                ImGui.TextWrapped(preview.Details);
                if(preview.HasProject)
                    ImGui.TextDisabled(I18n.T("xcp.import.world_note"));
            }
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();
        if(preview.Timeline != null)
        {
            ImGui.BeginDisabled(_recording != null);
            if(UiWidgets.IconTextButton("apply_imp", FontAwesomeIcon.Check, I18n.T("xcp.import.apply_btn"),
                I18n.T("xcp.import.apply_btn_tip"), active: true, brioTheme: brioTheme))
            {
                Try(() => ApplyImport(preview));
                if(_importPreview == null) ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
        }
        if(UiWidgets.IconTextButton("close_imp", FontAwesomeIcon.Times, I18n.T("common.close"), brioTheme: brioTheme))
        {
            _importPreview = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private bool DrawFitOptions(int count)
    {
        if(!ImGui.TreeNode($"{I18n.T("xcp.fit.options")}###xcp_fit_opts")) return false;
        float w = 130f * XcpStyle.Scale;
        ImGui.SetNextItemWidth(w);
        bool changed = ImGui.DragFloat($"{I18n.T("xcp.fit.pos_tol")}##pos_tol", ref _positionTolerance, 0.0001f, 0.00001f, 1f, "%.5f");
        ImGui.SetNextItemWidth(w);
        changed |= ImGui.DragFloat($"{I18n.T("xcp.fit.angle_tol")}##ang_tol", ref _angleTolerance, 0.005f, 0.001f, 10f, "%.3f");
        ImGui.SetNextItemWidth(w);
        changed |= ImGui.DragFloat($"{I18n.T("xcp.fit.fov_tol")}##fov_tol", ref _fovTolerance, 0.001f, 0.001f, 10f, "%.3f");
        if(_cutCandidates.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(I18n.T("xcp.fit.cuts_hint"));
            const int pageSize = 8;
            if(_cutPage > 0 && ImGui.SmallButton($"{I18n.T("xcp.fit.prev_page")}##cut_prev")) _cutPage--;
            if(_cutPage > 0 && (_cutPage + 1) * pageSize < _cutCandidates.Length) ImGui.SameLine();
            if((_cutPage + 1) * pageSize < _cutCandidates.Length && ImGui.SmallButton($"{I18n.T("xcp.fit.next_page")}##cut_next")) _cutPage++;
            foreach(int frame in _cutCandidates.Skip(_cutPage * pageSize).Take(pageSize).Where(f => f < count))
            {
                bool cut = _importCuts.Contains(frame);
                if(!ImGui.Checkbox($"{I18n.T("xcp.fit.cut_frame", frame)}##cut_{frame}", ref cut)) continue;
                if(cut) _importCuts.Add(frame); else _importCuts.Remove(frame);
                changed = true;
            }
        }
        ImGui.TreePop();
        return changed;
    }

    private static int[] FindCutCandidates(IReadOnlyList<XcpFrame> frames)
    {
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
        return file with { Timeline = fit.Timeline, Details = I18n.T("xcp.fit.details", name, count, fit.NodeCount, fit.CurveKeyCount, $"{fit.PositionError:G4}", $"{fit.AngleError:G4}", $"{fit.FovError:G4}") };
    }

    private void ApplyImport(ImportPreview preview)
    {
        var project = preview.Timeline!;
        bool vivacity = _source == BindSource.Vivacity;
        bool alignFps = vivacity && SourceAvailable && (_syncFpsWithBrio || _sync.Bound);
        if(alignFps && project.Fps != _brio.TimelineFps)
        {
            project = ProjectCodec.Decode(ProjectCodec.Encode(project));
            project.SetFps(_brio.TimelineFps);
        }
        var unresolved = new HashSet<string>();
        foreach(var constraint in project.Tracks.SelectMany(track => track.TrackConstraints))
        {
            var actor = _bones.Resolve(constraint);
            if(actor == null || _bones.BoneWorld(actor, constraint.Bone, out _) == null)
                unresolved.Add($"{(constraint.TargetName.Length > 0 ? constraint.TargetName : I18n.T("brio.actor_current_gpose"))} / {constraint.Bone}");
        }
        _tl.ReplaceProject(project);
        _importedVivacityPending = vivacity && _syncLengthWithSource;
        SyncImportedVivacityLength();
        _lookSynced = null; _clockEpoch++;
        Selected = null;
        _curveTool.ClearSelection(); _curveTool.CurveId = null; _curveTool.PointTrack = null;
        _graphShot = 0; _graphNode = Guid.Empty;
        _comp = null; _pathHash = ~ComputeHash();
        RebuildPathIfNeeded();
        _tlw.FitAll(_tl, MathF.Max(500f, ImGui.GetWindowWidth() - (_config.SidebarOpen ? _config.SidebarWidth : 0f)));
        _importPreview = null;
        var message = I18n.T("xcp.import.applied", Path.GetFileName(preview.Path), _tl.Fps, _tl.Length);
        if(LengthFromVivacity) message += I18n.T("xcp.import.vivacity_len_synced", _brio.FrameMax);
        if(unresolved.Count > 0) message += I18n.T("xcp.import.unresolved_constraints", string.Join(I18n.T("common.list_sep"), unresolved));
        SetStatus(message, unresolved.Count > 0);
    }
}
