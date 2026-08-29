using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ArrLinkedRepairDecisionTests
{
    private const string LibraryPath = "/media/movies/Some Movie (2020)/Some Movie (2020).mkv";
    private static readonly Guid DownloadId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task UnreachableRootFolders_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://unreachable",
                rootFolders: () => throw new HttpRequestException("connection refused"),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.MediaItemNotFound)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, decision);
    }

    [Fact]
    public async Task RemoveAndBlocklistThrows_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new HttpRequestException("timeout")),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, decision);
    }

    [Fact]
    public async Task EmptyRootFolderPath_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.MediaItemNotFound)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, decision);
    }

    [Fact]
    public async Task MediaItemMiss_WithAnotherInstanceUnreachable_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://unreachable",
                rootFolders: () => throw new HttpRequestException("down"),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.MediaItemNotFound)),
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.MediaItemNotFound)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, decision);
    }

    [Fact]
    public async Task RemoveAndBlocklistSucceeded_ReturnsRemoveAndBlocklist()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) =>
                    Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, decision);
    }

    [Fact]
    public async Task RemoveAndBlocklistSucceededSearchWithheld_PreservesWithheldOutcome()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) =>
                    Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceededSearchWithheld)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(
            HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceededSearchWithheld,
            decision);
    }

    [Fact]
    public async Task MissingDownloadIdentity_DefersWithoutCallingArrRepair()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, null, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadHistory, decision);
    }

    [Fact]
    public async Task MissingDownloadHistory_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) =>
                    Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadHistory, decision);
    }

    [Fact]
    public async Task NoMatchingRoot_ReturnsRootPathMismatchWithoutCallingArrRepair()
    {
        const string localPath = "/mnt/data/tv/Example/Example.S01E01.mkv";
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://sonarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/data/media/tv" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, localPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferRootPathMismatch, decision);
    }

    [Fact]
    public async Task MultipleReachableClientsWithNoMatchingRoot_ReturnRootPathMismatch()
    {
        const string localPath = "/mnt/data/tv/Example/Example.S01E01.mkv";
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://sonarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/data/media/tv" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/data/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, localPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferRootPathMismatch, decision);
    }

    [Fact]
    public async Task MatchingRootWithExactMediaMiss_ReturnsNoMatchingMediaItem()
    {
        var removeCalls = 0;
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) =>
                {
                    removeCalls++;
                    return Task.FromResult(ArrRepairOutcome.MediaItemNotFound);
                }),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(1, removeCalls);
        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, decision);
    }

    [Fact]
    public async Task OneMatchingRootAndOneUnrelatedRoot_ReturnsNoMatchingMediaItem()
    {
        var movieRemoveCalls = 0;
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://sonarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/tv" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("tv instance should not be called")),
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) =>
                {
                    movieRemoveCalls++;
                    return Task.FromResult(ArrRepairOutcome.MediaItemNotFound);
                }),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(1, movieRemoveCalls);
        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, decision);
    }

    [Fact]
    public async Task UnreachableClientTakesPrecedenceOverRootPathMismatch()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://unreachable",
                rootFolders: () => throw new HttpRequestException("connection refused"),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
            new ScriptedArrClient(
                host: "http://sonarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/tv" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, decision);
    }

    [Fact]
    public async Task EmptyRootPathAmongNonMatchingRoots_DefersUnreachable()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = null },
                    new() { Path = "/media/tv" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, decision);
    }

    [Fact]
    public async Task CancelledToken_ThrowsWithoutCallingArr()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => throw new InvalidOperationException("should not be called"),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HealthCheckService.DecideArrLinkedRepairAsync(
                clients, LibraryPath, DownloadId, cts.Token));
    }

    [Fact]
    public async Task MediaItemMiss_ContinuesToAnotherOwningInstance()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://first-radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.MediaItemNotFound)),
            new ScriptedArrClient(
                host: "http://second-radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) =>
                    Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, decision);
    }

    [Fact]
    public async Task NoArrInstances_DefersWithoutDelete()
    {
        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            [], LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, decision);
    }

    [Theory]
    [InlineData("/media/movies/title/file.mkv", "/media/movies", true)]
    [InlineData("/media/movies/title/file.mkv", "/media/movies/", true)]
    [InlineData("/media/movies", "/media/movies", true)]
    [InlineData("/media/movies/title/file.mkv", "/", true)]
    [InlineData("/media/movies-old/title/file.mkv", "/media/movies", false)]
    [InlineData("/mnt/data/title/file.mkv", "/data/media", false)]
    [InlineData("/Media/movies/title/file.mkv", "/media/movies", false)]
    [InlineData("/media/movies/title/file.mkv", "//", false)]
    public void IsPathWithinRoot_UsesOrdinalDirectoryBoundaries(
        string candidate,
        string root,
        bool expected)
    {
        Assert.Equal(expected, HealthCheckService.IsPathWithinRoot(candidate, root));
    }

    private sealed class ScriptedArrClient(
        string host,
        Func<Task<List<ArrRootFolder>>> rootFolders,
        Func<string, Guid, Task<ArrRepairOutcome>> removeAndBlocklist) : ArrClient(host, "test-key")
    {
        public override Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct) => rootFolders();

        public override Task<ArrRepairOutcome> RemoveAndBlocklist(
            string symlinkOrStrmPath,
            Guid downloadId,
            Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
            CancellationToken ct = default) =>
            removeAndBlocklist(symlinkOrStrmPath, downloadId);
    }
}
