using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Compressors.Rar;

internal partial class RarCrcStream : RarStream
{
    private readonly MultiVolumeReadOnlyStreamBase readStream;
    private uint currentCrc;
    private readonly bool disableCRC;

    private RarCrcStream(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStreamBase readStream,
        bool ownsUnpack,
        Action? onDispose
    )
        : base(unpack, fileHeader, readStream, ownsUnpack, onDispose)
    {
        this.readStream = readStream;
        disableCRC = fileHeader.IsEncrypted;
        ResetCrc();
    }

    public static RarCrcStream Create(
        IRarUnpack unpack,
        FileHeader fileHeader,
        MultiVolumeReadOnlyStream readStream,
        bool ownsUnpack = false,
        Action? onDispose = null
    )
    {
        var stream = new RarCrcStream(unpack, fileHeader, readStream, ownsUnpack, onDispose);
        return stream;
    }

    // Async methods moved to RarCrcStream.Async.cs

    public uint GetCrc() => ~currentCrc;

    public void ResetCrc() => currentCrc = 0xffffffff;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var result = base.Read(buffer, offset, count);
        if (result != 0)
        {
            currentCrc = RarCRC.CheckCrc(currentCrc, buffer, offset, result);
        }
        else if (
            !disableCRC
            && GetCrc()
                != BinaryPrimitives.ReadUInt32LittleEndian(
                    readStream.NotNull().CurrentCrc.NotNull().AsSpan()
                )
            && count != 0
        )
        {
            // NOTE: we use the last FileHeader in a multipart volume to check CRC
            throw new InvalidFormatException("file crc mismatch");
        }

        return result;
    }

    public override int Read(Span<byte> buffer)
    {
        var result = base.Read(buffer);
        if (result != 0)
        {
            currentCrc = RarCRC.CheckCrc(currentCrc, buffer, 0, result);
        }
        else if (
            !disableCRC
            && GetCrc()
                != BinaryPrimitives.ReadUInt32LittleEndian(
                    readStream.NotNull().CurrentCrc.NotNull().AsSpan()
                )
            && buffer.Length != 0
        )
        {
            throw new InvalidFormatException("file crc mismatch");
        }

        return result;
    }
}
