using System.Threading.Channels;
using System.Diagnostics.CodeAnalysis;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

internal sealed record PendingSegmentCacheWrite(
    string Hash,
    UsenetYencHeader Header,
    PooledBufferStream Body,
    long ReservedCapacityBytes,
    SegmentCacheWriteAttempt Attempt);

internal readonly record struct SegmentCacheWriteBehindSnapshot(
    long BudgetBytes,
    long ReservedBytes,
    long PeakReservedBytes,
    long QueuedJobs,
    long ActiveJobs,
    long CapacitySkips);

internal sealed class SegmentCacheWriteBehind : IDisposable
{
    internal const int DefaultMaximumJobs = 256;
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _disposeDrainTimeout;

    private readonly long _budgetBytes;
    private readonly int _maximumJobs;
    private readonly Func<PendingSegmentCacheWrite, CancellationToken, Task<SegmentCacheCommitResult>> _persist;
    private readonly Action _warnWriteFailure;
    private readonly Action<SegmentCacheWriteBehindSnapshot>? _publishSnapshot;
    private readonly Channel<PendingSegmentCacheWrite> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _stateLock = new();
    private readonly Task _worker;
    private long _reservedBytes;
    private long _peakReservedBytes;
    private long _queuedJobs;
    private long _activeJobs;
    private long _capacitySkips;
    private int _reservedJobs;
    private bool _accepting = true;
    private int _disposed;

    internal SegmentCacheWriteBehind(
        long budgetBytes,
        Func<PendingSegmentCacheWrite, CancellationToken, Task<SegmentCacheCommitResult>> persist,
        Action warnWriteFailure,
        Action<SegmentCacheWriteBehindSnapshot>? publishSnapshot = null,
        int maximumJobs = DefaultMaximumJobs,
        TimeSpan? disposeDrainTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumJobs);
        _budgetBytes = budgetBytes;
        _maximumJobs = maximumJobs;
        _persist = persist;
        _warnWriteFailure = warnWriteFailure;
        _publishSnapshot = publishSnapshot;
        _disposeDrainTimeout = disposeDrainTimeout ?? DisposeDrainTimeout;
        if (_disposeDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(disposeDrainTimeout));
        _channel = Channel.CreateBounded<PendingSegmentCacheWrite>(new BoundedChannelOptions(maximumJobs)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(ProcessAsync);
        _publishSnapshot?.Invoke(Snapshot());
    }

    internal bool TryRentBuffer(
        int capacityHint,
        [NotNullWhen(true)] out PooledBufferStream? body,
        out long reservedCapacity)
    {
        body = null;
        reservedCapacity = 0;
        if (capacityHint <= 0)
            return false;

        var estimatedCapacity = PooledBufferStream.EstimateDefaultRentedCapacity(capacityHint);
        lock (_stateLock)
        {
            if (!_accepting || _reservedJobs >= _maximumJobs ||
                estimatedCapacity > _budgetBytes - _reservedBytes)
            {
                _capacitySkips++;
                PublishSnapshotUnderLock();
                return false;
            }

            _reservedJobs++;
            _reservedBytes += estimatedCapacity;
            _peakReservedBytes = Math.Max(_peakReservedBytes, _reservedBytes);
            PublishSnapshotUnderLock();
        }

        PooledBufferStream? candidate = null;
        try
        {
            candidate = new PooledBufferStream(capacityHint);
            var physicalCapacity = candidate.RentedCapacity;
            lock (_stateLock)
            {
                var adjustment = physicalCapacity - estimatedCapacity;
                if (adjustment > _budgetBytes - _reservedBytes)
                {
                    _capacitySkips++;
                    _reservedJobs--;
                    _reservedBytes -= estimatedCapacity;
                    PublishSnapshotUnderLock();
                    return false;
                }

                _reservedBytes += adjustment;
                _peakReservedBytes = Math.Max(_peakReservedBytes, _reservedBytes);
                PublishSnapshotUnderLock();
            }

            body = candidate;
            candidate = null;
            reservedCapacity = physicalCapacity;
            return true;
        }
        catch
        {
            lock (_stateLock)
            {
                _reservedJobs--;
                _reservedBytes -= estimatedCapacity;
                PublishSnapshotUnderLock();
            }
            throw;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    internal bool TryEnqueue(PendingSegmentCacheWrite write)
    {
        lock (_stateLock)
        {
            if (!_accepting || !_channel.Writer.TryWrite(write))
                return false;
            _queuedJobs++;
            PublishSnapshotUnderLock();
            return true;
        }
    }

    internal void ReleaseReservation(long reservedCapacity)
    {
        lock (_stateLock)
        {
            _reservedJobs--;
            _reservedBytes -= reservedCapacity;
            if (_reservedJobs < 0 || _reservedBytes < 0)
                throw new InvalidOperationException("Segment-cache write-behind reservation underflow.");
            PublishSnapshotUnderLock();
        }
    }

    internal SegmentCacheWriteBehindSnapshot Snapshot()
    {
        lock (_stateLock)
        {
            return SnapshotUnderLock();
        }
    }

    internal void Retire()
    {
        lock (_stateLock)
        {
            if (!_accepting)
                return;
            _accepting = false;
            _channel.Writer.TryComplete();
            PublishSnapshotUnderLock();
        }
    }

    internal Task DrainForTestsAsync() => _worker;

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var write in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                lock (_stateLock)
                {
                    _queuedJobs--;
                    _activeJobs++;
                    PublishSnapshotUnderLock();
                }

                try
                {
                    var result = await _persist(write, _stop.Token).ConfigureAwait(false);
                    write.Attempt.Complete(
                        result == SegmentCacheCommitResult.Committed
                            ? SegmentCacheWriteOutcome.Committed
                            : result is SegmentCacheCommitResult.AlreadyPresent or SegmentCacheCommitResult.InvalidLength
                                ? SegmentCacheWriteOutcome.Skipped
                                : SegmentCacheWriteOutcome.Failed,
                        write.Body.Length);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    write.Attempt.Complete(SegmentCacheWriteOutcome.Skipped, write.Body.Length);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    write.Attempt.Complete(SegmentCacheWriteOutcome.Failed, write.Body.Length);
                    if (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                        _warnWriteFailure();
                }
                finally
                {
                    lock (_stateLock)
                    {
                        _activeJobs--;
                        PublishSnapshotUnderLock();
                    }
                    await write.Body.DisposeAsync().ConfigureAwait(false);
                    ReleaseReservation(write.ReservedCapacityBytes);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            while (_channel.Reader.TryRead(out var write))
            {
                lock (_stateLock)
                {
                    _queuedJobs--;
                    PublishSnapshotUnderLock();
                }
                write.Attempt.Complete(SegmentCacheWriteOutcome.Skipped, write.Body.Length);
                await write.Body.DisposeAsync().ConfigureAwait(false);
                ReleaseReservation(write.ReservedCapacityBytes);
            }
        }
    }

    private SegmentCacheWriteBehindSnapshot SnapshotUnderLock() => new(
        _budgetBytes,
        _reservedBytes,
        _peakReservedBytes,
        _queuedJobs,
        _activeJobs,
        _capacitySkips);

    private void PublishSnapshotUnderLock() =>
        _publishSnapshot?.Invoke(SnapshotUnderLock());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Retire();
        if (_worker.Wait(_disposeDrainTimeout))
        {
            _stop.Dispose();
            return;
        }

        _stop.Cancel();
        _ = _worker.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _stop,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
