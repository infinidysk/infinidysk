using NzbWebDAV.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

internal sealed class Par2VerifiedFileStream(
    Par2FileProof proof,
    Func<long, Memory<byte>, CancellationToken, Task> readCandidate,
    Func<long, Memory<byte>, CancellationToken, Task>? readPrefix = null) : FastReadOnlyStream
{
    private byte[]? _slice;
    private byte[]? _prefix;
    private int _verifiedSlice = -1;
    private long _position;
    private bool _disposed;

    public override bool CanSeek => true;
    public override long Length => proof.FileLength;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!proof.IsValidFor(Length)) throw new InvalidDataException("Invalid persisted PAR2 verification metadata.");
        if (buffer.IsEmpty || _position >= Length) return 0;
        var prefixLength = (int)Math.Min(16384, Length);
        if (readPrefix is not null && proof.File16kHash is { Length: 16 } && _position < prefixLength)
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
            _verifiedSlice = -1;
            _slice ??= new byte[proof.SliceSize];
            Array.Clear(_slice);
            var sliceStart = (long)sliceIndex * proof.SliceSize;
            var sliceLength = (int)Math.Min(proof.SliceSize, Length - sliceStart);
            await readCandidate(sliceStart, _slice.AsMemory(0, sliceLength), cancellationToken).ConfigureAwait(false);
            if (!proof.VerifySlice(_slice, sliceIndex))
                throw new InvalidDataException("PAR2 slice integrity verification failed; no unverified bytes were returned.");
            _verifiedSlice = sliceIndex;
        }

        var offset = (int)(_position % proof.SliceSize);
        var count = (int)Math.Min(Math.Min(buffer.Length, proof.SliceSize - offset), Length - _position);
        _slice!.AsMemory(offset, count).CopyTo(buffer);
        _position += count;
        return count;
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
        return _position = position;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _slice = null;
        _prefix = null;
        base.Dispose(disposing);
    }
}