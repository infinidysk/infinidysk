using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private static readonly byte[] QuitCommand = "QUIT\r\n"u8.ToArray();

    /// <summary>
    /// Sends QUIT and closes the connection after the server acknowledges (RFC 3977 §5.4).
    /// </summary>
    public async Task<UsenetResponse> QuitAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            using var operationCts = CreateOperationTokenSource(cancellationToken);
            using var ioTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);
            var (code, line) = await ExchangeSingleLineAsync(
                ioTimeout,
                QuitCommand,
                static (self, command, timeout) => self.WriteCommandAsync(command, timeout))
                .ConfigureAwait(false);
            CleanupConnection();
            return new UsenetResponse { ResponseCode = code, ResponseMessage = line };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task TryQuitBestEffortAsync()
    {
        Exception? backgroundException;
        lock (_stateLock)
        {
            backgroundException = _backgroundException?.SourceException;
        }

        // Do not use IsHealthy: DisposeAsync marks the client disposed first,
        // which would make IsConnected/IsHealthy false before QUIT can run.
        if (backgroundException != null ||
            _stream == null ||
            _reader == null ||
            !_commandLock.TryWait())
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await WriteCommandAsync(QuitCommand, cts.Token).ConfigureAwait(false);
                _ = await ReadLineAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort dispose path; swallow all failures.
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
