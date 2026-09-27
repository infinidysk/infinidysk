using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheProbeDeadlineTests
{
    [Fact]
    public async Task ProbeDeadline_ReportsFailure_AndCancelsUnderlyingWork()
    {
        using var caller = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = NativeCacheOperations.RunProbeWithDeadlineAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.TrySetResult(); }
            return new("unknown", "unknown", false, false, false, 0, null);
        }, TimeSpan.FromMilliseconds(30), caller.Token);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(caller.IsCancellationRequested);
        }
        finally
        {
            caller.Cancel();
            try { await pending; } catch (Exception) { }
        }
    }

    [Fact]
    public async Task ProbeDeadline_AlsoContainsSynchronousFilesystemStalls()
    {
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(() => NativeCacheOperations.RunProbeWithDeadlineAsync(_ =>
        {
            release.Wait(); // Models an OS filesystem call that ignores cancellation.
            finished.TrySetResult();
            return Task.FromResult(new NativeCacheProbeResult("unknown", "unknown", false, false, false, 0, null));
        }, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        try { await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try { await pending; } catch (IOException) { }
        }
    }
}
