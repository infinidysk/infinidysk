using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace NzbWebDAV.Utils;

/// <summary>
/// One shared HttpClient per distinct proxy URL and TLS-validation policy. Returned clients
/// have an infinite Timeout because they're shared across callers with different
/// budgets; each caller enforces its own per-request timeout via CancellationToken.
/// </summary>
public static class ProxyHttpClientPool
{
    private static readonly ConcurrentDictionary<ClientKey, HttpClient> Clients = new();

    public static HttpClient GetClient(string? proxyUrl, bool skipTlsVerification = false)
    {
        var key = new ClientKey(Normalize(proxyUrl) ?? "", skipTlsVerification);
        return Clients.GetOrAdd(key, k =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };

            if (k.ProxyUrl.Length > 0 && Uri.TryCreate(k.ProxyUrl, UriKind.Absolute, out var uri))
            {
                var address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
                var proxy = new WebProxy(address) { BypassProxyOnLocal = false };
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var creds = uri.UserInfo.Split(':', 2);
                    proxy.Credentials = new NetworkCredential(
                        Uri.UnescapeDataString(creds[0]),
                        creds.Length > 1 ? Uri.UnescapeDataString(creds[1]) : string.Empty);
                }
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
                handler.ConnectCallback = KeepAliveConnectAsync;
            }

            if (k.SkipTlsVerification)
            {
                handler.SslOptions = new SslClientAuthenticationOptions
                {
#pragma warning disable CA5359 // behind the explicit per-provider SkipTlsVerification setting for providers with broken/self-signed certs
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
#pragma warning restore CA5359
                };
            }

            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    private readonly record struct ClientKey(string ProxyUrl, bool SkipTlsVerification);

    private static async ValueTask<Stream> KeepAliveConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;

        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out var literal) && literal is not null
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? lastError = null;
        for (var i = 0; i < addresses.Length; i++)
        {
            var isLast = i == addresses.Length - 1;
#pragma warning disable CA2000 // socket is disposed on connect failure; on success the pooled connection stream owns it
            var socket = new Socket(addresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
#pragma warning restore CA2000
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            TrySetKeepAliveTuning(socket);
            try
            {
                if (isLast)
                {
                    await socket.ConnectAsync(addresses[i], endPoint.Port, ct).ConfigureAwait(false);
                }
                else
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await socket.ConnectAsync(addresses[i], endPoint.Port, attemptCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw new SocketException((int)SocketError.TimedOut);
                    }
                }
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception e)
            {
                socket.Dispose();
                lastError = e;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static void TrySetKeepAliveTuning(Socket socket)
    {
        // Keep-alive tuning is best-effort; platforms missing an option simply skip it.
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30); }
        catch { /* unsupported on this platform */ }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5); }
        catch { /* unsupported on this platform */ }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); }
        catch { /* unsupported on this platform */ }
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString();
    }
}
