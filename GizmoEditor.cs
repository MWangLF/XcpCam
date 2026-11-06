using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam;

/// <summary>
/// In-world ImGuizmo for the selected camera node: translate / rotate the node's camera directly in the scene.
/// Matrices are taken from the active game camera exactly like Brio's posing overlay
/// (Brio/Game/Camera/CameraExtensions.cs GetViewMatrix / GetProjectionMatrix).
/// Node pose is stored relative to the GPose target; the gizmo works in world space.
/// </summary>
public sealed unsafe class GizmoEditor
{
    public enum Op { Translate, Rotate }

    public bool Enabled = true;
    public Op Operation = Op.Translate;
    public bool LocalSpace = true;
    public bool Snap;
    public float SnapTranslate = 0.1f;
    public float SnapRotateDeg = 5f;

    /// <summary>True while the user is dragging the gizmo (true on the first frame of a drag as well).</summary>
    public bool IsUsing { get; private set; }
    public bool IsOver { get; private set; }
    /// <summary>Set on the frame a drag begins, so callers can take an undo snapshot.</summary>
    public bool DragStarted { get; private set; }

    private bool _wasUsing;
    private const int GizmoId = 0x58435043; // "XCPC"

    public static bool TryGetCameraMatrices(out Matrix4x4 view, out Matrix4x4 proj)
    {
        view = default; proj = default;
        var mgr = CameraManager.Instance();
        if(mgr == null) return false;
        var cam = mgr->GetActiveCamera();
        if(cam == null) return false;
        var scene = &cam->CameraBase.SceneCamera;
        var render = scene->RenderCamera;
        if(render == null) return false;

        view = scene->ViewMatrix;
        view.M44 = 1;
        proj = render->ProjectionMatrix;
        float far = render->FarPlane, near = render->NearPlane;
        proj.M33 = -(far + near) / (far - near);
        proj.M43 = -(2f * far * near) / (far - near);
        return true;
    }

    /// <summary>
    /// Draws and handles the gizmo for <paramref name="key"/>. Returns true when the node was changed this frame.
    /// </summary>
    public bool Draw(Keyframe key, Vector3 basePos, Quaternion baseRot)
    {
        DragStarted = false;
        IsUsing = false; IsOver = false;
        if(!Enabled) { _wasUsing = false; return false; }
        if(!TryGetCameraMatrices(out var view, out var proj)) return false;

        var worldPos = Vector3.Transform(key.Pos, baseRot) + basePos;
        var worldRot = Quaternion.Normalize(baseRot * key.Rot);
        var model = Matrix4x4.CreateFromQuaternion(worldRot) * Matrix4x4.CreateTranslation(worldPos);

        var io = ImGui.GetIO();
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist(); // current (overlay) window, same as Brio's posing overlay
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.Enable(true);

        var op = Operation == Op.Translate ? ImGuizmoOperation.Translate : ImGuizmoOperation.Rotate;
        var mode = LocalSpace ? ImGuizmoMode.Local : ImGuizmoMode.World;

        bool changed;
        if(Snap)
        {
            var snap = Operation == Op.Translate ? new Vector3(SnapTranslate) : new Vector3(SnapRotateDeg);
            var delta = Matrix4x4.Identity;
            changed = ImGuizmo.Manipulate(&view.M11, &proj.M11, op, mode, &model.M11, &delta.M11, &snap.X);
        }
        else
            changed = ImGuizmo.Manipulate(ref view, ref proj, op, mode, ref model);

        IsUsing = ImGuizmo.IsUsing();
        IsOver = ImGuizmo.IsOver();
        ImGuizmo.SetID(0);

        if(IsUsing && !_wasUsing) DragStarted = true;
        _wasUsing = IsUsing;

        if(!changed) return false;

        if(!Matrix4x4.Decompose(model, out _, out var newRot, out var newPos)) return false;
        if(!XcpMath.IsFinite(newPos) || !XcpMath.IsFinite(newRot)) return false;

        var (rel, relRot) = XcpMath.WorldToRelative(newPos, Quaternion.Normalize(newRot), basePos, baseRot);
        key.Pos = rel;
        key.Rot = relRot;
        return true;
    }
}
