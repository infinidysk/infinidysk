namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Process-lifetime segment-cache counters and the active wrapper generation's gauges.
/// Cache code records domain events; Prometheus and support-pack code read snapshots.
/// </summary>
public sealed class SegmentCacheStatistics
{
    private long _hits;
    private long _misses;
    private long _lookupUnavailable;
    private long _bytesServed;
    private long _batchBypassRequests;
    private long _batchBypassArticles;
    private long _writeAttempts;
    private long _writeCommits;
    private long _writeSkipped;
    private long _writeFailures;
    private long _readFailures;
    private long _evictions;
    private long _bytesEvicted;
    private long _temporaryFilesCleaned;
    private long _nextGenerationId;

    private readonly Lock _gaugeLock = new();
    private GaugeState _gauges;

    internal SegmentCacheGeneration BeginGeneration(bool enabled, long maxBytes)
    {
        var id = Interlocked.Increment(ref _nextGenerationId);
        var effectiveMax = enabled ? maxBytes : 0L;
        var generation = new SegmentCacheGeneration(this, id, enabled, effectiveMax);
        lock (_gaugeLock)
        {
            _gauges = new GaugeState(
                id,
                enabled,
                CatalogReady: false,
                CatalogLoadDurationMs: null,
                Entries: 0,
                CurrentBytes: 0,
                MaxBytes: effectiveMax,
                Writer: null);
        }

        return generation;
    }

    public void RecordHit(long bytes)
    {
        Interlocked.Increment(ref _hits);
        Interlocked.Add(ref _bytesServed, bytes);
    }

    public void RecordMiss() => Interlocked.Increment(ref _misses);

    public void RecordLookupUnavailable() => Interlocked.Increment(ref _lookupUnavailable);

    public void RecordBatchBypass(int articleCount)
    {
        Interlocked.Increment(ref _batchBypassRequests);
        Interlocked.Add(ref _batchBypassArticles, articleCount);
    }

    public void RecordReadFailure() => Interlocked.Increment(ref _readFailures);

    public void RecordEviction(long entries, long bytes)
    {
        Interlocked.Add(ref _evictions, entries);
        Interlocked.Add(ref _bytesEvicted, bytes);
    }

    public void RecordTemporaryFileCleaned() => Interlocked.Increment(ref _temporaryFilesCleaned);

    internal SegmentCacheWriteAttempt BeginWriteAttempt()
    {
        Interlocked.Increment(ref _writeAttempts);
        return new SegmentCacheWriteAttempt(CompleteWrite);
    }

    public SegmentCacheSnapshot GetSnapshot()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var lookupUnavailable = Interlocked.Read(ref _lookupUnavailable);
        var bytesServed = Interlocked.Read(ref _bytesServed);
        var batchBypassRequests = Interlocked.Read(ref _batchBypassRequests);
        var batchBypassArticles = Interlocked.Read(ref _batchBypassArticles);
        var writeAttempts = Interlocked.Read(ref _writeAttempts);
        var writeCommits = Interlocked.Read(ref _writeCommits);
        var writeSkipped = Interlocked.Read(ref _writeSkipped);
        var writeFailures = Interlocked.Read(ref _writeFailures);
        var readFailures = Interlocked.Read(ref _readFailures);
        var evictions = Interlocked.Read(ref _evictions);
        var bytesEvicted = Interlocked.Read(ref _bytesEvicted);
        var temporaryFilesCleaned = Interlocked.Read(ref _temporaryFilesCleaned);

        GaugeState gauges;
        lock (_gaugeLock)
            gauges = _gauges;

        return new SegmentCacheSnapshot(
            gauges.Enabled,
            gauges.CatalogReady,
            gauges.CatalogLoadDurationMs,
            gauges.Entries,
            gauges.CurrentBytes,
            gauges.MaxBytes,
            hits,
            misses,
            lookupUnavailable,
            bytesServed,
            batchBypassRequests,
            batchBypassArticles,
            writeAttempts,
            writeCommits,
            writeSkipped,
            writeFailures,
            readFailures,
            evictions,
            bytesEvicted,
            temporaryFilesCleaned,
            QueuedWriteBytes: gauges.Writer?.ReservedBytes,
                PeakQueuedWriteBytes: gauges.Writer?.PeakReservedBytes);
    }

    internal SegmentCacheWriteBehindSnapshot? GetWriterSnapshot()
    {
        lock (_gaugeLock)
            return _gauges.Writer;
    }

    internal void SetCatalog(long generationId, bool ready, long? durationMs, long entries, long currentBytes)
    {
        lock (_gaugeLock)
        {
            if (_gauges.GenerationId != generationId) return;
            _gauges = _gauges with
            {
                CatalogReady = ready,
                CatalogLoadDurationMs = durationMs,
                Entries = entries,
                CurrentBytes = currentBytes,
            };
        }
    }

    internal void SetIndex(long generationId, long entries, long currentBytes)
    {
        lock (_gaugeLock)
        {
            if (_gauges.GenerationId != generationId) return;
            _gauges = _gauges with { Entries = entries, CurrentBytes = currentBytes };
        }
    }

    internal void SetWriter(long generationId, SegmentCacheWriteBehindSnapshot snapshot)
    {
        lock (_gaugeLock)
        {
            if (_gauges.GenerationId != generationId) return;
            _gauges = _gauges with { Writer = snapshot };
        }
    }

    private void CompleteWrite(SegmentCacheWriteOutcome outcome, long _)
    {
        switch (outcome)
        {
            case SegmentCacheWriteOutcome.Committed:
                Interlocked.Increment(ref _writeCommits);
                break;
            case SegmentCacheWriteOutcome.Skipped:
                Interlocked.Increment(ref _writeSkipped);
                break;
            case SegmentCacheWriteOutcome.Failed:
                Interlocked.Increment(ref _writeFailures);
                break;
        }
    }

    private readonly record struct GaugeState(
        long GenerationId,
        bool Enabled,
        bool CatalogReady,
        long? CatalogLoadDurationMs,
        long Entries,
        long CurrentBytes,
        long MaxBytes,
        SegmentCacheWriteBehindSnapshot? Writer);
}

public sealed record SegmentCacheSnapshot(
    bool Enabled,
    bool CatalogReady,
    long? CatalogLoadDurationMs,
    long Entries,
    long CurrentBytes,
    long MaxBytes,
    long Hits,
    long Misses,
    long LookupUnavailable,
    long BytesServed,
    long BatchBypassRequests,
    long BatchBypassArticles,
    long WriteAttempts,
    long WriteCommits,
    long WriteSkipped,
    long WriteFailures,
    long ReadFailures,
    long Evictions,
    long BytesEvicted,
    long TemporaryFilesCleaned,
    long? QueuedWriteBytes,
    long? PeakQueuedWriteBytes);

internal sealed class SegmentCacheGeneration
{
    private readonly SegmentCacheStatistics _owner;

    internal SegmentCacheGeneration(SegmentCacheStatistics owner, long id, bool enabled, long maxBytes)
    {
        _owner = owner;
        Id = id;
        Enabled = enabled;
        MaxBytes = maxBytes;
    }

    internal long Id { get; }
    internal bool Enabled { get; }
    internal long MaxBytes { get; }

    internal void SetCatalogReady(long durationMs, long entries, long currentBytes) =>
        _owner.SetCatalog(Id, ready: true, durationMs, entries, currentBytes);

    internal void SetIndex(long entries, long currentBytes) =>
        _owner.SetIndex(Id, entries, currentBytes);

    internal void SetWriterSnapshot(SegmentCacheWriteBehindSnapshot snapshot) =>
        _owner.SetWriter(Id, snapshot);
}

internal enum SegmentCacheWriteOutcome
{
    Committed,
    Skipped,
    Failed,
}

internal sealed class SegmentCacheWriteAttempt(Action<SegmentCacheWriteOutcome, long> complete)
{
    private int _completed;

    public void Complete(SegmentCacheWriteOutcome outcome, long bytes)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            complete(outcome, bytes);
    }
}
