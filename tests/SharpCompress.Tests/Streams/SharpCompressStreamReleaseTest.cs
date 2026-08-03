using System;
using System.IO;
using SharpCompress.Common;
using SharpCompress.Factories;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Streams;

public class SharpCompressStreamReleaseTest
{
    [Fact]
    public void RarFactory_OpenReader_ReleasesRingBufferAfterDetection()
    {
        var path = Path.Combine(TestBase.TEST_ARCHIVES_PATH, "Rar5.none.rar");
        var data = File.ReadAllBytes(path);
        var inner = new MemoryStream(data);
        var nonSeekable = new ForwardOnlyStream(inner);
        var stream = SharpCompressStream.Create(nonSeekable, 128);
        stream.StartRecording();

        var factory = new RarFactory();
        Assert.True(factory.TryOpenReader(stream, ReaderOptions.ForExternalStream, out var reader));
        Assert.NotNull(reader);
        Assert.True(stream.IsBufferReleaseRequested);

        using (reader)
        {
            while (reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory)
                {
                    using var entryStream = reader.OpenEntryStream();
                    entryStream.CopyTo(Stream.Null);
                }
            }
        }

        Assert.False(stream.HasRingBuffer);
    }

    [Fact]
    public void FreezeAndReleaseBuffer_WhileLogicalBehind_ReplaysThenFreesBuffer()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var ms = new MemoryStream(data);
        var nonSeekable = new ForwardOnlyStream(ms);
        var stream = SharpCompressStream.Create(nonSeekable, 128);

        stream.StartRecording();
        var probe = new byte[6];
        Assert.Equal(6, stream.Read(probe, 0, 6));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, probe);
        Assert.True(stream.HasRingBuffer);

        stream.Rewind();
        Assert.True(stream.Position < 6);
        Assert.True(stream.HasRingBuffer);

        stream.FreezeAndReleaseBuffer();
        Assert.True(stream.IsBufferReleaseRequested);
        Assert.True(stream.HasRingBuffer);
        Assert.False(stream.IsRecording);

        var replay = new byte[6];
        Assert.Equal(6, stream.Read(replay, 0, 6));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, replay);
        Assert.False(stream.HasRingBuffer);

        var rest = new byte[4];
        Assert.Equal(4, stream.Read(rest, 0, 4));
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, rest);
        Assert.False(stream.HasRingBuffer);
    }

    [Fact]
    public void FreezeAndReleaseBuffer_WhenCaughtUp_FreesBufferImmediately()
    {
        var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var nonSeekable = new ForwardOnlyStream(ms);
        var stream = SharpCompressStream.Create(nonSeekable, 128);
        Assert.True(stream.HasRingBuffer);

        // No recording anchor and logical == streamPosition → free immediately.
        stream.FreezeAndReleaseBuffer();
        Assert.False(stream.HasRingBuffer);
        Assert.True(stream.IsBufferReleaseRequested);

        var buffer = new byte[5];
        Assert.Equal(5, stream.Read(buffer, 0, 5));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer);
        Assert.False(stream.HasRingBuffer);
    }

    [Fact]
    public void FreezeAndReleaseBuffer_AfterStartRecordingWithNoReads_FreesImmediately()
    {
        var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var nonSeekable = new ForwardOnlyStream(ms);
        var stream = SharpCompressStream.Create(nonSeekable, 128);

        stream.StartRecording();
        stream.FreezeAndReleaseBuffer();
        Assert.False(stream.HasRingBuffer);

        var buffer = new byte[3];
        Assert.Equal(3, stream.Read(buffer, 0, 3));
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
    }

    [Fact]
    public void Rewind_AfterFreezeAndReleaseBuffer_Throws()
    {
        var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var nonSeekable = new ForwardOnlyStream(ms);
        var stream = SharpCompressStream.Create(nonSeekable, 128);

        stream.StartRecording();
        stream.Read(new byte[3], 0, 3);
        stream.FreezeAndReleaseBuffer();

        Assert.Throws<ArchiveOperationException>(() => stream.Rewind());
        Assert.Throws<ArchiveOperationException>(() => stream.StartRecording());
        Assert.Throws<ArchiveOperationException>(() => stream.StopRecording());
        Assert.Throws<ArchiveOperationException>(() => stream.FreezeAndReleaseBuffer());
    }

    [Fact]
    public void FreezeAndReleaseBuffer_Passthrough_Throws()
    {
        var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var stream = SharpCompressStream.CreateNonDisposing(ms);
        Assert.Throws<ArchiveOperationException>(() => stream.FreezeAndReleaseBuffer());
    }

    [Fact]
    public void FreezeAndReleaseBuffer_Seekable_IsNoOp()
    {
        var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var stream = new SeekableSharpCompressStream(ms);
        stream.StartRecording();
        Assert.True(stream.IsRecording);

        stream.FreezeAndReleaseBuffer();
        Assert.False(stream.IsRecording);

        // Seekable has no ring buffer; further StartRecording still works (no-op release).
        stream.StartRecording();
        Assert.True(stream.IsRecording);
        stream.Read(new byte[2], 0, 2);
        stream.Rewind();
        Assert.Equal(0, stream.Position);
    }
}
