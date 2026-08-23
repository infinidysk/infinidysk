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
    public async Task NoMatchingRoot_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://sonarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/tv" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.MediaItemNotFound)),
        };

        var decision = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, decision);
    }

    [Fact]
    public async Task ExactMediaPathMiss_DefersWithoutDelete()
    {
        var clients = new ArrClient[]
        {
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

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, decision);
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

    private sealed class ScriptedArrClient(
        string host,
        Func<Task<List<ArrRootFolder>>> rootFolders,
        Func<string, Guid, Task<ArrRepairOutcome>> removeAndBlocklist) : ArrClient(host, "test-key")
    {
        public override Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct) => rootFolders();

        public override Task<ArrRepairOutcome> RemoveAndBlocklist(
            string symlinkOrStrmPath,
            Guid downloadId,
            Func<string, bool>? shouldRequestSearch = null,
            CancellationToken ct = default) =>
            removeAndBlocklist(symlinkOrStrmPath, downloadId);
    }
}
