using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Test.Mocks;

/// <summary>
/// Test-only stream wrapper that throws when disposed while armed.
/// Use to assert that archive/reader code does not dispose the caller's stream prematurely.
/// Call <see cref="Allow"/> / <see cref="Disarm"/> before intentional disposal.
/// </summary>
public sealed class DisposalGuardStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private bool _armed = true;
    private bool _isDisposed;

    public DisposalGuardStream(Stream inner, bool leaveInnerOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _leaveInnerOpen = leaveInnerOpen;
    }

    /// <summary>
    /// Gets whether dispose will throw.
    /// </summary>
    public bool IsArmed => _armed;

    /// <summary>
    /// Allows subsequent <see cref="Dispose"/> / <see cref="DisposeAsync"/> to succeed
    /// (equivalent to the old <c>ThrowOnDispose = false</c> disarm pattern).
    /// </summary>
    public void Allow() => _armed = false;

    /// <summary>
    /// Alias for <see cref="Allow"/>.
    /// </summary>
    public void Disarm() => Allow();

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => _inner.WriteAsync(buffer, cancellationToken);

    public override Task CopyToAsync(
        Stream destination,
        int bufferSize,
        CancellationToken cancellationToken
    ) => _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_armed)
        {
            throw new InvalidOperationException(
                $"Attempt to dispose of a {nameof(DisposalGuardStream)} while armed. Call {nameof(Allow)}/{nameof(Disarm)} first."
            );
        }

        _isDisposed = true;
        if (disposing && !_leaveInnerOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (_armed)
        {
            throw new InvalidOperationException(
                $"Attempt to dispose of a {nameof(DisposalGuardStream)} while armed. Call {nameof(Allow)}/{nameof(Disarm)} first."
            );
        }

        _isDisposed = true;
        if (!_leaveInnerOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
