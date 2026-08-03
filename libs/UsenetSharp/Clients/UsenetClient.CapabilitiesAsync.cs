using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private static readonly byte[] CapabilitiesCommand = "CAPABILITIES\r\n"u8.ToArray();
    private static readonly byte[] ModeReaderCommand = "MODE READER\r\n"u8.ToArray();

    /// <summary>
    /// Issues CAPABILITIES and returns the advertised capability labels (RFC 3977 §5.2).
    /// </summary>
    /// <remarks>
    /// Not issued automatically on connect. Per RFC 8143, implicit TLS plus this
    /// client's fixed command set makes CAPABILITIES optional in practice.
    /// Unknown labels are exposed unfiltered (clients MUST ignore unknowns).
    /// </remarks>
    public async Task<UsenetCapabilitiesResponse> CapabilitiesAsync(CancellationToken cancellationToken)
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

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                CapabilitiesCommand,
                static (self, command, timeout) => self.WriteCommandAsync(command, timeout))
                .ConfigureAwait(false);

            if (responseCode != (int)UsenetResponseType.CapabilityListFollows)
            {
                await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                    .ConfigureAwait(false);
                return new UsenetCapabilitiesResponse
                {
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    Capabilities = []
                };
            }

            List<string> capabilities;
            try
            {
                capabilities = await ReadCapabilityLinesAsync(operationCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                RecordConnectionFailure(e);            // framing failure: FIFO unknown
                throw;
            }

            if (capabilities.Count == 0 ||
                !capabilities[0].StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase))
            {
                // Block fully consumed — connection is at a clean boundary; do not poison.
                throw new UsenetProtocolException(
                    "CAPABILITIES response must begin with VERSION 2.");
            }

            return new UsenetCapabilitiesResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                Capabilities = capabilities
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// Issues MODE READER for mode-switching servers (RFC 3977 §5.3).
    /// </summary>
    public async Task<UsenetResponse> ModeReaderAsync(CancellationToken cancellationToken)
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

            var (responseCode, response) = await ExchangeSingleLineAsync(
                ioTimeout,
                ModeReaderCommand,
                static (self, command, timeout) => self.WriteCommandAsync(command, timeout))
                .ConfigureAwait(false);

            if (responseCode != (int)UsenetResponseType.ServerReadyPostingAllowed &&
                responseCode != (int)UsenetResponseType.ServerReadyNoPostingAllowed)
            {
                await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                    .ConfigureAwait(false);
            }

            return new UsenetResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<List<string>> ReadCapabilityLinesAsync(CancellationToken cancellationToken)
    {
        var capabilities = new List<string>();
        var totalBytes = 0;

        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new UsenetProtocolException(
                    "The NNTP connection closed before the CAPABILITIES terminator was received.");

            if (line == ".")
            {
                break;
            }

            // Dot-unstuff capability lines (RFC 3977 §3.1.1).
            if (line.StartsWith("..", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            totalBytes += Encoding.Latin1.GetByteCount(line) + 2;
            if (totalBytes > 64 * 1024)
            {
                throw new UsenetProtocolException(
                    "CAPABILITIES response exceeded the 64 KiB limit.");
            }

            capabilities.Add(line);
        }

        return capabilities;
    }
}
