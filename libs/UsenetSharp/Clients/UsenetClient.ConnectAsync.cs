using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCommandValue(host, nameof(host), 253);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CleanupConnection();
            ThrowIfDisposed();
            using var operationCts = CreateOperationTokenSource(cancellationToken);

            _tcpClient = new TcpClient
            {
                NoDelay = true
            };
            var socket = _tcpClient.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveTime,
                (int)_options.TcpKeepAliveTime.TotalSeconds);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveInterval,
                (int)_options.TcpKeepAliveInterval.TotalSeconds);
            socket.SetSocketOption(
                SocketOptionLevel.Tcp,
                SocketOptionName.TcpKeepAliveRetryCount,
                _options.TcpKeepAliveRetryCount);
            await _tcpClient.ConnectAsync(host, port, operationCts.Token).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();

            if (useSsl)
            {
                var sslStream = new SslStream(_stream, false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = _options.SkipTlsVerification
                        ? X509RevocationMode.NoCheck
                        : _options.CertificateRevocationCheckMode,
                    RemoteCertificateValidationCallback = _options.SkipTlsVerification
                        ? static (_, _, _, _) => true
                        : null
                }, operationCts.Token).ConfigureAwait(false);
                _stream = sslStream;
            }

            // Use Latin1 encoding to preserve exact byte values 0-255 for yEnc-encoded content
            _reader = new NntpLineReader(_stream);

            // Read the server response
            using var greetingTimeout = new CoalescedReadTimeout(
                operationCts.Token, _options.ReadTimeout, _timeProvider);
            var response = await ReadLineAsync(greetingTimeout).ConfigureAwait(false);
            var responseCode = ParseResponseCode(response);

            // NNTP servers typically respond with "200" or "201" for successful connection
            if (responseCode != (int)UsenetResponseType.ServerReadyPostingAllowed &&
                responseCode != (int)UsenetResponseType.ServerReadyNoPostingAllowed)
                throw new UsenetConnectionException(response!) { ResponseCode = responseCode };

            Volatile.Write(ref _connectionState, 1);
        }
        catch
        {
            CleanupConnection();
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
