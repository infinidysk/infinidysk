using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.StreamTrace;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Where a multipart volume sits in its file, and whether that file is encrypted.
/// Carried into read failures and warnings so a shortfall can be told apart from
/// ordinary end-of-part behaviour without correlating several log lines.
/// </summary>
public sealed record MultipartPartContext
{
    public required int PartNumber { get; init; }
    public required int PartCount { get; init; }
    public required long SeekOffsetWithinPart { get; init; }
    public required long DeclaredVolumeLength { get; init; }
    public required bool IsEncrypted { get; init; }
}

/// <summary>
/// Caps a stream at a declared length. An encrypted file may pad a block-scale shortfall
/// with zeros so the AES blocks after it stay aligned; every larger or unexplained
/// shortfall means the volume is missing data, and the read fails rather than passing
/// zeros off as content.
/// </summary>
public sealed class PaddedLengthStream(
    Stream stream,
    long length,
    string partId,
    string? fileName = null,
    MultipartPartContext? context = null) : FastReadOnlyNonSeekableStream
{
    private readonly string _fileName = string.IsNullOrEmpty(fileName) ? "unknown" : fileName;
    private long _position;
    private bool _underlyingEnded;
    private bool _shortfallReported;
    private bool _disposed;

    public override long Length => length;
    public override long Position => _position;

    // An encrypted volume can end a fraction of an AES block short, and every later block
    // depends on that alignment, so those few bytes are worth padding. Anything beyond one
    // block is missing ciphertext: padding it decrypts to garbage the player cannot tell
    // from content. Unencrypted files (and callers that pass no context) are always held to
    // the declared length.
    private const int MaxAlignmentPaddingBytes = 16;

    private bool ShouldPadShortfall(long bytes) =>
        (context?.IsEncrypted ?? false) && bytes <= MaxAlignmentPaddingBytes;

    public override void Flush() => stream.Flush();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= length)
            return 0;

        var bytesToRead = (int)Math.Min(length - _position, buffer.Length);
        if (!_underlyingEnded)
        {
            var bytesRead = await stream.ReadAsync(buffer[..bytesToRead], cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead > 0)
            {
                _position += bytesRead;
                return bytesRead;
            }

            _underlyingEnded = true;
            OnShortfall(length - _position);
        }

        buffer.Span[..bytesToRead].Clear();
        _position += bytesToRead;
        return bytesToRead;
    }

    private void OnShortfall(long bytes)
    {
        if (!ShouldPadShortfall(bytes))
            throw new IncompleteMultipartPartException(BuildShortfallMessage(bytes));

        if (_shortfallReported)
            return;

        _shortfallReported = true;
        ZeroFillLogLimiter.Write(
            "Encrypted packed part {SegmentId} ended early while reading {FileName}. " +
            "Zero-filling {Bytes} bytes to keep the following AES blocks aligned.",
            partId,
            _fileName,
            bytes,
            context: Describe());

        if (MultiProviderNntpClient.CurrentReadSessionId is { } sessionId)
            StreamTrace.TryZeroFill(sessionId, partId, bytes);
    }

    private string BuildShortfallMessage(long bytes)
    {
        var message =
            $"Packed part {partId} of \"{_fileName}\" ended {bytes} bytes early " +
            $"(delivered {_position} of {length} expected bytes). {Describe()}";
        Log.Debug("Failing multipart read: {Reason}", message);
        return message;
    }

    private string Describe() =>
        context is null
            ? "No multipart context was recorded for this part."
            : $"Part {context.PartNumber} of {context.PartCount}, " +
              $"declared volume length {context.DeclaredVolumeLength}, " +
              $"read from offset {context.SeekOffsetWithinPart} within the part, " +
              $"encrypted: {context.IsEncrypted}.";

    protected override void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
            return;

        stream.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await stream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
