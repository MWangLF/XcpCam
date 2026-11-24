using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace XcpCam.Core;

public sealed record XcpReadResult(IReadOnlyList<XcpFrame> Frames, byte[]? Project, string? Warning,
    Vector3 BasePos, Quaternion BaseRot);

public static class XcpFile
{
    const int MaxRecords = 2_000_001;
    const int MaxJson = 32 * 1024 * 1024;
    const int MaxCompressed = MaxJson + 1024 * 1024;
    const uint CameraMagic = 0x31504358; // XCP1, little endian
    const uint ProjectMagic = 0x50504358; // XCPP
    const int Version = 1;
    const int DigestSize = 32;

    public static XcpReadResult Read(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if(file.Length < 8 || reader.ReadUInt32() != CameraMagic)
            throw new InvalidDataException(I18n.T("core.xcp.invalid_header"));
        var count = reader.ReadUInt32();
        if(count == 0 || count > MaxRecords || 32L * count > file.Length - file.Position)
            throw new InvalidDataException(I18n.T("core.xcp.invalid_frames"));
        var frames = new XcpFrame[count];
        for(int i = 0; i < frames.Length; i++)
        {
            frames[i] = new(ReadPosition(reader), ReadRotation(reader), reader.ReadSingle());
            ValidateFrame(frames[i]);
        }
        if(file.Position == file.Length)
            return new(frames, null, null, Vector3.Zero, Quaternion.Identity);

        // V1: magic, version, effective count, base xyz/xyzw, JSON and Brotli lengths,
        // Brotli payload, then SHA-256 of every preceding byte (including camera records).
        try
        {
            if(reader.ReadUInt32() != ProjectMagic || reader.ReadInt32() != Version)
                throw new InvalidDataException(I18n.T("core.xcp.unknown_trailer"));
            int effective = reader.ReadInt32();
            if(effective < 1 || effective != count - 1 || frames[^1] != frames[^2])
                throw new InvalidDataException(I18n.T("core.xcp.frame_count_mismatch"));
            var pos = ReadPosition(reader);
            var rot = ReadRotation(reader);
            ValidateBase(pos, rot);
            int rawSize = reader.ReadInt32();
            int packedSize = reader.ReadInt32();
            if(rawSize is < 1 or > MaxJson || packedSize is < 1 or > MaxCompressed ||
                file.Length - file.Position != packedSize + DigestSize)
                throw new InvalidDataException(I18n.T("core.xcp.invalid_project_len"));
            byte[] packed = reader.ReadBytes(packedSize);
            long digestOffset = file.Position;
            byte[] expected = reader.ReadBytes(DigestSize);
            if(!Digest(file, digestOffset).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException(I18n.T("core.xcp.checksum_mismatch"));
            byte[] json = new byte[rawSize];
            using var decoder = new BrotliDecoder();
            if(decoder.Decompress(packed, json, out int consumed, out int written) != OperationStatus.Done ||
                consumed != packed.Length || written != json.Length)
                throw new InvalidDataException(I18n.T("core.xcp.invalid_compressed"));
            ValidateJson(json);
            return new(new ArraySegment<XcpFrame>(frames, 0, effective), json, null, pos, rot);
        }
        catch(Exception e) when(e is InvalidDataException or EndOfStreamException)
        {
            return new(frames, null, I18n.T("core.xcp.project_read_warn", e.Message), Vector3.Zero, Quaternion.Identity);
        }
    }

    public static void Write(string path, IReadOnlyList<XcpFrame> frames, byte[]? project, Vector3 basePos, Quaternion baseRot)
    {
        int count = frames.Count;
        if(count < 1 || count >= MaxRecords)
            throw new InvalidDataException(I18n.T("core.xcp.export_frame_range"));
        if(project != null)
        {
            ValidateBase(basePos, baseRot);
            ValidateJson(project);
        }
        string destination = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using(var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                using var writer = new BinaryWriter(file);
                writer.Write(CameraMagic);
                writer.Write((uint)count + 1);
                XcpFrame last = default;
                for(int i = 0; i < count; i++)
                {
                    last = frames[i];
                    ValidateFrame(last);
                    WriteFrame(writer, last);
                }
                WriteFrame(writer, last);
                if(project != null)
                {
                    writer.Write(ProjectMagic);
                    writer.Write(Version);
                    writer.Write(count);
                    WritePose(writer, basePos, baseRot);
                    writer.Write(project.Length);
                    long sizeOffset = file.Position;
                    writer.Write(0);
                    using(var encoder = new BrotliStream(file, CompressionLevel.Fastest, leaveOpen: true))
                        encoder.Write(project);
                    long end = file.Position;
                    int packedSize = checked((int)(end - sizeOffset - sizeof(int)));
                    if(packedSize > MaxCompressed) throw new InvalidDataException(I18n.T("core.xcp.compressed_too_large"));
                    file.Position = sizeOffset;
                    writer.Write(packedSize);
                    writer.Write(Digest(file, end)); // Digest leaves the stream positioned at end.
                }
                file.Flush(true);
            }
            File.Move(temp, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if(created) File.Delete(temp);
        }
    }

    static byte[] Digest(Stream stream, long length)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while(length > 0)
        {
            int n = (int)Math.Min(length, buffer.Length);
            stream.ReadExactly(buffer.AsSpan(0, n));
            hash.AppendData(buffer, 0, n);
            length -= n;
        }
        return hash.GetHashAndReset();
    }

    static void ValidateJson(byte[] json)
    {
        if(json.Length is < 1 or > MaxJson) throw new InvalidDataException(I18n.T("core.xcp.invalid_json_len"));
        try { using var document = JsonDocument.Parse(json); }
        catch(JsonException e) { throw new InvalidDataException(I18n.T("core.xcp.invalid_json"), e); }
    }

    static void ValidateFrame(XcpFrame frame)
    {
        if(!XcpMath.IsFinite(frame.Pos) || !XcpMath.IsFinite(frame.Rot) || !float.IsFinite(frame.Fov))
            throw new InvalidDataException(I18n.T("core.xcp.nan_frame"));
        float norm = frame.Rot.LengthSquared();
        if(!(norm > 1e-20f) || !float.IsFinite(norm)) throw new InvalidDataException(I18n.T("core.xcp.invalid_rot"));
    }

    static void ValidateBase(Vector3 pos, Quaternion rot)
    {
        if(!XcpMath.IsFinite(pos) || !XcpMath.IsFinite(rot) || !(rot.LengthSquared() > 0) || !float.IsFinite(rot.LengthSquared()))
            throw new InvalidDataException(I18n.T("core.xcp.invalid_base"));
    }

    static Vector3 ReadPosition(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static Quaternion ReadRotation(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static void WriteFrame(BinaryWriter w, XcpFrame frame)
    {
        WritePose(w, frame.Pos, frame.Rot);
        w.Write(frame.Fov);
    }

    static void WritePose(BinaryWriter w, Vector3 p, Quaternion q)
    {
        w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
    }
}
