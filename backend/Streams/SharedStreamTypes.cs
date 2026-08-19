namespace NzbWebDAV.Streams;

internal sealed class NullAsyncDisposable : IAsyncDisposable
{
    public static readonly NullAsyncDisposable Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal enum SharedStreamEntryState
{
    Opening = 0,
    Ready = 1,
    Draining = 2,
    Disposing = 3,
    Disposed = 4,
}

public enum SharedStreamReapReason
{
    Grace,
    Failure,
    Shutdown,
}

public enum SharedStreamAttachMissReason
{
    BehindWindow,
    AheadOfFrontier,
    EntryUnusable,
    AtEntryCap,
    AtGlobalCap,
    SmallRangeNoEntry,
    Ineligible,
    NoCoveringEntry,
}

public delegate Task<Stream> SharedStreamFallbackFactory(long offset, CancellationToken cancellationToken);
