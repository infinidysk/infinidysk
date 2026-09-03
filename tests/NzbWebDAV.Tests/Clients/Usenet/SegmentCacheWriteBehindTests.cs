using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class SegmentCacheWriteBehindTests
{
    [Fact]
    public async Task JobCapacityCountsQueuedAndActiveWrites()
    {
        var persistStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersist = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var writer = new SegmentCacheWriteBehind(
            budgetBytes: 1024 * 1024,
            persist: async (_, cancellationToken) =>
            {
                persistStarted.TrySetResult();
                await releasePersist.Task.WaitAsync(cancellationToken);
                return SegmentCacheCommitResult.Committed;
            },
            warnWriteFailure: () => { },
            maximumJobs: 2);

        Assert.True(writer.TryRentBuffer(1024, out var firstBody, out var firstCapacity));
        firstBody.Write(new byte[1024]);
        Assert.True(writer.TryEnqueue(CreateWrite("first", firstBody, firstCapacity)));
        await persistStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(writer.TryRentBuffer(1024, out var secondBody, out var secondCapacity));
        secondBody.Write(new byte[1024]);
        Assert.True(writer.TryEnqueue(CreateWrite("second", secondBody, secondCapacity)));
        Assert.False(writer.TryRentBuffer(1024, out var rejected, out _));
        Assert.Null(rejected);
        Assert.Equal(1, writer.Snapshot().CapacitySkips);

        releasePersist.TrySetResult();
        writer.Retire();
        await writer.DrainForTestsAsync().WaitAsync(TimeSpan.FromSeconds(1));

        var drained = writer.Snapshot();
        Assert.Equal(0, drained.ReservedBytes);
        Assert.Equal(0, drained.QueuedJobs);
        Assert.Equal(0, drained.ActiveJobs);
    }

    [Fact]
    public async Task WorkerPersistsJobsInFifoOrder()
    {
        var order = new ConcurrentQueue<string>();
        using var writer = new SegmentCacheWriteBehind(
            budgetBytes: 1024 * 1024,
            persist: (write, _) =>
            {
                order.Enqueue(write.Hash);
                return Task.FromResult(SegmentCacheCommitResult.Committed);
            },
            warnWriteFailure: () => { });

        Assert.True(writer.TryRentBuffer(16, out var firstBody, out var firstCapacity));
        firstBody.Write(new byte[16]);
        Assert.True(writer.TryRentBuffer(16, out var secondBody, out var secondCapacity));
        secondBody.Write(new byte[16]);
        Assert.True(writer.TryEnqueue(CreateWrite("first", firstBody, firstCapacity)));
        Assert.True(writer.TryEnqueue(CreateWrite("second", secondBody, secondCapacity)));

        writer.Retire();
        await writer.DrainForTestsAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(["first", "second"], order.ToArray());
        Assert.Equal(0, writer.Snapshot().ReservedBytes);
    }

    [Fact]
    public void RetireRejectsEnqueueAndCallerReleasesReservation()
    {
        using var writer = new SegmentCacheWriteBehind(
            budgetBytes: 1024 * 1024,
            persist: (_, _) => Task.FromResult(SegmentCacheCommitResult.Committed),
            warnWriteFailure: () => { });
        Assert.True(writer.TryRentBuffer(1024, out var body, out var capacity));
        body.Write(new byte[1024]);

        writer.Retire();
        Assert.False(writer.TryEnqueue(CreateWrite("retired", body, capacity)));
        body.Dispose();
        writer.ReleaseReservation(capacity);

        var snapshot = writer.Snapshot();
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.QueuedJobs);
        Assert.Equal(0, snapshot.ActiveJobs);
    }

    [Fact]
    public async Task DisposeAfterTimeoutCancelsPersistenceAndReleasesOwnership()
    {
        var persistStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new SegmentCacheWriteBehind(
            budgetBytes: 1024 * 1024,
            persist: async (_, cancellationToken) =>
            {
                persistStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return SegmentCacheCommitResult.Committed;
            },
            warnWriteFailure: () => { },
            disposeDrainTimeout: TimeSpan.FromMilliseconds(10));
        Assert.True(writer.TryRentBuffer(1024, out var body, out var capacity));
        body.Write(new byte[1024]);
        Assert.True(writer.TryEnqueue(CreateWrite("stuck", body, capacity)));
        await persistStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        writer.Dispose();
        await writer.DrainForTestsAsync().WaitAsync(TimeSpan.FromSeconds(1));

        var snapshot = writer.Snapshot();
        Assert.Equal(0, snapshot.ReservedBytes);
        Assert.Equal(0, snapshot.QueuedJobs);
        Assert.Equal(0, snapshot.ActiveJobs);
    }

    private static PendingSegmentCacheWrite CreateWrite(
        string hash,
        NzbWebDAV.Streams.PooledBufferStream body,
        long reservedCapacity) =>
        new(
            hash,
            new UsenetYencHeader
            {
                FileName = "test.bin",
                FileSize = body.Length,
                PartOffset = 0,
                PartSize = body.Length,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
            },
            body,
            reservedCapacity,
            new SegmentCacheWriteAttempt((_, _) => { }));
}
