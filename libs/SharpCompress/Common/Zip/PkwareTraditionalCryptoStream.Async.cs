using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Common.Zip;

internal partial class PkwareTraditionalCryptoStream
{
    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (_mode == CryptoMode.Encrypt)
        {
            throw new NotSupportedException("This stream does not encrypt via Read()");
        }

        ThrowHelper.ThrowIfNull(buffer);

        var readBytes = await _stream
            .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        _encryptor.Decrypt(buffer.AsSpan(offset, readBytes));
        return readBytes;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_mode == CryptoMode.Encrypt)
        {
            throw new NotSupportedException("This stream does not encrypt via Read()");
        }

        var readBytes = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _encryptor.Decrypt(buffer.Span[..readBytes]);
        return readBytes;
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (_mode == CryptoMode.Decrypt)
        {
            throw new NotSupportedException("This stream does not Decrypt via Write()");
        }

        if (count == 0)
        {
            return;
        }

        byte[] plaintext;
        if (offset != 0)
        {
            plaintext = new byte[count];
            Buffer.BlockCopy(buffer, offset, plaintext, 0, count);
        }
        else
        {
            plaintext = buffer;
        }

        var encrypted = _encryptor.Encrypt(plaintext, count);
        await _stream
            .WriteAsync(encrypted.AsMemory(0, encrypted.Length), cancellationToken)
            .ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_mode == CryptoMode.Decrypt)
        {
            throw new NotSupportedException("This stream does not Decrypt via Write()");
        }

        if (buffer.Length == 0)
        {
            return;
        }

        byte[] plaintext;
        if (buffer.Span.Overlaps(buffer.Span))
        {
            plaintext = buffer.ToArray();
        }
        else
        {
            plaintext = new byte[buffer.Length];
            buffer.CopyTo(plaintext);
        }

        var encrypted = _encryptor.Encrypt(plaintext, buffer.Length);
        await _stream
            .WriteAsync(encrypted.AsMemory(0, encrypted.Length), cancellationToken)
            .ConfigureAwait(false);
    }
}
