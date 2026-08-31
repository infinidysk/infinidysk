using System.Diagnostics;
using System.Runtime.ExceptionServices;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Owns an unbuffered first-segment head and a one-shot buffered remainder factory.
/// Remainder construction starts after the first positive requested read (when eager)
/// or after the head is disposed at EOF (legacy lazy / empty head), and is never
/// awaited before that first read returns.
/// </summary>
internal sealed class FirstSegmentHandoffStream : FastReadOnlyNonSeekableStream
{
    private Stream? _head;
    private readonly Func<Stream>? _remainderFactory;
    private readonly bool _startRemainderAfterFirstRead;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _disposeGate = new();

    private Task<Stream>? _remainderTask;
    private Stream? _remainder;
    private Task? _disposeTask;
    private long _position;
    private int _remainderStarted;
    private int _disposed;

    internal FirstSegmentHandoffStream(
        Stream head,
        Func<Stream>? remainderFactory,
        bool startRemainderAfterFirstRead,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(head);
        _remainderFactory = remainderFactory;
        _startRemainderAfterFirstRead =
            remainderFactory is not null && startRemainderAfterFirstRead;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _head = head;
    }

    public override long Position
    {
        get => Interlocked.Read(ref _position);
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        _head?.Flush();
        _remainder?.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_head is { } head)
            return head.FlushAsync(cancellationToken);
        if (_remainder is { } remainder)
            return remainder.FlushAsync(cancellationToken);
        return Task.CompletedTask;
    }

    internal bool RemainderStartedForTests => Volatile.Read(ref _remainderStarted) != 0;

    private void StartRemainderOnce()
    {
        var factory = _remainderFactory;
        if (factory is null || Volatile.Read(ref _disposed) != 0)
            return;
        if (Interlocked.CompareExchange(ref _remainderStarted, 1, 0) != 0)
            return;

        // Yield so the current positive head read can return without waiting for
        // remainder construction. ExecutionContext still flows to the continuation.
        _remainderTask = CreateRemainderAfterYieldAsync(factory);
        FirstByteTrace.TryRecord(FirstByteTrace.HandoffStarted);
    }

    private async Task<Stream> CreateRemainderAfterYieldAsync(Func<Stream> factory)
    {
        await Task.Yield();
        _lifetimeCts.Token.ThrowIfCancellationRequested();
        return factory();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!await _readGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Concurrent ReadAsync calls are not supported by this stream.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token);
            var readToken = readCts.Token;

            while (true)
            {
                if (_head is not null)
                {
                    var read = await _head.ReadAsync(buffer, readToken).ConfigureAwait(false);
                    if (read > 0)
                    {
                        Interlocked.Add(ref _position, read);
                        if (_startRemainderAfterFirstRead)
                            StartRemainderOnce();
                        return read;
                    }

                    // Dispose the head before an EOF-only start so the first BODY
                    // completion/permit can release before remainder admission.
                    var completedHead = _head;
                    _head = null;
                    await completedHead.DisposeAsync().ConfigureAwait(false);

                    if (_remainderFactory is null)
                        return 0;

                    StartRemainderOnce();
                }

                if (_remainderFactory is null && _remainder is null)
                    return 0;

                if (_remainder is null)
                {
                    var pending = _remainderTask
                        ?? throw new InvalidOperationException(
                            "The remainder was required but was not started.");

                    var waitStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        // Caller cancellation stops this wait but does not abandon the
                        // owned task. A later read may resume; lifetime cancellation
                        // is owned here.
                        _remainder = await pending.WaitAsync(readToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException
                        && exception is not OutOfMemoryException)
                    {
                        FirstByteTrace.TryRecord(FirstByteTrace.RemainderFactoryFailed);
                        throw;
                    }
                    finally
                    {
                        FirstByteTrace.TryRecord(
                            FirstByteTrace.RemainderWait,
                            elapsed: Stopwatch.GetElapsedTime(waitStarted));
                    }

                    FirstByteTrace.TryRecord(FirstByteTrace.HandoffActivated);
                }

                var remainderRead = await _remainder
                    .ReadAsync(buffer, readToken)
                    .ConfigureAwait(false);
                Interlocked.Add(ref _position, remainderRead);
                return remainderRead;
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _ = EnsureDisposeAsync();

        // Must be the protected overload: the parameterless Stream.Dispose() routes
        // back through Close() into this method and recurses until the stack overflows.
        base.Dispose(disposing);
    }

#pragma warning disable CA2215 // base.DisposeAsync() would route through Close()/Dispose(true) back into EnsureDisposeAsync
    public override async ValueTask DisposeAsync()
    {
        await EnsureDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
#pragma warning restore CA2215

    private Task EnsureDisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
                return _disposeTask;

            Volatile.Write(ref _disposed, 1);
            try
            {
#pragma warning disable CA1849 // sync Dispose starts this path; Cancel must run before DisposeCoreAsync is scheduled
                _lifetimeCts.Cancel();
#pragma warning restore CA1849
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            _disposeTask = DisposeCoreAsync();
            _ = _disposeTask.ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return _disposeTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        ExceptionDispatchInfo? firstFailure = null;
        await _readGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var head = _head;
            _head = null;
            await DisposeOwnedAsync(head).ConfigureAwait(false);

            var remainder = _remainder;
            _remainder = null;
            if (remainder is not null)
            {
                await DisposeOwnedAsync(remainder).ConfigureAwait(false);
            }
            else if (_remainderTask is { } pending)
            {
                try
                {
                    var created = await pending.ConfigureAwait(false);
                    await DisposeOwnedAsync(created).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    // Expected teardown cancellation; task is observed.
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Factory faults are observed here so they cannot become
                    // unobserved, but teardown should not replace a head-dispose
                    // failure or fail solely because ReadAsync already surfaced them.
                    _ = exception;
                }
            }
        }
        finally
        {
            _readGate.Release();
            _readGate.Dispose();
            _lifetimeCts.Dispose();
        }

        firstFailure?.Throw();
        return;

        async ValueTask DisposeOwnedAsync(Stream? stream)
        {
            if (stream is null)
                return;
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }
}
