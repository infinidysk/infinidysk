using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Common.Rar;
using SharpCompress.Crypto;
using SharpCompress.IO;
using SharpCompress.Readers;

namespace SharpCompress.Common.Rar.Headers;

public partial class RarHeaderFactory
{
    private bool _isRar5;

    private Rar5CryptoInfo? _cryptInfo;

    // Seekable mode only: absolute position to reposition to on the next enumerator advance,
    // deferring the seek past a header's packed data until after it has been yielded. This lets
    // consumers that stop after a match avoid a data seek entirely (see issue #118).
    private long? _pendingSkipPosition;

    public RarHeaderFactory(StreamingMode mode, ReaderOptions options)
    {
        StreamingMode = mode;
        Options = options;
    }

    public ReaderOptions Options { get; }
    public StreamingMode StreamingMode { get; }
    public bool IsEncrypted { get; private set; }

    /// <summary>
    /// Enumerates RAR headers from <paramref name="stream"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Header-parse failures are surfaced as <see cref="RarHeaderReadException"/>:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <see cref="RarHeaderReadException.Truncated"/> = <c>true</c> — unexpected EOF while
    /// reading the signature, or seek-past-end while skipping packed data.
    /// </item>
    /// <item>
    /// <see cref="RarHeaderReadException.Truncated"/> = <c>false</c> — CRC mismatch, unknown
    /// header code, missing signature after a full scan, or unsupported format.
    /// </item>
    /// </list>
    /// <para>
    /// Prefer matching <see cref="RarHeaderReadException"/> over raw BCL types
    /// (<see cref="ArgumentOutOfRangeException"/>, <see cref="EndOfStreamException"/>,
    /// <see cref="IncompleteArchiveException"/>).
    /// </para>
    /// <para>
    /// Mid-enumeration EOF while filling the next header block (after a successful mark /
    /// prior headers) ends the enumeration gracefully (returns) for back-compat with archives
    /// that omit an EndArchive marker. Signature-scan EOF and seek-past-end during packed-data
    /// skip still throw with <see cref="RarHeaderReadException.Truncated"/> = <c>true</c>.
    /// </para>
    /// </remarks>
    public IEnumerable<IRarHeader> ReadHeaders(Stream stream)
    {
        _pendingSkipPosition = null;
        var markHeader = MarkHeader.Read(stream, Options.LeaveStreamOpen, Options.LookForHeader);
        _isRar5 = markHeader.IsRar5;
        yield return markHeader;

        RarHeader? header;
        while ((header = TryReadNextHeader(stream)) != null)
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

    // Builds the per-header AES decryptor when header encryption is active. The salt (RAR3) or
    // initialization vector (RAR5) precedes each encrypted header block as plaintext and is
    // consumed here before the encrypted block is read.
    private RarAesDecryptor? CreateHeaderDecryptor(Stream stream)
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
            _cryptInfo.InitV = RarBlockBuffer.ReadStreamBytes(stream, EncryptionConstV5.SIZE_INITV);
            var headerKey = new CryptKey5(Options.Password, _cryptInfo);
            return new RarAesDecryptor(headerKey.Transformer(_cryptInfo.Salt));
        }

        var salt = RarBlockBuffer.ReadStreamBytes(stream, EncryptionConstV5.SIZE_SALT30);
        var key = new CryptKey3(Options.Password);
        return new RarAesDecryptor(key.Transformer(salt));
    }

    private RarHeader? TryReadNextHeader(Stream stream)
    {
        // Reposition past the previous header's packed data before reading the next header.
        // Deferred in seekable mode so stopping after a match performs no data seek.
        ApplyPendingSkip(stream);

        // The per-header salt/IV read mirrors the previous readers, which read it eagerly and
        // let a short read surface; only the header block fill is treated as a graceful end.
        var decryptor = CreateHeaderDecryptor(stream);
        RarBlockBuffer buffer;
        try
        {
            buffer = RarBlockBuffer.ReadHeaderBlock(stream, _isRar5, decryptor);
        }
        catch (RarHeaderReadException ex) when (ex.Truncated)
        {
            // Archives may end without an EndArchive marker; treat mid-enumeration EOF as
            // graceful end of headers (same soft-end as the pre-#119 InvalidFormatException path).
            // MarkHeader / ApplyPendingSkip still throw Truncated for true early truncation.
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
                                stream.Skip(ph.DataSize);
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
                        // on the next advance even when the consumer never reads it (previously
                        // seekable CMT left the stream sitting on comment data).
                        if (StreamingMode == StreamingMode.Seekable)
                        {
                            fh.DataStartPosition = stream.Position;
                            _pendingSkipPosition = fh.DataStartPosition + fh.CompressedSize;
                        }
                        fh.PackedStream = new ReadOnlySubStream(stream, fh.CompressedSize);
                    }
                    else
                    {
                        SkipData(fh, stream);
                    }
                    return fh;
                }

                case HeaderCodeV.RAR4_NEW_SUB_HEADER:
                {
                    var fh = FileHeader.Create(header, buffer, HeaderType.NewSub);
                    SkipData(fh, stream);
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

    private void SkipData(FileHeader fh, Stream stream)
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
                    stream.Skip(fh.CompressedSize);
                }
                break;
            default:
            {
                throw new InvalidFormatException("Invalid StreamingMode");
            }
        }
    }

    private void ApplyPendingSkip(Stream stream)
    {
        if (_pendingSkipPosition is not { } position)
        {
            return;
        }

        try
        {
            stream.Position = position;
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new RarHeaderReadException(
                $"Failed to skip packed data to position {position} (seek past stream end).",
                truncated: true,
                e
            );
        }

        _pendingSkipPosition = null;
    }
}
