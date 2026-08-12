using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Tests.Queue;

public class MultipartMkvProcessorTests
{
    [Fact]
    public async Task ProcessAsync_AssemblesOnlyTheGivenEpisodePartsInOrder()
    {
        var ep01 = new MultipartMkvProcessor(
            [
                Part("EP01.mkv.002", "ep01-2", 20),
                Part("EP01.mkv.001", "ep01-1", 10),
            ],
            new UnusedNntpClient(),
            CancellationToken.None);
        var ep02 = new MultipartMkvProcessor(
            [
                Part("EP02.mkv.001", "ep02-1", 30),
                Part("EP02.mkv.002", "ep02-2", 40),
            ],
            new UnusedNntpClient(),
            CancellationToken.None);

        var result01 = Assert.IsType<MultipartMkvProcessor.Result>(await ep01.ProcessAsync());
        var result02 = Assert.IsType<MultipartMkvProcessor.Result>(await ep02.ProcessAsync());

        Assert.Equal("EP01.mkv", result01.Filename);
        Assert.Equal(["ep01-1", "ep01-2"], result01.Parts.Select(p => p.SegmentIds.Single()));
        Assert.Equal([10, 20], result01.Parts.Select(p => p.FilePartByteRange.Count));

        Assert.Equal("EP02.mkv", result02.Filename);
        Assert.Equal(["ep02-1", "ep02-2"], result02.Parts.Select(p => p.SegmentIds.Single()));
        Assert.Equal([30, 40], result02.Parts.Select(p => p.FilePartByteRange.Count));
    }

    [Fact]
    public async Task ProcessAsync_UsesCaseInsensitiveMkvBaseName()
    {
        var processor = new MultipartMkvProcessor(
            [Part("EP01.MKV.001", "seg", 10)],
            new UnusedNntpClient(),
            CancellationToken.None);

        var result = Assert.IsType<MultipartMkvProcessor.Result>(await processor.ProcessAsync());

        Assert.Equal("EP01.MKV", result.Filename);
    }

    private static GetFileInfosStep.FileInfo Part(string fileName, string segmentId, long size)
    {
        var nzbFile = new NzbFile { Subject = $"\"{fileName}\"" };
        nzbFile.Segments.Add(new NzbSegment
        {
            MessageId = segmentId,
            Bytes = size,
            ByteRange = LongRange.FromStartAndSize(0, size),
        });
        return new GetFileInfosStep.FileInfo
        {
            NzbFile = nzbFile,
            FileName = fileName,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            FileSize = size,
        };
    }

    private sealed class UnusedNntpClient() : WrappingNntpClient(null!);
}
