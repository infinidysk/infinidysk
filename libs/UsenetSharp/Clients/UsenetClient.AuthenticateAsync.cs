using System.Net.Security;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    // "AUTHINFO USER " / "AUTHINFO PASS " = 14 octets; 512 - 14 - 2 (CRLF) = 496.
    private const int MaxAuthInfoArgumentLength = 496;

    /// <summary>
    /// Authenticates with AUTHINFO USER/PASS.
    /// </summary>
    /// <remarks>
    /// Credentials sent without TLS are transmitted in plaintext. Prefer connecting
    /// with SSL/TLS, or enable <see cref="UsenetClientOptions.RequireTlsForAuthentication"/>.
    /// Passwords may contain spaces (last-argument interop); usernames must not
    /// (RFC 4643 §2.4).
    /// </remarks>
    public async Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCommandValue(user, nameof(user), MaxAuthInfoArgumentLength);
        ValidateCommandValue(pass, nameof(pass), MaxAuthInfoArgumentLength);
        if (ContainsWhitespace(user))
        {
            throw new ArgumentException(
                "Username must not contain whitespace (RFC 4643 §2.4).", nameof(user));
        }

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfUnhealthy();
            ThrowIfNotConnected();
            if (_options.RequireTlsForAuthentication && _stream is not SslStream)
            {
                throw new InvalidOperationException(
                    "Refusing to send credentials over a plaintext connection. " +
                    "Connect with useSsl: true or disable RequireTlsForAuthentication.");
            }

            using var operationCts = CreateOperationTokenSource(cancellationToken);
            using var ioTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);

            var (userResponseCode, userResponse) = await ExchangeSingleLineAsync(
                ioTimeout,
                user,
                static (self, u, timeout) => self.WriteAuthInfoCommandAsync("USER", u, timeout))
                .ConfigureAwait(false);
            await DrainUnexpectedMultiLineAsync(userResponseCode, operationCts.Token)
                .ConfigureAwait(false);

            // Password required
            if (userResponseCode == (int)UsenetResponseType.PasswordRequired)
            {
                var (passResponseCode, passResponse) = await ExchangeSingleLineAsync(
                    ioTimeout,
                    pass,
                    static (self, p, timeout) => self.WriteAuthInfoCommandAsync("PASS", p, timeout))
                    .ConfigureAwait(false);
                await DrainUnexpectedMultiLineAsync(passResponseCode, operationCts.Token)
                    .ConfigureAwait(false);

                return new UsenetResponse()
                {
                    ResponseCode = passResponseCode,
                    ResponseMessage = passResponse,
                };
            }

            return new UsenetResponse()
            {
                ResponseCode = userResponseCode,
                ResponseMessage = userResponse,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
