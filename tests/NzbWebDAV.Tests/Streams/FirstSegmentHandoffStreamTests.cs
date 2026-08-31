using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Streams;

public class FirstSegmentHandoffStreamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadAsync_FirstPositiveHeadReadReturnsBeforeHeadEof()
    {
        var head = new StagedBodyStream([], [0x41], [0x42, 0x43]);
        await using var stream = new FirstSegmentHandoffStream(
            head, remainderFactory: null, RemainderStartPolicy.None, CancellationToken.None);

        var buffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(buffer));
        Assert.Equal(0x41, buffer[0]);
        Assert.True(head.TailGateClosed);
        Assert.False(head.TailReadStarted.IsCompleted);
    }

    [Fact]
    public async Task ReadAsync_FirstPositiveHeadReadStartsRemainderBeforeHeadEof()
    {
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryStarted = NewGate();
        Stream? remainder = null;
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                factoryStarted.TrySetResult();
                remainder = new MemoryStream([0x5A], writable: false);
                return remainder;
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await factoryStarted.Task.WaitAsync(Timeout);
        Assert.True(head.TailGateClosed);
        Assert.True(stream.RemainderScheduledForTests);
    }

    [Fact]
    public async Task ReadAsync_DoesNotStartRemainderBeforePositiveHeadRead()
    {
        var entered = NewGate();
        var release = NewGate();
        var head = new GatedReadStream([0x41], entered, release);
        var factoryCalls = 0;
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new MemoryStream([0x5A], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        var readTask = stream.ReadAsync(new byte[1]).AsTask();
        await entered.Task.WaitAsync(Timeout);
        Assert.Equal(0, factoryCalls);
        Assert.False(stream.RemainderScheduledForTests);

        release.TrySetResult();
        Assert.Equal(1, await readTask.WaitAsync(Timeout));
        await WaitUntil(() => Volatile.Read(ref factoryCalls) == 1);
        Assert.True(stream.RemainderScheduledForTests);
    }

    [Fact]
    public async Task ReadAsync_DoesNotAwaitRemainderConstructionBeforeReturningHeadBytes()
    {
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryStarted = NewGate();
        var factoryRelease = NewGate();
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                factoryStarted.TrySetResult();
                factoryRelease.Task.GetAwaiter().GetResult();
                return new MemoryStream([0x5A], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        var readTask = stream.ReadAsync(new byte[1]).AsTask();
        Assert.Equal(1, await readTask.WaitAsync(Timeout));
        await factoryStarted.Task.WaitAsync(Timeout);
        Assert.False(factoryRelease.Task.IsCompleted);

        factoryRelease.TrySetResult();
        head.ReleaseTail();
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task ReadAsync_PreservesCancellationTokenContextsForHeadAndRemainder()
    {
        using var parent = new CancellationTokenSource();
        var priority = new DownloadPriorityContext { Priority = SemaphorePriority.High };
        var timeout = new StreamingTimeoutContext
        {
            PerSegmentTimeout = TimeSpan.FromSeconds(3),
            MaxRetries = 2,
        };
        using var priorityScope = CancellationTokenContext.SetContext(parent.Token, priority);
        using var timeoutScope = CancellationTokenContext.SetContext(parent.Token, timeout);
        DownloadPriorityContext? headPriority = null;
        StreamingTimeoutContext? headTimeout = null;
        DownloadPriorityContext? remainderPriority = null;
        StreamingTimeoutContext? remainderTimeout = null;
        var factoryStarted = NewGate();
        var head = new TokenCapturingStream([0x41], token =>
        {
            headPriority = CancellationTokenContext.GetContext<DownloadPriorityContext>(token);
            headTimeout = CancellationTokenContext.GetContext<StreamingTimeoutContext>(token);
        });
        await using var stream = new FirstSegmentHandoffStream(
            head,
            token =>
            {
                remainderPriority = CancellationTokenContext.GetContext<DownloadPriorityContext>(token);
                remainderTimeout = CancellationTokenContext.GetContext<StreamingTimeoutContext>(token);
                factoryStarted.TrySetResult();
                return new MemoryStream([0x42], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            parent.Token);

        Assert.Equal(1, await stream.ReadAsync(new byte[1], parent.Token));
        await factoryStarted.Task.WaitAsync(Timeout);

        Assert.Same(priority, headPriority);
        Assert.Same(timeout, headTimeout);
        Assert.Same(priority, remainderPriority);
        Assert.Same(timeout, remainderTimeout);
    }

    [Fact]
    public async Task Read_SynchronousBoundaryDoesNotCaptureCallerSynchronizationContext()
    {
        var factoryCalls = 0;
        var stream = new FirstSegmentHandoffStream(
            new MemoryStream([0x41], writable: false),
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new MemoryStream([0x42], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        var reads = await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                var buffer = new byte[1];
                return new[] { stream.Read(buffer), stream.Read(buffer) };
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }).WaitAsync(Timeout);

        await stream.DisposeAsync();
        Assert.Equal([1, 1], reads);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task ReadAsync_TransitionsToAlreadyStartedRemainderAtHeadEof()
    {
        var head = new StagedBodyStream([], [0x41], []);
        var factoryCalls = 0;
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new MemoryStream([0x42, 0x43], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, await ReadAllAsync(stream));
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task ReadAsync_EmptyHeadStartsRemainderOnlyAfterHeadDisposal()
    {
        var headDisposed = NewGate();
        var factoryStarted = NewGate();
        var head = new CallbackDisposeStream(Stream.Null, () => headDisposed.TrySetResult());
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                Assert.True(headDisposed.Task.IsCompleted);
                factoryStarted.TrySetResult();
                return new MemoryStream([0x5A], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await factoryStarted.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReadAsync_AtHeadEofPolicyStartsRemainderOnlyAfterHeadEof()
    {
        var head = new StagedBodyStream([], [0x41], []);
        var factoryCalls = 0;
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new MemoryStream([0x42], writable: false);
            },
            RemainderStartPolicy.AtHeadEof,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Equal(0, factoryCalls);
        Assert.False(stream.RemainderScheduledForTests);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Equal(1, factoryCalls);
        Assert.True(stream.RemainderScheduledForTests);
    }

    [Fact]
    public async Task ReadAsync_NoRemainderFactoryReturnsCleanEof()
    {
        await using var stream = new FirstSegmentHandoffStream(
            new MemoryStream([0x41, 0x42], writable: false),
            remainderFactory: null,
            RemainderStartPolicy.None,
            CancellationToken.None);

        Assert.Equal(new byte[] { 0x41, 0x42 }, await ReadAllAsync(stream));
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task ReadAsync_RemainderFactoryFailureIsDeferredUntilBoundary()
    {
        var head = new StagedBodyStream([], [0x41], []);
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ => throw new InvalidDataException("factory failed"),
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await stream.ReadAsync(new byte[1]));
        Assert.Equal("factory failed", failure.Message);
    }

    [Fact]
    public async Task ReadAsync_RemainderReadFailurePropagatesWithoutReplayingHead()
    {
        var headReads = 0;
        var head = new CountingReadStream(new MemoryStream([0x41], writable: false), () => headReads++);
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ => new ThrowingReadStream(() => new IOException("remainder failed")),
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[1]));
        var afterTransition = headReads;
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[1]));
        Assert.Equal(afterTransition, headReads);
    }

    [Fact]
    public async Task ReadAsync_ConcurrentCallFailsWithoutAdvancingStreams()
    {
        var entered = NewGate();
        var release = NewGate();
        var head = new GatedReadStream([0x41, 0x42], entered, release);
        await using var stream = new FirstSegmentHandoffStream(
            head, remainderFactory: null, RemainderStartPolicy.None, CancellationToken.None);

        var first = stream.ReadAsync(new byte[1]).AsTask();
        await entered.Task.WaitAsync(Timeout);
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await stream.ReadAsync(new byte[1]));
        Assert.Contains("Concurrent ReadAsync", second.Message, StringComparison.Ordinal);

        release.TrySetResult();
        Assert.Equal(1, await first.WaitAsync(Timeout));
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task DisposeAsync_BeforeFirstReadDisposesOnlyHead()
    {
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryCalls = 0;
        var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new MemoryStream([0x5A], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        await stream.DisposeAsync();
        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, head.AsyncDisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterStartObservesFactoryAndDisposesBothSides()
    {
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryStarted = NewGate();
        var factoryRelease = NewGate();
        var remainderDisposed = NewGate();
        var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                factoryStarted.TrySetResult();
                factoryRelease.Task.GetAwaiter().GetResult();
                return new CallbackDisposeStream(
                    new MemoryStream([0x5A], writable: false),
                    () => remainderDisposed.TrySetResult());
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await factoryStarted.Task.WaitAsync(Timeout);

        var disposeTask = stream.DisposeAsync().AsTask();
        factoryRelease.TrySetResult();
        await disposeTask.WaitAsync(Timeout);
        await remainderDisposed.Task.WaitAsync(Timeout);
        Assert.Equal(1, head.AsyncDisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_ObservesAndSuppressesFaultedRemainderTask()
    {
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryStarted = NewGate();
        var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                factoryStarted.TrySetResult();
                throw new InvalidDataException("factory failed");
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await factoryStarted.Task.WaitAsync(Timeout);
        await WaitUntil(() => stream.RemainderScheduledForTests);

        await stream.DisposeAsync();
        Assert.Equal(1, head.AsyncDisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_HeadDisposeFailureStillDisposesRemainder()
    {
        var remainderCreated = NewGate();
        var remainderDisposed = NewGate();
        var head = new CallbackDisposeStream(
            new MemoryStream([0x41], writable: false),
            () => throw new IOException("head dispose failed"));
        var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                var remainder = new CallbackDisposeStream(
                    new MemoryStream([0x5A], writable: false),
                    () => remainderDisposed.TrySetResult());
                remainderCreated.TrySetResult();
                return remainder;
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await remainderCreated.Task.WaitAsync(Timeout);

        var failure = await Assert.ThrowsAsync<IOException>(async () => await stream.DisposeAsync());
        Assert.Equal("head dispose failed", failure.Message);
        await remainderDisposed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Dispose_IsNonBlockingAndDisposeAsyncJoinsSameCleanup()
    {
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryStarted = NewGate();
        var factoryRelease = NewGate();
        var remainderDisposed = NewGate();
        var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                factoryStarted.TrySetResult();
                factoryRelease.Task.GetAwaiter().GetResult();
                return new CallbackDisposeStream(
                    new MemoryStream([0x5A], writable: false),
                    () => remainderDisposed.TrySetResult());
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await factoryStarted.Task.WaitAsync(Timeout);

        stream.Dispose();
        Assert.False(factoryRelease.Task.IsCompleted);

        var join = stream.DisposeAsync().AsTask();
        factoryRelease.TrySetResult();
        await join.WaitAsync(Timeout);
        await remainderDisposed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Cancellation_BeforeFirstByteNeverStartsRemainder()
    {
        using var cts = new CancellationTokenSource();
        var entered = NewGate();
        var release = NewGate();
        var head = new GatedReadStream([0x41], entered, release);
        var factoryCalls = 0;
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new MemoryStream([0x5A], writable: false);
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            CancellationToken.None);

        var readTask = stream.ReadAsync(new byte[1], cts.Token).AsTask();
        await entered.Task.WaitAsync(Timeout);
        await cts.CancelAsync();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Cancellation_AfterStartCancelsAndOwnsConstructedRemainder()
    {
        using var cts = new CancellationTokenSource();
        var head = new StagedBodyStream([], [0x41], [0x42]);
        var factoryStarted = NewGate();
        var factoryRelease = NewGate();
        var remainderDisposed = NewGate();
        await using var stream = new FirstSegmentHandoffStream(
            head,
            _ =>
            {
                factoryStarted.TrySetResult();
                factoryRelease.Task.GetAwaiter().GetResult();
                return new CallbackDisposeStream(
                    new MemoryStream([0x5A], writable: false),
                    () => remainderDisposed.TrySetResult());
            },
            RemainderStartPolicy.AfterFirstPositiveRead,
            cts.Token);

        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        await factoryStarted.Task.WaitAsync(Timeout);
        await cts.CancelAsync();
        var disposeTask = stream.DisposeAsync().AsTask();
        factoryRelease.TrySetResult();
        await disposeTask.WaitAsync(Timeout);
        await remainderDisposed.Task.WaitAsync(Timeout);
        Assert.Equal(1, head.AsyncDisposeCount);
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Intentionally never pumped: context-capturing continuations deadlock.
        }
    }

    private sealed class TokenCapturingStream(
        byte[] payload,
        Action<CancellationToken> capture) : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            capture(cancellationToken);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class GatedReadStream(
        byte[] payload,
        TaskCompletionSource entered,
        TaskCompletionSource release) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var n = Math.Min(buffer.Length, payload.Length - _offset);
            if (n <= 0) return 0;
            payload.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CallbackDisposeStream(Stream inner, Action onDispose) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                onDispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                onDispose();
            }

            GC.SuppressFinalize(this);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingReadStream(Stream inner, Action onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead();
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingReadStream(Func<Exception> exceptionFactory) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exceptionFactory();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exceptionFactory());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
