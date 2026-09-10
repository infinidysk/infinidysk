using NzbWebDAV.Config;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Tests.Queue;

public class SevenZipProcessorTests
{
    [Theory]
    [InlineData(new[] { "A.7z.003", "A.7z.001", "A.7z.002" }, new[] { "A.7z.001", "A.7z.002", "A.7z.003" })]
    [InlineData(new[] { "Movie.7z" }, new[] { "Movie.7z" })]
    public void OrderVolumes_SortsByOrdinal(string[] names, string[] expected)
    {
        var ordered = SevenZipProcessor.OrderVolumes(names.Select(name => Info(name)).ToList());

        Assert.Equal(expected, ordered.Select(x => x.FileName).ToArray());
    }

    [Fact]
    public void OrderVolumes_MultipartBaseIsCaseInsensitive()
    {
        var ordered = SevenZipProcessor.OrderVolumes([
            Info("A.7z.002"),
            Info("a.7Z.001"),
        ]);

        Assert.Equal(["a.7Z.001", "A.7z.002"], ordered.Select(x => x.FileName).ToArray());
    }

    [Fact]
    public void OrderVolumes_DuplicateOrdinalIsRejected()
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([
            Info("A.7z.001", "a@example.com"),
            Info("A.7z.001", "b@example.com"),
            Info("A.7z.002"),
        ]));
    }

    [Theory]
    [InlineData("A.7z.001", "A.7z.003")]
    [InlineData("A.7z.002", "A.7z.003")]
    [InlineData("A.7z.002")]
    public void OrderVolumes_GapOrMissingFirstVolumeIsRejected(params string[] names)
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes(names.Select(name => Info(name)).ToList()));
    }

    [Fact]
    public void OrderVolumes_DifferentBasesAreRejected()
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([
            Info("A.7z.001"),
            Info("B.7z.002"),
        ]));
    }

    [Fact]
    public void OrderVolumes_StandaloneAndMultipartAreRejected()
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([
            Info("A.7z"),
            Info("A.7z.001"),
        ]));
    }

    [Theory]
    [InlineData("A.7z.000")]
    [InlineData("A.7z.999999999999")]
    [InlineData("A.7z.001\n")]
    public void OrderVolumes_InvalidNamesAreRejected(string name)
    {
        Assert.Throws<NonRetryableDownloadException>(() => SevenZipProcessor.OrderVolumes([Info(name)]));
    }

    private static GetFileInfosStep.FileInfo Info(string filename, string messageId = "archive@example.com") =>
        new()
        {
            NzbFile = new NzbFile
            {
                Subject = filename,
                Segments =
                {
                    new NzbSegment { MessageId = messageId, Bytes = 1024 }
                },
            },
            FileName = filename,
            FileSize = 1024,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };
}
