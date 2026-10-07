namespace NzbWebDAV.Streams;

/// <summary>
/// Prefetch allowance for a multipart volume opened ahead of the reader: the shared
/// read-ahead window less every unread byte in front of the volume. It grows as the
/// reader advances, so all speculative volumes together stay within one window.
/// </summary>
internal sealed class SpeculativeReadAhead
{
    private ReadAheadCursor? _cursor;
    private long _start;
    private long _window;

    /// <summary>
    /// Bytes this volume may hold in flight. Unlimited until a combined stream schedules
    /// the volume ahead of its reader, and again once the reader reaches the volume.
    /// </summary>
    internal long AvailableBytes
    {
        get
        {
            if (Volatile.Read(ref _cursor) is not { } cursor) return long.MaxValue;
            var unreadInFront = _start - cursor.Position;
            return unreadInFront <= 0 ? long.MaxValue : Math.Max(0, _window - unreadInFront);
        }
    }

    /// <summary>Completes the next time the reader advances; null while unbound.</summary>
    internal Task? WhenReaderAdvances() => Volatile.Read(ref _cursor)?.WhenAdvanced();

    internal void Bind(ReadAheadCursor cursor, long start, long window)
    {
        _start = start;
        _window = window;
        Volatile.Write(ref _cursor, cursor);
    }
}

/// <summary>The reader position of a combined stream, observable by its prefetched parts.</summary>
internal sealed class ReadAheadCursor
{
    private long _position;
    private TaskCompletionSource? _advanced;

    internal long Position => Interlocked.Read(ref _position);

    internal void Advance(long position)
    {
        Interlocked.Exchange(ref _position, position);
        Interlocked.Exchange(ref _advanced, null)?.TrySetResult();
    }

    internal Task WhenAdvanced()
    {
        if (Volatile.Read(ref _advanced) is { } existing) return existing.Task;
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (Interlocked.CompareExchange(ref _advanced, created, null) ?? created).Task;
    }
}
