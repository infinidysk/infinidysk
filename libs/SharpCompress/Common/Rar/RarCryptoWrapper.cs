using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Crypto;

namespace SharpCompress.Common.Rar;

internal sealed class RarCryptoWrapper : Stream
{
    private readonly Stream _actualStream;
    private readonly RarAesDecryptor _decryptor;

    public RarCryptoWrapper(Stream actualStream, byte[] salt, ICryptKey key)
    {
        _actualStream = actualStream;
        // Packed-data decryption shares the single AES decrypt + carry engine used by the
        // RAR header block reader.
        _decryptor = new RarAesDecryptor(key.Transformer(salt));
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAndDecrypt(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => ReadAndDecrypt(buffer);

    public int ReadAndDecrypt(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        _decryptor.Fill(_actualStream, buffer);
        return buffer.Length;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => ReadAndDecryptAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => await ReadAndDecryptAsync(buffer, cancellationToken).ConfigureAwait(false);

    private async ValueTask<int> ReadAndDecryptAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (buffer.IsEmpty)
        {
            return 0;
        }

        await _decryptor.FillAsync(_actualStream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get; set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _decryptor.Dispose();
        }

        base.Dispose(disposing);
    }
}
