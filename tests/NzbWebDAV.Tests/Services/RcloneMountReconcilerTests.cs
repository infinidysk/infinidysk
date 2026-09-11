using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneMountReconcilerTests
{
    private static RcloneMountConfig Mount(string id = "library", string mountPoint = "/mnt/remote") =>
        new() { Id = id, MountPoint = mountPoint };

    [Fact]
    public async Task ReconcileAsync_MountsWhatIsConfiguredButNotYetMounted()
    {
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var reconciler = new RcloneMountReconciler(client);

        var result = await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        var mounted = Assert.Single(client.Mounted);
        Assert.Equal("/mnt/remote", mounted.MountPoint);
        Assert.Equal("infinidysk:/", mounted.Fs);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ReconcileAsync_LeavesAnAlreadyCorrectMountAlone()
    {
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/", MountPoint = "/mnt/remote" }],
        };
        var reconciler = new RcloneMountReconciler(client);

        await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Empty(client.Mounted);
        Assert.Empty(client.Unmounted);
    }

    // rclone normalizes a root mount: mounting "infinidysk:/" is reported back by
    // mount/listmounts as "infinidysk:". Comparing the two literally would make
    // every reconcile pass unmount and remount a perfectly good root mount.
    [Fact]
    public async Task ReconcileAsync_TreatsARootMountAsUnchanged_DespiteRcloneDroppingTheTrailingSlash()
    {
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:", MountPoint = "/mnt/remote" }],
        };
        var reconciler = new RcloneMountReconciler(client);

        await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Empty(client.Unmounted);
        Assert.Empty(client.Mounted);
    }

    [Fact]
    public async Task ReconcileAsync_UnmountsWhatIsNoLongerConfigured()
    {
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/", MountPoint = "/mnt/stale" }],
        };
        var reconciler = new RcloneMountReconciler(client);

        await reconciler.ReconcileAsync([], CancellationToken.None);

        Assert.Equal(["/mnt/stale"], client.Unmounted);
    }

    [Fact]
    public async Task ReconcileAsync_UnmountsADisabledMount()
    {
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/", MountPoint = "/mnt/remote" }],
        };
        var reconciler = new RcloneMountReconciler(client);

        var mount = Mount();
        mount.Enabled = false;

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        Assert.Equal(["/mnt/remote"], client.Unmounted);
    }

    [Fact]
    public async Task ReconcileAsync_RemountsWhenTheRemotePathChanged()
    {
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/old", MountPoint = "/mnt/remote" }],
        };
        var reconciler = new RcloneMountReconciler(client);

        var mount = Mount();
        mount.RemotePath = "/new";

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        Assert.Equal(["/mnt/remote"], client.Unmounted);
        Assert.Equal("infinidysk:/new", Assert.Single(client.Mounted).Fs);
    }

    [Fact]
    public async Task ReconcileAsync_RefusesToMountBeforeTheRemoteExists()
    {
        var client = new FakeRcloneClient { Remotes = [] };
        var reconciler = new RcloneMountReconciler(client);

        var result = await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Empty(client.Mounted);
        var error = Assert.Single(result.Errors);
        Assert.Contains("WebDAV", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAsync_TranslatesCacheSettingsIntoVfsOptions()
    {
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var reconciler = new RcloneMountReconciler(client);

        var mount = Mount();
        mount.VfsCacheMode = RcloneVfsCacheMode.Writes;
        mount.AllowOther = true;

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        var call = Assert.Single(client.Mounted);
        Assert.Equal("writes", call.VfsOpt!["CacheMode"]);
        Assert.Equal(true, call.MountOpt!["AllowOther"]);
    }

    [Fact]
    public async Task ReconcileAsync_DisablesChangePolling()
    {
        // The remote is InfiniDysk's own WebDAV server, which has no change
        // notification, so rclone's one-minute poll asks a question nothing can
        // answer.
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var reconciler = new RcloneMountReconciler(client);

        await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Equal("0s", Assert.Single(client.Mounted).VfsOpt!["PollInterval"]);
    }

    [Fact]
    public async Task ReconcileAsync_SendsTheTunedDefaults_WhenAMountSetsNothing()
    {
        // A mount saved before these fields existed omits them, so the defaults
        // here are what most installations actually run with.
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var reconciler = new RcloneMountReconciler(client);

        await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.Equal("168h0m0s", vfsOpt["DirCacheTime"]);
        Assert.Equal("168h0m0s", vfsOpt["CacheMaxAge"]);
        Assert.Equal(536_870_912L, vfsOpt["ReadAhead"]);
    }

    // An import that reads a user's tuned settings and then does not apply them
    // would be worse than no import at all: the mount would look migrated while
    // quietly running on defaults.
    [Fact]
    public async Task ReconcileAsync_AppliesEveryTunedSettingItWasGiven()
    {
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var reconciler = new RcloneMountReconciler(client);

        var mount = Mount();
        mount.DirCacheTime = TimeSpan.FromSeconds(20);
        mount.VfsCacheMaxAge = TimeSpan.FromHours(24);
        mount.VfsCacheMaxSizeBytes = 21_474_836_480;
        mount.ReadAheadBytes = 536_870_912;
        mount.Links = true;

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.Equal("20s", vfsOpt["DirCacheTime"]);
        Assert.Equal("24h0m0s", vfsOpt["CacheMaxAge"]);
        Assert.Equal(21_474_836_480L, vfsOpt["CacheMaxSize"]);
        Assert.Equal(536_870_912L, vfsOpt["ReadAhead"]);
        Assert.Equal(true, vfsOpt["Links"]);
    }

    [Fact]
    public async Task ReconcileAsync_OmitsUnsetSizeLimits_SoRcloneKeepsItsDefaults()
    {
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var reconciler = new RcloneMountReconciler(client);

        var mount = Mount();
        mount.VfsCacheMaxSizeBytes = null;
        mount.ReadAheadBytes = null;

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.False(vfsOpt.ContainsKey("CacheMaxSize"));
        Assert.False(vfsOpt.ContainsKey("ReadAhead"));
    }

    [Fact]
    public async Task ReconcileAsync_ReportsAMountFailureWithoutAbandoningTheOtherMounts()
    {
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            MountError = mountPoint => mountPoint == "/mnt/busy" ? "mount point is not empty" : null,
        };
        var reconciler = new RcloneMountReconciler(client);

        var result = await reconciler.ReconcileAsync(
            [Mount("busy", "/mnt/busy"), Mount("ok", "/mnt/ok")],
            CancellationToken.None);

        Assert.Contains(client.Mounted, m => m.MountPoint == "/mnt/ok");
        var error = Assert.Single(result.Errors);
        // The message is rewritten into something actionable, but it still has to
        // name the mount that failed.
        Assert.Contains("/mnt/busy", error, StringComparison.Ordinal);
        Assert.Contains("not empty", error, StringComparison.Ordinal);
    }

    private sealed record MountCall(
        string Fs,
        string MountPoint,
        IReadOnlyDictionary<string, object?>? MountOpt,
        IReadOnlyDictionary<string, object?>? VfsOpt);

    [Fact]
    public async Task ReconcileAsync_ChangesNothing_WhenTheMountListingFails()
    {
        // Treating a failed listing as "nothing is mounted" would mount every
        // configured path a second time on top of the live one.
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/", MountPoint = "/mnt/remote" }],
            ListMountsSucceeds = false,
        };
        var reconciler = new RcloneMountReconciler(client);

        var result = await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Empty(client.Mounted);
        Assert.Empty(client.Unmounted);
        Assert.Contains(result.Errors, e => e.Contains("connection refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconcileAsync_SendsACacheCeiling_WhenNoneIsConfigured()
    {
        // rclone's own default is unlimited, which fills whichever volume the
        // cache directory sits on.
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var budget = new RcloneCacheBudget
        {
            AvailableFreeSpace = _ => 400L * 1024 * 1024 * 1024,
            MountPointOf = _ => "/mnt/fast",
        };
        var reconciler = new RcloneMountReconciler(client, budget, "/mnt/fast/cache", "/config");

        await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.Equal(RcloneCacheBudget.DefaultCapBytes, vfsOpt["CacheMaxSize"]);
    }

    [Fact]
    public async Task ReconcileAsync_KeepsAnExplicitCacheCeiling()
    {
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var budget = new RcloneCacheBudget
        {
            AvailableFreeSpace = _ => 400L * 1024 * 1024 * 1024,
            MountPointOf = _ => "/mnt/fast",
        };
        var reconciler = new RcloneMountReconciler(client, budget, "/mnt/fast/cache", "/config");
        var mount = Mount();
        mount.VfsCacheMaxSizeBytes = 3L * 1024 * 1024 * 1024;

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.Equal(3L * 1024 * 1024 * 1024, vfsOpt["CacheMaxSize"]);
    }

    [Fact]
    public async Task ReconcileAsync_AppliesTheInstallWideCacheCeiling()
    {
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var budget = new RcloneCacheBudget
        {
            AvailableFreeSpace = _ => 400L * 1024 * 1024 * 1024,
            MountPointOf = _ => "/mnt/fast",
        };
        var reconciler = new RcloneMountReconciler(
            client, budget, "/mnt/fast/cache", "/config", 8L * 1024 * 1024 * 1024);

        await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.Equal(8L * 1024 * 1024 * 1024, vfsOpt["CacheMaxSize"]);
    }

    [Fact]
    public async Task ReconcileAsync_PrefersTheInstallWideCeiling_OverAnImportedPerMountOne()
    {
        // The size in Settings is the only cache ceiling an operator can see or
        // change; a per-mount one only ever arrives by importing an external
        // rclone. A visible control that silently loses to an invisible one is
        // worse than either rule on its own.
        var client = new FakeRcloneClient { Remotes = ["infinidysk"] };
        var budget = new RcloneCacheBudget
        {
            AvailableFreeSpace = _ => 400L * 1024 * 1024 * 1024,
            MountPointOf = _ => "/mnt/fast",
        };
        var reconciler = new RcloneMountReconciler(
            client, budget, "/mnt/fast/cache", "/config", 8L * 1024 * 1024 * 1024);
        var mount = Mount();
        mount.VfsCacheMaxSizeBytes = 3L * 1024 * 1024 * 1024;

        await reconciler.ReconcileAsync([mount], CancellationToken.None);

        var vfsOpt = Assert.Single(client.Mounted).VfsOpt!;
        Assert.Equal(8L * 1024 * 1024 * 1024, vfsOpt["CacheMaxSize"]);
    }

    [Fact]
    public async Task ReconcileAsync_UnmountsNothing_WhenTheRemoteListingFails()
    {
        // The mount below has a changed remote path, so reconciling would unmount
        // it before remounting. If the remote cannot be read the remount is not
        // going to happen, and taking the library down on the way to finding that
        // out leaves the operator worse off than doing nothing.
        var client = new FakeRcloneClient
        {
            Live = [new RcloneMountPoint { Fs = "infinidysk:/old", MountPoint = "/mnt/remote/infinidysk" }],
            ListRemotesSucceeds = false,
        };
        var reconciler = new RcloneMountReconciler(client);
        var mount = Mount();
        mount.RemotePath = "/new";

        var result = await reconciler.ReconcileAsync([mount], CancellationToken.None);

        Assert.Empty(client.Unmounted);
        Assert.Empty(client.Mounted);
        Assert.Contains(result.Errors, e => e.Contains("No mounts were changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconcileAsync_RemountsWhenTheLiveTuningNoLongerMatches()
    {
        // rclone applies VFS options at mount time. Without this the operator
        // changes the cache mode, the save succeeds, the pass reports success,
        // and the mount carries on exactly as before.
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/", MountPoint = "/mnt/remote" }],
            LiveOptions = new VfsOptions
            {
                CacheMode = "writes",
                Links = true,
                DirCacheTime = TimeSpan.FromDays(7).Ticks * 100,
                CacheMaxAge = TimeSpan.FromDays(7).Ticks * 100,
                ReadAhead = 536_870_912,
            },
        };
        var reconciler = new RcloneMountReconciler(client);

        // Configured as full; the mount is running as writes.
        var result = await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Contains("/mnt/remote", client.Unmounted);
        Assert.Contains("/mnt/remote", result.Mounted);
    }

    [Fact]
    public async Task ReconcileAsync_LeavesAMountAloneWhenItsTuningStillMatches()
    {
        // The other half: an unchanged mount must not be torn down every pass.
        var client = new FakeRcloneClient
        {
            Remotes = ["infinidysk"],
            Live = [new RcloneMountPoint { Fs = "infinidysk:/", MountPoint = "/mnt/remote" }],
            LiveOptions = new VfsOptions
            {
                CacheMode = "full",
                Links = true,
                DirCacheTime = TimeSpan.FromDays(7).Ticks * 100,
                CacheMaxAge = TimeSpan.FromDays(7).Ticks * 100,
                ReadAhead = 536_870_912,
            },
        };
        var reconciler = new RcloneMountReconciler(client);

        var result = await reconciler.ReconcileAsync([Mount()], CancellationToken.None);

        Assert.Empty(client.Unmounted);
        Assert.Empty(result.Mounted);
    }

    [Fact]
    public void DescribeMountFailure_ExplainsAMountPointSomethingElseHolds()
    {
        // rclone refuses to mount over a live mount point. A FUSE mount outlives
        // the process that made it, so this is what a hard restart or a running
        // sidecar looks like.
        var message = RcloneMountReconciler.DescribeMountFailure(
            "/data/nzbdav",
            "failed to mount FUSE fs: directory already mounted, use --allow-non-empty to mount anyway: /data/nzbdav");

        Assert.Contains("already mounted there", message, StringComparison.Ordinal);
        Assert.Contains("fusermount3 -uz /data/nzbdav", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeMountFailure_DistinguishesANonEmptyDirectory()
    {
        // A different rclone guard with a different fix: there is no mount to
        // clear, the directory simply has files in it.
        var message = RcloneMountReconciler.DescribeMountFailure(
            "/data/nzbdav",
            """failed to mount FUSE fs: "/data/nzbdav" is not empty, use --allow-non-empty to mount anyway""");

        Assert.Contains("not empty", message, StringComparison.Ordinal);
        Assert.DoesNotContain("fusermount3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeMountFailure_PassesAnythingElseThrough()
    {
        var message = RcloneMountReconciler.DescribeMountFailure("/data/nzbdav", "permission denied");

        Assert.Contains("permission denied", message, StringComparison.Ordinal);
    }

    private sealed class FakeRcloneClient : IRcloneClient
    {
        public List<string> Remotes { get; init; } = [];
        public List<RcloneMountPoint> Live { get; init; } = [];
        public List<MountCall> Mounted { get; } = [];
        public List<string> Unmounted { get; } = [];
        public Func<string, string?> MountError { get; init; } = _ => null;

        public string? Host => "http://127.0.0.1:5572";
        public bool IsRemoteControlEnabled => true;
        public (string Message, DateTimeOffset At)? LastForgetError => null;

        public int UnmountAllCalls { get; private set; }

        public Task<RcloneResponse> UnmountAll(CancellationToken cancellationToken = default)
        {
            UnmountAllCalls++;
            return Task.FromResult(new RcloneResponse { Success = true });
        }

        public bool ListRemotesSucceeds { get; init; } = true;

        public Task<ListRemotesResponse> ListRemotes(CancellationToken cancellationToken = default) =>
            Task.FromResult(ListRemotesSucceeds
                ? new ListRemotesResponse { Success = true, Remotes = Remotes }
                : new ListRemotesResponse { Success = false, Error = "connection refused" });

        public bool ListMountsSucceeds { get; init; } = true;

        public Task<ListMountsResponse> ListMounts(CancellationToken cancellationToken = default) =>
            Task.FromResult(ListMountsSucceeds
                ? new ListMountsResponse { Success = true, MountPoints = Live }
                : new ListMountsResponse { Success = false, Error = "connection refused" });

        public Task<MountResponse> MountFs(
            string fs,
            string mountPoint,
            IReadOnlyDictionary<string, object?>? mountOpt,
            IReadOnlyDictionary<string, object?>? vfsOpt,
            CancellationToken cancellationToken = default)
        {
            var error = MountError(mountPoint);
            if (error is not null)
                return Task.FromResult(new MountResponse { Success = false, Error = error });

            Mounted.Add(new MountCall(fs, mountPoint, mountOpt, vfsOpt));
            return Task.FromResult(new MountResponse { Success = true, MountPoint = mountPoint });
        }

        public Task<RcloneResponse> UnmountFs(string mountPoint, CancellationToken cancellationToken = default)
        {
            Unmounted.Add(mountPoint);
            Live.RemoveAll(m => m.MountPoint == mountPoint);
            return Task.FromResult(new RcloneResponse { Success = true });
        }

        public Task<RcloneResponse> CreateRemote(
            string name,
            string type,
            IReadOnlyDictionary<string, string> parameters,
            CancellationToken cancellationToken = default)
        {
            Remotes.Add(name);
            return Task.FromResult(new RcloneResponse { Success = true });
        }

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

        /// <summary>What the running mount reports, when a test cares.</summary>
        public VfsOptions? LiveOptions { get; init; }

        public Task<VfsStatsResponse> GetVfsStats(string? fs = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VfsStatsResponse { Success = true, Options = LiveOptions });

        public Task<CoreVersionResponse> GetVersion(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoreVersionResponse { Success = true });

        public Task<RcloneResponse> NoOp(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RcloneResponse { Success = true });

        public Task<bool> IsAvailable(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
