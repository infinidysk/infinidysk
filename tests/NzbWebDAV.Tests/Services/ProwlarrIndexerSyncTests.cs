using NzbWebDAV.Clients.Prowlarr;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ProwlarrIndexerSyncTests
{
    [Fact]
    public void Merge_ImportsEnabledSearchableUsenetIndexersThroughProwlarrProxy()
    {
        var indexers = new IndexerConfig();
        var profiles = new ProfileConfig();

        var result = ProwlarrIndexerSync.Merge(
            indexers,
            profiles,
            [
                Remote(7, "Usenet One", enable: true, supportsSearch: true, protocol: "usenet"),
                Remote(8, "Torrent", enable: true, supportsSearch: true, protocol: "torrent"),
                Remote(9, "Disabled", enable: false, supportsSearch: true, protocol: "usenet"),
                Remote(10, "No Search", enable: true, supportsSearch: false, protocol: "usenet"),
            ],
            "http://prowlarr:9696/prowlarr/",
            "prowlarr-key");

        var imported = Assert.Single(indexers.Indexers);
        Assert.Equal("Usenet One", imported.Name);
        Assert.Equal("http://prowlarr:9696/prowlarr/7/api", imported.Url);
        Assert.Equal("prowlarr-key", imported.ApiKey);
        Assert.True(imported.Enabled);
        Assert.Equal(7, imported.ProwlarrIndexerId);
        Assert.Equal(1, result.Added);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(4, result.RemoteIndexerCount);
    }

    [Fact]
    public void Merge_UpdatesOwnedFieldsAndPreservesLocalTuning()
    {
        var indexers = new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "Old Name",
                    Url = "http://old/7/api",
                    ApiKey = "old-key",
                    Enabled = true,
                    ProwlarrIndexerId = 7,
                    MaxRequestsPerMinute = 12,
                    HitLimit = 100,
                    ProxyUrl = "http://proxy:8888",
                    EnableStrictMatching = true,
                    Filter = new IndexerConfig.ResultFilter { Enabled = true, MinGrabs = 5 },
                },
            ],
        };

        var result = ProwlarrIndexerSync.Merge(
            indexers,
            new ProfileConfig(),
            [Remote(7, "New Name", enable: false, supportsSearch: true, protocol: "usenet")],
            "http://prowlarr:9696",
            "new-key");

        var updated = Assert.Single(indexers.Indexers);
        Assert.Equal("New Name", updated.Name);
        Assert.Equal("http://prowlarr:9696/7/api", updated.Url);
        Assert.Equal("new-key", updated.ApiKey);
        Assert.False(updated.Enabled);
        Assert.Equal(12, updated.MaxRequestsPerMinute);
        Assert.Equal(100, updated.HitLimit);
        Assert.Equal("http://proxy:8888", updated.ProxyUrl);
        Assert.True(updated.EnableStrictMatching);
        Assert.Equal(5, updated.Filter?.MinGrabs);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public void Merge_RenamesProfileReferencesAndRemovesStaleManagedIndexers()
    {
        var indexers = new IndexerConfig
        {
            Indexers =
            [
                Managed(7, "Before"),
                Managed(8, "Stale"),
                new IndexerConfig.ConnectionDetails
                {
                    Name = "Manual",
                    Url = "https://manual.example/api",
                    ApiKey = "manual-key",
                },
            ],
        };
        var profiles = new ProfileConfig
        {
            Profiles =
            [
                new ProfileConfig.Profile
                {
                    Token = "token",
                    Name = "profile",
                    IndexerNames = ["Before", "Stale", "Manual"],
                },
            ],
        };

        var result = ProwlarrIndexerSync.Merge(
            indexers,
            profiles,
            [Remote(7, "After", enable: true, supportsSearch: true, protocol: "usenet")],
            "http://prowlarr:9696",
            "key");

        Assert.Equal(["After", "Manual"], indexers.Indexers.Select(x => x.Name));
        Assert.Equal(["After", "Manual"], profiles.Profiles[0].IndexerNames);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Removed);
        Assert.True(result.ProfilesChanged);
    }

    [Fact]
    public void Merge_IsIdempotentAndDoesNotModifyManualNameCollision()
    {
        var indexers = new IndexerConfig
        {
            Indexers =
            [
                new IndexerConfig.ConnectionDetails
                {
                    Name = "Shared",
                    Url = "https://manual.example/api",
                    ApiKey = "manual-key",
                },
            ],
        };
        var profiles = new ProfileConfig();
        var remote = new[]
        {
            Remote(7, "Shared", enable: true, supportsSearch: true, protocol: "usenet"),
        };

        var first = ProwlarrIndexerSync.Merge(
            indexers,
            profiles,
            remote,
            "http://prowlarr:9696",
            "key");
        var second = ProwlarrIndexerSync.Merge(
            indexers,
            profiles,
            remote,
            "http://prowlarr:9696",
            "key");

        Assert.Single(indexers.Indexers);
        Assert.Null(indexers.Indexers[0].ProwlarrIndexerId);
        Assert.Equal("manual-key", indexers.Indexers[0].ApiKey);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(1, second.Skipped);
        Assert.False(second.IndexersChanged);
    }

    [Fact]
    public void Merge_DuplicateRemoteNamesImportOnlyTheFirstDeterministically()
    {
        var indexers = new IndexerConfig();

        var result = ProwlarrIndexerSync.Merge(
            indexers,
            new ProfileConfig(),
            [
                Remote(9, "Same", enable: true, supportsSearch: true, protocol: "usenet"),
                Remote(7, "Same", enable: true, supportsSearch: true, protocol: "usenet"),
            ],
            "http://prowlarr:9696",
            "key");

        var imported = Assert.Single(indexers.Indexers);
        Assert.Equal(7, imported.ProwlarrIndexerId);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Skipped);
    }

    private static ProwlarrIndexer Remote(
        int id,
        string name,
        bool enable,
        bool supportsSearch,
        string protocol) => new()
        {
            Id = id,
            Name = name,
            Enable = enable,
            SupportsSearch = supportsSearch,
            Protocol = protocol,
        };

    private static IndexerConfig.ConnectionDetails Managed(int id, string name) => new()
    {
        Name = name,
        Url = $"http://prowlarr/{id}/api",
        ApiKey = "key",
        ProwlarrIndexerId = id,
    };
}
