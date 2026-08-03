using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.Rar;
using SharpCompress.Crypto;
using SharpCompress.IO;
using SharpCompress.Readers;

namespace SharpCompress.Common.Rar.Headers;

public partial class RarHeaderFactory
{
    /// <inheritdoc cref="ReadHeaders"/>
    public async IAsyncEnumerable<IRarHeader> ReadHeadersAsync(Stream stream)
    {
        _pendingSkipPosition = null;
        var markHeader = await MarkHeader
            .ReadAsync(
                stream,
                Options.LeaveStreamOpen,
                Options.LookForHeader,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        _isRar5 = markHeader.IsRar5;
        yield return markHeader;

        RarHeader? header;
        while (
            (
                header = await TryReadNextHeaderAsync(stream, CancellationToken.None)
                    .ConfigureAwait(false)
            ) != null
        )
        {
            yield return header;
            if (header.HeaderType == HeaderType.EndArchive)
            {
                // End of archive marker. RAR does not read anything after this header letting to use third
                // party tools to add extra information such as a digital signature to archive.
                yield break;
            }
        }
    }

    // Async twin of CreateHeaderDecryptor: only the plaintext salt/IV read differs.
    private async ValueTask<RarAesDecryptor?> CreateHeaderDecryptorAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        if (!IsEncrypted)
        {
            return null;
        }

        if (Options.Password is null)
        {
            throw new CryptographicException("Encrypted Rar archive has no password specified.");
        }

        if (_isRar5 && _cryptInfo != null)
        {
            _cryptInfo.InitV = await RarBlockBuffer
                .ReadStreamBytesAsync(stream, EncryptionConstV5.SIZE_INITV, cancellationToken)
                .ConfigureAwait(false);
            var headerKey = new CryptKey5(Options.Password, _cryptInfo);
            return new RarAesDecryptor(headerKey.Transformer(_cryptInfo.Salt));
        }

        var salt = await RarBlockBuffer
            .ReadStreamBytesAsync(stream, EncryptionConstV5.SIZE_SALT30, cancellationToken)
            .ConfigureAwait(false);
        var key = new CryptKey3(Options.Password);
        return new RarAesDecryptor(key.Transformer(salt));
    }

    private async ValueTask<RarHeader?> TryReadNextHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        // Reposition past the previous header's packed data before reading the next header.
        // Deferred in seekable mode so stopping after a match performs no data seek.
        ApplyPendingSkip(stream);

        // The per-header salt/IV read mirrors the previous readers, which read it eagerly and
        // let a short read surface; only the header block fill is treated as a graceful end.
        var decryptor = await CreateHeaderDecryptorAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        RarBlockBuffer buffer;
        try
        {
            buffer = await RarBlockBuffer
                .ReadHeaderBlockAsync(stream, _isRar5, decryptor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RarHeaderReadException ex) when (ex.Truncated)
        {
            // Archives may end without an EndArchive marker; treat mid-enumeration EOF as
            // graceful end of headers (same soft-end as the pre-#119 InvalidFormatException path).
            decryptor?.Dispose();
            return null;
        }
        catch (InvalidFormatException)
        {
            decryptor?.Dispose();
            return null;
        }

        using (decryptor)
        using (buffer)
        {
            // Header field decoding is IO-free and shared with the synchronous path; only the
            // trailing data skip in Streaming mode needs to await.
            var header = RarHeader.TryReadBase(buffer, _isRar5, Options.ArchiveEncoding);
            if (header is null)
            {
                return null;
            }
            switch (header.HeaderCode)
            {
                case HeaderCodeV.RAR5_ARCHIVE_HEADER:
                case HeaderCodeV.RAR4_ARCHIVE_HEADER:
                {
                    var ah = ArchiveHeader.Create(header, buffer);
                    if (ah.IsEncrypted == true)
                    {
                        //!!! rar5 we don't know yet
                        IsEncrypted = true;
                    }
                    return ah;
                }

                case HeaderCodeV.RAR4_PROTECT_HEADER:
                {
                    var ph = ProtectHeader.Create(header, buffer);
                    // skip the recovery record data, we do not use it.
                    switch (StreamingMode)
                    {
                        case StreamingMode.Seekable:
                            {
                                stream.Position += ph.DataSize;
                            }
                            break;
                        case StreamingMode.Streaming:
                            {
                                await stream
                                    .SkipAsync(ph.DataSize, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            break;
                        default:
                        {
                            throw new InvalidFormatException("Invalid StreamingMode");
                        }
                    }
                    return ph;
                }

                case HeaderCodeV.RAR5_SERVICE_HEADER:
                {
                    var fh = FileHeader.Create(header, buffer, HeaderType.Service);
                    if (fh.FileName == "CMT")
                    {
                        // Expose the comment as a readable substream. In seekable mode also
                        // record a deferred skip so the stream is repositioned past the comment
                        // on the next advance even when the consumer never reads it.
                        if (StreamingMode == StreamingMode.Seekable)
                        {
                            fh.DataStartPosition = stream.Position;
                            _pendingSkipPosition = fh.DataStartPosition + fh.CompressedSize;
                        }
                        fh.PackedStream = new ReadOnlySubStream(stream, fh.CompressedSize);
                    }
                    else
                    {
                        await SkipDataAsync(fh, stream, cancellationToken).ConfigureAwait(false);
                    }
                    return fh;
                }

                case HeaderCodeV.RAR4_NEW_SUB_HEADER:
                {
                    var fh = FileHeader.Create(header, buffer, HeaderType.NewSub);
                    await SkipDataAsync(fh, stream, cancellationToken).ConfigureAwait(false);
                    return fh;
                }

                case HeaderCodeV.RAR5_FILE_HEADER:
                case HeaderCodeV.RAR4_FILE_HEADER:
                {
                    var fh = FileHeader.Create(header, buffer, HeaderType.File);
                    switch (StreamingMode)
                    {
                        case StreamingMode.Seekable:
                            {
                                // Defer the seek past packed data until the next advance so
                                // stop-after-match performs no data seek.
                                fh.DataStartPosition = stream.Position;
                                _pendingSkipPosition = fh.DataStartPosition + fh.CompressedSize;
                            }
                            break;
                        case StreamingMode.Streaming:
                            {
                                var ms = new ReadOnlySubStream(stream, fh.CompressedSize);
                                if (fh.R4Salt is null && fh.Rar5CryptoInfo is null)
                                {
                                    fh.PackedStream = ms;
                                }
                                else
                                {
                                    fh.PackedStream = new RarCryptoWrapper(
                                        ms,
                                        fh.R4Salt is null
                                            ? fh.Rar5CryptoInfo.NotNull().Salt
                                            : fh.R4Salt,
                                        fh.R4Salt is null
                                            ? new CryptKey5(
                                                Options.Password,
                                                fh.Rar5CryptoInfo.NotNull()
                                            )
                                            : new CryptKey3(Options.Password)
                                    );
                                }
                            }
                            break;
                        default:
                        {
                            throw new InvalidFormatException("Invalid StreamingMode");
                        }
                    }
                    return fh;
                }
                case HeaderCodeV.RAR5_END_ARCHIVE_HEADER:
                case HeaderCodeV.RAR4_END_ARCHIVE_HEADER:
                {
                    return EndArchiveHeader.Create(header, buffer);
                }
                case HeaderCodeV.RAR5_ARCHIVE_ENCRYPTION_HEADER:
                {
                    var cryptoHeader = ArchiveCryptHeader.Create(header, buffer);
                    IsEncrypted = true;
                    _cryptInfo = cryptoHeader.CryptInfo;

                    return cryptoHeader;
                }
                default:
                {
                    throw new RarHeaderReadException(
                        "Unknown Rar Header: " + header.HeaderCode,
                        truncated: false
                    );
                }
            }
        }
    }

    private async ValueTask SkipDataAsync(
        FileHeader fh,
        Stream stream,
        CancellationToken cancellationToken
    )
    {
        switch (StreamingMode)
        {
            case StreamingMode.Seekable:
                {
                    // Defer the seek past packed data until the next advance.
                    fh.DataStartPosition = stream.Position;
                    _pendingSkipPosition = fh.DataStartPosition + fh.CompressedSize;
                }
                break;
            case StreamingMode.Streaming:
                {
                    //skip the data because it's useless?
                    await stream
                        .SkipAsync(fh.CompressedSize, cancellationToken)
                        .ConfigureAwait(false);
                }
                break;
            default:
            {
                throw new InvalidFormatException("Invalid StreamingMode");
            }
        }
    }
}
