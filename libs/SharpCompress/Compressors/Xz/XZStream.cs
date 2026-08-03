using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.Compressors.Xz;

[CLSCompliant(false)]
public sealed partial class XZStream : XZReadOnlyStream
{
    public XZStream(Stream baseStream)
        : base(baseStream) { }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    public static bool IsXZStream(Stream stream)
    {
        try
        {
            return null != XZHeader.FromStream(stream);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AssertBlockCheckTypeIsSupported()
    {
        switch (Header.BlockCheckType)
        {
            case CheckType.NONE:
            case CheckType.CRC32:
            case CheckType.CRC64:
            case CheckType.SHA256:
                break;
            default:
                throw new InvalidFormatException("Check Type unknown to this version of decoder.");
        }
    }

    public XZHeader Header { get; private set; } = null!;
    public XZIndex Index { get; private set; } = null!;
    public XZFooter Footer { get; private set; } = null!;
    public bool HeaderIsRead { get; private set; }
    private XZBlock? _currentBlock;
    private readonly List<(ulong UnpaddedSize, ulong UncompressedSize)> _blockSizes = new();

    private bool _endOfStream;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = 0;
        if (_endOfStream)
        {
            return bytesRead;
        }

        if (!HeaderIsRead)
        {
            ReadHeader();
        }

        bytesRead = ReadBlocks(buffer, offset, count);
        if (bytesRead < count)
        {
            _endOfStream = true;
            ReadIndex();
            ReadFooter();
        }
        return bytesRead;
    }

    private void ReadHeader()
    {
        Header = XZHeader.FromStream(BaseStream);
        AssertBlockCheckTypeIsSupported();
        HeaderIsRead = true;
    }

    private void ReadIndex()
    {
        Index = XZIndex.FromStream(BaseStream, true);
        VerifyIndexRecords();
    }

    private void ReadFooter()
    {
        Footer = XZFooter.FromStream(BaseStream);
        VerifyFooter();
    }

    private void VerifyIndexRecords()
    {
        if ((ulong)_blockSizes.Count != Index.NumberOfRecords)
        {
            throw new InvalidFormatException("Index record count does not match decoded blocks");
        }

        for (var i = 0; i < _blockSizes.Count; i++)
        {
            var (observedUnpadded, observedUncompressed) = _blockSizes[i];
            var record = Index.Records[i];
            if (
                record.UnpaddedSize != observedUnpadded
                || record.UncompressedSize != observedUncompressed
            )
            {
                throw new InvalidFormatException(
                    "Index record sizes do not match decoded block sizes"
                );
            }
        }
    }

    private void VerifyFooter()
    {
        if (Footer.BackwardSize != Index.IndexSize)
        {
            throw new InvalidFormatException("Footer Backward Size does not match Index size");
        }

        if (
            Header.StreamFlags is null
            || Footer.StreamFlags is null
            || !Header.StreamFlags.AsSpan().SequenceEqual(Footer.StreamFlags)
        )
        {
            throw new InvalidFormatException("Footer Stream Flags do not match Header");
        }
    }

    private int ReadBlocks(byte[] buffer, int offset, int count)
    {
        var bytesRead = 0;
        if (_currentBlock is null)
        {
            NextBlock();
        }

        for (; ; )
        {
            try
            {
                if (bytesRead >= count)
                {
                    break;
                }

                var remaining = count - bytesRead;
                var newOffset = offset + bytesRead;
                var justRead = _currentBlock.NotNull().Read(buffer, newOffset, remaining);
                if (justRead < remaining)
                {
                    NextBlock();
                }

                bytesRead += justRead;
            }
            catch (XZIndexMarkerReachedException)
            {
                break;
            }
        }
        return bytesRead;
    }

    private void NextBlock()
    {
        if (_currentBlock is not null && _currentBlock.IsComplete)
        {
            _blockSizes.Add(
                (_currentBlock.ObservedUnpaddedSize, _currentBlock.ObservedUncompressedSize)
            );
        }

        _currentBlock = new XZBlock(BaseStream, Header.BlockCheckType, Header.BlockCheckSize);
    }
}
