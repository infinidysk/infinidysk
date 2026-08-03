using System.Security.Cryptography;
using System.Runtime.InteropServices;
using NzbWebDAV.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams
{
    internal sealed class AesDecoderStream : FastReadOnlyStream
    {
        private readonly Stream _mStream;
        private Aes _aes; // keep Aes alive for transform lifetime
        private ICryptoTransform _mDecoder;
        private readonly byte[] _plainBuffer; // decrypted bytes cache
        private int _plainStart;
        private int _plainEnd;
        private readonly byte[] _cipherBuffer; // buffer to read ciphertext blocks in chunks

        private long _mWritten; // number of decoded bytes returned already
        private readonly long _mLimit;
        private bool _isDisposed;
        private long? _pendingSeekPosition = null;

        // store for reinitializing on Seek
        private readonly byte[] _mKey;
        private readonly byte[] _mBaseIv;

        private const int BlockSize = 16;
        private const int BufferSize = 256 * 1024;
        internal const string TruncatedCiphertextMessagePrefix =
            "Unexpected end of ciphertext stream";

        public AesDecoderStream(Stream input, AesParams aesParams)
        {
            _mStream = input ?? throw new ArgumentNullException(nameof(input));
            _mLimit = aesParams.DecodedSize;
            _mKey = aesParams.Key ?? throw new ArgumentNullException(nameof(aesParams.Key));
            _mBaseIv = aesParams.Iv ?? throw new ArgumentNullException(nameof(aesParams.Iv));

            if (((uint)input.Length & (BlockSize - 1)) != 0)
            {
                throw new NotSupportedException("AES decoder does not support padding.");
            }

            // Create and hold Aes instance for the lifetime of this stream (safer)
            _aes = Aes.Create();
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.None;

            _mDecoder = _aes.CreateDecryptor(_mKey, _mBaseIv);

            _plainBuffer = new byte[BufferSize];
            _cipherBuffer = new byte[BufferSize];

            _plainStart = _plainEnd = 0;
            _mWritten = 0;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (_isDisposed) return;
                _isDisposed = true;
                if (disposing)
                {
                    _mStream.Dispose();
                    _mDecoder?.Dispose();
                    _aes?.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override void Flush() => throw new NotImplementedException();

        public override long Position
        {
            get => _pendingSeekPosition ?? _mWritten;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override bool CanWrite => false;
        public override long Length => _mLimit;
        public override bool CanRead => true;
        public override bool CanSeek => true;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("AesDecoderStream supports asynchronous reads only.");

        public override int Read(Span<byte> buffer) =>
            throw new NotSupportedException("AesDecoderStream supports asynchronous reads only.");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken ct = default)
        {
            // Perform pending seek (deferred heavy work)
            if (_pendingSeekPosition != null)
            {
                var pos = _pendingSeekPosition.Value;
                _pendingSeekPosition = null;
                await SeekInternalAsync(pos, ct).ConfigureAwait(false);
            }

            if (buffer.Length == 0 || _mWritten == _mLimit)
                return 0;

            // Cap to remaining logical decoded bytes
            int toReturnAllowed = (int)Math.Min(buffer.Length, _mLimit - _mWritten);
            int totalCopied = 0;

            // First, deliver any already-decrypted bytes in the plain buffer
            if (_plainEnd > _plainStart)
            {
                int have = _plainEnd - _plainStart;
                int take = Math.Min(have, toReturnAllowed);
                _plainBuffer.AsSpan(_plainStart, take).CopyTo(buffer.Span);
                _plainStart += take;
                _mWritten += take;
                totalCopied += take;
                toReturnAllowed -= take;

                if (_plainStart == _plainEnd)
                {
                    _plainStart = _plainEnd = 0;
                }

                if (toReturnAllowed == 0 || _mWritten == _mLimit)
                    return totalCopied;
            }

            // Decrypt aligned runs directly into array-backed caller memory. For partial
            // blocks or non-array-backed memory, decrypt a large run into the cache.
            while (toReturnAllowed > 0 && _mWritten < _mLimit)
            {
                int directBytes = Math.Min(toReturnAllowed & ~(BlockSize - 1), BufferSize);
                var destination = buffer.Slice(totalCopied, toReturnAllowed);
                if (directBytes > 0 &&
                    MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination,
                        out ArraySegment<byte> destinationSegment))
                {
                    int cipherRead = await ReadCiphertextAsync(directBytes, ct).ConfigureAwait(false);
                    if (cipherRead == 0)
                    {
                        return totalCopied;
                    }

                    int processed = _mDecoder.TransformBlock(_cipherBuffer, 0, cipherRead,
                        destinationSegment.Array!, destinationSegment.Offset);
                    _mWritten += processed;
                    totalCopied += processed;
                    toReturnAllowed -= processed;
                    continue;
                }

                long remainingDecoded = _mLimit - _mWritten;
                int cacheTarget = remainingDecoded >= BufferSize
                    ? BufferSize
                    : ((int)remainingDecoded + BlockSize - 1) & ~(BlockSize - 1);
                int bufferedCipherRead = await ReadCiphertextAsync(cacheTarget, ct).ConfigureAwait(false);
                if (bufferedCipherRead == 0)
                {
                    return totalCopied;
                }

                _plainStart = 0;
                _plainEnd = _mDecoder.TransformBlock(_cipherBuffer, 0, bufferedCipherRead,
                    _plainBuffer, 0);

                int bufferedTake = Math.Min(_plainEnd, toReturnAllowed);
                _plainBuffer.AsSpan(0, bufferedTake).CopyTo(destination.Span);
                _plainStart = bufferedTake;
                _mWritten += bufferedTake;
                totalCopied += bufferedTake;
                toReturnAllowed -= bufferedTake;

                if (_plainStart == _plainEnd)
                {
                    _plainStart = _plainEnd = 0;
                }
            }

            return totalCopied;
        }

        private async ValueTask<int> ReadCiphertextAsync(int maximumBytes, CancellationToken ct)
        {
            int cipherRead = await _mStream.ReadAsync(_cipherBuffer.AsMemory(0, maximumBytes), ct)
                .ConfigureAwait(false);
            if (cipherRead == 0)
            {
                return 0;
            }

            // Complete a short final block without forcing every short read to fill the
            // whole buffer. The constructor guarantees the ciphertext length is aligned.
            while ((cipherRead & (BlockSize - 1)) != 0)
            {
                int need = BlockSize - (cipherRead & (BlockSize - 1));
                int read = await _mStream.ReadAsync(_cipherBuffer.AsMemory(cipherRead, need), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"{TruncatedCiphertextMessagePrefix} after decoding {_mWritten} of {_mLimit} bytes: " +
                        $"read {cipherRead} ciphertext bytes in the current batch, leaving a partial block of " +
                        $"{cipherRead & (BlockSize - 1)} bytes.");
                }

                cipherRead += read;
            }

            return cipherRead;
        }

        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    target = offset;
                    break;
                case SeekOrigin.Current:
                    target = _mWritten + offset;
                    break;
                case SeekOrigin.End:
                    target = _mLimit + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
            }

            if (target < 0 || target > _mLimit)
                throw new ArgumentOutOfRangeException(nameof(offset), "Seek position outside stream limits");

            // Defer heavy work to next ReadAsync (seek is "instant")
            _pendingSeekPosition = target;
            // clear decrypted buffer so it doesn't leak across seeks
            _plainStart = _plainEnd = 0;
            return target;
        }

        /// <summary>
        /// Perform the real seek work: set underlying stream position to correct ciphertext block,
        /// create a decryptor with appropriate IV (previous ciphertext block or base IV),
        /// and populate the plain buffer if seeking into the middle of a block.
        /// </summary>
        private async Task SeekInternalAsync(long offset, CancellationToken ct)
        {
            if (offset < 0 || offset > _mLimit) throw new ArgumentOutOfRangeException(nameof(offset));

            long blockIndex = offset / BlockSize;
            int blockOffset = (int)(offset % BlockSize);

            long cipherPosForRead = (blockIndex > 0) ? (blockIndex - 1) * BlockSize : 0L;
            _mStream.Seek(cipherPosForRead, SeekOrigin.Begin);

            // determine IV: previous ciphertext block (if blockIndex>0) or base IV
            var iv = new byte[BlockSize];
            if (blockIndex > 0)
            {
                int read = 0;
                while (read < BlockSize)
                {
                    int r = await _mStream.ReadAsync(iv, read, BlockSize - read, ct).ConfigureAwait(false);
                    if (r == 0) throw new EndOfStreamException("Unable to read previous block for IV during seek.");
                    read += r;
                }
                // after this read, stream at start of target block
            }
            else
            {
                Buffer.BlockCopy(_mBaseIv, 0, iv, 0, BlockSize);
                // stream already at start (0)
            }

            // recreate decryptor using existing Aes instance
            _mDecoder?.Dispose();
            _mDecoder = _aes.CreateDecryptor(_mKey, iv);

            // reset plain buffer
            _plainStart = _plainEnd = 0;

            // logical written position is start of block
            _mWritten = offset - blockOffset;

            // If seeking into middle of block, decrypt that block and store remainder
            if (blockOffset > 0)
            {
                var block = new byte[BlockSize];
                int read = 0;
                while (read < BlockSize)
                {
                    int r = await _mStream.ReadAsync(block, read, BlockSize - read, ct).ConfigureAwait(false);
                    if (r == 0) throw new EndOfStreamException("Unable to read target block during seek.");
                    read += r;
                }

                var tempPlain = new byte[BlockSize];
                int dec = _mDecoder.TransformBlock(block, 0, BlockSize, tempPlain, 0);
                if (dec != BlockSize) throw new CryptographicException("Unexpected decrypted block size during seek.");

                int remainder = BlockSize - blockOffset;
                if (remainder > 0)
                {
                    if (remainder > _plainBuffer.Length)
                        throw new InvalidOperationException("Plain buffer too small for remainder.");
                    Buffer.BlockCopy(tempPlain, blockOffset, _plainBuffer, 0, remainder);
                    _plainStart = 0;
                    _plainEnd = remainder;
                }

                // set written to match requested offset
                _mWritten = offset; // offset - blockOffset + blockOffset
            }
        }

        public override void Close()
        {
            Dispose(true);
            base.Close();
        }
    }
}
