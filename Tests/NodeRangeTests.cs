using System;
using System.Numerics;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class NodeRangeTests
{
    public static void Run(Action<bool,string> check)
    {
        check(NodeRange.MaxRange == NodeRange.DefaultRange && NodeRange.DefaultRange == 2000f, "Range: default movement limit is 2000 m");
        NodeRange.MaxRange = 1f; check(NodeRange.MaxRange == NodeRange.MinimumRange, "Range: user limit clamps to minimum");
        NodeRange.MaxRange = float.NaN; check(NodeRange.MaxRange == NodeRange.DefaultRange, "Range: invalid limit falls back to default");
        NodeRange.MaxRange = 500f;
        check(Math.Abs(NodeRange.Clamp(new Vector3(0, 0, 900)).Length() - 500f) < 1e-3f, "Range: user-adjusted limit applies");
        NodeRange.MaxRange = NodeRange.DefaultRange;
        var near = new Vector3(3, 1, -4);
        check(NodeRange.Clamp(near) == near, "Range: positions inside the limit are unchanged");
        var far = NodeRange.Clamp(new Vector3(1e7f, 0, 1e7f));
        check(Math.Abs(far.Length() - NodeRange.MaxRange) < 1e-2f && far.X > 0 && far.Z > 0, "Range: far positions clamp to the limit along their direction");
        check(NodeRange.Clamp(new Vector3(float.NaN, 0, 0)) == Vector3.Zero, "Range: non-finite positions are rejected");

        // Camera at origin looking down -Z (view basis identity), perspective with W = -z_view.
        var view = Matrix4x4.Identity;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, .1f, 1000f);
        check(NodeRange.Visible(new Vector3(0, 0, -5), view, proj), "Gizmo: point in front of the camera is visible");
        check(!NodeRange.Visible(new Vector3(0, 0, 5), view, proj), "Gizmo: point behind the camera is skipped");
        check(!NodeRange.Visible(new Vector3(500, 0, -1), view, proj), "Gizmo: point far off screen is skipped");
    }
}
