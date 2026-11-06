using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
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
    private readonly TimelineSync _sync;
    private readonly TimelineBake _bake;

    public readonly FileDialogManager FileDialog = new();

    private readonly List<Keyframe> _keys = [];
    private readonly PathSettings _settings = new();
    private bool _useProjectionFov = true;
    private float _defaultGap = 2f;
    private string _status = "";
    private bool _statusIsError;
    private string _lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // cached path; rebuilt when keys/settings change
    private CameraPath? _path;
    private int _pathHash;

    // preview
    private bool _previewPlaying;
    private float _previewTime;
    private bool _previewLoop = true;

    // bake target
    private int _bakeCamIndex;

    private static readonly string[] TangentNames = ["自动(平滑)", "停顿", "匀速"];
    private static readonly string[] ModeNames = ["按时间平滑", "整体匀速"];

    public MainWindow(ITargetManager targets, IClientState clientState, IPluginLog log, BrioBridge brio, Overlay overlay)
        : base("XcpCam - 镜头关键帧 → .xcp###xcpcam_main")
    {
        _targets = targets;
        _clientState = clientState;
        _log = log;
        _brio = brio;
        _overlay = overlay;
        _sync = new TimelineSync(brio, log);
        _bake = new TimelineBake(brio, log);
        Size = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        try { _sync.Unbind(); } catch { /* Brio may already be gone */ }
    }

    // ------------------------------------------------------------------ ticking

    public void FrameworkTick(float dt)
    {
        RebuildPathIfNeeded();

        if(_previewPlaying && _path != null)
        {
            _previewTime += dt;
            if(_previewTime > _path.Duration)
            {
                if(_previewLoop) _previewTime = 0;
                else { _previewTime = _path.Duration; _previewPlaying = false; }
            }
        }

        try { _sync.Tick(_path?.Duration ?? 0); }
        catch(Exception ex) { _log.Warning(ex, "TimelineSync tick failed"); }
    }

    public void DrawOverlay()
    {
        _overlay.PreviewTime = _previewPlaying || _previewTime > 0 ? _previewTime : null;
        _overlay.Draw(_path, _keys, _sync.PlayheadSec);
    }

    private int ComputeHash()
    {
        var h = new HashCode();
        h.Add(_settings.Mode); h.Add(_settings.EaseStart); h.Add(_settings.EaseEnd);
        h.Add(_settings.Duration); h.Add(_settings.EaseInFraction); h.Add(_settings.EaseOutFraction); h.Add(_settings.RotationWeight);
        foreach(var k in _keys) { h.Add(k.TimeSec); h.Add(k.Pos); h.Add(k.Rot); h.Add(k.Fov); h.Add(k.Tangent); h.Add(k.HoldSec); }
        h.Add(_keys.Count);
        return h.ToHashCode();
    }

    private void RebuildPathIfNeeded()
    {
        int hash = ComputeHash();
        if(hash == _pathHash && (_path != null || _keys.Count < 2)) return;
        _pathHash = hash;
        try
        {
            _path = CameraPath.Create(_keys, _settings);
            if(_sync.Bound && _path != null) _sync.Refresh(_path);
        }
        catch(Exception ex)
        {
            _path = null;
            _log.Warning(ex, "path build failed");
        }
    }

    // ------------------------------------------------------------------ UI

    public override void Draw()
    {
        bool gpose = _clientState.IsGPosing;
        if(!gpose)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "需要在 GPose 中使用。");

        if(ImGui.BeginTabBar("##tabs"))
        {
            if(ImGui.BeginTabItem("关键帧"))
            {
                DrawKeysTab(gpose);
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("可视化"))
            {
                DrawVisualTab();
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("Brio 时间轴"))
            {
                DrawBrioTab(gpose);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if(!string.IsNullOrEmpty(_status))
            ImGui.TextColored(_statusIsError ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.5f, 1, 0.5f, 1), _status);
    }

    // ---------------- keys tab ----------------

    private void DrawKeysTab(bool gpose)
    {
        ImGui.TextDisabled("在 Brio 里切到/摆好一个镜头 → 抓取 → 换下一个镜头 → 抓取 … → 导出 .xcp 或绑定 Brio 时间轴");

        ImGui.BeginDisabled(!gpose);
        if(ImGui.Button("抓取当前镜头"))
            Capture();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        ImGui.DragFloat("默认间隔(秒)", ref _defaultGap, 0.1f, 0.1f, 60f, "%.1f");
        ImGui.SameLine();
        ImGui.Checkbox("FoV 取投影矩阵", ref _useProjectionFov);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选：从投影矩阵反算实际垂直 FoV（推荐）；不勾：读 RenderCamera.FoV 字段。");

        ImGui.Separator();
        DrawPacing();
        ImGui.Separator();
        DrawTable();
        ImGui.Separator();

        ImGui.BeginDisabled(_keys.Count < 2);
        if(ImGui.Button("导出 .xcp"))
            OpenSaveDialog();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if(ImGui.Button("按时间排序"))
            _keys.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
        ImGui.SameLine();
        if(ImGui.Button("清空"))
        {
            _keys.Clear();
            _overlay.SelectedKey = -1;
        }

        if(_path != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"时长 {_path.Duration:0.00}s · 路径长 {_path.TotalLength:0.0}y · {Math.Max(2, (int)MathF.Round(_path.Duration * PathBuilder.Fps) + 1)} 帧 @30fps");
        }

        DrawTargetWarnings(gpose);
    }

    private void DrawPacing()
    {
        int mode = (int)_settings.Mode;
        ImGui.SetNextItemWidth(130);
        if(ImGui.Combo("运镜节奏", ref mode, ModeNames, ModeNames.Length))
            _settings.Mode = (TimingMode)mode;
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("按时间平滑：每个镜头在设定的时间到达，经过镜头时速度连续、不顿挫。\n整体匀速：忽略各镜头时间，整条路径以恒定速度运动，只在首尾缓入缓出。");

        ImGui.SameLine();
        if(_settings.Mode == TimingMode.Timed)
        {
            ImGui.Checkbox("开头缓入", ref _settings.EaseStart);
            ImGui.SameLine();
            ImGui.Checkbox("结尾缓出", ref _settings.EaseEnd);
        }
        else
        {
            ImGui.SetNextItemWidth(70);
            ImGui.DragFloat("总时长(秒)", ref _settings.Duration, 0.1f, 0.5f, 600f, "%.1f");
            ImGui.SameLine();
            float a = _settings.EaseInFraction * 100, b = _settings.EaseOutFraction * 100;
            ImGui.SetNextItemWidth(60);
            if(ImGui.DragFloat("缓入%", ref a, 0.5f, 0f, 50f, "%.0f")) _settings.EaseInFraction = a / 100f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            if(ImGui.DragFloat("缓出%", ref b, 0.5f, 0f, 50f, "%.0f")) _settings.EaseOutFraction = b / 100f;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            ImGui.DragFloat("转向权重", ref _settings.RotationWeight, 0.05f, 0f, 10f, "%.2f");
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("原地转向/变焦时也分配时间：1 弧度转向按多少米移动来计。0 = 只按移动距离分配时间。");
        }
    }

    private void DrawTable()
    {
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        var height = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 80f);
        bool timed = _settings.Mode == TimingMode.Timed;
        if(!ImGui.BeginTable("##keys", 7, flags, new Vector2(0, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn(timed ? "时间(秒)" : "到达(秒)");
        ImGui.TableSetupColumn("经过方式");
        ImGui.TableSetupColumn("停留(秒)");
        ImGui.TableSetupColumn("相对位置 / FoV°");
        ImGui.TableSetupColumn("自检");
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        int remove = -1, up = -1, down = -1;
        for(int i = 0; i < _keys.Count; i++)
        {
            var k = _keys[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool sel = _overlay.SelectedKey == i;
            if(ImGui.Selectable($"{i + 1}", sel))
                _overlay.SelectedKey = sel ? -1 : i;

            ImGui.TableNextColumn();
            if(timed)
            {
                ImGui.SetNextItemWidth(65);
                ImGui.DragFloat("##t", ref k.TimeSec, 0.05f, 0f, 3600f, "%.2f");
            }
            else
            {
                float t = _path != null && i < _path.KeyTimes.Count ? _path.KeyTimes[i] : 0;
                ImGui.TextDisabled($"{t:0.00}");
            }

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!timed);
            ImGui.SetNextItemWidth(95);
            int tan = (int)k.Tangent;
            if(ImGui.Combo("##tan", ref tan, TangentNames, TangentNames.Length))
                k.Tangent = (TangentMode)tan;
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip("自动：平滑穿过，速度连续（推荐）\n停顿：在这个镜头处减速停下再出发\n匀速：从这个镜头到下一个镜头保持匀速");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(55);
            ImGui.DragFloat("##hold", ref k.HoldSec, 0.05f, 0f, 60f, "%.2f");
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.Text($"({k.Pos.X:0.00}, {k.Pos.Y:0.00}, {k.Pos.Z:0.00})  {k.Fov * 180f / MathF.PI:0.0}°");

            ImGui.TableNextColumn();
            bool ok = k.SelfCheckError < 1e-3f;
            ImGui.TextColored(ok ? new Vector4(0.5f, 1, 0.5f, 1) : new Vector4(1, 0.4f, 0.4f, 1), ok ? "OK" : $"{k.SelfCheckError:0.0e0}");
            if(ImGui.IsItemHovered())
                ImGui.SetTooltip($"按 Brio 播放公式重建视图矩阵与实际视图矩阵的最大误差：{k.SelfCheckError:0.000000}\n目标：{k.TargetName}");

            ImGui.TableNextColumn();
            if(ImGui.SmallButton("↑")) up = i;
            ImGui.SameLine();
            if(ImGui.SmallButton("↓")) down = i;
            ImGui.SameLine();
            if(ImGui.SmallButton("重抓")) Recapture(i);
            ImGui.SameLine();
            if(ImGui.SmallButton("删除")) remove = i;

            ImGui.PopID();
        }
        ImGui.EndTable();

        if(remove >= 0)
        {
            _keys.RemoveAt(remove);
            if(_overlay.SelectedKey >= _keys.Count) _overlay.SelectedKey = -1;
        }
        if(up > 0) (_keys[up - 1], _keys[up]) = (_keys[up], _keys[up - 1]);
        if(down >= 0 && down < _keys.Count - 1) (_keys[down + 1], _keys[down]) = (_keys[down], _keys[down + 1]);
    }

    private void DrawTargetWarnings(bool gpose)
    {
        if(_keys.Count < 1) return;
        var names = _keys.Select(k => k.TargetName).Distinct().Count();
        if(names > 1)
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "警告：关键帧抓取时的 GPose 目标不一致，播放时会错位。");
        else if(gpose && CameraCapture.TryGetBase(_targets, out var bp, out var br, out var nm, out _))
        {
            var k0 = _keys[0];
            if(nm != k0.TargetName || Vector3.Distance(bp, k0.BasePos) > 0.01f || MathF.Abs(Quaternion.Dot(br, k0.BaseRot)) < 0.9999f)
                ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "提示：目标角色在抓取后移动/旋转或已更换。.xcp 是相对目标的，播放会跟随当前目标。");
        }
    }

    // ---------------- visual tab ----------------

    private void DrawVisualTab()
    {
        ImGui.Checkbox("在游戏中显示", ref _overlay.Enabled);
        ImGui.Indent();
        ImGui.Checkbox("运镜路径", ref _overlay.ShowPath);
        ImGui.SameLine();
        ImGui.Checkbox("按速度着色（蓝=慢，红=快）", ref _overlay.SpeedColors);
        ImGui.Checkbox("镜头视锥", ref _overlay.ShowFrustums);
        ImGui.SameLine();
        ImGui.Checkbox("编号/时间标签", ref _overlay.ShowLabels);
        ImGui.SetNextItemWidth(120);
        ImGui.DragFloat("视锥大小", ref _overlay.FrustumSize, 0.01f, 0.05f, 3f, "%.2f");
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.Text("预览（只在画面中移动绿色视锥，不接管游戏镜头）");
        if(_path == null)
        {
            ImGui.TextDisabled("至少需要 2 个关键帧。");
            return;
        }

        if(ImGui.Button(_previewPlaying ? "暂停" : "播放预览"))
        {
            if(!_previewPlaying && _previewTime >= _path.Duration) _previewTime = 0;
            _previewPlaying = !_previewPlaying;
        }
        ImGui.SameLine();
        if(ImGui.Button("停止"))
        {
            _previewPlaying = false;
            _previewTime = 0;
        }
        ImGui.SameLine();
        ImGui.Checkbox("循环", ref _previewLoop);

        ImGui.SetNextItemWidth(-1);
        float t = _previewTime;
        if(ImGui.SliderFloat("##scrub", ref t, 0f, _path.Duration, $"%.2f / {_path.Duration:0.00}s"))
        {
            _previewTime = t;
            _previewPlaying = false;
        }

        var s = _path.Sample(_previewTime);
        ImGui.TextDisabled($"位置 ({s.Pos.X:0.00}, {s.Pos.Y:0.00}, {s.Pos.Z:0.00})  FoV {s.Fov * 180f / MathF.PI:0.0}°");
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

        ImGui.TextDisabled($"Brio 时间轴：{(_brio.TimelineIsPlaying ? "播放中" : "已停止")}  第 {_brio.TimelineTime:0} 帧 / {_brio.FrameMax}  @ {_brio.TimelineFps}fps");

        // ---- mode 1 ----
        ImGui.Separator();
        ImGui.Text("方式 1：联动 Brio 的 XAT 播放器");
        ImGui.TextDisabled("时间轴开始播放时自动从头播放本运镜；时间轴停止/暂停时 XAT 也停止。\n限制：XAT 不能跳转，拖动进度条或暂停后继续不会对齐；需要使用非自由相机。");
        ImGui.BeginDisabled(!_brio.CanSync || _path == null);
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("开始于时间轴帧", ref _sync.StartFrame);
        if(_sync.StartFrame < 0) _sync.StartFrame = 0;
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
        ImGui.TextDisabled("由 Brio 时间轴直接驱动自由相机，拖动/暂停/循环都完全同步。会改写所选相机轨道（可撤销）。");

        var cams = _brio.CanBake ? _brio.GetFreeCameras() : [];
        if(cams.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "没有可用的 Brio 自由相机。请先在 Brio 相机窗口中新建一个 Free 相机。");
        }
        else
        {
            _bakeCamIndex = Math.Clamp(_bakeCamIndex, 0, cams.Count - 1);
            var names = cams.Select(c => c.Name + (c.InTimeline ? "" : "（未加入时间轴）")).ToArray();
            ImGui.SetNextItemWidth(200);
            ImGui.Combo("目标自由相机", ref _bakeCamIndex, names, names.Length);

            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("起始帧", ref _bake.StartFrame);
            if(_bake.StartFrame < 0) _bake.StartFrame = 0;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputInt("每隔几帧一个关键帧", ref _bake.StepFrames);
            _bake.StepFrames = Math.Clamp(_bake.StepFrames, 1, 30);
            ImGui.Checkbox("清除该相机轨道原有关键帧", ref _bake.ClearExisting);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            float baseDeg = _bake.BaseFov * 180f / MathF.PI;
            if(ImGui.DragFloat("基础 FoV°", ref baseDeg, 0.1f, 20f, 120f, "%.1f")) _bake.BaseFov = baseDeg * MathF.PI / 180f;
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Brio 自由相机的 FoV 是在游戏基础 FoV 上的偏移量（默认约 44.7°）。烘焙后如果视角大小不对，调整这里。");

            ImGui.BeginDisabled(_path == null || !gpose);
            if(ImGui.Button("烘焙到时间轴"))
                Try(() =>
                {
                    if(!CameraCapture.TryGetBase(_targets, out var bp, out var br, out _, out var err))
                        throw new InvalidOperationException(err);
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
        if(!CameraCapture.TryCapture(_targets, out var s, out var err) || s is null)
        {
            SetStatus(err, true);
            return null;
        }

        var (pos, rot) = XcpMath.WorldToRelative(s.WorldPos, s.WorldRot, s.BasePos, s.BaseRot);
        var rebuilt = XcpMath.BrioPlaybackView(pos, rot, s.BasePos, s.BaseRot);
        var err2 = XcpMath.MaxAffineDiff(rebuilt, s.View);

        float fov = _useProjectionFov && s.FovProjection > 0.01f ? s.FovProjection : s.FovRender;
        _log.Info($"capture world={s.WorldPos} base={s.BasePos} baseRot={s.BaseRot} rel={pos} rot={rot} fovRender={s.FovRender} fovProj={s.FovProjection} selfCheck={err2}");
        _log.Debug($"  sceneView={XcpMath.Dump(s.View)}");
        _log.Debug($"  renderView={XcpMath.Dump(s.RenderView)}");

        if(!XcpMath.IsFinite(pos) || !XcpMath.IsFinite(rot) || !float.IsFinite(fov))
        {
            SetStatus("抓取结果无效（NaN），已丢弃。请查看 /xllog 中 XcpCam 的矩阵输出。", true);
            return null;
        }

        return new Keyframe
        {
            Pos = pos, Rot = rot, Fov = fov, SelfCheckError = err2,
            TargetName = s.TargetName, BasePos = s.BasePos, BaseRot = s.BaseRot,
        };
    }

    private void Capture()
    {
        var k = MakeKey();
        if(k is null) return;
        k.TimeSec = _keys.Count == 0 ? 0f : _keys.Max(x => x.TimeSec + x.HoldSec) + _defaultGap;
        _keys.Add(k);
        SetStatus($"已抓取第 {_keys.Count} 帧（{k.TimeSec:0.00}s）", false);
    }

    private void Recapture(int i)
    {
        var k = MakeKey();
        if(k is null) return;
        k.TimeSec = _keys[i].TimeSec;
        k.Tangent = _keys[i].Tangent;
        k.HoldSec = _keys[i].HoldSec;
        _keys[i] = k;
        SetStatus($"已重抓第 {i + 1} 帧", false);
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
            if(_path == null) throw new InvalidOperationException("至少需要 2 个关键帧");
            var frames = _path.Bake(PathBuilder.Fps);
            XcpWriter.Write(path, frames);
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            SetStatus($"已导出 {frames.Count} 帧 → {path}", false);
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
