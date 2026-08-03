using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress.Compressors.Xz;

[CLSCompliant(false)]
public partial class XZIndex
{
    private readonly BinaryReader _reader;

    public long StreamStartPosition { get; private set; }
    public ulong NumberOfRecords { get; private set; }
    public List<XZIndexRecord> Records { get; } = new();

    /// <summary>
    /// Total size of the Index including the CRC32 field.
    /// Used to verify the Stream Footer Backward Size field.
    /// </summary>
    internal long IndexSize { get; private set; }

    private readonly bool _indexMarkerAlreadyVerified;

    public XZIndex(BinaryReader reader, bool indexMarkerAlreadyVerified)
    {
        _reader = reader;
        _indexMarkerAlreadyVerified = indexMarkerAlreadyVerified;
        StreamStartPosition = reader.BaseStream.Position;
        if (indexMarkerAlreadyVerified)
        {
            StreamStartPosition--;
        }
    }

    public static XZIndex FromStream(Stream stream, bool indexMarkerAlreadyVerified)
    {
        var index = new XZIndex(
            new BinaryReader(stream, Encoding.UTF8, true),
            indexMarkerAlreadyVerified
        );
        index.Process();
        return index;
    }

    public void Process()
    {
        using var crcStream = new Crc32TrackingStream(_reader.BaseStream);
        if (_indexMarkerAlreadyVerified)
        {
            // Index indicator byte was already consumed; include it in the CRC.
            Span<byte> indicator = stackalloc byte[1];
            indicator[0] = 0;
            crcStream.Update(indicator);
        }

        using var reader = new BinaryReader(crcStream, Encoding.UTF8, leaveOpen: true);

        if (!_indexMarkerAlreadyVerified)
        {
            VerifyIndexMarker(reader);
        }

        NumberOfRecords = reader.ReadXZInteger();
        for (ulong i = 0; i < NumberOfRecords; i++)
        {
            Records.Add(XZIndexRecord.FromBinaryReader(reader));
        }
        SkipPadding(reader);
        VerifyCrc32(crcStream);
    }

    private static void VerifyIndexMarker(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        if (marker != 0)
        {
            throw new InvalidFormatException("Not an index block");
        }
    }

    private void SkipPadding(BinaryReader reader)
    {
        var bytes = (int)(reader.BaseStream.Position - StreamStartPosition) % 4;
        if (bytes > 0)
        {
            var paddingBytes = reader.ReadBytes(4 - bytes);
            if (paddingBytes.Any(b => b != 0))
            {
                throw new InvalidFormatException("Padding bytes were non-null");
            }
        }
    }

    private void VerifyCrc32(Crc32TrackingStream crcStream)
    {
        var expected = crcStream.FinalCrc;
        // Read the stored CRC from the underlying stream so it is not included in the hash.
        var crc = crcStream.WrappedStream.ReadLittleEndianUInt32();
        if (crc != expected)
        {
            throw new InvalidFormatException("Index corrupt");
        }

        IndexSize = crcStream.WrappedStream.Position - StreamStartPosition;
    }
}
