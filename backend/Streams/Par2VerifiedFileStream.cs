using System.Buffers;
using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Sequential candidate source for a PAR2-verified file. One persistent candidate stream keeps
/// normal read-ahead across slices; a failed slice goes to <see cref="Recover"/>.
/// </summary>
internal sealed record Par2SequentialCandidateSource(
    // Opens a candidate stream positioned at the start offset that will read up to the end offset.
    Func<long, long, Stream> Open,
    // Scope the candidate bytes are read in.
    Func<IDisposable> BeginRead,
    // Retries a slice after the sequential attempt failed (null: checksum mismatch); fills the target
    // only with verified bytes.
    Func<long, Memory<byte>, Exception?, CancellationToken, Task> Recover,
    // Bytes the current request still wants from the current position, or null when open-ended.
    Func<long?> ReadBudget,
    // Slice buffers this stream may hold at once: queued, being filled, and the one the reader holds.
    // One means each slice is verified on demand.
    int BufferSlices);

internal sealed class Par2VerifiedFileStream(
    Par2FileProof proof,
    Func<long, Memory<byte>, CancellationToken, Task> readCandidate,
    Func<long, Memory<byte>, CancellationToken, Task>? readPrefix = null,
    Par2SequentialCandidateSource? sequential = null) : FastReadOnlyStream, ISegmentIssueProgress
{
    private byte[]? _slice;
    private byte[]? _prefix;
    private int _verifiedSlice = -1;
    private long _position;
    private bool _disposed;
    private SliceProducer? _producer;
    // Teardown of a producer a Seek abandoned; the next read joins it before leasing again.
    private Task? _pendingTeardown;
    // Bounds every sequential slice buffer, including the producer's working buffer and the reader's slice.
#pragma warning disable CA2213 // a read continuation may still return a buffer after disposal; no wait handle is created
    private readonly SemaphoreSlim? _bufferCredits =
        sequential is null ? null : new SemaphoreSlim(Math.Max(1, sequential.BufferSlices));
#pragma warning restore CA2213

    // Verification buffers are held outside the article budget, so their total stays bounded by bytes.
    internal const long MaximumBufferBytes = 16L * 1024 * 1024;
    private const int MinimumBufferSlices = 4;

    // Sequential slice buffers (queued, being filled, and the reader's) for the wanted read-ahead bytes.
    // A slice too large for MaximumBufferBytes gets one buffer and is verified on demand.
    internal static int GetBufferSlices(long readAheadBytes, int sliceSize) =>
        (int)Math.Max(1, Math.Min(Math.Max(readAheadBytes, (long)MinimumBufferSlices * sliceSize),
            MaximumBufferBytes) / sliceSize);

    public override bool CanSeek => true;
    public override long Length => proof.FileLength;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }

    bool ISegmentIssueProgress.AllSegmentsIssued => _producer?.AllSegmentsIssued == true;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!proof.IsValidFor(Length)) throw new InvalidDataException("Invalid persisted PAR2 verification metadata.");
        if (buffer.IsEmpty || _position >= Length) return 0;
        var prefixLength = (int)Math.Min(16384, Length);
        if (readPrefix is not null && proof.File16kHash is { Length: 16 } && _position < prefixLength
            && _verifiedSlice != 0)
        {
            if (_prefix is null)
            {
                var prefix = new byte[prefixLength];
                await readPrefix(0, prefix, cancellationToken).ConfigureAwait(false);
                if (!proof.VerifyPrefix(prefix))
                    throw new InvalidDataException("PAR2 prefix integrity verification failed; no unverified bytes were returned.");
                _prefix = prefix;
            }
            var prefixCount = Math.Min(buffer.Length, prefixLength - (int)_position);
            _prefix.AsMemory((int)_position, prefixCount).CopyTo(buffer);
            _position += prefixCount;
            return prefixCount;
        }
        var sliceIndex = checked((int)(_position / proof.SliceSize));
        if (_verifiedSlice != sliceIndex)
        {
            if (sequential is null) await ReadSliceDirectAsync(sliceIndex, cancellationToken).ConfigureAwait(false);
            else await ReadSliceSequentialAsync(sliceIndex, cancellationToken).ConfigureAwait(false);
        }

        var offset = (int)(_position % proof.SliceSize);
        var count = (int)Math.Min(Math.Min(buffer.Length, proof.SliceSize - offset), Length - _position);
        _slice!.AsMemory(offset, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    private int SliceLength(int sliceIndex) =>
        (int)Math.Min(proof.SliceSize, Length - (long)sliceIndex * proof.SliceSize);

    private async Task ReadSliceDirectAsync(int sliceIndex, CancellationToken cancellationToken)
    {
        _verifiedSlice = -1;
        _slice ??= ArrayPool<byte>.Shared.Rent(proof.SliceSize);
        Array.Clear(_slice, 0, proof.SliceSize);
        await readCandidate((long)sliceIndex * proof.SliceSize, _slice.AsMemory(0, SliceLength(sliceIndex)),
            cancellationToken).ConfigureAwait(false);
        if (!proof.VerifySlice(_slice.AsSpan(0, proof.SliceSize), sliceIndex))
            throw new InvalidDataException("PAR2 slice integrity verification failed; no unverified bytes were returned.");
        _verifiedSlice = sliceIndex;
    }

    private async Task ReadSliceSequentialAsync(int sliceIndex, CancellationToken cancellationToken)
    {
        if (_pendingTeardown is { } pending)
        {
            _pendingTeardown = null;
            await pending.ConfigureAwait(false);
        }
        // The reader is moving to another slice, so its buffer is free for the producer.
        _verifiedSlice = -1;
        ReturnSlice();

        while (true)
        {
            if (_producer is { } current && !current.CanServe(sliceIndex))
            {
                _producer = null;
                await current.DisposeAsync().ConfigureAwait(false);
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            var producer = _producer ??= StartProducer(sliceIndex, cancellationToken);
            if (_disposed)
            {
                // Synchronous disposal raced the start and cannot see this producer.
                _producer = null;
                await producer.DisposeAsync().ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            VerifiedSlice next;
            try
            {
                next = await producer.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // Ended without a failure record only when disposed or past its planned end.
                _producer = null;
                await producer.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            if (next.Failed)
            {
                // The producer stopped at this slice and already released its candidate stream.
                _producer = null;
                await producer.DisposeAsync().ConfigureAwait(false);
                if (next.Index != sliceIndex) continue;
                await _bufferCredits!.WaitAsync(cancellationToken).ConfigureAwait(false);
                _slice = ArrayPool<byte>.Shared.Rent(proof.SliceSize);
                Array.Clear(_slice, 0, proof.SliceSize);
                await sequential!.Recover((long)sliceIndex * proof.SliceSize,
                    _slice.AsMemory(0, SliceLength(sliceIndex)), next.Failure, cancellationToken).ConfigureAwait(false);
                if (!proof.VerifySlice(_slice.AsSpan(0, proof.SliceSize), sliceIndex))
                    throw new InvalidDataException("PAR2 slice integrity verification failed; no unverified bytes were returned.");
                _verifiedSlice = sliceIndex;
                return;
            }

            if (next.Index != sliceIndex)
            {
                // A short forward seek inside the window skips already verified slices.
                ReturnBuffer(next.Buffer!);
                continue;
            }

            _slice = next.Buffer;
            _verifiedSlice = sliceIndex;
            return;
        }
    }

    private SliceProducer StartProducer(int firstSlice, CancellationToken cancellationToken)
    {
        var source = sequential!;
        var sliceSize = (long)proof.SliceSize;
        var start = firstSlice * sliceSize;
        var end = Length;
        if (source.ReadBudget() is > 0 and var budget && budget < Length - _position)
            end = Math.Min(Length, ((_position + budget + sliceSize - 1) / sliceSize) * sliceSize);
        var endSlice = (int)((end - 1) / sliceSize) + 1;
        return new SliceProducer(proof, source, _bufferCredits!, firstSlice, Math.Max(firstSlice + 1, endSlice),
            cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long position;
        try
        {
            position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (position < 0 || position > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (_producer is { } producer && position < Length)
        {
            var sliceIndex = (int)(position / proof.SliceSize);
            if (sliceIndex != _verifiedSlice && !producer.CanServe(sliceIndex))
            {
                // Seek is synchronous: start teardown now and join it on the next read.
                _producer = null;
                var previous = _pendingTeardown;
                var teardown = producer.DisposeAsync().AsTask();
                _pendingTeardown = previous is null ? teardown : Task.WhenAll(previous, teardown);
            }
        }
        return _position = position;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_producer is { } producer)
        {
            _producer = null;
            await producer.DisposeAsync().ConfigureAwait(false);
        }
        if (_pendingTeardown is { } pending)
        {
            _pendingTeardown = null;
            await pending.ConfigureAwait(false);
        }
        ReturnSlice();
        _prefix = null;
        GC.SuppressFinalize(this);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            // Synchronous disposal cannot wait for the candidate stream; teardown completes in the background.
            var teardown = _producer?.DisposeAsync().AsTask();
            if (_pendingTeardown is { } pending) teardown = teardown is null ? pending : Task.WhenAll(pending, teardown);
            _producer = null;
            _pendingTeardown = null;
            if (teardown is null) ReturnSlice();
            else
                _ = teardown.ContinueWith(static (_, state) => ((Par2VerifiedFileStream)state!).ReturnSlice(),
                    this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _prefix = null;
        }
        _disposed = true;
        base.Dispose(disposing);
    }

    private void ReturnSlice()
    {
        if (Interlocked.Exchange(ref _slice, null) is { } slice) ReturnBuffer(slice);
    }

    private void ReturnBuffer(byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer);
        _bufferCredits?.Release();
    }

    // Buffer is null when the slice failed; Failure is null for a checksum mismatch.
    private readonly record struct VerifiedSlice(int Index, byte[]? Buffer, Exception? Failure)
    {
        internal bool Failed => Buffer is null;
    }

    /// <summary>
    /// Reads slices in order from one candidate stream, verifies each as it arrives, and keeps at
    /// most <see cref="Par2SequentialCandidateSource.BufferSlices"/> slice buffers together with the reader.
    /// It stops at the first failing slice and reports it so only that slice takes the recovery path.
    /// </summary>
    private sealed class SliceProducer : IAsyncDisposable
    {
        private readonly Par2FileProof _proof;
        private readonly Par2SequentialCandidateSource _source;
        private readonly SemaphoreSlim _bufferCredits;
        private readonly bool _coversFileEnd;
        private readonly Channel<VerifiedSlice> _verified;
        private readonly ContextualCancellationTokenSource _cts;
        private readonly int _endSlice;
        private readonly Task _run;
        private int _nextSlice;
        private Stream? _candidate;
        private volatile bool _finished;
        private int _disposed;

        internal SliceProducer(
            Par2FileProof proof,
            Par2SequentialCandidateSource source,
            SemaphoreSlim bufferCredits,
            int firstSlice,
            int endSlice,
            CancellationToken contextToken)
        {
            _proof = proof;
            _source = source;
            _bufferCredits = bufferCredits;
            _coversFileEnd = (long)endSlice * proof.SliceSize >= proof.FileLength;
            _nextSlice = firstSlice;
            _endSlice = endSlice;
            _verified = Channel.CreateBounded<VerifiedSlice>(new BoundedChannelOptions(Math.Max(1, source.BufferSlices))
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            // Owned by this stream generation, not the read that started it.
            _cts = ContextualCancellationTokenSource.CreateWithContextsOf(contextToken);
            _run = RunAsync(firstSlice, _cts.Token);
        }

        // Only once every remaining slice is verified: a failed slice's recovery and the candidate that resumes
        // after it both need shared article credits, which a next part opened ahead of the reader would hold.
        internal bool AllSegmentsIssued => _coversFileEnd && _finished;

        internal bool CanServe(int sliceIndex) =>
            sliceIndex >= _nextSlice && sliceIndex < _endSlice
            && sliceIndex - _nextSlice <= Math.Max(1, _source.BufferSlices);

        internal async ValueTask<VerifiedSlice> ReadAsync(CancellationToken cancellationToken)
        {
            var slice = await _verified.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _nextSlice = slice.Index + 1;
            return slice;
        }

        private async Task RunAsync(int firstSlice, CancellationToken ct)
        {
            await Task.Yield();
            var sliceSize = (long)_proof.SliceSize;
            var length = _proof.FileLength;
            using var scope = _source.BeginRead();
            var index = firstSlice;
            try
            {
                var candidate = _source.Open(firstSlice * sliceSize, Math.Min(length, _endSlice * sliceSize));
                Volatile.Write(ref _candidate, candidate);
                for (; index < _endSlice; index++)
                {
                    var start = index * sliceSize;
                    var count = (int)Math.Min(sliceSize, length - start);
                    await _bufferCredits.WaitAsync(ct).ConfigureAwait(false);
                    var buffer = ArrayPool<byte>.Shared.Rent(_proof.SliceSize);
                    var failed = false;
                    Exception? failure = null;
                    try
                    {
                        Array.Clear(buffer, count, _proof.SliceSize - count);
                        await candidate.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        failed = !_proof.VerifySlice(buffer.AsSpan(0, _proof.SliceSize), index);
                    }
                    catch (Exception exception) when (!ct.IsCancellationRequested)
                    {
                        failed = true;
                        failure = exception;
                    }
                    catch
                    {
                        ReturnBuffer(buffer);
                        throw;
                    }

                    if (failed)
                    {
                        ReturnBuffer(buffer);
                        // Release the candidate's leases before the reader retries this slice.
                        await ReleaseCandidateAsync().ConfigureAwait(false);
                        await _verified.Writer.WriteAsync(new VerifiedSlice(index, null, failure), ct).ConfigureAwait(false);
                        return;
                    }

                    try
                    {
                        await _verified.Writer.WriteAsync(new VerifiedSlice(index, buffer, null), ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        ReturnBuffer(buffer);
                        throw;
                    }
                }
                _finished = true;
            }
            catch (Exception exception) when (ct.IsCancellationRequested || exception is ChannelClosedException)
            {
                // Seek or dispose ended this generation.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _verified.Writer.TryWrite(new VerifiedSlice(index, null, exception));
            }
            finally
            {
                await ReleaseCandidateAsync().ConfigureAwait(false);
                _verified.Writer.TryComplete();
            }
        }

        private async ValueTask ReleaseCandidateAsync()
        {
            if (Interlocked.Exchange(ref _candidate, null) is not { } candidate) return;
            try
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Serilog.Log.Debug(exception, "PAR2 sequential candidate stream failed to dispose");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Serilog.Log.Debug(exception, "PAR2 sequential candidate producer ended with an error during teardown");
            }
            _cts.Dispose();
            while (_verified.Reader.TryRead(out var slice))
            {
                if (slice.Buffer is { } buffer) ReturnBuffer(buffer);
            }
        }

        private void ReturnBuffer(byte[] buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _bufferCredits.Release();
        }
    }
}
