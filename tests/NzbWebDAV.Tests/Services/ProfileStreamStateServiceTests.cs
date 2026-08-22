using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class ProfileStreamStateServiceTests
{
    [Fact]
    public async Task MarksExactCompletedVideoReleaseReady()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(
            historyId,
            HistoryItem.DownloadStatusOption.Completed,
            "Movie.2024.1080p.nzb");
        await factory.AddDavItemsAsync(UsenetFile("Movie.2024.1080p.mkv", historyId));

        var ready = await GetReadyAsync(factory, Title: "Movie.2024.1080p");

        Assert.Contains("Movie.2024.1080p.nzb", ready);
    }

    [Fact]
    public async Task CompletedHistoryWithOnlySubtitles_IsNotReady()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(
            historyId,
            HistoryItem.DownloadStatusOption.Completed,
            "Movie.2024.1080p.nzb");
        await factory.AddDavItemsAsync(UsenetFile("Movie.2024.1080p.srt", historyId));

        var ready = await GetReadyAsync(factory, Title: "Movie.2024.1080p");

        Assert.Empty(ready);
    }

    [Fact]
    public async Task BrokenHistoryIds_AreExcluded()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(
            historyId,
            HistoryItem.DownloadStatusOption.Completed,
            "Movie.2024.1080p.nzb");
        await factory.AddDavItemsAsync(UsenetFile("Movie.2024.1080p.mkv", historyId));
        factory.Services.GetRequiredService<CandidateNegativeCache>().MarkHistoryItemBroken(historyId);

        var ready = await GetReadyAsync(factory, Title: "Movie.2024.1080p");

        Assert.Empty(ready);
    }

    [Fact]
    public async Task FilenameMatching_IsCaseInsensitive()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(
            historyId,
            HistoryItem.DownloadStatusOption.Completed,
            "movie.2024.1080p.nzb");
        await factory.AddDavItemsAsync(UsenetFile("movie.2024.1080p.mkv", historyId));

        var ready = await GetReadyAsync(factory, Title: "Movie.2024.1080p");

        Assert.Contains("movie.2024.1080p.nzb", ready, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotMarkSiblingReleaseReady()
    {
        await using var factory = new NzbDavWebApplicationFactory();
        var historyId = Guid.NewGuid();
        await factory.SeedHistoryItemAsync(
            historyId,
            HistoryItem.DownloadStatusOption.Completed,
            "Movie.2024.2160p.nzb");
        await factory.AddDavItemsAsync(UsenetFile("Movie.2024.2160p.mkv", historyId));

        var ready = await GetReadyAsync(factory, Title: "Movie.2024.1080p");

        Assert.Empty(ready);
    }

    private static async Task<IReadOnlySet<string>> GetReadyAsync(
        NzbDavWebApplicationFactory factory,
        string Title)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ProfileStreamStateService>();
        return await service.GetReadyNzbFileNamesAsync(
            [
                new NzbResolutionCache.Candidate
                {
                    IndexerName = "idx",
                    IndexerUserAgent = "ua",
                    NzbUrl = "https://indexer.example/nzb",
                    Title = Title,
                    Size = 1,
                },
            ],
            CancellationToken.None);
    }

    private static DavItem UsenetFile(string name, Guid historyItemId) => DavItem.New(
        Guid.NewGuid(),
        DavItem.ContentFolder,
        name,
        fileSize: 100,
        DavItem.ItemType.UsenetFile,
        DavItem.ItemSubType.NzbFile,
        releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
        lastHealthCheck: null,
        historyItemId: historyItemId,
        fileBlobId: null);
}
