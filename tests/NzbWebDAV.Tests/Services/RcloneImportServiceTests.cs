using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneImportServiceTests
{
    private static FakeExternalRclone External() => new()
    {
        Live = [new RcloneMountPoint { Fs = "nzbdav:", MountPoint = "/mnt/remote/nzbdav" }],
        Options = new VfsOptions
        {
            CacheMode = "full",
            DirCacheTime = 20_000_000_000,
            CacheMaxAge = 86_400_000_000_000,
            CacheMaxSize = 21_474_836_480,
            ReadAhead = 536_870_912,
            Links = true,
        },
    };

    [Fact]
    public async Task PreviewAsync_ProducesAMountMatchingTheExternalOne()
    {
        var preview = await new RcloneImportService().PreviewAsync(External(), "/config", CancellationToken.None);

        Assert.True(preview.Success);
        var mount = Assert.Single(preview.Mounts);
        Assert.Equal("/mnt/remote/nzbdav", mount.MountPoint);
        Assert.Equal(TimeSpan.FromSeconds(20), mount.DirCacheTime);
        Assert.True(mount.Links);
    }

    [Fact]
    public async Task PreviewAsync_KeepsTheTuningTheExternalMountIsRunningWith()
    {
        var preview = await new RcloneImportService().PreviewAsync(External(), "/config", CancellationToken.None);

        var mount = Assert.Single(preview.Mounts);
        Assert.Equal(21_474_836_480, mount.VfsCacheMaxSizeBytes);
        Assert.Equal(536_870_912, mount.ReadAheadBytes);
        Assert.Equal(TimeSpan.FromHours(24), mount.VfsCacheMaxAge);
    }

    [Fact]
    public async Task PreviewAsync_SaysThePasswordIsNotCarriedAcross()
    {
        // The import reads mount settings only. Reading a credential it cannot
        // apply would widen what a bug could leak for no benefit.
        var preview = await new RcloneImportService().PreviewAsync(External(), "/config", CancellationToken.None);

        Assert.Contains(preview.Warnings, w => w.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAsync_WarnsWhenTheExternalInstanceHasNothingMounted()
    {
        var external = External();
        external.Live.Clear();

        var preview = await new RcloneImportService().PreviewAsync(external, "/config", CancellationToken.None);

        Assert.Empty(preview.Mounts);
        Assert.Contains(preview.Warnings, w => w.Contains("no mounts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAsync_ReportsAnUnreachableExternalInstance()
    {
        var external = External();
        external.Reachable = false;

        var preview = await new RcloneImportService().PreviewAsync(external, "/config", CancellationToken.None);

        Assert.False(preview.Success);
        Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public async Task PreviewAsync_SurfacesAMountPointThatWouldBeRejected()
    {
        var external = External();
        external.Live[0] = new RcloneMountPoint { Fs = "nzbdav:", MountPoint = "/config/mnt" };

        var preview = await new RcloneImportService().PreviewAsync(external, "/config", CancellationToken.None);

        Assert.Contains(preview.Warnings, w => w.Contains("config directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAsync_AlwaysWarnsAboutTheCutoverOrder()
    {
        var preview = await new RcloneImportService().PreviewAsync(External(), "/config", CancellationToken.None);

        // Two rclone processes cannot serve one mount point, and the user's
        // library resolves through it, so the order is not optional.
        Assert.Contains(preview.Warnings, w => w.Contains("stop", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeExternalRclone : IRcloneClient
    {
        public List<RcloneMountPoint> Live { get; init; } = [];
        public VfsOptions? Options { get; init; }
        public bool Reachable { get; set; } = true;

        public string? Host => "http://nzbdav_rclone:5572";
        public bool IsRemoteControlEnabled => true;
        public (string Message, DateTimeOffset At)? LastForgetError => null;

        public Task<ListMountsResponse> ListMounts(CancellationToken cancellationToken = default) =>
            Task.FromResult(Reachable
                ? new ListMountsResponse { Success = true, MountPoints = Live }
                : new ListMountsResponse { Success = false, Error = "connection refused" });

        public Task<VfsStatsResponse> GetVfsStats(string? fs = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VfsStatsResponse { Success = true, Options = Options });

        public Task<MountResponse> MountFs(
            string fs,
            string mountPoint,
            IReadOnlyDictionary<string, object?>? mountOpt,
            IReadOnlyDictionary<string, object?>? vfsOpt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An import must never modify the external instance.");

        public Task<RcloneResponse> UnmountFs(string mountPoint, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An import must never modify the external instance.");

        public Task<RcloneResponse> UnmountAll(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An import must never modify the external instance.");

        public Task<RcloneResponse> CreateRemote(
            string name,
            string type,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An import must never modify the external instance.");

        public Task<ListRemotesResponse> ListRemotes(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListRemotesResponse { Success = true, Remotes = ["nzbdav"] });

        public Task<RcloneResponse> RefreshVfsPaths(
            IEnumerable<string> paths,
            bool recursive = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RcloneResponse { Success = true });

        public Task<VfsForgetResponse> ForgetVfsPaths(
            IEnumerable<string> paths,
            string? fs = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VfsForgetResponse { Success = true });

        public Task<CoreVersionResponse> GetVersion(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoreVersionResponse { Success = true });

        public Task<RcloneResponse> NoOp(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RcloneResponse { Success = true });

        public Task<bool> IsAvailable(CancellationToken cancellationToken = default) => Task.FromResult(Reachable);
    }
}
