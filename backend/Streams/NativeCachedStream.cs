using System.Buffers;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>Final-byte, lazy-source read-through stream. Never interprets sparse holes as data.</summary>
public sealed class NativeCachedStream : FastReadOnlyStream, ICacheReadEvidence, IStreamGenerationEvidence
{
    private readonly NativeCacheStore _store;
    private readonly NativeCacheIdentity _identity;
    private readonly Func<CancellationToken, Task<Stream>> _openSource;
    private readonly Func<bool> _generationIsCurrent;
    private readonly IDisposable _lease;
    private IDisposable? _bufferAdmission;
    private readonly bool _background;
    private readonly bool _writeBehind;
    private Task<bool>? _pendingWrite;
    private readonly NativeCacheStatistics? _statistics;
    private NativeCacheStatistics.NativeCacheTransfer? _transfer;
    private Stream? _source;
    private byte[]? _buffer;
    private long _bufferStart = -1;
    private int _bufferCount;
    private bool _bufferVerified;
    private bool _bufferFromCache;
    private long _position;
    private bool _disposed;
    private bool _bypassFill;
    private bool _servedBytes;
    private bool _untrackedSource;
    private long _lastDirectMissBlock = -1;
    internal TimeSpan CacheIoTimeout { get; init; } = TimeSpan.FromSeconds(1);
    internal Func<bool, CancellationToken, Task>? BeforeCacheIo { get; init; }

    public NativeCachedStream(NativeCacheStore store, NativeCacheIdentity identity,
        Func<CancellationToken, Task<Stream>> openSource, Func<bool> generationIsCurrent,
        IDisposable? bufferAdmission = null, bool background = false, NativeCacheStatistics? statistics = null, bool writeBehind = false)
    {
        _store = store;
        _identity = identity;
        _openSource = openSource;
        _generationIsCurrent = generationIsCurrent;
        _lease = store.AcquireLease(identity);
        _bufferAdmission = bufferAdmission;
        _background = background;
        _writeBehind = writeBehind && !background;
        _statistics = statistics;
    }

    public bool LastReadCacheable { get; private set; }
    public NativeCacheIdentity Identity => _identity;
    public string GenerationIdentity => _identity.Key;
    public bool IsSourceCurrent => _generationIsCurrent();
    public override long Length => _identity.Length;
    public override bool CanSeek => true;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }

    /// <summary>Checks existing bytes without opening the source or spending a warming budget.</summary>
    internal async Task<bool> VerifyCachedBlockAsync(long blockStart, CancellationToken cancellationToken)
    {
        var valid = await _store.VerifyOnceAsync(_identity, blockStart,
            () => VerifyOwnedBlockAsync(blockStart, cancellationToken), cancellationToken).ConfigureAwait(false);
        return valid && _generationIsCurrent();
    }

    private async Task<bool> VerifyOwnedBlockAsync(long blockStart, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_bypassFill || !_generationIsCurrent()) return false;
        // Use this stream's admitted buffer and IO lifetime handling. Cancellation
        // must not return memory still owned by an uncancellable NAS read.
        _buffer ??= ArrayPool<byte>.Shared.Rent(NativeCacheStore.BlockSize);
        _bufferStart = -1;
        _bufferCount = 0;
        _bufferVerified = false;
        _bufferFromCache = false;
        LastReadCacheable = false;
        var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
        var buffer = _buffer;
        var count = await CacheIoAsync(false,
            token => _store.ReadBlockAsync(_identity, blockStart, buffer.AsMemory(0, expected), token),
            cancellationToken).ConfigureAwait(false);
        return count == expected && _generationIsCurrent();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LastReadCacheable = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.IsEmpty || _position == Length) return 0;
        if (!_untrackedSource && !_generationIsCurrent())
        {
            if (_servedBytes) throw new IOException("Media source changed during this response. Retry the range against the current source.");
            _untrackedSource = true;
            _bypassFill = true;
        }
        if (_bypassFill) return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
        var blockStart = _position / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        if (_bufferFromCache && !_generationIsCurrent()) _bufferStart = -1;
        if (_bufferStart != blockStart)
        {
            if (_pendingWrite is { } pending)
            {
                try { await CacheIoAsync(true, _ => pending, cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false); }
                finally { _pendingWrite = null; }
            }
            using var fill = await AcquireFillAsync(blockStart, cancellationToken).ConfigureAwait(false);
            if (fill is null) return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
            _buffer ??= ArrayPool<byte>.Shared.Rent(NativeCacheStore.BlockSize);
            _bufferStart = -1;
            _bufferCount = 0;
            _bufferVerified = false;
            _bufferFromCache = false;
            var expected = (int)Math.Min(NativeCacheStore.BlockSize, Length - blockStart);
            if (_generationIsCurrent())
            {
                try
                {
                    var buffer = _buffer;
                    _bufferCount = await CacheIoAsync(false, token => _store.ReadBlockAsync(_identity, blockStart,
                        buffer.AsMemory(0, expected), token), cancellationToken).ConfigureAwait(false);
                    _bufferVerified = _bufferCount == expected;
                    _bufferFromCache = _bufferVerified;
                    if (!_generationIsCurrent()) _bufferCount = 0;
                    if (_bufferCount == expected) _statistics?.Hit(expected);
                }
                catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
                { /* Cache storage failures never prevent source playback. */ }
                catch (TimeoutException)
                {
                    return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }
            if (_bufferCount != expected)
            {
                _statistics?.Miss();
                _lastDirectMissBlock = blockStart;
                try
                {
                using var verifiedRead = new NativeCacheReadContext();
                _bufferFromCache = false;
                _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
                _source.Position = blockStart;
                _bufferCount = 0;
                _bufferVerified = true;
                while (_bufferCount < expected)
                {
                    var read = await _source.ReadAsync(_buffer.AsMemory(_bufferCount, expected - _bufferCount), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Source ended before the declared media length.");
                    _statistics?.SourceBytes(read);
                    _bufferVerified &= _source is ICacheReadEvidence { LastReadCacheable: true };
                    _bufferCount += read;
                }
                if (_bufferVerified && _generationIsCurrent())
                {
                    try
                    {
                        _transfer ??= _statistics?.BeginTransfer(_identity.ItemId, _identity.DisplayName, _identity.Length, _background);
                        var buffer = _buffer;
                        if (_writeBehind)
                        {
                            // The buffer stays immutable until this write finishes. The next
                            // block and disposal drain it or transfer ownership on timeout.
                            _pendingWrite = Task.Run(async () =>
                            {
                                try
                                {
                                    if (BeforeCacheIo is not null) await BeforeCacheIo(true, cancellationToken).ConfigureAwait(false);
                                    var committed = await _store.WriteBlockAsync(_identity, blockStart, buffer.AsMemory(0, expected),
                                        waitForWriter: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                                    if (committed) { _statistics?.Committed(expected); _transfer?.Committed(expected); }
                                    return committed;
                                }
                                catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException or OperationCanceledException)
                                { return false; }
                            }, CancellationToken.None);
                        }
                        else if (await CacheIoAsync(true, token => _store.WriteBlockAsync(_identity, blockStart, buffer.AsMemory(0, expected),
                            waitForWriter: _background, cancellationToken: token), cancellationToken)
                            .ConfigureAwait(false)) { _statistics?.Committed(expected); _transfer?.Committed(expected); }
                    }
                    catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException) { }
                }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Read-ahead is speculative. A later missing article must not
                    // break a readable prefix/range requested by the player. Reopen
                    // without native proof/read-ahead context and use ordinary reads.
                    if (_source is not null)
                    {
                        try { await _source.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception teardown) when (teardown is not OutOfMemoryException) { }
                    }
                    _source = null;
                    _bufferCount = 0;
                    _bufferVerified = false;
                    _bypassFill = true;
                    if (exception is not TimeoutException) _statistics?.Fallback();
                    return await ReadSourceRangeAsync(destination, cancellationToken).ConfigureAwait(false);
                }
            }
            _bufferStart = blockStart;
        }
        EnsureResponseGeneration();
        var bufferOffset = checked((int)(_position - _bufferStart));
        var count = Math.Min(destination.Length, _bufferCount - bufferOffset);
        _buffer!.AsMemory(bufferOffset, count).CopyTo(destination);
        EnsureResponseGeneration();
        _position += count;
        _servedBytes |= count > 0;
        LastReadCacheable = _bufferVerified && _generationIsCurrent();
        return count;
    }

    private async Task<IDisposable?> AcquireFillAsync(long blockStart, CancellationToken cancellationToken)
    {
        if (_background) return await _store.AcquireFillAsync(_identity, blockStart, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CacheIoTimeout);
        try { return await _store.AcquireFillAsync(_identity, blockStart, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _bypassFill = true;
            _statistics?.Fallback(timeout: true);
            return null;
        }
    }

    private async Task<T> CacheIoAsync<T>(bool write, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        // NAS syscalls can block synchronously and ignore cancellation. Admission
        // bounds both these operations and their buffers; never recycle either
        // until a detached operation has really stopped touching the memory.
        var io = Task.Run(async () =>
        {
            if (BeforeCacheIo is not null) await BeforeCacheIo(write, cancellationToken).ConfigureAwait(false);
            return await operation(cancellationToken).ConfigureAwait(false);
        }, CancellationToken.None);
        try
        {
            return _background
                ? await io.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await io.WaitAsync(CacheIoTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            if (exception is TimeoutException) _statistics?.Fallback(timeout: true);
            var buffer = _buffer;
            var admission = _bufferAdmission;
            _buffer = null;
            _bufferAdmission = null;
            _bufferStart = -1;
            _bufferCount = 0;
            _bufferVerified = false;
            _bypassFill = true;
            _ = ReleaseAfterIoAsync(io, buffer, admission);
            throw;
        }
    }

    private static async Task ReleaseAfterIoAsync(Task io, byte[]? buffer, IDisposable? admission)
    {
        try { await io.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* Request already fell back or cancelled. */ }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            admission?.Dispose();
        }
    }

    private async ValueTask<int> ReadSourceRangeAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        _source ??= await _openSource(cancellationToken).ConfigureAwait(false);
        if (_source.Position != _position) _source.Position = _position;
        var start = _position;
        var count = await _source.ReadAsync(destination[..(int)Math.Min(destination.Length, Length - _position)], cancellationToken).ConfigureAwait(false);
        _statistics?.SourceBytes(count);
        for (var block = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
            block < start + count; block += NativeCacheStore.BlockSize)
            if (block != _lastDirectMissBlock) { _statistics?.Miss(); _lastDirectMissBlock = block; }
        EnsureResponseGeneration();
        _position += count;
        _servedBytes |= count > 0;
        LastReadCacheable = false;
        return count;
    }

    private void EnsureResponseGeneration()
    {
        if (!_untrackedSource && !_generationIsCurrent())
            throw new IOException("Media source changed during this response. Retry the range against the current source.");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        LastReadCacheable = false;
        return _position = position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try { _source?.Dispose(); }
            finally { Release(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                try { if (_source is not null) await _source.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try
                    {
                        if (_pendingWrite is { } pending)
                        {
                            try { await pending.WaitAsync(CacheIoTimeout).ConfigureAwait(false); }
                            catch (TimeoutException) { }
                        }
                    }
                    finally { Release(); }
                }
            }
        }
        finally { await base.DisposeAsync().ConfigureAwait(false); }
    }

    private void Release()
    {
        if (_pendingWrite is { IsCompleted: false } pending)
        {
            _ = ReleaseAfterIoAsync(pending, _buffer, _bufferAdmission);
            _bufferAdmission = null;
        }
        else if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        _lease.Dispose();
        _transfer?.Dispose();
        _bufferAdmission?.Dispose();
    }
}
