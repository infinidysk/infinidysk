using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

public sealed class FilesLibraryIndexTests
{
    private static ConfigManager Config(string root = "/synthetic/library")
    {
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = root }]);
        return config;
    }

    [Fact]
    public async Task ConcurrentReads_StartOneScan()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var index = new FilesLibraryIndex(Config(), new ControllableTimeProvider())
        {
            ReadLinks = (_, _, token) =>
            {
                Interlocked.Increment(ref count);
                started.TrySetResult();
                release.Wait(token);
                return new Dictionary<Guid, string[]>();
            },
        };
        Assert.Equal("unknown", index.GetSnapshot().State);
        await started.Task;
        var waiters = Enumerable.Range(0, 20).Select(_ => index.RefreshAsync(CancellationToken.None)).ToArray();
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal("unknown", index.GetSnapshot().State));
        release.Set();
        var results = await Task.WhenAll(waiters);
        Assert.All(results, result => Assert.Equal("ready", result.State));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CompleteScan_PreservesAllLinksPerDavItem()
    {
        var root = Path.Join(Path.GetTempPath(), $"files-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(Path.Join(root, "second.strm"), $"http://localhost/view/.ids/{id}.mkv");
            await File.WriteAllTextAsync(Path.Join(root, "first.strm"), $"http://localhost/view/.ids/{id}.mkv");
            using var index = new FilesLibraryIndex(Config(root), new ControllableTimeProvider());
            var snapshot = await index.RefreshAsync(CancellationToken.None);
            Assert.Equal("ready", snapshot.State);
            Assert.Equal([Path.Join(root, "first.strm"), Path.Join(root, "second.strm")], snapshot.Links[id]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PartialFailure_DoesNotPublishNegativeMembership()
    {
        using var index = new FilesLibraryIndex(Config(), new ControllableTimeProvider())
        {
            ReadLinks = (_, _, _) => throw new IOException("synthetic failure"),
        };
        var snapshot = await index.RefreshAsync(CancellationToken.None);
        Assert.Equal("unknown", snapshot.State);
        Assert.Empty(snapshot.Links);
        Assert.DoesNotContain("synthetic failure", snapshot.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedScan_IsThrottledUntilNextAttempt()
    {
        var clock = new ControllableTimeProvider();
        var attempts = 0;
        using var index = new FilesLibraryIndex(Config(), clock)
        {
            ReadLinks = (_, _, _) => { Interlocked.Increment(ref attempts); throw new IOException(); },
        };
        await index.RefreshAsync(CancellationToken.None);
        for (var indexRead = 0; indexRead < 20; indexRead++) Assert.Equal("unknown", index.GetSnapshot().State);
        Assert.Equal(1, attempts);
        clock.Advance(FilesLibraryIndex.RefreshInterval);
        await index.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ConfigurationChange_DiscardsOldRootResult()
    {
        using var release = new ManualResetEventSlim();
        var config = Config();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var index = new FilesLibraryIndex(config, new ControllableTimeProvider())
        {
            ReadLinks = (_, _, token) => { started.TrySetResult(); release.Wait(token); return new Dictionary<Guid, string[]>(); },
        };
        var refresh = index.RefreshAsync(CancellationToken.None);
        await started.Task;
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = "/synthetic/changed" }]);
        release.Set();
        Assert.Equal("unknown", (await refresh).State);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelOtherWaiters()
    {
        using var release = new ManualResetEventSlim();
        using var caller = new CancellationTokenSource();
        using var index = new FilesLibraryIndex(Config(), new ControllableTimeProvider())
        {
            ReadLinks = (_, _, token) => { release.Wait(token); return new Dictionary<Guid, string[]>(); },
        };
        var cancelled = index.RefreshAsync(caller.Token);
        var surviving = index.RefreshAsync(CancellationToken.None);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.Set();
        Assert.Equal("ready", (await surviving).State);
    }

    [Fact]
    public async Task Dispose_CancelsScanAndObservesItsCompletion()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new FilesLibraryIndex(Config(), new ControllableTimeProvider())
        {
            ReadLinks = (_, _, token) => { started.TrySetResult(); release.Wait(token); return new Dictionary<Guid, string[]>(); },
        };
        var refresh = index.RefreshAsync(CancellationToken.None);
        await started.Task;
        index.Dispose();
        Assert.Equal("unknown", (await refresh).State);
        index.Dispose();
    }
}