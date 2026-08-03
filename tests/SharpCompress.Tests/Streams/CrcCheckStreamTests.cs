using System;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.LZMA.Utilities;
using Xunit;

namespace SharpCompress.Test.Streams;

public class CrcCheckStreamTests
{
    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
#pragma warning disable CS0618
        var stream = new CrcCheckStream(0);
#pragma warning restore CS0618
        stream.Dispose();
        var ex = Record.Exception(() => stream.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Finish_WithMatchingCrc_DoesNotThrow()
    {
        byte[] data = Encoding.UTF8.GetBytes("crc-check-stream");
        uint expectedCrc = ComputeCrc(data);

#pragma warning disable CS0618
        using var stream = new CrcCheckStream(expectedCrc);
#pragma warning restore CS0618
        stream.Write(data, 0, data.Length);

        var ex = Record.Exception(() => stream.Finish());
        Assert.Null(ex);
    }

    [Fact]
    public void Finish_WithMismatchedCrc_ThrowsArchiveOperationException()
    {
        byte[] data = Encoding.UTF8.GetBytes("crc-check-stream");
        const uint wrongCrc = 0xDEADBEEF;

#pragma warning disable CS0618
        using var stream = new CrcCheckStream(wrongCrc);
#pragma warning restore CS0618
        stream.Write(data, 0, data.Length);

        Assert.Throws<ArchiveOperationException>(() => stream.Finish());
    }

    private static uint ComputeCrc(byte[] data)
    {
        uint crc = Crc.INIT_CRC;
        crc = Crc.Update(crc, data, 0, data.Length);
        return Crc.Finish(crc);
    }
}
