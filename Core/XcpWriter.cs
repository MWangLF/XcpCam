using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XcpCam.Core;

/// <summary>
/// Writes XAT .xcp: "XCP1", uint32 count, count * (pos xyz, rot xyzw, fov), little endian.
/// Brio's reader (Files/XATCameraFile.cs) loops i &lt; count-1 and drops the last record,
/// so we write count = N+1 and append a duplicate of the last frame. Brio then reads exactly N frames.
/// </summary>
public static class XcpWriter
{
    public static void Write(string path, IReadOnlyList<XcpFrame> frames)
    {
        if(frames.Count == 0)
            throw new InvalidDataException("没有帧可导出");
        foreach(var f in frames)
            if(!XcpMath.IsFinite(f.Pos) || !XcpMath.IsFinite(f.Rot) || !float.IsFinite(f.Fov))
                throw new InvalidDataException("帧数据包含 NaN/Infinity，已取消导出");

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Encoding.ASCII.GetBytes("XCP1"));
        w.Write((uint)frames.Count + 1);
        foreach(var f in frames)
            WriteFrame(w, f);
        WriteFrame(w, frames[^1]);
    }

    static void WriteFrame(BinaryWriter w, XcpFrame f)
    {
        w.Write(f.Pos.X); w.Write(f.Pos.Y); w.Write(f.Pos.Z);
        w.Write(f.Rot.X); w.Write(f.Rot.Y); w.Write(f.Rot.Z); w.Write(f.Rot.W);
        w.Write(f.Fov);
    }
}
