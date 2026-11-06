using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using XcpCam.BrioInterop;
using XcpCam.Core;

namespace XcpCam;

public sealed class MainWindow : Window, IDisposable
{
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly BrioBridge _brio;
    private readonly Overlay _overlay;
    private readonly GizmoEditor _gizmo = new();
    private readonly TimelineWidget _tlw = new();
    private readonly TimelineSync _sync;
    private readonly TimelineBake _bake;

    public readonly FileDialogManager FileDialog = new();

    private readonly CamTimeline _tl = new();
    private readonly PathSettings _settings = new();
    private bool _useProjectionFov = true;
    private bool _syncFpsWithBrio = true;
    private bool _followBrioPlayhead;
    private bool _drivePlayheadToBrio;
    private bool _lookThrough;
    private int _defaultGapFrames = 60;
    private string _status = "";
    private bool _statusIsError;
    private string _lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private CameraPath? _path;
    private int _pathHash;
    private float _maxSpeed = 1;

    private bool _playing;
    private bool _loop = true;

    private int _bakeCamIndex;

    private static readonly string[] TangentNames = ["自动(平滑)", "停顿", "匀速"];
    private static readonly string[] ModeNames = ["按时间平滑", "整体匀速"];

    public MainWindow(ITargetManager targets, IClientState clientState, IPluginLog log, BrioBridge brio, Overlay overlay)
        : base("XcpCam - 相机时间轴###xcpcam_main")
    {
        _targets = targets;
        _clientState = clientState;
        _log = log;
        _brio = brio;
        _overlay = overlay;
        _sync = new TimelineSync(brio, log);
        _bake = new TimelineBake(brio, log);
        Size = new Vector2(820, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        try { _sync.Unbind(); } catch { /* Brio may already be gone */ }
    }

    private Keyframe? Selected
    {
        get => _tlw.Selected != null && _tl.Keys.Contains(_tlw.Selected) ? _tlw.Selected : null;
        set => _tlw.Selected = value;
    }

    // ------------------------------------------------------------------ ticking

    public void FrameworkTick(float dt)
    {
        if(_syncFpsWithBrio && _brio.IsConnected)
        {
            int fps = _brio.TimelineFps;
            if(fps != _tl.Fps)
            {
                _tl.SetFps(fps);
                SetStatus($"已同步 Brio 灵动时间轴帧率：{fps}fps（镜头节点按秒保持不变）", false);
            }
        }

        RebuildPathIfNeeded();

        if(_followBrioPlayhead && _brio.IsConnected)
            _tl.Playhead = (float)Math.Clamp(_brio.TimelineTime, 0, _tl.Length);
        else if(_playing)
        {
            _tl.Playhead += dt * _tl.Fps;
            float end = _path != null ? _tl.LastFrame : _tl.Length;
            if(_tl.Playhead > end)
            {
                if(_loop) _tl.Playhead = _tl.FirstFrame;
                else { _tl.Playhead = end; _playing = false; }
            }
        }

        if(_lookThrough && _path != null) LookThroughPlayhead();

        try { _sync.Tick(_path?.Duration ?? 0); }
        catch(Exception ex) { _log.Warning(ex, "TimelineSync tick failed"); }
    }

    private void LookThroughPlayhead()
    {
        if(!_brio.IsConnected || _path == null) return;
        if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out _)) return;
        var s = _path.Sample(_tl.FrameToPathTime(_tl.Playhead));
        var wp = Vector3.Transform(s.Pos, br) + bp;
        var wr = Quaternion.Normalize(br * s.Rot);
        var (yaw, pitch, roll) = FreeCamMath.FromWorldRotation(wr);
        if(!_brio.SetCurrentFreeCamPose(wp, yaw, pitch, roll, s.Fov - _bake.BaseFov))
        {
            _lookThrough = false;
            SetStatus("镜头预览需要 Brio 当前相机是自由相机（Free）。", true);
        }
    }

    // ------------------------------------------------------------------ overlay (drawn in OverlayWindow)

    public bool OverlayWantsInput => _gizmo.Enabled && Selected != null && (_gizmo.IsOver || _gizmo.IsUsing);

    public void DrawOverlay()
    {
        float? playT = _path != null && (_playing || _followBrioPlayhead || _tl.Playhead != _tl.FirstFrame)
            ? _tl.FrameToPathTime(_tl.Playhead) : null;
        _overlay.PreviewTime = _lookThrough ? null : playT;
        _overlay.Draw(_path, _tl.Keys, _sync.PlayheadSec, Selected);

        var sel = Selected;
        if(sel != null && _clientState.IsGPosing && CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out _))
        {
            var prePos = sel.Pos;
            var preRot = sel.Rot;
            _gizmo.Draw(sel, bp, br);
            if(_gizmo.DragStarted)
            {
                // snapshot the pose from before this drag, then re-apply the first frame of the drag
                var newPos = sel.Pos; var newRot = sel.Rot;
                sel.Pos = prePos; sel.Rot = preRot;
                _tl.Snapshot();
                sel.Pos = newPos; sel.Rot = newRot;
            }
        }
    }

    private int ComputeHash()
    {
        var h = new HashCode();
        h.Add(_settings.Mode); h.Add(_settings.EaseStart); h.Add(_settings.EaseEnd);
        h.Add(_settings.Duration); h.Add(_settings.EaseInFraction); h.Add(_settings.EaseOutFraction); h.Add(_settings.RotationWeight);
        h.Add(_tl.Fps);
        foreach(var k in _tl.Keys) { h.Add(k.Frame); h.Add(k.Pos); h.Add(k.Rot); h.Add(k.Fov); h.Add(k.Tangent); h.Add(k.HoldFrames); }
        h.Add(_tl.Keys.Count);
        return h.ToHashCode();
    }

    private void RebuildPathIfNeeded()
    {
        int hash = ComputeHash();
        if(hash == _pathHash && (_path != null || _tl.Keys.Count < 2)) return;
        _pathHash = hash;
        try
        {
            _tl.SyncSeconds();
            // constant-speed mode: span the keys' first..last frames
            if(_settings.Mode == TimingMode.ConstantSpeed && _tl.Keys.Count >= 2)
                _settings.Duration = MathF.Max(0.1f, (_tl.LastFrame - _tl.FirstFrame) / (float)_tl.Fps);
            _path = CameraPath.Create(_tl.Sorted(), _settings);
            _maxSpeed = 1e-3f;
            if(_path != null)
                for(float t = 0; t < _path.Duration; t += 1f / 30f)
                    _maxSpeed = MathF.Max(_maxSpeed, SpeedAtTime(t));
            if(_sync.Bound && _path != null) _sync.Refresh(_path);
        }
        catch(Exception ex)
        {
            _path = null;
            _log.Warning(ex, "path build failed");
        }
    }

    private float SpeedAtTime(float t)
    {
        if(_path == null) return 0;
        const float e = 1f / 60f;
        return Vector3.Distance(_path.Sample(t).Pos, _path.Sample(t + e).Pos) / e;
    }

    // ------------------------------------------------------------------ UI

    public override void Draw()
    {
        bool gpose = _clientState.IsGPosing;
        if(!gpose)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "需要在 GPose 中使用。");

        if(ImGui.BeginTabBar("##tabs"))
        {
            if(ImGui.BeginTabItem("相机时间轴"))
            {
                DrawTimelineTab(gpose);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("Brio 联动"))
            {
                DrawBrioTab(gpose);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if(!string.IsNullOrEmpty(_status))
            ImGui.TextColored(_statusIsError ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.5f, 1, 0.5f, 1), _status);
    }

    // ---------------- timeline tab ----------------

    private void DrawTimelineTab(bool gpose)
    {
        // --- transport ---
        if(ImGui.Button(_playing ? "暂停" : "播放")) { _playing = !_playing; if(_playing && _tl.Playhead >= _tl.LastFrame) _tl.Playhead = _tl.FirstFrame; }
        ImGui.SameLine();
        if(ImGui.Button("停止")) { _playing = false; _tl.Playhead = _tl.FirstFrame; }
        ImGui.SameLine();
        if(ImGui.ArrowButton("##prev", ImGuiDir.Left)) StepPlayhead(-1);
        ImGui.SameLine();
        if(ImGui.ArrowButton("##next", ImGuiDir.Right)) StepPlayhead(1);
        ImGui.SameLine();
        ImGui.Checkbox("循环", ref _loop);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int ph = (int)MathF.Round(_tl.Playhead);
        if(ImGui.InputInt("帧", ref ph)) { _tl.Playhead = Math.Clamp(ph, 0, _tl.Length); _playing = false; }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_tl.Playhead / _tl.Fps:0.00}s");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        int len = _tl.Length;
        if(ImGui.InputInt("总长(帧)", ref len, 10)) _tl.Length = Math.Clamp(len, Math.Max(1, _tl.LastFrame), 100000);
        ImGui.SameLine();
        if(_syncFpsWithBrio && _brio.IsConnected)
            ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), $"{_tl.Fps}fps (Brio)");
        else
        {
            ImGui.SetNextItemWidth(60);
            int fps = _tl.Fps;
            if(ImGui.InputInt("fps", ref fps, 0)) _tl.SetFps(fps);
        }

        // --- capture row ---
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("在播放头抓取镜头")) CaptureAtPlayhead();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在当前播放头所在帧添加镜头节点（该帧已有节点则替换它的镜头）");
        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("追加到末尾")) CaptureAppend();
        ImGui.EndDisabled();
        ImGui.SameLine();
        bool inPath = _path != null && _tl.Playhead >= _tl.FirstFrame && _tl.Playhead <= _tl.LastFrame;
        ImGui.BeginDisabled(!inPath);
        if(ImGui.Button("从运镜生成关键帧")) InsertKeyFromPath();
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(inPath
                ? "在播放头所在帧插入一个镜头节点，位置/旋转/FoV 取自当前运镜在这一帧的值。\n之后可以单独拖动、用手柄调整它来修改这一段运镜。"
                : "需要至少 2 个镜头节点，且播放头位于第一个和最后一个节点之间。");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60);
        ImGui.InputInt("间隔帧", ref _defaultGapFrames, 0);
        _defaultGapFrames = Math.Clamp(_defaultGapFrames, 1, 10000);
        ImGui.SameLine();
        ImGui.BeginDisabled(!_tl.CanUndo);
        if(ImGui.Button("撤销")) { _tl.Undo(); Selected = null; }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_tl.CanRedo);
        if(ImGui.Button("重做")) { _tl.Redo(); Selected = null; }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if(ImGui.Button("适配")) _tlw.FitAll(_tl, ImGui.GetContentRegionAvail().X + 300);
        ImGui.SameLine();
        ImGui.BeginDisabled(_tl.Keys.Count < 2);
        if(ImGui.Button("导出 .xcp")) OpenSaveDialog();
        ImGui.EndDisabled();

        // --- the timeline ---
        ImGui.Spacing();
        _tlw.Draw(_tl, _path == null ? null : f =>
        {
            float t = _tl.FrameToPathTime(f);
            if(t < 0 || t > _path.Duration) return null;
            return SpeedAtTime(t) / _maxSpeed;
        });
        if(_tlw.DragStarted) _tl.Snapshot();
        if(_tlw.PlayheadChanged)
        {
            _playing = false;
            if(_drivePlayheadToBrio && _brio.IsConnected) _brio.SetTimelineTime(_tl.Playhead);
        }
        ImGui.TextDisabled("拖动◆移动镜头节点（精确到帧）· 点击空白处移动播放头 · 滚轮平移，Ctrl+滚轮缩放，中键拖动 · 双击节点跳转");
        DrawGizmoBar();

        ImGui.Separator();
        DrawPacing();
        ImGui.Separator();
        DrawInspector(gpose);
        DrawTargetWarnings(gpose);
        ImGui.Spacing();
        DrawDisplayOptions();
    }

    private void StepPlayhead(int dir)
    {
        _playing = false;
        var frames = _tl.Keys.Select(k => k.Frame).OrderBy(f => f).ToList();
        int cur = (int)MathF.Round(_tl.Playhead);
        int? next = dir > 0 ? frames.Where(f => f > cur).Cast<int?>().FirstOrDefault() : frames.Where(f => f < cur).Cast<int?>().LastOrDefault();
        _tl.Playhead = next ?? Math.Clamp(cur + dir, 0, _tl.Length);
        var k = _tl.Keys.FirstOrDefault(x => x.Frame == (int)_tl.Playhead);
        if(k != null) Selected = k;
        if(_drivePlayheadToBrio && _brio.IsConnected) _brio.SetTimelineTime(_tl.Playhead);
    }

    private void DrawPacing()
    {
        int mode = (int)_settings.Mode;
        ImGui.SetNextItemWidth(130);
        if(ImGui.Combo("运镜节奏", ref mode, ModeNames, ModeNames.Length))
            _settings.Mode = (TimingMode)mode;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("按时间平滑：每个镜头在它所在的帧到达，经过镜头时速度连续、不顿挫。\n整体匀速：首尾节点之间以恒定速度运动（中间节点的帧位置被忽略），只在首尾缓入缓出。");
        ImGui.SameLine();
        if(_settings.Mode == TimingMode.Timed)
        {
            ImGui.Checkbox("开头缓入", ref _settings.EaseStart);
            ImGui.SameLine();
            ImGui.Checkbox("结尾缓出", ref _settings.EaseEnd);
        }
        else
        {
            float a = _settings.EaseInFraction * 100, b = _settings.EaseOutFraction * 100;
            ImGui.SetNextItemWidth(60);
            if(ImGui.DragFloat("缓入%", ref a, 0.5f, 0f, 50f, "%.0f")) _settings.EaseInFraction = a / 100f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            if(ImGui.DragFloat("缓出%", ref b, 0.5f, 0f, 50f, "%.0f")) _settings.EaseOutFraction = b / 100f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            ImGui.DragFloat("转向权重", ref _settings.RotationWeight, 0.05f, 0f, 10f, "%.2f");
        }
        if(_path != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"| 时长 {_path.Duration:0.00}s · 路径 {_path.TotalLength:0.0}y");
        }
    }

    private void DrawInspector(bool gpose)
    {
        var k = Selected;
        if(k == null)
        {
            ImGui.TextDisabled(_tl.Keys.Count == 0
                ? "还没有镜头节点。在 Brio 里摆好镜头后点「在播放头抓取镜头」。"
                : _gizmo.Enabled
                    ? "点击时间轴上的◆选中一个镜头节点进行编辑；选中后可在游戏画面中用变换手柄直接移动/旋转它。"
                    : "点击时间轴上的◆选中一个镜头节点进行编辑。（勾选上方「变换手柄」可在游戏画面中直接调整）");
            return;
        }

        int idx = _tl.Keys.IndexOf(k) + 1;
        ImGui.Text($"镜头 {idx}");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        int frame = k.Frame;
        if(ImGui.InputInt("所在帧", ref frame))
        {
            _tl.Snapshot();
            int got = _tl.MoveKey(k, frame);
            if(got != frame) SetStatus($"第 {frame} 帧已有节点", true);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        int hold = k.HoldFrames;
        if(ImGui.InputInt("停留帧", ref hold, 0)) { _tl.Snapshot(); k.HoldFrames = Math.Clamp(hold, 0, 100000); }
        ImGui.SameLine();
        ImGui.BeginDisabled(_settings.Mode != TimingMode.Timed);
        ImGui.SetNextItemWidth(100);
        int tan = (int)k.Tangent;
        if(ImGui.Combo("经过方式", ref tan, TangentNames, TangentNames.Length)) { _tl.Snapshot(); k.Tangent = (TangentMode)tan; }
        ImGui.EndDisabled();

        // transform fields (relative to target)
        var pos = k.Pos;
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("相对位置 (米)", ref pos, 0.01f))
            k.Pos = pos;
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        var euler = QuatToEulerDeg(k.Rot);
        ImGui.SetNextItemWidth(260);
        if(ImGui.DragFloat3("旋转 偏航/俯仰/滚转 (°)", ref euler, 0.25f))
            k.Rot = EulerDegToQuat(euler);
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        float fovDeg = k.Fov * 180f / MathF.PI;
        ImGui.SetNextItemWidth(120);
        if(ImGui.DragFloat("FoV (°)", ref fovDeg, 0.1f, 5f, 150f, "%.1f"))
            k.Fov = fovDeg * MathF.PI / 180f;
        if(ImGui.IsItemActivated()) _tl.Snapshot();

        ImGui.SameLine();
        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("用当前镜头替换")) Recapture(k);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if(ImGui.Button("跳到此帧")) { _tl.Playhead = k.Frame; if(_drivePlayheadToBrio && _brio.IsConnected) _brio.SetTimelineTime(k.Frame); }
        ImGui.SameLine();
        if(ImGui.Button("复制节点")) Duplicate(k);
        ImGui.SameLine();
        if(ImGui.Button("删除节点")) { _tl.Snapshot(); _tl.Keys.Remove(k); Selected = null; }

        bool ok = k.SelfCheckError < 1e-3f;
        ImGui.TextColored(ok ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.4f, 0.4f, 1),
            ok ? "自检 OK" : $"自检误差 {k.SelfCheckError:0.0e0}");
        ImGui.SameLine();
        ImGui.TextDisabled($"目标：{k.TargetName}");
    }

    private static Vector3 QuatToEulerDeg(Quaternion q)
    {
        // yaw (Y), pitch (X), roll (Z) matching Quaternion.CreateFromYawPitchRoll
        q = Quaternion.Normalize(q);
        float sinp = 2 * (q.W * q.X - q.Y * q.Z);
        float pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);
        float yaw = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z));
        const float d = 180f / MathF.PI;
        return new Vector3(yaw * d, pitch * d, roll * d);
    }

    private static Quaternion EulerDegToQuat(Vector3 e)
    {
        const float r = MathF.PI / 180f;
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(e.X * r, e.Y * r, e.Z * r));
    }

    private void DrawTargetWarnings(bool gpose)
    {
        if(_tl.Keys.Count < 1) return;
        var names = _tl.Keys.Select(k => k.TargetName).Where(n => n.Length > 0).Distinct().Count();
        if(names > 1)
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "警告：镜头节点抓取时的 GPose 目标不一致，播放时会错位。");
        else if(gpose && CameraCapture.TryGetBase(_targets, out var bp, out var br, out var nm, out _))
        {
            var k0 = _tl.Keys[0];
            if(k0.TargetName.Length > 0 && (nm != k0.TargetName || Vector3.Distance(bp, k0.BasePos) > 0.01f || MathF.Abs(Quaternion.Dot(br, k0.BaseRot)) < 0.9999f))
                ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "提示：目标角色在抓取后移动/旋转或已更换。运镜是相对目标的，会跟随当前目标。");
        }
    }

    // ---------------- in-game display & gizmo (merged into the timeline tab) ----------------

    /// <summary>One row: overlay on/off + gizmo mode, shown right under the timeline.</summary>
    private void DrawGizmoBar()
    {
        ImGui.Checkbox("游戏内显示", ref _overlay.Enabled);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("在游戏画面中显示运镜路径（蓝=慢，红=快）和各镜头视锥。详细选项见下方「显示选项」。");
        ImGui.SameLine();
        ImGui.Checkbox("变换手柄", ref _gizmo.Enabled);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("选中一个镜头节点后，在游戏画面中该镜头处显示手柄，可直接拖动移动/旋转。");
        ImGui.BeginDisabled(!_gizmo.Enabled);
        ImGui.SameLine();
        int op = (int)_gizmo.Operation;
        if(ImGui.RadioButton("移动", ref op, 0)) _gizmo.Operation = GizmoEditor.Op.Translate;
        ImGui.SameLine();
        if(ImGui.RadioButton("旋转", ref op, 1)) _gizmo.Operation = GizmoEditor.Op.Rotate;
        ImGui.SameLine();
        ImGui.Checkbox("本地坐标", ref _gizmo.LocalSpace);
        ImGui.SameLine();
        ImGui.Checkbox("吸附", ref _gizmo.Snap);
        if(_gizmo.Snap)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            if(_gizmo.Operation == GizmoEditor.Op.Translate)
                ImGui.DragFloat("米##snapT", ref _gizmo.SnapTranslate, 0.01f, 0.01f, 5f, "%.2f");
            else
                ImGui.DragFloat("°##snapR", ref _gizmo.SnapRotateDeg, 0.5f, 0.5f, 90f, "%.1f");
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        DrawLookThroughToggle();
    }

    /// <summary>Collapsible detailed display options.</summary>
    private void DrawDisplayOptions()
    {
        if(!ImGui.CollapsingHeader("显示选项")) return;
        ImGui.BeginDisabled(!_overlay.Enabled);
        ImGui.Checkbox("运镜路径", ref _overlay.ShowPath);
        ImGui.SameLine();
        ImGui.Checkbox("按速度着色（蓝=慢，红=快）", ref _overlay.SpeedColors);
        ImGui.SameLine();
        ImGui.Checkbox("镜头视锥", ref _overlay.ShowFrustums);
        ImGui.SameLine();
        ImGui.Checkbox("编号/帧标签", ref _overlay.ShowLabels);
        ImGui.SetNextItemWidth(120);
        ImGui.DragFloat("视锥大小", ref _overlay.FrustumSize, 0.01f, 0.05f, 3f, "%.2f");
        ImGui.EndDisabled();
    }

    private void DrawLookThroughToggle()
    {
        ImGui.BeginDisabled(!_brio.IsConnected || _path == null);
        if(ImGui.Checkbox("预览镜头", ref _lookThrough) && _lookThrough && !_brio.CurrentCameraIsFree)
        {
            _lookThrough = false;
            SetStatus("请先在 Brio 中切换到一个自由相机（Free）。", true);
        }
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!_brio.IsConnected ? "需要连接 Brio。"
                : _path == null ? "需要至少 2 个镜头节点。"
                : "把 Brio 当前的自由相机放到播放头位置，直接看到这一帧的画面。\n播放或拖动时间轴即可预览运镜。");
        if(_lookThrough)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "预览镜头：Brio 自由相机正被运镜接管，抓取镜头前请先关闭。");
    }

    // ---------------- Brio tab ----------------

    private void DrawBrioTab(bool gpose)
    {
        ImGui.TextColored(_brio.IsConnected ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.7f, 0.3f, 1), _brio.Status);
        if(_brio.Missing.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(缺少 {_brio.Missing.Count} 项)");
            if(ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", _brio.Missing));
        }
        if(!_brio.IsConnected) return;

        ImGui.TextDisabled($"Brio 灵动时间轴：{(_brio.TimelineIsPlaying ? "播放中" : "已停止")}  第 {_brio.TimelineTime:0} 帧 / {_brio.FrameMax}  @ {_brio.TimelineFps}fps");

        ImGui.Separator();
        ImGui.Text("时间轴同步");
        if(ImGui.Checkbox("帧率跟随 Brio 灵动时间轴", ref _syncFpsWithBrio) && !_syncFpsWithBrio)
            SetStatus("已停止跟随 Brio 帧率，可在时间轴上手动设置 fps。", false);
        ImGui.Checkbox("播放头跟随 Brio 时间轴", ref _followBrioPlayhead);
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Brio 时间轴播放/拖动时，本插件的播放头同步移动（预览和可视化跟着走）。");
        ImGui.Checkbox("拖动本插件播放头时同时移动 Brio 时间轴", ref _drivePlayheadToBrio);
        ImGui.SameLine();
        if(ImGui.Button("总长 = Brio 时间轴长度")) _tl.Length = Math.Max(_brio.FrameMax, _tl.LastFrame);

        // ---- mode 1 ----
        ImGui.Separator();
        ImGui.Text("方式 1：联动 Brio 的 XAT 播放器");
        ImGui.TextDisabled("Brio 时间轴播放到起始帧时从头播放运镜；停止/暂停时 XAT 也停止。\n限制：XAT 不能跳转；需要使用非自由相机。");
        ImGui.BeginDisabled(!_brio.CanSync || _path == null);
        _sync.StartFrame = _tl.Keys.Count > 0 ? _tl.FirstFrame : 0;
        ImGui.TextDisabled($"起始帧 = 第一个镜头节点所在帧（{_sync.StartFrame}）");
        ImGui.SameLine();
        if(!_sync.Bound)
        {
            if(ImGui.Button("绑定"))
                Try(() => { _sync.Bind(_path!); SetStatus("已绑定到 Brio 时间轴（XAT）", false); });
        }
        else if(ImGui.Button("解绑"))
            Try(() => { _sync.Unbind(); SetStatus("已解绑", false); });
        ImGui.EndDisabled();
        if(_sync.Bound) ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), _sync.Status);

        // ---- mode 2 ----
        ImGui.Separator();
        ImGui.Text("方式 2：烘焙进 Brio 自由相机的时间轴相机轨道");
        ImGui.TextDisabled("由 Brio 时间轴直接驱动自由相机，拖动/暂停/循环完全同步。节点帧位置原样对应 Brio 时间轴帧。");

        var cams = _brio.CanBake ? _brio.GetFreeCameras() : [];
        if(cams.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "没有可用的 Brio 自由相机。请先在 Brio 相机窗口中新建一个 Free 相机。");
            return;
        }

        _bakeCamIndex = Math.Clamp(_bakeCamIndex, 0, cams.Count - 1);
        var names = cams.Select(c => c.Name + (c.InTimeline ? "" : "（未加入时间轴）")).ToArray();
        ImGui.SetNextItemWidth(200);
        ImGui.Combo("目标自由相机", ref _bakeCamIndex, names, names.Length);
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("每隔几帧一个关键帧", ref _bake.StepFrames);
        _bake.StepFrames = Math.Clamp(_bake.StepFrames, 1, 30);
        ImGui.SameLine();
        ImGui.Checkbox("清除该相机轨道原有关键帧", ref _bake.ClearExisting);
        ImGui.SetNextItemWidth(70);
        float baseDeg = _bake.BaseFov * 180f / MathF.PI;
        if(ImGui.DragFloat("基础 FoV°", ref baseDeg, 0.1f, 20f, 120f, "%.1f")) _bake.BaseFov = baseDeg * MathF.PI / 180f;
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Brio 自由相机的 FoV 是在游戏基础 FoV 上的偏移量（默认约 44.7°）。");

        bool fpsMismatch = _tl.Fps != _brio.TimelineFps;
        if(fpsMismatch)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), $"本时间轴 {_tl.Fps}fps 与 Brio {_brio.TimelineFps}fps 不同，烘焙时按秒换算到 Brio 帧。");

        ImGui.BeginDisabled(_path == null || !gpose);
        if(ImGui.Button("烘焙到 Brio 时间轴"))
            Try(() =>
            {
                if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out var err))
                    throw new InvalidOperationException(err);
                _bake.StartFrame = (int)MathF.Round(_tl.FirstFrame * (_brio.TimelineFps / (float)_tl.Fps));
                _bake.Bake(cams[_bakeCamIndex], _path!, bp, br);
                SetStatus(_bake.Status, false);
            });
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!_bake.CanUndo);
        if(ImGui.Button("撤销烘焙"))
            Try(() => { _bake.Undo(); SetStatus(_bake.Status, false); });
        ImGui.EndDisabled();
        if(!string.IsNullOrEmpty(_bake.Status)) ImGui.TextColored(new Vector4(0.6f, 0.9f, 1, 1), _bake.Status);
    }

    private void Try(Action a)
    {
        try { a(); }
        catch(Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            SetStatus($"失败：{inner.Message}", true);
            _log.Error(inner, "XcpCam action failed");
        }
    }

    // ---------------- capture / export ----------------

    private Keyframe? MakeKey()
    {
        if(_lookThrough)
        {
            SetStatus("请先关闭「预览镜头」再抓取新镜头。", true);
            return null;
        }
        if(!CameraCapture.TryCapture(_targets, out var s, out var err) || s is null)
        {
            SetStatus(err, true);
            return null;
        }

        var (pos, rot) = XcpMath.WorldToRelative(s.WorldPos, s.WorldRot, s.BasePos, s.BaseRot);
        var rebuilt = XcpMath.BrioPlaybackView(pos, rot, s.BasePos, s.BaseRot);
        var err2 = XcpMath.MaxAffineDiff(rebuilt, s.View);

        float fov = _useProjectionFov && s.FovProjection > 0.01f ? s.FovProjection : s.FovRender;
        _log.Info($"capture world={s.WorldPos} base={s.BasePos} rel={pos} rot={rot} fov={fov} selfCheck={err2}");

        if(!XcpMath.IsFinite(pos) || !XcpMath.IsFinite(rot) || !float.IsFinite(fov))
        {
            SetStatus("抓取结果无效（NaN），已丢弃。", true);
            return null;
        }

        return new Keyframe
        {
            Pos = pos, Rot = rot, Fov = fov, SelfCheckError = err2,
            TargetName = s.TargetName, BasePos = s.BasePos, BaseRot = s.BaseRot,
        };
    }

    private void CaptureAtPlayhead()
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        int frame = (int)MathF.Round(_tl.Playhead);
        var existing = _tl.Keys.FirstOrDefault(x => x.Frame == frame);
        if(existing != null)
        {
            CopyPose(k, existing);
            Selected = existing;
            SetStatus($"已替换第 {frame} 帧的镜头", false);
            return;
        }
        k.Frame = frame;
        if(k.Frame > _tl.Length) _tl.Length = k.Frame;
        _tl.Keys.Add(k);
        Selected = k;
        SetStatus($"已在第 {frame} 帧添加镜头 {_tl.Keys.Count}", false);
    }

    private void CaptureAppend()
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        k.Frame = _tl.Keys.Count == 0 ? (int)MathF.Round(_tl.Playhead) : _tl.LastFrame + _defaultGapFrames;
        if(k.Frame > _tl.Length) _tl.Length = k.Frame;
        _tl.Keys.Add(k);
        Selected = k;
        _tl.Playhead = k.Frame;
        _tlw.EnsureVisible(k.Frame, 700);
        SetStatus($"已在第 {k.Frame} 帧添加镜头 {_tl.Keys.Count}", false);
    }

    /// <summary>Inserts a node at the playhead whose pose is the current path's pose at that frame.</summary>
    private void InsertKeyFromPath()
    {
        if(_path == null) return;
        int frame = (int)MathF.Round(_tl.Playhead);
        var existing = _tl.Keys.FirstOrDefault(x => x.Frame == frame);
        if(existing != null)
        {
            Selected = existing;
            SetStatus($"第 {frame} 帧已经是镜头 {_tl.Keys.IndexOf(existing) + 1}，已选中它。", true);
            return;
        }

        var s = _path.Sample(_tl.FrameToPathTime(frame));
        if(!XcpMath.IsFinite(s.Pos) || !XcpMath.IsFinite(s.Rot) || !float.IsFinite(s.Fov))
        {
            SetStatus("这一帧的运镜数据无效，未生成。", true);
            return;
        }

        // carry the target info of the node this frame belongs to
        var src = _tl.Sorted().LastOrDefault(k => k.Frame <= frame) ?? _tl.Sorted()[0];
        _tl.Snapshot();
        var k = new Keyframe
        {
            Frame = frame,
            Pos = s.Pos,
            Rot = Quaternion.Normalize(s.Rot),
            Fov = s.Fov,
            Tangent = TangentMode.Auto,
            TargetName = src.TargetName,
            BasePos = src.BasePos,
            BaseRot = src.BaseRot,
        };
        // a frame inside a hold: split the hold so the camera still stays put
        if(src.HoldFrames > 0 && frame < src.Frame + src.HoldFrames)
        {
            k.HoldFrames = src.Frame + src.HoldFrames - frame;
            src.HoldFrames = frame - src.Frame;
        }
        _tl.Keys.Add(k);
        Selected = k;
        SetStatus($"已从运镜在第 {frame} 帧生成镜头 {_tl.Keys.Count}。", false);
    }

    private void Recapture(Keyframe target)
    {
        var k = MakeKey();
        if(k is null) return;
        _tl.Snapshot();
        CopyPose(k, target);
        SetStatus($"已用当前镜头替换镜头 {_tl.Keys.IndexOf(target) + 1}", false);
    }

    private static void CopyPose(Keyframe from, Keyframe to)
    {
        to.Pos = from.Pos; to.Rot = from.Rot; to.Fov = from.Fov; to.SelfCheckError = from.SelfCheckError;
        to.TargetName = from.TargetName; to.BasePos = from.BasePos; to.BaseRot = from.BaseRot;
    }

    private void Duplicate(Keyframe k)
    {
        _tl.Snapshot();
        var c = k.Clone();
        c.Frame = _tl.NearestFreeFrame(k.Frame + _defaultGapFrames);
        if(c.Frame > _tl.Length) _tl.Length = c.Frame;
        _tl.Keys.Add(c);
        Selected = c;
    }

    private void OpenSaveDialog()
    {
        FileDialog.SaveFileDialog("导出 XAT Camera###xcpcam_save", "XAT Camera File{.xcp}", "camera", ".xcp",
            (ok, path) =>
            {
                if(!ok) return;
                if(!path.EndsWith(".xcp", StringComparison.OrdinalIgnoreCase)) path += ".xcp";
                Export(path);
            }, _lastDir, true);
    }

    private void Export(string path)
    {
        try
        {
            if(_path == null) throw new InvalidOperationException("至少需要 2 个镜头节点");
            var frames = _path.Bake(PathBuilder.Fps);
            XcpWriter.Write(path, frames);
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            SetStatus($"已导出 {frames.Count} 帧 (30fps) → {path}", false);
            _log.Info($"exported {frames.Count} frames to {path}");
        }
        catch(Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", true);
            _log.Error(ex, "export failed");
        }
    }

    private void SetStatus(string s, bool error)
    {
        _status = s;
        _statusIsError = error;
    }
}
