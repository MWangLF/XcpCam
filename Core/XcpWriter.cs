using System.Collections.Generic;
using System.Numerics;

namespace XcpCam.Core;

/// <summary>Writes a plain XAT camera file, including the compatibility frame for Brio.</summary>
public static class XcpWriter
{
    public static void Write(string path, IReadOnlyList<XcpFrame> frames) =>
        XcpFile.Write(path, frames, null, Vector3.Zero, Quaternion.Identity);
}
