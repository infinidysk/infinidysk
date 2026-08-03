using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.Crypto;

/// <summary>
/// AES-CBC decryptor with a single-block carry, used by both the RAR header block reader
/// and the RAR packed-data wrapper. This is the single home for the RAR AES decrypt +
/// carry orchestration so the logic is not duplicated between the sync and async readers.
/// </summary>
internal sealed class RarAesDecryptor : IDisposable
{
    private readonly BlockTransformer _rijndael;
    private RarAesDecryptCarry _carry;

    public RarAesDecryptor(ICryptoTransform transformer)
    {
        _rijndael = new BlockTransformer(transformer);
        _carry = new RarAesDecryptCarry();
    }

    /// <summary>
    /// Number of leftover decrypted bytes currently held in the carry.
    /// </summary>
    public int CarryCount => _carry.Count;

    /// <summary>
    /// Fills <paramref name="destination"/> with decrypted bytes, reading and decrypting
    /// aligned ciphertext from <paramref name="stream"/> as needed. Leftover plaintext from
    /// the final AES block is retained in the carry for the next read.
    /// </summary>
    public void Fill(Stream stream, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        var written = _carry.Read(destination);
        if (written >= destination.Length)
        {
            return;
        }

        var sizeToRead = destination.Length - written;
        var alignedSize = AlignedSize(sizeToRead);
        var cipherText = ArrayPool<byte>.Shared.Rent(alignedSize);
        try
        {
            ReadExactlyOrThrow(stream, cipherText.AsSpan(0, alignedSize));
            DecryptBlock(cipherText, alignedSize, destination.Slice(written), sizeToRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipherText, clearArray: true);
        }
    }

    /// <summary>
    /// Asynchronous twin of <see cref="Fill"/>. Only the ciphertext read differs; the
    /// decrypt + carry step is shared through <see cref="DecryptBlock"/>.
    /// </summary>
    public async ValueTask FillAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken
    )
    {
        if (destination.IsEmpty)
        {
            return;
        }

        var written = _carry.Read(destination.Span);
        if (written >= destination.Length)
        {
            return;
        }

        var sizeToRead = destination.Length - written;
        var alignedSize = AlignedSize(sizeToRead);
        var cipherText = ArrayPool<byte>.Shared.Rent(alignedSize);
        try
        {
            await ReadExactlyOrThrowAsync(
                    stream,
                    cipherText.AsMemory(0, alignedSize),
                    cancellationToken
                )
                .ConfigureAwait(false);
            DecryptBlock(cipherText, alignedSize, destination.Span.Slice(written), sizeToRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipherText, clearArray: true);
        }
    }

    // Rounds a byte count up to the next multiple of the AES block size (16).
    private static int AlignedSize(int sizeToRead) => sizeToRead + ((~sizeToRead + 1) & 0xf);

    // Decrypts a block-aligned ciphertext, copies the requested plaintext into the
    // destination, and retains any decrypted overshoot in the carry. This is the single
    // shared decrypt core.
    private void DecryptBlock(
        byte[] cipherText,
        int alignedSize,
        Span<byte> destination,
        int sizeToRead
    )
    {
        var plainText = ArrayPool<byte>.Shared.Rent(alignedSize);
        try
        {
            _rijndael.Process(cipherText, 0, alignedSize, plainText, 0);
            plainText.AsSpan(0, sizeToRead).CopyTo(destination);
            if (alignedSize > sizeToRead)
            {
                _carry.Store(plainText.AsSpan(sizeToRead, alignedSize - sizeToRead));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plainText, clearArray: true);
        }
    }

    private static void ReadExactlyOrThrow(Stream stream, Span<byte> buffer)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidFormatException("Unexpected end of encrypted stream", e);
        }
    }

    private static async ValueTask ReadExactlyOrThrowAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidFormatException("Unexpected end of encrypted stream", e);
        }
    }

    public void Dispose() => _rijndael.Dispose();
}
