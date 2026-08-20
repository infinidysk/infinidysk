using System.Runtime.ExceptionServices;

namespace NzbWebDAV.Streams;

/// <summary>
/// Bounded chunk ring that holds decoded bytes for a shared Usenet stream region.
/// One lock guards every chunk access (append-copy in, read-copy out, return to pool)
/// and every state check so <see cref="ReleaseAll"/> cannot race a concurrent
/// <see cref="TryCopyAt"/> into a use-after-return.
/// </summary>
internal sealed class SharedStreamRingBuffer
{
    internal const int DefaultChunkSize = 1024 * 1024;
    internal const int LeadBytes = 4 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly ISegmentBufferPool _pool;
    private readonly int _chunkSize;
    private readonly long _ringSize;
    private readonly List<Chunk> _chunks = [];
    private readonly Dictionary<long, ReaderSlot> _readers = [];

    private long _tailStart;
    private long _frontier;
    private bool _complete;
    private Exception? _failure;
    private bool _released;
    private TaskCompletionSource _dataAvailable = NewTcs();

    public SharedStreamRingBuffer(
        long ringSizeBytes,
        long tailStart = 0,
        ISegmentBufferPool? pool = null,
        int? chunkSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tailStart);
        ArgumentOutOfRangeException.ThrowIfLessThan(ringSizeBytes, 1);
        _ringSize = ringSizeBytes;
        _tailStart = tailStart;
        _frontier = tailStart;
        _pool = pool ?? SharedStreamAccountingPool.Ring;
        _chunkSize = chunkSize ?? DefaultChunkSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(_chunkSize, 1);
    }

    internal int ChunkSize => _chunkSize;
    internal long RingSize => _ringSize;

    public long TailStart
    {
        get { lock (_lock) return _tailStart; }
    }

    public long Frontier
    {
        get { lock (_lock) return _frontier; }
    }

    public long RetainedBytes
    {
        get { lock (_lock) return RetainedBytesLocked(); }
    }

    public long RentedBytes
    {
        get { lock (_lock) return RentedBytesLocked(); }
    }

    public bool IsComplete
    {
        get { lock (_lock) return _complete; }
    }

    public bool IsFailed
    {
        get { lock (_lock) return _failure is not null; }
    }

    public bool IsReleased
    {
        get { lock (_lock) return _released; }
    }

    internal int ChunkCount
    {
        get { lock (_lock) return _chunks.Count; }
    }

    internal int ReaderCount
    {
        get { lock (_lock) return _readers.Count; }
    }

    internal int CountLaggingReaders(int leadBytes)
    {
        lock (_lock)
        {
            if (_readers.Count == 0 || leadBytes <= 0)
                return 0;

            var threshold = MaxCursorLocked() - leadBytes;
            var count = 0;
            foreach (var slot in _readers.Values)
            {
                if (slot.Cursor < threshold)
                    count++;
            }

            return count;
        }
    }

    public void RegisterReader(long readerId, long cursor)
    {
        lock (_lock)
        {
            _readers[readerId] = new ReaderSlot { Cursor = cursor };
        }
    }

    public void UnregisterReader(long readerId)
    {
        lock (_lock)
        {
            _readers.Remove(readerId);
        }
    }

    public void AdvanceCursor(long readerId, long cursor)
    {
        lock (_lock)
        {
            if (_readers.TryGetValue(readerId, out var slot))
                slot.Cursor = cursor;
        }
    }

    public long? GetMinCursor()
    {
        lock (_lock) return MinCursorLocked();
    }

    public long GetMaxCursor()
    {
        lock (_lock) return MaxCursorLocked();
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock)
        {
            if (_released || _complete || _failure is not null) return;
            CopyInLocked(data);
            var minCursor = MinCursorLocked();
            if (minCursor is { } min)
                EvictThroughLocked(min);
            SignalWaitersLocked();
        }
    }

    public void SetComplete()
    {
        lock (_lock)
        {
            if (_released || _complete) return;
            _complete = true;
            SignalWaitersLocked();
        }
    }

    public void SetFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_lock)
        {
            if (_released || _failure is not null) return;
            _failure = exception;
            SignalWaitersLocked();
        }
    }

    /// <summary>
    /// Copy bytes at the reader's cursor into <paramref name="dest"/>. All chunk
    /// access and state checks run under the ring lock.
    /// </summary>
    public RingReadResult TryCopyAt(long readerId, long cursor, Span<byte> dest)
    {
        lock (_lock)
        {
            if (_failure is not null)
            {
                if (_readers.TryGetValue(readerId, out var failedSlot))
                {
                    if (failedSlot.FailureDelivered)
                        return RingReadResult.Detached();
                    failedSlot.FailureDelivered = true;
                    return RingReadResult.Failed(_failure);
                }

                return RingReadResult.Failed(_failure);
            }

            if (_released)
                return RingReadResult.Released();

            if (!_readers.TryGetValue(readerId, out var slot))
                return RingReadResult.Detached();

            if (cursor < _tailStart)
                return RingReadResult.Evicted();

            if (cursor < _frontier)
            {
                var copied = CopyOutLocked(cursor, dest);
                slot.Cursor = cursor + copied;
                return RingReadResult.Copied(copied);
            }

            if (_complete)
                return RingReadResult.Copied(0);

            return RingReadResult.NeedWait();
        }
    }

    public async Task WaitForDataAsync(long readerId, long cursor, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_released || _failure is not null || _complete) return;
                if (!_readers.TryGetValue(readerId, out var slot)) return;
                if (cursor < _frontier || slot.Cursor < _frontier) return;
                wait = _dataAvailable.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void EvictThrough(long minCursor)
    {
        lock (_lock)
        {
            if (_released) return;
            EvictThroughLocked(minCursor);
        }
    }

    /// <summary>
    /// Advance <see cref="TailStart"/> and return readers whose cursor is now
    /// behind the window. Chunks are returned after the remaining min-cursor is
    /// recomputed so a boundary-sitting reader never loses its next chunk.
    /// </summary>
    public IReadOnlyList<long> ForceEvictBelow(long newTailStart)
    {
        lock (_lock)
        {
            if (_released || newTailStart <= _tailStart)
                return [];

            _tailStart = newTailStart;
            var evicted = new List<long>();
            foreach (var pair in _readers)
            {
                if (pair.Value.Cursor < _tailStart)
                    evicted.Add(pair.Key);
            }

            var minRemaining = MinCursorAmongLocked(cursor => cursor >= _tailStart);
            EvictThroughLocked(minRemaining ?? _tailStart);
            return evicted;
        }
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_released) return;
            _released = true;
            foreach (var chunk in _chunks)
                _pool.Return(chunk.Buffer);
            _chunks.Clear();
            SignalWaitersLocked();
        }
    }

    private void CopyInLocked(ReadOnlySpan<byte> data)
    {
        var remaining = data;
        while (!remaining.IsEmpty)
        {
            if (_chunks.Count == 0 || _chunks[^1].IsFull)
            {
                var buffer = _pool.Rent(_chunkSize);
                _chunks.Add(new Chunk(buffer, _chunkSize, _frontier));
            }

            var last = _chunks[^1];
            var copied = last.Append(remaining);
            _frontier += copied;
            remaining = remaining[copied..];
        }
    }

    private int CopyOutLocked(long cursor, Span<byte> dest)
    {
        var copied = 0;
        var offset = cursor;
        foreach (var chunk in _chunks)
        {
            if (copied == dest.Length || offset >= _frontier) break;
            if (offset >= chunk.End || offset < chunk.Start) continue;
            var from = (int)(offset - chunk.Start);
            var n = Math.Min(dest.Length - copied, chunk.Length - from);
            n = Math.Min(n, (int)Math.Min(int.MaxValue, _frontier - offset));
            if (n <= 0) continue;
            chunk.Buffer.AsSpan(from, n).CopyTo(dest.Slice(copied, n));
            copied += n;
            offset += n;
        }

        return copied;
    }

    /// <summary>
    /// Chunk <c>[a,b)</c> is evictable iff <c>b ≤ minCursor</c>: a reader sitting
    /// exactly on a chunk boundary keeps that next chunk.
    /// </summary>
    private void EvictThroughLocked(long minCursor)
    {
        while (_chunks.Count > 0 && _chunks[0].End <= minCursor)
        {
            var chunk = _chunks[0];
            _chunks.RemoveAt(0);
            if (chunk.End > _tailStart)
                _tailStart = chunk.End;
            _pool.Return(chunk.Buffer);
        }
    }

    private long? MinCursorLocked() => MinCursorAmongLocked(_ => true);

    private long? MinCursorAmongLocked(Func<long, bool> predicate)
    {
        long? min = null;
        foreach (var slot in _readers.Values)
        {
            if (!predicate(slot.Cursor)) continue;
            min = min is { } m ? Math.Min(m, slot.Cursor) : slot.Cursor;
        }

        return min;
    }

    private long MaxCursorLocked()
    {
        long max = _tailStart;
        var any = false;
        foreach (var slot in _readers.Values)
        {
            if (!any || slot.Cursor > max)
            {
                max = slot.Cursor;
                any = true;
            }
        }

        return any ? max : _frontier;
    }

    private long RetainedBytesLocked()
    {
        long total = 0;
        foreach (var chunk in _chunks)
            total += chunk.Length;
        return total;
    }

    private long RentedBytesLocked()
    {
        long total = 0;
        foreach (var chunk in _chunks)
            total += chunk.Buffer.Length;
        return total;
    }

    private void SignalWaitersLocked()
    {
        var prior = _dataAvailable;
        _dataAvailable = NewTcs();
        prior.TrySetResult();
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ReaderSlot
    {
        public long Cursor;
        public bool FailureDelivered;
    }

    private sealed class Chunk(byte[] buffer, int capacity, long start)
    {
        public byte[] Buffer { get; } = buffer;
        public int Capacity { get; } = capacity;
        public long Start { get; } = start;
        public int Length { get; private set; }
        public long End => Start + Length;
        public bool IsFull => Length >= Capacity;

        public int Append(ReadOnlySpan<byte> data)
        {
            var n = Math.Min(Capacity - Length, data.Length);
            data[..n].CopyTo(Buffer.AsSpan(Length, n));
            Length += n;
            return n;
        }
    }
}

internal enum RingReadKind
{
    Copied,
    NeedWait,
    Evicted,
    Failed,
    Released,
    Detached,
}

internal readonly struct RingReadResult
{
    public RingReadKind Kind { get; }
    public int Count { get; }
    public Exception? Exception { get; }

    private RingReadResult(RingReadKind kind, int count, Exception? exception)
    {
        Kind = kind;
        Count = count;
        Exception = exception;
    }

    public static RingReadResult Copied(int count) => new(RingReadKind.Copied, count, null);
    public static RingReadResult NeedWait() => new(RingReadKind.NeedWait, 0, null);
    public static RingReadResult Evicted() => new(RingReadKind.Evicted, 0, null);
    public static RingReadResult Failed(Exception exception) => new(RingReadKind.Failed, 0, exception);
    public static RingReadResult Released() => new(RingReadKind.Released, 0, null);
    public static RingReadResult Detached() => new(RingReadKind.Detached, 0, null);

    public Exception DispatchFailure()
    {
        var exception = Exception ?? new IOException("Shared stream failed.");
        ExceptionDispatchInfo.Capture(exception).Throw();
        return exception;
    }
}
