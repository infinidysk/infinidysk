using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
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
                DateCommand,
                static (self, command, timeout) => self.WriteCommandAsync(command, timeout))
                .ConfigureAwait(false);

            DateTimeOffset? dateTime = null;
            if (responseCode == (int)UsenetResponseType.DateAndTime)
            {
                try
                {
                    dateTime = ParseNntpDateTime(response);
                }
                catch (Exception e)
                {
                    RecordConnectionFailure(e);
                    throw;
                }
            }
            else
            {
                await DrainUnexpectedMultiLineAsync(responseCode, operationCts.Token)
                    .ConfigureAwait(false);
            }

            return new UsenetDateResponse
            {
                ResponseCode = responseCode,
                ResponseMessage = response,
                DateTime = dateTime
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static DateTimeOffset ParseNntpDateTime(ReadOnlySpan<char> response)
    {
        // "111 yyyymmddhhmmss" — exactly 14 ASCII digits after the code.
        var payload = response.Length >= 18 ? response[4..].Trim() : default;
        if (payload.Length != 14 || !AllAsciiDigits(payload))
        {
            throw new UsenetProtocolException($"Malformed DATE response: {response}");
        }

        try
        {
            return new DateTimeOffset(
                DigitsToInt(payload[..4]),
                DigitsToInt(payload[4..6]),
                DigitsToInt(payload[6..8]),
                DigitsToInt(payload[8..10]),
                DigitsToInt(payload[10..12]),
                DigitsToInt(payload[12..14]),
                TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new UsenetProtocolException($"Malformed DATE response: {response}", exception);
        }
    }

    private static bool AllAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static int DigitsToInt(ReadOnlySpan<char> digits)
    {
        var value = 0;
        foreach (var digit in digits)
        {
            value = (value * 10) + (digit - '0');
        }

        return value;
    }
}
