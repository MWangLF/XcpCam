using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System.Numerics;
using XcpCam.Core;

using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XcpCam;

/// <summary>
/// Reads the camera that is actually being rendered (works for Brio free cameras and normal orbit cameras),
/// and the GPose target's draw-object transform (same source Brio's cutscene player uses as its base).
/// The view comes from SceneCamera.ViewMatrix, which is what Brio writes for free cams and for .xcp playback
/// (Brio/Game/Camera/CameraService.cs CameraSceneUpdateDetour), so capture and playback share one convention.
/// </summary>
public static unsafe class CameraCapture
{
    public sealed record Snapshot(
        Matrix4x4 View, Matrix4x4 RenderView, Vector3 WorldPos, Quaternion WorldRot,
        float FovRender, float FovProjection,
        Vector3 BasePos, Quaternion BaseRot, string TargetName);

    public static bool TryGetBase(ITargetManager targets, out Vector3 basePos, out Quaternion baseRot, out string name, out string error)
    {
        basePos = default; baseRot = Quaternion.Identity; name = ""; error = "";
        var go = targets.GPoseTarget;
        if(go == null) { error = I18n.T("capture.cam.no_gpose_target"); return false; }

        var native = (GameObjectStruct*)go.Address;
        if(native == null || native->DrawObject == null) { error = I18n.T("capture.cam.no_draw_object"); return false; }

        var obj = &native->DrawObject->Object;
        basePos = new Vector3(obj->Position.X, obj->Position.Y, obj->Position.Z);
        baseRot = new Quaternion(obj->Rotation.X, obj->Rotation.Y, obj->Rotation.Z, obj->Rotation.W);
        name = go.Name.TextValue;
        return true;
    }

    public static bool TryCapture(ITargetManager targets, out Snapshot? snap, out string error)
    {
        snap = null;
        if(!TryGetBase(targets, out var basePos, out var baseRot, out var name, out error))
            return false;

        var mgr = CameraManager.Instance();
        if(mgr == null) { error = I18n.T("capture.cam.no_manager"); return false; }
        var cam = mgr->GetActiveCamera();
        if(cam == null) { error = I18n.T("capture.cam.no_active"); return false; }

        var scene = &cam->CameraBase.SceneCamera;
        var render = scene->RenderCamera;

        Matrix4x4 view = scene->ViewMatrix;
        Matrix4x4 renderView = render != null ? render->ViewMatrix : default;
        Matrix4x4 proj = render != null ? render->ProjectionMatrix : default;
        float fovRender = render != null ? render->FoV : 0f;

        if(!XcpMath.DecomposeView(view, out var wp, out var wr))
        {
            error = I18n.T("capture.cam.invalid_view", XcpMath.Dump(view));
            return false;
        }

        snap = new Snapshot(view, renderView, wp, wr, fovRender, XcpMath.FovFromProjection(proj), basePos, baseRot, name);
        return true;
    }
}
