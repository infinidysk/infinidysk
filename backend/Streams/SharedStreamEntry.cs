using System.Diagnostics;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav.Base;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// One shared upstream Usenet stream for a file region. Owns the pump, ring, grace
/// timer, and entry-scoped cancellation token. Attach is valid only in Ready/Draining.
/// </summary>
internal sealed class SharedStreamEntry : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly object _disposeGate = new();
    private readonly SharedStreamRingBuffer _ring;
    private readonly CancellationTokenSource _entryCts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _grace;
    private readonly long _ringSize;
    private readonly int _leadBytes;
    private readonly int _chunkSize;
    private readonly Dictionary<long, SharedReaderStream> _readers = [];
    private readonly DateTimeOffset _createdAt;

    private Stream? _upstream;
    private IAsyncDisposable? _ownership;
    private DavItem? _davItem;
    private SharedStreamEntryState _state;
    private ITimer? _graceTimer;
    private TaskCompletionSource _pumpWakeup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pumpTask;
    private Task? _disposeTask;
    private long _nextReaderId;
    private long _bytesPumped;
    private SharedStreamReapReason _reapReason = SharedStreamReapReason.Grace;

    internal SharedStreamEntry(
        string path,
        long anchor,
        long fileSize,
        long ringSizeBytes,
        TimeSpan grace,
        CancellationToken registryRootToken,
        TimeProvider? timeProvider = null,
        int? chunkSize = null,
        int? leadBytes = null,
        ISegmentBufferPool? pool = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(anchor);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        Path = path;
        Anchor = anchor;
        FileSize = fileSize;
        EntryId = Guid.NewGuid();
        _ringSize = ringSizeBytes;
        _grace = grace < TimeSpan.Zero ? TimeSpan.Zero : grace;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chunkSize = chunkSize ?? SharedStreamRingBuffer.DefaultChunkSize;
        _leadBytes = leadBytes ?? SharedStreamRingBuffer.LeadBytes;
        _ring = new SharedStreamRingBuffer(ringSizeBytes, anchor, pool, _chunkSize);
        _entryCts = CancellationTokenSource.CreateLinkedTokenSource(registryRootToken);
        _createdAt = _timeProvider.GetUtcNow();
        _state = SharedStreamEntryState.Opening;
    }

    internal Guid EntryId { get; }
    internal string Path { get; }
    internal long Anchor { get; }
    internal long FileSize { get; }
    internal SharedStreamRingBuffer Ring => _ring;
    internal SharedStreamEntryState State
    {
        get { lock (_lock) return _state; }
    }
    internal long BytesPumped => Interlocked.Read(ref _bytesPumped);
    internal int AttachedReaderCount
    {
        get { lock (_lock) return _readers.Count; }
    }
    internal long RingSize => _ringSize;
    internal int LeadBytes => _leadBytes;
    internal DavItem? DavItem => _davItem;
    internal DateTimeOffset CreatedAt => _createdAt;
    internal SharedStreamReapReason ReapReason => _reapReason;
    internal TimeProvider TimeProvider => _timeProvider;
    internal CancellationToken EntryToken => _entryCts.Token;

    internal Action<SharedStreamEntry, SharedStreamReapReason>? OnReaped { get; set; }
    internal Action<long>? OnRingRetainedBytes { get; set; }

    internal bool IsAttachable
    {
        get
        {
            lock (_lock)
                return _state is SharedStreamEntryState.Ready or SharedStreamEntryState.Draining;
        }
    }

    internal void BindAndStart(DetachedStreamLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(lease.Stream);
        ArgumentNullException.ThrowIfNull(lease.Ownership);
        lock (_lock)
        {
            if (_state != SharedStreamEntryState.Opening)
                throw new InvalidOperationException($"Cannot bind shared entry in state {_state}.");
            _upstream = lease.Stream;
            _ownership = lease.Ownership;
            _davItem = lease.DavItem;
        }

        StartPump();
        lock (_lock)
            _state = SharedStreamEntryState.Ready;
        Log.Debug(
            "Shared stream entry {EntryId} started for {Path} at anchor {Anchor}",
            EntryId, Path, Anchor);
    }

    internal void AbandonOpening()
    {
        lock (_lock)
            _state = SharedStreamEntryState.Disposed;
        _ring.ReleaseAll();
        try { _entryCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _entryCts.Dispose();
    }

    internal SharedReaderStream? TryAttach(
        long startOffset,
        SharedStreamFallbackFactory fallbackFactory,
        out SharedStreamAttachMissReason? missReason)
    {
        ArgumentNullException.ThrowIfNull(fallbackFactory);
        lock (_lock)
        {
            if (_state is not (SharedStreamEntryState.Ready or SharedStreamEntryState.Draining))
            {
                missReason = SharedStreamAttachMissReason.EntryUnusable;
                return null;
            }

            var tail = _ring.TailStart;
            var frontier = _ring.Frontier;
            if (startOffset < tail)
            {
                missReason = SharedStreamAttachMissReason.BehindWindow;
                return null;
            }

            if (startOffset > frontier + _ringSize)
            {
                missReason = SharedStreamAttachMissReason.AheadOfFrontier;
                return null;
            }

            if (_state == SharedStreamEntryState.Draining)
            {
                CancelGraceLocked();
                _state = SharedStreamEntryState.Ready;
            }

            var readerId = ++_nextReaderId;
            var reader = new SharedReaderStream(
                this, _ring, readerId, startOffset, FileSize, _ringSize, fallbackFactory);
            _readers[readerId] = reader;
            _ring.RegisterReader(readerId, startOffset);
            SignalPumpLocked();
            missReason = null;
            return reader;
        }
    }

    internal void Detach(long readerId)
    {
        var dispose = false;
        lock (_lock)
        {
            if (!_readers.Remove(readerId))
            {
                SignalPumpLocked();
                return;
            }

            _ring.UnregisterReader(readerId);
            if (_ring.IsFailed || _state >= SharedStreamEntryState.Disposing)
            {
                _reapReason = SharedStreamReapReason.Failure;
                if (_state < SharedStreamEntryState.Disposing)
                    _state = SharedStreamEntryState.Disposing;
                dispose = true;
            }
            else if (_state == SharedStreamEntryState.Ready && _readers.Count == 0)
            {
                _state = SharedStreamEntryState.Draining;
                StartGraceLocked();
            }

            SignalPumpLocked();
        }

        if (dispose)
            _ = EnsureDisposeAsync();
    }

    internal void NotifyCursorAdvanced(long readerId, long cursor)
    {
        _ring.AdvanceCursor(readerId, cursor);
        MaybeEvict();
        lock (_lock)
            SignalPumpLocked();
    }

    public ValueTask DisposeAsync() => new(EnsureDisposeAsync());

    internal Task EnsureDisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            lock (_lock)
            {
                if (_state < SharedStreamEntryState.Disposing)
                    _state = SharedStreamEntryState.Disposing;
            }

            _disposeTask = DisposeCoreAsync();
            return _disposeTask;
        }
    }

    private void StartPump()
    {
        var suppressed = ExecutionContext.SuppressFlow();
        try
        {
            _pumpTask = Task.Run(PumpLoopAsync);
        }
        finally
        {
            suppressed.Undo();
        }
    }

    private async Task PumpLoopAsync()
    {
        var scratch = SharedArrayPoolAdapter.Instance.Rent(_chunkSize);
        try
        {
            var upstream = _upstream
                ?? throw new InvalidOperationException("Shared stream pump started without an upstream.");
            if (Anchor > 0 && upstream.CanSeek)
                upstream.Seek(Anchor, SeekOrigin.Begin);

            var ct = _entryCts.Token;
            while (!ct.IsCancellationRequested)
            {
                await WaitForPumpSpaceAsync(ct).ConfigureAwait(false);
                var read = await upstream.ReadAsync(scratch.AsMemory(0, _chunkSize), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    _ring.SetComplete();
                    return;
                }

                _ring.Append(scratch.AsSpan(0, read));
                Interlocked.Add(ref _bytesPumped, read);
                OnRingRetainedBytes?.Invoke(_ring.RetainedBytes);
                MaybeEvict();
            }
        }
        catch (OperationCanceledException) when (_entryCts.IsCancellationRequested)
        {
            // Entry teardown cancelled the pump.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex.TryGetKnownErrorMessage(out var reason))
            {
                Log.Warning(
                    "Shared stream pump failed. EntryId: {EntryId} Path: {Path} Anchor: {Anchor} Reason: {Reason}",
                    EntryId, Path, Anchor, reason);
                Log.Debug(ex, "Shared stream pump known failure stack. EntryId: {EntryId}", EntryId);
            }
            else
            {
                Log.Error(
                    ex,
                    "Shared stream pump failed. EntryId: {EntryId} Path: {Path} Anchor: {Anchor}",
                    EntryId, Path, Anchor);
            }

            _ring.SetFailure(ex);
            lock (_lock)
            {
                _reapReason = SharedStreamReapReason.Failure;
                if (_state < SharedStreamEntryState.Disposing)
                    _state = SharedStreamEntryState.Disposing;
            }

            // Never await EnsureDisposeAsync on the pump task: teardown joins the pump.
            _ = Task.Run(EnsureDisposeAsync);
        }
        finally
        {
            SharedArrayPoolAdapter.Instance.Return(scratch);
        }
    }

    private async Task WaitForPumpSpaceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_lock)
            {
                if (!ShouldPausePumpLocked())
                    return;
                wait = _pumpWakeup.Task;
                if (!ShouldPausePumpLocked())
                    return;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ShouldPausePumpLocked()
    {
        if (_state >= SharedStreamEntryState.Disposing)
            return true;
        if (_readers.Count == 0)
            return true;
        return _ring.Frontier - _ring.GetMaxCursor() >= _leadBytes;
    }

    private void MaybeEvict()
    {
        var min = _ring.GetMinCursor();
        if (min is { } minCursor)
            _ring.EvictThrough(minCursor);

        min = _ring.GetMinCursor();
        if (min is not { } pinning)
            return;
        var frontier = _ring.Frontier;
        if (frontier - pinning <= _ringSize)
            return;

        var newTail = frontier - _ringSize;
        if (newTail < Anchor)
            newTail = Anchor;
        _ring.ForceEvictBelow(newTail);
    }

    private void StartGraceLocked()
    {
        CancelGraceLocked();
        _graceTimer = _timeProvider.CreateTimer(
            static state => ((SharedStreamEntry)state!).OnGraceTimer(),
            this,
            _grace,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelGraceLocked()
    {
        _graceTimer?.Dispose();
        _graceTimer = null;
    }

    private void OnGraceTimer()
    {
        lock (_lock)
        {
            if (_state != SharedStreamEntryState.Draining)
                return;
            _reapReason = SharedStreamReapReason.Grace;
            _state = SharedStreamEntryState.Disposing;
        }

        _ = EnsureDisposeAsync();
    }

    private void SignalPumpLocked()
    {
        var prior = _pumpWakeup;
        _pumpWakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        prior.TrySetResult();
    }

    private async Task DisposeCoreAsync()
    {
        // 1. Signal waiters so parked readers cannot hang. Failure already woke
        // them via SetFailure; otherwise complete so WaitForDataAsync returns.
        if (!_ring.IsFailed)
            _ring.SetComplete();
        lock (_lock)
        {
            if (_state < SharedStreamEntryState.Disposing)
                _state = SharedStreamEntryState.Disposing;
            CancelGraceLocked();
            SignalPumpLocked();
        }

        // 2. Cancel the unlinked entry token so in-flight upstream reads unwind.
        try { await _entryCts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        // 3. Join the pump before disposing the upstream it reads.
        if (_pumpTask is { } pump)
        {
            try { await pump.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }

        // 4. Dispose upstream (joins MultiSegmentStream lease/pipe teardown).
        if (_upstream is { } upstream)
        {
            try { await upstream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug(ex, "Shared stream upstream dispose failed. EntryId: {EntryId}", EntryId);
            }
        }

        // 5. Ownership handle AFTER the upstream (semaphore still valid for in-flight fetches).
        if (_ownership is { } ownership)
        {
            try { await ownership.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug(ex, "Shared stream ownership dispose failed. EntryId: {EntryId}", EntryId);
            }
        }

        // 6. Return ring chunks last.
        _ring.ReleaseAll();
        _entryCts.Dispose();

        lock (_lock)
            _state = SharedStreamEntryState.Disposed;

        try { OnReaped?.Invoke(this, _reapReason); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Debug(ex, "Shared stream OnReaped callback failed. EntryId: {EntryId}", EntryId);
        }

        Log.Debug(
            "Shared stream entry {EntryId} reaped ({Reason}) path {Path} anchor {Anchor} bytesPumped {BytesPumped}",
            EntryId, _reapReason, Path, Anchor, BytesPumped);
    }
}
