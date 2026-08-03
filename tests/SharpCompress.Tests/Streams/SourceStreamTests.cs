using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Streams;

public class SourceStreamTests
{
    private static SourceStream CreateMultiPartSource(
        out MemoryStream[] parts,
        bool leaveStreamOpen = true
    )
    {
        var localParts = new MemoryStream[]
        {
            new MemoryStream(new byte[] { 1, 2, 3 }),
            new MemoryStream(new byte[] { 4, 5, 6, 7 }),
            new MemoryStream(new byte[] { 8, 9 }),
        };
        parts = localParts;

        return new SourceStream(
            localParts[0],
            i => i < localParts.Length ? localParts[i] : null,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = leaveStreamOpen,
            }
        );
    }

    [Fact]
    public void SourceStream_MultiPart_Length_UsesCachedTotal()
    {
        using var source = CreateMultiPartSource(out _);

        Assert.Equal(3, source.Length);

        source.LoadAllParts();
        Assert.Equal(9, source.Length);
    }

    [Fact]
    public void SourceStream_MultiPart_LoadAllParts_LengthMatchesTotal()
    {
        using var source = CreateMultiPartSource(out _);
        source.LoadAllParts();

        Assert.Equal(9, source.Length);
        Assert.Equal(3, source.Streams.Count());
    }

    [Fact]
    public void SourceStream_MultiPart_Seek_BeginOrigin_FindsCorrectPart()
    {
        using var source = CreateMultiPartSource(out _);

        Assert.Equal(6, source.Seek(6, SeekOrigin.Begin));
        Assert.Equal(6, source.Position);

        var buffer = new byte[2];
        Assert.Equal(2, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(new byte[] { 7, 8 }, buffer);
    }

    [Fact]
    public void SourceStream_MultiPart_Seek_CurrentAndEndOrigins()
    {
        using var source = CreateMultiPartSource(out _);
        source.LoadAllParts();

        source.Seek(3, SeekOrigin.Begin);
        Assert.Equal(6, source.Seek(3, SeekOrigin.Current));
        Assert.Equal(6, source.Position);

        Assert.Equal(9, source.Seek(0, SeekOrigin.End));
        Assert.Equal(9, source.Position);
    }

    [Fact]
    public void SourceStream_MultiPart_Read_CrossesPartBoundary()
    {
        using var source = CreateMultiPartSource(out _);

        source.Seek(2, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7 }, buffer);
        Assert.Equal(7, source.Position);
    }

    [Fact]
    public void SourceStream_MultiPart_ReadSpan_CrossesPartBoundary()
    {
        using var source = CreateMultiPartSource(out _);

        source.Seek(2, SeekOrigin.Begin);

        Span<byte> buffer = stackalloc byte[5];
        Assert.Equal(5, source.Read(buffer));
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7 }, buffer.ToArray());
        Assert.Equal(7, source.Position);
    }

    [Fact]
    public void SourceStream_MultiPart_ReadAsync_CrossesPartBoundary()
    {
        using var source = CreateMultiPartSource(out _);

        source.Seek(2, SeekOrigin.Begin);

        var buffer = new byte[5];
        Assert.Equal(5, source.ReadAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult());
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7 }, buffer);
        Assert.Equal(7, source.Position);
    }

    [Fact]
    public void SourceStream_SetLength_ThrowsNotSupportedException()
    {
        using var source = CreateMultiPartSource(out _);

        Assert.Throws<NotSupportedException>(() => source.SetLength(1));
    }

    [Fact]
    public void SourceStream_Write_ThrowsNotSupportedException()
    {
        using var source = CreateMultiPartSource(out _);

        Assert.Throws<NotSupportedException>(() => source.Write(new byte[] { 1 }, 0, 1));
    }

    [Fact]
    public async Task SourceStream_DisposeAsync_DisposesPartsWhenLeaveStreamOpenFalse()
    {
        var parts = new TestStream[]
        {
            new TestStream(new MemoryStream(new byte[] { 1, 2, 3 })),
            new TestStream(new MemoryStream(new byte[] { 4, 5 })),
        };

        var source = new SourceStream(
            parts[0],
            i => i < parts.Length ? parts[i] : null,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = false,
            }
        );
        source.LoadAllParts();

        await source.DisposeAsync();

        Assert.True(parts[0].IsDisposed);
        Assert.True(parts[1].IsDisposed);
    }

    [Fact]
    public void SourceStream_MultiPart_Seek_BeyondEnd_Throws()
    {
        using var source = CreateMultiPartSource(out _);

        var ex = Assert.Throws<ArchiveOperationException>(() => source.Seek(20, SeekOrigin.Begin));
        Assert.Contains("End of stream reached", ex.Message);
    }
}
