using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Tests.Clients;

public class ArrDownloadIdResolverTests
{
    private static readonly ArrMediaFileMatch Movie =
        new(ArrMediaKind.Movie, FileId: 201, MediaIds: [101]);
    private const string Path = "/library/movies/Title/Title.mkv";

    [Fact]
    public void UniqueExactFileIdAndPath_Resolves()
    {
        var downloadId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var resolution = ArrDownloadIdResolver.Resolve(
            [
                Record(downloadId.ToString(), "201", Path),
            ],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.Unique, resolution.Kind);
        Assert.Equal(downloadId, resolution.DownloadId);
    }

    [Fact]
    public void DuplicateRowsWithSameDownloadId_DeduplicateToUnique()
    {
        var downloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var resolution = ArrDownloadIdResolver.Resolve(
            [
                Record(downloadId.ToString(), "201", Path),
                Record(downloadId.ToString(), "201", Path),
            ],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.Unique, resolution.Kind);
        Assert.Equal(downloadId, resolution.DownloadId);
    }

    [Fact]
    public void TwoDistinctDownloadIds_AreAmbiguous()
    {
        var resolution = ArrDownloadIdResolver.Resolve(
            [
                Record("11111111-1111-1111-1111-111111111111", "201", Path),
                Record("22222222-2222-2222-2222-222222222222", "201", Path),
            ],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.Ambiguous, resolution.Kind);
        Assert.Null(resolution.DownloadId);
    }

    [Fact]
    public void PathWithoutMatchingFileId_IsIgnored()
    {
        var resolution = ArrDownloadIdResolver.Resolve(
            [Record(Guid.NewGuid().ToString(), "999", Path)],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.NotFound, resolution.Kind);
    }

    [Fact]
    public void FileIdWithoutMatchingPath_IsIgnored()
    {
        var resolution = ArrDownloadIdResolver.Resolve(
            [Record(Guid.NewGuid().ToString(), "201", "/other/path.mkv")],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.NotFound, resolution.Kind);
    }

    [Fact]
    public void MalformedFileId_IsIgnored()
    {
        var resolution = ArrDownloadIdResolver.Resolve(
            [Record(Guid.NewGuid().ToString(), "not-an-int", Path)],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.NotFound, resolution.Kind);
    }

    [Fact]
    public void NonGuidDownloadId_IsIgnored()
    {
        var resolution = ArrDownloadIdResolver.Resolve(
            [Record("not-a-guid", "201", Path)],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.NotFound, resolution.Kind);
    }

    [Fact]
    public void PathComparison_IsOrdinal()
    {
        var resolution = ArrDownloadIdResolver.Resolve(
            [Record(Guid.NewGuid().ToString(), "201", "/Library/movies/Title/Title.mkv")],
            Movie,
            Path);

        Assert.Equal(ArrDownloadIdResolutionKind.NotFound, resolution.Kind);
    }

    private static ArrHistoryRecord Record(string downloadId, string fileId, string importedPath) =>
        new()
        {
            DownloadId = downloadId,
            EventType = 3,
            Data = new ArrHistoryData
            {
                FileId = fileId,
                ImportedPath = importedPath,
            },
        };
}
