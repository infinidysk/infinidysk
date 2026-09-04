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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(
            HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceededSearchWithheld,
            result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, null, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadIdentity, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadHistory, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, localPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferRootPathMismatch, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, localPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferRootPathMismatch, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(1, removeCalls);
        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(1, movieRemoveCalls);
        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
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

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
    }

    [Fact]
    public async Task NoArrInstances_DefersWithoutDelete()
    {
        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            [], LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, result.Decision);
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

    [Fact]
    public async Task StoredArrDownloadId_IsPassedInsteadOfLocalBlobId()
    {
        var observed = new List<Guid>();
        var arrDownloadId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var legacyDownloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, downloadId) =>
                {
                    observed.Add(downloadId);
                    return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
                }),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            arrDownloadId,
            CancellationToken.None,
            legacyDownloadId: legacyDownloadId);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
        Assert.Equal([arrDownloadId], observed);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task NullProvenance_WithoutLegacyIdentityDefers()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("must not use NzbBlobId"),
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory())),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, null, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadIdentity, result.Decision);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task MissingProvenance_UsesVerifiedLegacyDownloadId()
    {
        var legacyDownloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, downloadId) =>
                {
                    Assert.Equal(legacyDownloadId, downloadId);
                    return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
                },
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory())),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: legacyDownloadId);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task UniqueExactLegacyRecovery_IsUsedAndReturned()
    {
        var recovered = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var legacyDownloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr.test",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, downloadId) =>
                {
                    Assert.Equal(recovered, downloadId);
                    return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
                },
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 1,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = recovered.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData
                            {
                                FileId = "1",
                                ImportedPath = LibraryPath,
                            },
                        },
                    ],
                })),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: legacyDownloadId);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
        Assert.Equal(recovered, result.RecoveredDownloadId);
        Assert.Equal("http://radarr.test", result.RecoveryHost);
    }

    [Fact]
    public async Task AmbiguousProvenance_UsesVerifiedLegacyDownloadId()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var legacyDownloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, downloadId) =>
                {
                    Assert.Equal(legacyDownloadId, downloadId);
                    return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
                },
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 2,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = first.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                        new ArrHistoryRecord
                        {
                            DownloadId = second.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                    ],
                })),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: legacyDownloadId);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task MissingProvenance_UnrecognizedLegacyIdDefersAsMissingHistory()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound),
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory())),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadHistory, result.Decision);
    }

    [Fact]
    public async Task AmbiguousProvenance_UnrecognizedLegacyIdRemainsAmbiguous()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound),
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 2,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = first.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                        new ArrHistoryRecord
                        {
                            DownloadId = second.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                    ],
                })),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferAmbiguousDownloadIdentity, result.Decision);
    }

    [Fact]
    public async Task AmbiguousLegacyEvidence_DefersWithoutMutation()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("must not mutate"),
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 2,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = first.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                        new ArrHistoryRecord
                        {
                            DownloadId = second.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                    ],
                })),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, null, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferAmbiguousDownloadIdentity, result.Decision);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task UniqueRecoveryThenAmbiguousLegacySuccess_DoesNotReturnRecoveredId()
    {
        var recovered = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var other = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var legacyDownloadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr-unique",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, id) =>
                {
                    Assert.Equal(recovered, id);
                    return Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound);
                },
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 1,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = recovered.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                    ],
                })),
            new ScriptedArrClient(
                host: "http://radarr-ambiguous",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, id) =>
                {
                    Assert.Equal(legacyDownloadId, id);
                    return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
                },
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 2,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = recovered.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                        new ArrHistoryRecord
                        {
                            DownloadId = other.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                    ],
                })),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: legacyDownloadId);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task TruncatedImportHistoryPage_DoesNotTreatUniqueMatchAsProven()
    {
        var recovered = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("must not mutate"),
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory
                {
                    TotalRecords = 100,
                    Records =
                    [
                        new ArrHistoryRecord
                        {
                            DownloadId = recovered.ToString(),
                            EventType = 3,
                            Data = new ArrHistoryData { FileId = "1", ImportedPath = LibraryPath },
                        },
                    ],
                })),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, null, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferMissingDownloadIdentity, result.Decision);
        Assert.Null(result.RecoveredDownloadId);
    }

    [Fact]
    public async Task FirstInstanceMissingHistory_DoesNotPreventLaterInstanceSuccess()
    {
        var downloadId = DownloadId;
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://first-radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound)),
            new ScriptedArrClient(
                host: "http://second-radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, id) =>
                {
                    Assert.Equal(downloadId, id);
                    return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
                }),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, downloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.RemoveAndBlocklistSucceeded, result.Decision);
    }

    [Fact]
    public async Task UnreachableOwnerPlusHistoryMiss_ReturnsDeferUnreachable()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://unreachable",
                rootFolders: () => throw new HttpRequestException("down"),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound)),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
    }

    [Fact]
    public async Task UnreachableOwnerPlusLegacyHistoryMiss_ReturnsDeferUnreachable()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://unreachable",
                rootFolders: () => throw new HttpRequestException("down"),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not be called")),
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => Task.FromResult(ArrRepairOutcome.DownloadHistoryNotFound),
                importHistory: (_, _, _) => Task.FromResult(new ArrHistory())),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients,
            LibraryPath,
            null,
            CancellationToken.None,
            legacyDownloadId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferUnreachable, result.Decision);
    }

    [Fact]
    public async Task RootMatchWithoutExactMedia_DoesNotClaimMediaOwnership()
    {
        var clients = new ArrClient[]
        {
            new ScriptedArrClient(
                host: "http://radarr",
                rootFolders: () => Task.FromResult(new List<ArrRootFolder>
                {
                    new() { Path = "/media/movies" },
                }),
                removeAndBlocklist: (_, _) => throw new InvalidOperationException("should not mutate"),
                findMediaFile: _ => Task.FromResult<ArrMediaFileMatch?>(null)),
        };

        var result = await HealthCheckService.DecideArrLinkedRepairAsync(
            clients, LibraryPath, DownloadId, CancellationToken.None);

        Assert.Equal(HealthCheckService.ArrLinkedRepairDecision.DeferNoMatchingMediaItem, result.Decision);
    }

    private static readonly ArrMediaFileMatch DummyMediaFile =
        new(ArrMediaKind.Movie, FileId: 1, MediaIds: [1]);

    private sealed class ScriptedArrClient(
        string host,
        Func<Task<List<ArrRootFolder>>> rootFolders,
        Func<string, Guid, Task<ArrRepairOutcome>> removeAndBlocklist,
        Func<string, Task<ArrMediaFileMatch?>>? findMediaFile = null,
        Func<ArrMediaFileMatch, int, int, Task<ArrHistory>>? importHistory = null,
        Func<ArrMediaFileMatch, Guid, Task<ArrRepairOutcome>>? removeAndBlocklistMatch = null)
        : ArrClient(host, "test-key")
    {
        public List<Guid> BlocklistDownloadIds { get; } = [];

        public override Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct) => rootFolders();

        public override Task<ArrMediaFileMatch?> FindMediaFileAsync(
            string symlinkOrStrmPath,
            CancellationToken ct = default) =>
            findMediaFile?.Invoke(symlinkOrStrmPath)
            ?? Task.FromResult<ArrMediaFileMatch?>(DummyMediaFile);

        public override Task<ArrHistory> GetMediaImportHistoryAsync(
            ArrMediaFileMatch mediaFile,
            int page,
            int pageSize,
            CancellationToken ct = default) =>
            importHistory?.Invoke(mediaFile, page, pageSize)
            ?? Task.FromResult(new ArrHistory());

        public override Task<ArrRepairOutcome> RemoveAndBlocklist(
            string symlinkOrStrmPath,
            Guid downloadId,
            Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
            CancellationToken ct = default)
        {
            BlocklistDownloadIds.Add(downloadId);
            return removeAndBlocklist(symlinkOrStrmPath, downloadId);
        }

        public override Task<ArrRepairOutcome> RemoveAndBlocklist(
            ArrMediaFileMatch mediaFile,
            Guid downloadId,
            Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
            CancellationToken ct = default)
        {
            BlocklistDownloadIds.Add(downloadId);
            return removeAndBlocklistMatch is not null
                ? removeAndBlocklistMatch(mediaFile, downloadId)
                : removeAndBlocklist(LibraryPath, downloadId);
        }
    }
}
