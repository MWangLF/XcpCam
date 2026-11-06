using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

public sealed class MainWindow : Window
{
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;

    public readonly FileDialogManager FileDialog = new();

    private readonly List<Keyframe> _keys = [];
    private bool _useProjectionFov = true;
    private float _defaultGap = 2f;
    private string _status = "";
    private bool _statusIsError;
    private string _lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static readonly string[] EaseNames = ["Linear", "Smooth", "In", "Out"];

    public MainWindow(ITargetManager targets, IClientState clientState, IPluginLog log)
        : base("XcpCam - 镜头关键帧 → .xcp###xcpcam_main")
    {
        _targets = targets;
        _clientState = clientState;
        _log = log;
        Size = new Vector2(620, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        bool gpose = _clientState.IsGPosing;
        if(!gpose)
            ImGui.TextColored(new Vector4(1, 0.7f, 0.3f, 1), "需要在 GPose 中使用。");

        ImGui.TextDisabled("流程：在 Brio 里切到/摆好一个镜头 → 抓取 → 换下一个镜头 → 抓取 … → 导出 .xcp");
        ImGui.TextDisabled("播放：Brio 相机编辑器 → XAT Camera 选择文件（同一个 GPose 目标）。");
        ImGui.Separator();

        using(ImRaiiDisabled(!gpose))
        {
            if(ImGui.Button("抓取当前镜头"))
                Capture();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.DragFloat("默认间隔(秒)", ref _defaultGap, 0.1f, 0.1f, 60f, "%.1f");
        ImGui.SameLine();
        ImGui.Checkbox("FoV 取投影矩阵", ref _useProjectionFov);
        if(ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选：从投影矩阵反算实际垂直 FoV（推荐）；不勾：读 RenderCamera.FoV 字段。");

        ImGui.Separator();
        DrawTable();
        ImGui.Separator();

        using(ImRaiiDisabled(_keys.Count < 2))
        {
            if(ImGui.Button("导出 .xcp"))
                OpenSaveDialog();
        }
        ImGui.SameLine();
        if(ImGui.Button("按时间排序"))
            _keys.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
        ImGui.SameLine();
        if(ImGui.Button("清空"))
            _keys.Clear();

        if(_keys.Count >= 2)
        {
            var sorted = _keys.OrderBy(k => k.TimeSec).ToList();
            float dur = sorted[^1].TimeSec - sorted[0].TimeSec;
            ImGui.SameLine();
            ImGui.TextDisabled($"时长 {dur:0.00}s ≈ {Math.Max(2, (int)MathF.Round(dur * PathBuilder.Fps) + 1)} 帧 @30fps");

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

        if(!string.IsNullOrEmpty(_status))
            ImGui.TextColored(_statusIsError ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.5f, 1, 0.5f, 1), _status);
    }

    private void DrawTable()
    {
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        var height = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 70f);
        if(!ImGui.BeginTable("##keys", 6, flags, new Vector2(0, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn("时间(秒)");
        ImGui.TableSetupColumn("缓动(到下一帧)");
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
            ImGui.Text($"{i + 1}");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(70);
            ImGui.DragFloat("##t", ref k.TimeSec, 0.05f, 0f, 3600f, "%.2f");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(90);
            int ease = (int)k.Ease;
            if(ImGui.Combo("##e", ref ease, EaseNames, EaseNames.Length))
                k.Ease = (Ease)ease;

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

        if(remove >= 0) _keys.RemoveAt(remove);
        if(up > 0) (_keys[up - 1], _keys[up]) = (_keys[up], _keys[up - 1]);
        if(down >= 0 && down < _keys.Count - 1) (_keys[down + 1], _keys[down]) = (_keys[down], _keys[down + 1]);
    }

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
        _log.Info($"  sceneView={XcpMath.Dump(s.View)}");
        _log.Info($"  renderView={XcpMath.Dump(s.RenderView)}");

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
        k.TimeSec = _keys.Count == 0 ? 0f : _keys.Max(x => x.TimeSec) + _defaultGap;
        _keys.Add(k);
        SetStatus($"已抓取第 {_keys.Count} 帧（{k.TimeSec:0.00}s）", false);
    }

    private void Recapture(int i)
    {
        var k = MakeKey();
        if(k is null) return;
        k.TimeSec = _keys[i].TimeSec;
        k.Ease = _keys[i].Ease;
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
            var frames = PathBuilder.Build(_keys);
            XcpWriter.Write(path, frames);
            _lastDir = Path.GetDirectoryName(path) ?? _lastDir;
            SetStatus($"已导出 {frames.Count} 帧 → {path}", false);
            _log.Info($"[XcpCam] exported {frames.Count} frames to {path}");
        }
        catch(Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", true);
            _log.Error(ex, "[XcpCam] export failed");
        }
    }

    private void SetStatus(string s, bool error)
    {
        _status = s;
        _statusIsError = error;
    }

    private static IDisposable ImRaiiDisabled(bool disabled)
    {
        ImGui.BeginDisabled(disabled);
        return new EndDisabled();
    }

    private sealed class EndDisabled : IDisposable
    {
        public void Dispose() => ImGui.EndDisabled();
    }
}
