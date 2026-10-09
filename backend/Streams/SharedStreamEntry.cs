using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Logging;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.WebDav.Base;
using Serilog;

namespace NzbWebDAV.Streams;

/// <summary>
/// One shared upstream Usenet stream for a file region. Owns the pump, ring, grace
/// timer, and entry-scoped cancellation token. Attach is valid only in Ready/Draining.
/// </summary>
internal sealed class SharedStreamEntry : IAsyncDisposable
{
    private static readonly LogThrottle CircuitFailureThrottle = new();
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
    private SharedContentIdentity _contentIdentity;
    private readonly SharedContentIdentity? _reservedContentIdentity;
    private SharedStreamEntryState _state;
    private ITimer? _graceTimer;
    private TaskCompletionSource _pumpWakeup =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pumpTask;
    private Task? _disposeTask;
    private long _nextReaderId;
    private long _bytesPumped;
    private long _validatedThrough;
    private Exception? _validationFailure;
    private bool _validationClosed;
    private Task _validationTail = Task.CompletedTask;
    private TaskCompletionSource _validationProgress =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        ISegmentBufferPool? pool = null,
        SharedContentIdentity? contentIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(anchor);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        Path = path;
        Anchor = anchor;
        _validatedThrough = anchor;
        FileSize = fileSize;
        EntryId = Guid.NewGuid();
        _reservedContentIdentity = contentIdentity;
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
    internal Action<int>? OnForceEvictions { get; set; }

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
        try
        {
            if (_reservedContentIdentity is { } reservedIdentity && lease.ContentIdentity != reservedIdentity)
                throw new InvalidOperationException("Detached stream content changed before shared binding.");
            if (lease.ContentIdentity.FileSize != FileSize)
                throw new InvalidOperationException("Detached stream content size changed before shared binding.");
            lock (_lock)
            {
                if (_state != SharedStreamEntryState.Opening)
                    throw new InvalidOperationException($"Cannot bind shared entry in state {_state}.");
                _upstream = lease.Stream;
                _ownership = lease.Ownership;
                _davItem = lease.DavItem;
                _contentIdentity = lease.ContentIdentity;
            }
        }
        catch
        {
            try
            {
                lease.Stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                lease.Ownership.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            throw;
        }

        StartPump();
        lock (_lock)
            _state = SharedStreamEntryState.Ready;
        Log.Debug(
            "Shared stream entry {EntryId} started for {Path} at anchor {Anchor}",
            EntryId, Path, Anchor);
    }

    internal SharedContentIdentity ContentIdentity
    {
        get { lock (_lock) return _reservedContentIdentity ?? _contentIdentity; }
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

    /// <summary>Completes once the pump has validated every byte before the position.</summary>
    internal async ValueTask ValidateThroughAsync(long position, CancellationToken cancellationToken)
    {
        while (true)
        {
            var progress = Volatile.Read(ref _validationProgress).Task;
            if (position <= Interlocked.Read(ref _validatedThrough)) return;
            if (Volatile.Read(ref _validationFailure) is { } failure)
            {
                if (failure is OperationCanceledException)
                    throw new IOException("Shared stream closed before its delivered bytes were validated.", failure);
                ExceptionDispatchInfo.Throw(failure);
            }

            if (Volatile.Read(ref _validationClosed))
                throw new IOException("Shared stream closed before its delivered bytes were validated.");
            await progress.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Called by the pump right after a read, so the chain still references the article it read.
    private void TrackValidation(ValueTask validation, long through)
    {
        if (validation.IsCompletedSuccessfully && _validationTail.IsCompleted)
        {
            validation.GetAwaiter().GetResult();
            AdvanceValidated(through);
            return;
        }

        _validationTail = ObserveValidationAsync(_validationTail, validation.AsTask(), through);
    }

    // Chained so a failure is recorded only after every earlier read's validation settled.
    private async Task ObserveValidationAsync(Task previous, Task validation, long through)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await validation.ConfigureAwait(false);
            AdvanceValidated(through);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _validationFailure, ex, null);
            SignalValidation();
        }
    }

    private void AdvanceValidated(long through)
    {
        // Bytes past a failed validation are never vouched for.
        if (Volatile.Read(ref _validationFailure) is not null) return;
        var current = Interlocked.Read(ref _validatedThrough);
        while (current < through)
        {
            var seen = Interlocked.CompareExchange(ref _validatedThrough, through, current);
            if (seen == current) break;
            current = seen;
        }

        SignalValidation();
    }

    private void SignalValidation() =>
        Interlocked.Exchange(
                ref _validationProgress,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

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
        var scratch = SharedStreamAccountingPool.PumpScratch.Rent(_chunkSize);
        // The pump runs without the request's flow, so it traces as its own session keyed by EntryId.
        var traceRange = StreamTrace.Buffer?.RangeOpen(
            EntryId, Path, "PUMP", Anchor, null, FileSize, null, null, System.IO.Path.GetFileName(Path));
        using var traceScope = MultiProviderNntpClient.BeginStreamTraceRangeScope(traceRange);
        // Covers the first-article phase that runs before any buffered pipeline exists.
#pragma warning disable CA2000 // disposed in finally, before RangeEnd, so no sample trails the range
        var pumpSampler = traceRange is { } sampledRange
            ? new Timer(
                static state =>
                {
                    var (entry, range) = ((SharedStreamEntry, StreamTraceRangeContext))state!;
                    StreamTrace.TryPumpSample(range, entry.BytesPumped);
                },
                (this, sampledRange), TimeSpan.Zero, TimeSpan.FromSeconds(1))
            : null;
#pragma warning restore CA2000
        var traceEnd = ReadSession.EndReasonCode.Aborted;
        string? traceMessage = null;
        try
        {
            var upstream = _upstream
                ?? throw new InvalidOperationException("Shared stream pump started without an upstream.");
            using var pumpScope = SharedStreamPumpContext.Begin();
            using var fetchAttribution = FetchAttributionContext.Begin(System.IO.Path.GetFileName(Path));
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
                    // Logical EOF can precede a pending trailer, e.g. AES plaintext ending before its ciphertext.
                    TrackValidation(upstream.ValidateDeliveredAsync(ct), long.MaxValue);
                    _ring.SetComplete();
                    traceEnd = ReadSession.EndReasonCode.Completed;
                    return;
                }

                _ring.Append(scratch.AsSpan(0, read));
                var pumped = Interlocked.Add(ref _bytesPumped, read);
                TrackValidation(upstream.ValidateDeliveredAsync(ct), Anchor + pumped);
                SynchronousObserverInvoker.Invoke(
                    OnRingRetainedBytes,
                    _ring.RetainedBytes,
                    SynchronousObserverSource.SharedStreamRingRetainedBytes);
                MaybeEvict();
            }
        }
        catch (OperationCanceledException) when (_entryCts.IsCancellationRequested)
        {
            // Entry teardown cancelled the pump.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            traceEnd = ReadSession.EndReasonCode.Error;
            traceMessage = ex.GetType().Name;
            if (ex.TryGetCausingException(out CircuitAdmissionRejectedException? _))
            {
                if (CircuitFailureThrottle.ShouldLog("circuit-admission", TimeSpan.FromSeconds(30), out var suppressed))
                    Log.Warning(
                        "Shared stream deferred by provider circuit. EntryId: {EntryId} Path: {Path} SuppressedCount: {SuppressedCount}",
                        EntryId, Path, suppressed);
            }
            else if (ex.TryGetKnownErrorMessage(out var reason))
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
            SharedStreamAccountingPool.PumpScratch.Return(scratch);
            if (pumpSampler is not null) await pumpSampler.DisposeAsync().ConfigureAwait(false);
            if (traceRange is { } endedRange) EndPumpTrace(endedRange, traceEnd, traceMessage);
        }
    }

    // A clean EOF is final only once trailing validation settles, so defer Completed until then.
    private void EndPumpTrace(
        StreamTraceRangeContext range, ReadSession.EndReasonCode end, string? message)
    {
        if (end == ReadSession.EndReasonCode.Completed)
        {
            if (!_validationTail.IsCompleted)
            {
                _ = _validationTail.ContinueWith(
                    static (_, state) =>
                    {
                        var (entry, deferred) = ((SharedStreamEntry, StreamTraceRangeContext))state!;
                        entry.EndPumpTrace(deferred, ReadSession.EndReasonCode.Completed, null);
                    },
                    (this, range), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            if (Volatile.Read(ref _validationFailure) is { } failure)
                (end, message) = failure is OperationCanceledException
                    ? (ReadSession.EndReasonCode.Aborted, message)
                    : (ReadSession.EndReasonCode.Error, failure.GetType().Name);
        }

        StreamTrace.Buffer?.RangeEnd(EntryId, range, end, BytesPumped, message);
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

        SynchronousObserverInvoker.Invoke(
            OnRingRetainedBytes,
            _ring.RetainedBytes,
            SynchronousObserverSource.SharedStreamRingRetainedBytes);

        min = _ring.GetMinCursor();
        if (min is not { } pinning)
            return;
        var frontier = _ring.Frontier;
        if (frontier - pinning <= _ringSize)
            return;

        var newTail = frontier - _ringSize;
        if (newTail < Anchor)
            newTail = Anchor;
        var evicted = _ring.ForceEvictBelow(newTail);
        if (evicted.Count > 0)
        {
            SynchronousObserverInvoker.Invoke(
                OnForceEvictions,
                evicted.Count,
                SynchronousObserverSource.SharedStreamForceEvictions);
        }

        SynchronousObserverInvoker.Invoke(
            OnRingRetainedBytes,
            _ring.RetainedBytes,
            SynchronousObserverSource.SharedStreamRingRetainedBytes);
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

        // Pending validations observe the cancelled entry token; let them settle before closing.
        await _validationTail.ConfigureAwait(false);
        Volatile.Write(ref _validationClosed, true);
        SignalValidation();

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
