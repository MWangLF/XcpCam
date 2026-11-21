using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using XcpCam.Core;

namespace XcpCam.Tests;

public static class XcpFileTests
{
    public static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("XcpFileTests-");
        string path = Path.Combine(directory.FullName, "camera.xcp");
        void Check(bool ok, string message)
        {
            if(!ok) throw new InvalidOperationException(message);
        }
        void Reject(Action action)
        {
            try { action(); }
            catch(InvalidDataException) { return; }
            throw new InvalidOperationException("Expected invalid data");
        }
        try
        {
            var frame = new XcpFrame(new(1, -2, 3), new(0, 0, 0, 1), 0.5f);
            const string record = "0000803F000000C0000040400000000000000000000000000000803F0000003F";
            var legacy = Convert.FromHexString("5843503103000000" + record + record + record);
            var frames = new[] { frame, frame }; // Intentional duplicate in the user's shot.
            XcpWriter.Write(path, frames);
            Check(File.ReadAllBytes(path).SequenceEqual(legacy), "Legacy bytes changed");
            var plain = XcpFile.Read(path);
            Check(plain.Project == null && plain.Warning == null && plain.Frames.SequenceEqual(frames.Append(frame)), "Plain records lost");
            var single = Convert.FromHexString("5843503101000000" + record);
            File.WriteAllBytes(path, single);
            Check(XcpFile.Read(path).Frames.SequenceEqual(new[] { frame }), "Single unpadded record lost");

            var pos = new Vector3(30, -7, 6);
            var rot = Quaternion.CreateFromYawPitchRoll(.4f, .2f, .1f);
            byte[] json = "{\"场景\":1}"u8.ToArray();
            XcpFile.Write(path, frames, json, pos, rot);
            var enhanced = File.ReadAllBytes(path);
            Check(enhanced.AsSpan(0, legacy.Length).SequenceEqual(legacy), "Enhanced prefix changed");
            var read = XcpFile.Read(path);
            Check(read.Warning == null && read.Project != null && read.Project.SequenceEqual(json), "Project round trip");
            Check(read.BasePos == pos && read.BaseRot == rot && read.Frames.SequenceEqual(frames), "Export basis or effective count changed");

            Reject(() => XcpWriter.Write(path, new[] { frame, frame with { Fov = float.NaN } }));
            Reject(() => XcpFile.Write(path, frames, "{"u8.ToArray(), pos, rot));
            Check(enhanced.SequenceEqual(File.ReadAllBytes(path)) && directory.GetFiles().Length == 1, "Failed save changed original or leaked temporary file");
            void Fallback(byte[] damaged)
            {
                File.WriteAllBytes(path, damaged);
                var result = XcpFile.Read(path);
                Check(result.Project == null && !string.IsNullOrEmpty(result.Warning) && result.Frames.Count == 3, "Expected full frame fallback");
            }
            void Alter(int offset, int value, bool refreshDigest = false)
            {
                var damaged = enhanced.ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(offset), value);
                if(refreshDigest) SHA256.HashData(damaged.AsSpan(0, damaged.Length - 32), damaged.AsSpan(damaged.Length - 32));
                Fallback(damaged);
            }
            int tail = legacy.Length;
            Fallback(enhanced[..(tail + 1)]);
            Fallback(enhanced[..^1]);
            Fallback(enhanced.Concat(new byte[1]).ToArray());
            Alter(8, 0); // Finite camera change invalidates the project digest.
            Alter(tail + 4, 2);
            Alter(tail + 8, 1, true);
            Alter(tail + 40, int.MaxValue);
            Alter(tail + 44, -1);
            Alter(tail + 40, json.Length - 1, true);
            Alter(tail + 48, -1, true);
            foreach(var bad in new[] { single[..^1], Convert.FromHexString("58435031FFFFFFFF"), new byte[8] })
            {
                File.WriteAllBytes(path, bad);
                Reject(() => XcpFile.Read(path));
            }
        }
        finally { directory.Delete(true); }
    }
}
