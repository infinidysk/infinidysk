using System.Collections.Concurrent;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class SharedAttachResult
{
    public required Stream Stream { get; init; }
    public DavItem? DavItem { get; init; }
}

/// <summary>
/// Path-keyed registry of shared Usenet stream region entries. Caps and eligibility
/// are read from config on every decision so settings take effect without restart.
/// </summary>
public sealed class SharedStreamRegistry : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, List<SharedStreamEntry>> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly ConfigManager _config;
    private readonly ConcurrentReadTracker _tracker;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _rootCts = new();
    private int _disposed;

    public SharedStreamRegistry(
        ConfigManager config,
        ConcurrentReadTracker tracker,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _tracker = tracker;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int LiveEntryCount
    {
        get
        {
            lock (_gate)
                return _entries.Values.Sum(list => list.Count);
        }
    }

    public static string NormalizePath(string path) => "/" + path.TrimStart('/');

    public async Task<SharedAttachResult?> TryAttachAsync(
        string path,
        long startOffset,
        long? endOffset,
        long fileSize,
        IDetachedStreamSource source,
        SharedStreamFallbackFactory privateFallbackFactory,
        CancellationToken readerCt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(privateFallbackFactory);
        readerCt.ThrowIfCancellationRequested();

        path = NormalizePath(path);

        if (Volatile.Read(ref _disposed) != 0 || !_config.IsSharedStreamsEnabled())
        {
            _tracker.RecordSharedAttachMiss(SharedStreamAttachMissReason.Ineligible);
            return null;
        }

        if (startOffset < 0 || fileSize < 0)
        {
            _tracker.RecordSharedAttachMiss(SharedStreamAttachMissReason.Ineligible);
            return null;
        }

        if (TryAttachExisting(path, startOffset, privateFallbackFactory, out var hit, out var windowMiss))
            return hit;

        if (HasOpeningEntry(path))
        {
            _tracker.RecordSharedAttachMiss(SharedStreamAttachMissReason.EntryUnusable);
            return null;
        }

        var closedRange = endOffset is { } end && end >= startOffset;
        var rangeBytes = closedRange
            ? endOffset!.Value - startOffset + 1
            : Math.Max(0, fileSize - startOffset);
        var createEligible = !closedRange || rangeBytes > _config.GetSharedStreamsSmallRangeMaxBytes();
        if (!createEligible)
        {
            _tracker.RecordSharedAttachMiss(
                windowMiss is SharedStreamAttachMissReason.BehindWindow
                    or SharedStreamAttachMissReason.AheadOfFrontier
                    ? windowMiss.Value
                    : SharedStreamAttachMissReason.SmallRangeNoEntry);
            return null;
        }

        readerCt.ThrowIfCancellationRequested();
        var reserved = TryReserveOpening(path, startOffset, fileSize, out var capMiss);
        if (reserved is null)
        {
            _tracker.RecordSharedAttachMiss(capMiss ?? SharedStreamAttachMissReason.AtEntryCap);
            return null;
        }

        try
        {
            readerCt.ThrowIfCancellationRequested();
            var lease = await source.GetDetachedReadableStreamAsync(reserved.EntryToken)
                .ConfigureAwait(false);
            reserved.BindAndStart(lease);
        }
        catch
        {
            ForgetReservation(path, reserved);
            reserved.AbandonOpening();
            throw;
        }

        _tracker.RecordSharedAttachMiss(windowMiss ?? SharedStreamAttachMissReason.NoCoveringEntry);
        _tracker.RecordSharedEntryCreated();

        var reader = reserved.TryAttach(startOffset, privateFallbackFactory, out _);
        if (reader is null)
            return null;

        _tracker.RecordSharedReadersServed(1);
        return new SharedAttachResult { Stream = reader, DavItem = reserved.DavItem };
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _entries.IsEmpty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { await _rootCts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        List<SharedStreamEntry> all;
        lock (_gate)
        {
            all = _entries.Values.SelectMany(static list => list).ToList();
            _entries.Clear();
        }

        foreach (var entry in all)
        {
            entry.OnReaped = null;
            entry.OnRingRetainedBytes = null;
            entry.OnForceEvictions = null;
            try { await entry.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug(ex, "Shared stream entry dispose failed during registry shutdown");
            }
        }

        _rootCts.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private bool TryAttachExisting(
        string path,
        long startOffset,
        SharedStreamFallbackFactory fallbackFactory,
        out SharedAttachResult? result,
        out SharedStreamAttachMissReason? windowMiss)
    {
        result = null;
        windowMiss = null;
        SharedStreamEntry? best = null;
        var bestDistance = long.MaxValue;

        foreach (var entry in SnapshotPath(path))
        {
            if (entry.State == SharedStreamEntryState.Opening)
                continue;
            if (!entry.IsAttachable)
            {
                windowMiss = SharedStreamAttachMissReason.EntryUnusable;
                continue;
            }

            var tail = entry.Ring.TailStart;
            var frontier = entry.Ring.Frontier;
            if (startOffset < tail)
            {
                windowMiss = SharedStreamAttachMissReason.BehindWindow;
                continue;
            }

            if (startOffset > frontier + entry.RingSize)
            {
                windowMiss = SharedStreamAttachMissReason.AheadOfFrontier;
                continue;
            }

            var distance = startOffset >= frontier ? startOffset - frontier : frontier - startOffset;
            if (distance < bestDistance)
            {
                best = entry;
                bestDistance = distance;
            }
        }

        if (best is null)
            return false;

        var reader = best.TryAttach(startOffset, fallbackFactory, out var miss);
        if (reader is null)
        {
            windowMiss = miss ?? SharedStreamAttachMissReason.EntryUnusable;
            return false;
        }

        _tracker.RecordSharedAttachHit();
        result = new SharedAttachResult { Stream = reader, DavItem = best.DavItem };
        return true;
    }

    private bool HasOpeningEntry(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(path, out var list)
                && list.Any(entry => entry.State == SharedStreamEntryState.Opening);
        }
    }

    private SharedStreamEntry? TryReserveOpening(
        string path,
        long startOffset,
        long fileSize,
        out SharedStreamAttachMissReason? capMiss)
    {
        capMiss = null;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                capMiss = SharedStreamAttachMissReason.Ineligible;
                return null;
            }

            var list = _entries.GetOrAdd(path, static _ => []);
            if (list.Any(entry => entry.State == SharedStreamEntryState.Opening))
            {
                capMiss = SharedStreamAttachMissReason.EntryUnusable;
                return null;
            }

            var live = _entries.Values.Sum(static entries => entries.Count);
            if (live >= _config.GetSharedStreamsMaxEntries())
            {
                capMiss = SharedStreamAttachMissReason.AtGlobalCap;
                return null;
            }

            if (list.Count >= _config.GetSharedStreamsMaxEntriesPerFile())
            {
                capMiss = SharedStreamAttachMissReason.AtEntryCap;
                return null;
            }

            var entry = new SharedStreamEntry(
                path,
                startOffset,
                fileSize,
                _config.GetSharedStreamsRingBytes(),
                TimeSpan.FromSeconds(_config.GetSharedStreamsGraceSeconds()),
                _rootCts.Token,
                _timeProvider);
            entry.OnReaped = HandleReaped;
            entry.OnRingRetainedBytes = _ => PublishRetainedBytes();
            entry.OnForceEvictions = _tracker.RecordSharedReaderEvictions;
            list.Add(entry);
            return entry;
        }
    }

    private void ForgetReservation(string path, SharedStreamEntry entry)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var list))
                return;
            if (!list.Remove(entry))
                return;
            if (list.Count == 0)
                _entries.TryRemove(path, out _);
        }
    }

    private void HandleReaped(SharedStreamEntry entry, SharedStreamReapReason reason)
    {
        ForgetReservation(entry.Path, entry);
        var lifetimeMs = Math.Max(
            0,
            (long)(_timeProvider.GetUtcNow() - entry.CreatedAt).TotalMilliseconds);
        _tracker.RecordSharedEntryReaped(reason, entry.BytesPumped, lifetimeMs);
        PublishRetainedBytes();
    }

    private void PublishRetainedBytes()
    {
        long logical = 0;
        long live = 0;
        long ready = 0;
        long draining = 0;
        long lagging = 0;
        lock (_gate)
        {
            foreach (var list in _entries.Values)
            {
                foreach (var entry in list)
                {
                    live++;
                    var state = entry.State;
                    if (state == SharedStreamEntryState.Ready)
                        ready++;
                    else if (state == SharedStreamEntryState.Draining)
                        draining++;
                    logical += entry.Ring.RetainedBytes;
                    lagging += entry.Ring.CountLaggingReaders(entry.LeadBytes);
                }
            }
        }

        _tracker.UpdateSharedRingLogicalBytes(logical);
        _tracker.UpdateSharedStreamCensus(live, ready, draining, lagging);
    }

    private List<SharedStreamEntry> SnapshotPath(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(path, out var list)
                ? list.ToList()
                : [];
        }
    }
}
