using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private static readonly byte[] DateCommand = "DATE\r\n"u8.ToArray();

    private static readonly PipeOptions RawBodyPipeOptions = new(
        pauseWriterThreshold: 1024 * 1024,
        resumeWriterThreshold: 512 * 1024,
        minimumSegmentSize: 8 * 1024,
        useSynchronizationContext: false);

    /// <summary>
    /// Replaces the connection stream for deterministic mid-write failure tests.
    /// </summary>
    internal void ReplaceConnectionStreamForTests(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        _stream = stream;
    }

    private void CleanupConnection(bool createNewLifetime = true)
    {
        Volatile.Write(ref _connectionState, 0);
        lock (_connectionCtsLock)
        {
            _connectionCts.Cancel();
            _connectionCts.Dispose();
            if (createNewLifetime)
            {
                _connectionCts = new CancellationTokenSource();
            }
        }

        DisposeConnectionResource(_reader);
        DisposeConnectionResource(_stream);
        DisposeConnectionResource(_tcpClient);

        _reader = null;
        _stream = null;
        _tcpClient = null;

        lock (_stateLock)
        {
            _backgroundException = null;
        }
    }

    private static void DisposeConnectionResource(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (IOException)
        {
            // A failed connection may also fail while flushing or closing.
        }
        catch (ObjectDisposedException)
        {
            // Concurrent network cancellation may have already closed the resource.
        }
    }

    private static int ParseResponseCode(string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            throw new UsenetProtocolException($"Invalid NNTP response: {response}");
        }

        return ParseResponseCode(response.AsSpan());
    }

    private static int ParseResponseCode(ReadOnlySpan<char> response)
    {
        if (response.Length < 3 ||
            response[0] is < '1' or > '5' ||
            !char.IsAsciiDigit(response[1]) ||
            !char.IsAsciiDigit(response[2]) ||
            (response.Length > 3 && response[3] != ' '))
        {
            throw new UsenetProtocolException($"Invalid NNTP response: {response}");
        }

        return (response[0] - '0') * 100 + (response[1] - '0') * 10 + (response[2] - '0');
    }

    // Response codes that are always followed by a multi-line data block
    // (RFC 3977 Appendix C; 211 is only multi-line for LISTGROUP, which this
    // client never issues, so it is intentionally excluded).
    private static bool IsMultiLineCode(int code) => code is
        100 or 101 or 215 or 220 or 221 or 222 or 224 or 225 or 230 or 231;

    private async ValueTask DrainUnexpectedMultiLineAsync(int code, CancellationToken _)
    {
        if (!IsMultiLineCode(code))
        {
            return;
        }

        // Bound the drain so a hostile payload cannot pin the connection.
        var drainFailure = await TryDrainBodyAsync().ConfigureAwait(false);
        if (drainFailure != null)
        {
            RecordConnectionFailure(drainFailure);
        }
    }

    /// <summary>
    /// Writes a single-line command and reads its status line through one coalesced
    /// I/O timeout shared by the write and the read. Any failure after command bytes
    /// may be on the wire poisons the session (RFC 3977 §3.5).
    /// </summary>
    private async ValueTask<(int Code, string Line)> ExchangeSingleLineAsync<TState>(
        CoalescedReadTimeout ioTimeout,
        TState state,
        Func<UsenetClient, TState, CoalescedReadTimeout, ValueTask> writeCommand)
    {
        try
        {
            await writeCommand(this, state, ioTimeout).ConfigureAwait(false);
            var line = await ReadLineAsync(ioTimeout).ConfigureAwait(false)
                ?? throw new UsenetProtocolException(
                    "The NNTP connection closed before a response was received.");
            return (ParseResponseCode(line), line);
        }
        catch (Exception e)
        {
            // Once bytes may be on the wire the response FIFO cannot be trusted.
            RecordConnectionFailure(e);
            throw;
        }
    }

    private void ThrowIfNotConnected()
    {
        if (_reader == null || _tcpClient == null || !_tcpClient.Connected)
        {
            throw new UsenetNotConnectedException("Not connected to server. Call ConnectAsync first.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private void ThrowIfUnhealthy()
    {
        var backgroundException = _backgroundException?.SourceException;
        if (backgroundException != null)
        {
            throw new UsenetProtocolException(
                "connection unusable after an earlier interrupted operation",
                backgroundException);
        }
    }

    private CancellationTokenSource CreateOperationTokenSource(CancellationToken cancellationToken)
    {
        lock (_connectionCtsLock)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _connectionCts.Token);
        }
    }

    private void CancelConnectionLifetime()
    {
        lock (_connectionCtsLock)
        {
            try
            {
                _connectionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A concurrent cleanup has already ended this connection lifetime.
            }
        }
    }

    private static void ValidateSegmentId(SegmentId segmentId)
    {
        var value = segmentId.Value;
        // 250 octets including <> that the client adds (RFC 5536 §3.1.3).
        ValidateCommandValue(value, nameof(segmentId), 248);
        if (value.Length < 3 || value[0] == '@' || value[^1] == '@' || !value.Contains('@') ||
            value.Contains('<') || value.Contains('>') || ContainsWhitespace(value))
        {
            throw new ArgumentException("Segment ID must be a valid NNTP message-id without angle brackets.",
                nameof(segmentId));
        }
    }

    private static void ValidateCommandValue(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ValidateCommandValue(value.AsSpan(), parameterName, maximumLength);
    }

    private static void ValidateCommandValue(
        ReadOnlySpan<char> value,
        string parameterName,
        int maximumLength)
    {
        if (value.Length == 0 || value.Length > maximumLength ||
            ContainsControlCharacter(value))
        {
            throw new ArgumentException(
                $"Value must contain 1-{maximumLength} characters and no control characters.",
                parameterName);
        }
    }

    private static bool ContainsWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsControlCharacter(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private CancellationTokenSource CreateCtsWithTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.ReadTimeout);
        return cts;
    }

    private ValueTask WriteAuthInfoCommandAsync(
        ReadOnlySpan<char> keyword,
        string argument,
        CoalescedReadTimeout ioTimeout)
    {
        // "AUTHINFO " + keyword + ' ' + argument + CRLF
        var length = 9 + keyword.Length + 1 + argument.Length + 2;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var destination = buffer.AsSpan(0, length);
            var written = Encoding.Latin1.GetBytes("AUTHINFO ", destination);
            written += Encoding.Latin1.GetBytes(keyword, destination[written..]);
            destination[written++] = (byte)' ';
            written += Encoding.Latin1.GetBytes(argument, destination[written..]);
            destination[written++] = (byte)'\r';
            destination[written++] = (byte)'\n';
            return WritePooledCommandAsync(buffer, written, ioTimeout);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private ValueTask WriteMessageIdCommandAsync(
        string command,
        SegmentId segmentId,
        CoalescedReadTimeout ioTimeout)
    {
        var messageId = segmentId.Value;
        var length = command.Length + messageId.Length + 5;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var destination = buffer.AsSpan(0, length);
            var written = Encoding.Latin1.GetBytes(command, destination);
            destination[written++] = (byte)' ';
            destination[written++] = (byte)'<';
            written += Encoding.Latin1.GetBytes(messageId, destination[written..]);
            destination[written++] = (byte)'>';
            destination[written++] = (byte)'\r';
            destination[written++] = (byte)'\n';
            return WritePooledCommandAsync(buffer, written, ioTimeout);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private async ValueTask WritePooledCommandAsync(
        byte[] buffer,
        int length,
        CoalescedReadTimeout ioTimeout)
    {
        try
        {
            ioTimeout.BeginIo();
            try
            {
                await _stream!.WriteAsync(buffer.AsMemory(0, length), ioTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ioTimeout.IsTimeoutCancellation)
            {
                throw new TimeoutException("Timeout writing to NNTP stream.");
            }
            finally
            {
                ioTimeout.EndIo();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask WriteCommandAsync(
        ReadOnlyMemory<byte> command,
        CancellationToken cancellationToken)
    {
        using var cts = CreateCtsWithTimeout(cancellationToken);
        try
        {
            await _stream!.WriteAsync(command, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout writing to NNTP stream.");
        }
    }

    private async ValueTask WriteCommandAsync(
        ReadOnlyMemory<byte> command,
        CoalescedReadTimeout ioTimeout)
    {
        ioTimeout.BeginIo();
        try
        {
            await _stream!.WriteAsync(command, ioTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ioTimeout.IsTimeoutCancellation)
        {
            throw new TimeoutException("Timeout writing to NNTP stream.");
        }
        finally
        {
            ioTimeout.EndIo();
        }
    }

    private async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        using var cts = CreateCtsWithTimeout(ct);
        try
        {
            return await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
    }

    private async ValueTask<string?> ReadLineAsync(CoalescedReadTimeout ioTimeout)
    {
        ioTimeout.BeginIo();
        try
        {
            return await _reader!.ReadLineAsync(ioTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ioTimeout.IsTimeoutCancellation)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
        finally
        {
            ioTimeout.EndIo();
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>?> ReadLineBytesAsync(
        CoalescedReadTimeout readTimeout)
    {
        readTimeout.BeginIo();
        try
        {
            return await _reader!.ReadLineBytesAsync(readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (readTimeout.IsTimeoutCancellation)
        {
            throw new TimeoutException("Timeout reading from NNTP stream.");
        }
        finally
        {
            readTimeout.EndIo();
        }
    }

    private async ValueTask<T> RunWithTimeoutAsync<T>(Func<CancellationToken, ValueTask<T>> func, CancellationToken ct)
    {
        using var cts = CreateCtsWithTimeout(ct);
        try
        {
            return await func(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout encountered within NNTP stream.");
        }
    }
}
