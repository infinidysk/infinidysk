using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;

namespace NzbWebDAV.Par2Recovery;

#pragma warning disable CA5351 // PAR 2.0 uses MD5 for file/slice integrity per spec

/// <summary>
/// Builds small PAR2 sets for deterministic tests.
/// Regenerate fixtures: slice size 64, 256-byte file, 2 recovery exponents (0, 1).
/// </summary>
public static class Par2TestEncoder
{
    private static readonly Gf16Field Field = new();

    public static (byte[] indexBytes, byte[] volumeBytes) EncodeSet(
        string fileName,
        byte[] fileData,
        ulong sliceSize,
        IReadOnlyList<uint> recoveryExponents,
        byte[]? fileHashOverride = null)
        => EncodeSet(
            [(fileName, fileData)],
            sliceSize,
            recoveryExponents,
            fileHashOverride is null
                ? null
                : new Dictionary<string, byte[]>(StringComparer.Ordinal) { [fileName] = fileHashOverride });

    public static (byte[] indexBytes, byte[] volumeBytes) EncodeSet(
        IReadOnlyList<(string FileName, byte[] FileData)> files,
        ulong sliceSize,
        IReadOnlyList<uint> recoveryExponents,
        IReadOnlyDictionary<string, byte[]>? fileHashOverrides = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(files.Count, 0);

        var recoverySetId = RandomNumberGenerator.GetBytes(16);
        var fileIds = new byte[files.Count][];
        var perFileSlices = new byte[files.Count][][];
        using var index = new MemoryStream();

        for (var i = 0; i < files.Count; i++)
        {
            var (fileName, fileData) = files[i];
            fileIds[i] = files.Count == 1
                ? MD5.HashData(fileData.Length >= 16 ? fileData.AsSpan(0, 16) : fileData)
                : MD5.HashData(
                    Encoding.UTF8.GetBytes($"{i}:{fileName}")
                        .Concat(fileData.Length >= 16 ? fileData.AsSpan(0, 16).ToArray() : fileData)
                        .ToArray());
            var fileHash = fileHashOverrides is not null
                           && fileHashOverrides.TryGetValue(fileName, out var overrideHash)
                ? overrideHash
                : MD5.HashData(fileData);
            var file16k = MD5.HashData(fileData.AsSpan(0, Math.Min(16 * 1024, fileData.Length)));
            perFileSlices[i] = BuildSlices(fileData, sliceSize);
            WritePacket(
                index,
                FileDesc.PacketType,
                BuildFileDescBody(fileIds[i], fileHash, file16k, fileData.Length, fileName),
                recoverySetId);
        }

        WritePacket(index, MainPacket.PacketType, BuildMainBody(sliceSize, (uint)files.Count, fileIds), recoverySetId);
        for (var i = 0; i < files.Count; i++)
            WritePacket(index, IfscPacket.PacketType, BuildIfscBody(fileIds[i], perFileSlices[i]), recoverySetId);

        var allSlices = perFileSlices.SelectMany(slices => slices).ToArray();
        var recoveryPayloads = recoveryExponents
            .Select(exp => (exp, ComputeRecoverySlice(exp, sliceSize, allSlices)))
            .ToList();

        using var volume = new MemoryStream();
        index.Position = 0;
        volume.Write(index.ToArray());
        foreach (var (exp, payload) in recoveryPayloads)
        {
            var body = new byte[4 + payload.Length];
            BitConverter.TryWriteBytes(body.AsSpan(0, 4), exp);
            payload.CopyTo(body, 4);
            WritePacket(volume, RecvSlic.PacketType, body, recoverySetId);
        }

        return (index.ToArray(), volume.ToArray());
    }

    private static byte[][] BuildSlices(byte[] data, ulong sliceSize)
    {
        var count = (int)((data.Length + (long)sliceSize - 1) / (long)sliceSize);
        var slices = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var slice = new byte[(int)sliceSize];
            var offset = i * (int)sliceSize;
            var len = Math.Min((int)sliceSize, data.Length - offset);
            data.AsSpan(offset, len).CopyTo(slice);
            slices[i] = slice;
        }
        return slices;
    }

    private static byte[] ComputeRecoverySlice(uint exponent, ulong sliceSize, byte[][] fileSlices)
    {
        var size = (int)sliceSize;
        var payload = new byte[size];
        var words = new ushort[size / 2];
        for (var sliceIndex = 0; sliceIndex < fileSlices.Length; sliceIndex++)
        {
            var coeff = Field.RecoveryCoefficient(exponent, sliceIndex);
            var slice = fileSlices[sliceIndex];
            for (var w = 0; w < words.Length; w++)
            {
                var word = BitConverter.ToUInt16(slice, w * 2);
                words[w] = Field.Add(words[w], Field.Mul(coeff, word));
            }
        }

        for (var w = 0; w < words.Length; w++)
            BitConverter.TryWriteBytes(payload.AsSpan(w * 2, 2), words[w]);
        return payload;
    }

    private static byte[] BuildMainBody(ulong sliceSize, uint fileCount, byte[][] fileIds)
    {
        var body = new byte[12 + fileIds.Length * 16];
        BitConverter.TryWriteBytes(body.AsSpan(0, 8), sliceSize);
        BitConverter.TryWriteBytes(body.AsSpan(8, 4), fileCount);
        for (var i = 0; i < fileIds.Length; i++)
            fileIds[i].CopyTo(body, 12 + i * 16);
        return body;
    }

    private static byte[] BuildIfscBody(byte[] fileId, byte[][] slices)
    {
        var body = new byte[16 + slices.Length * 20];
        fileId.CopyTo(body, 0);
        for (var i = 0; i < slices.Length; i++)
        {
            var offset = 16 + i * 20;
            MD5.HashData(slices[i]).CopyTo(body, offset);
            BitConverter.TryWriteBytes(body.AsSpan(offset + 16, 4), BitConverter.ToUInt32(Crc32.Hash(slices[i])));
        }
        return body;
    }

    private static byte[] BuildFileDescBody(
        byte[] fileId, byte[] fileHash, byte[] file16k, int fileLength, string fileName)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var padded = (nameBytes.Length + 3) / 4 * 4;
        var body = new byte[56 + padded];
        fileId.CopyTo(body, 0);
        fileHash.CopyTo(body, 16);
        file16k.CopyTo(body, 32);
        BitConverter.TryWriteBytes(body.AsSpan(48, 8), (ulong)fileLength);
        nameBytes.CopyTo(body, 56);
        return body;
    }

    private static void WritePacket(Stream stream, string packetType, byte[] body, byte[] recoverySetId)
    {
        var headerSize = Marshal.SizeOf<Par2PacketHeader>();
        var packetLength = headerSize + body.Length;
        var header = new Par2PacketHeader
        {
            Magic = "PAR2\0PKT"u8.ToArray(),
            PacketLength = (ulong)packetLength,
            PacketHash = new byte[16],
            RecoverySetID = recoverySetId,
            PacketType = Encoding.ASCII.GetBytes(packetType.PadRight(16, '\0')[..16]),
        };

        var packet = new byte[packetLength];
        var headerBytes = new byte[headerSize];
        var pin = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(header, pin.AddrOfPinnedObject(), false);
        }
        finally
        {
            pin.Free();
        }

        headerBytes.CopyTo(packet, 0);
        body.CopyTo(packet, headerSize);
        Par2PacketHash.Compute(packet).CopyTo(packet, 16);
        stream.Write(packet);
    }
}

#pragma warning restore CA5351
