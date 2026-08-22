using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using System.Net;
using System.Net.Sockets;
using Serilog;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlRequest() : AddFileRequest
{
    // Static fields initialize in declaration order; InitializeHttpClient uses this timeout.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);
    private const int MaxAutomaticRedirections = 10;
    private static readonly HttpClient StrictDirectHttpClient = InitializeHttpClient(skipTlsVerification: false);
    private static readonly HttpClient PermissiveDirectHttpClient = InitializeHttpClient(skipTlsVerification: true);
    private static readonly HttpRequestOptionsKey<TrustedHostsMatcher> TrustedHostsOptionKey = new("TrustedHosts");

    public static async Task<AddUrlRequest> New(HttpContext context, ConfigManager configManager, IndexerHitTracker hitTracker)
    {
        var errors = new ValidationErrors();
        var nzbUrl = context.GetRequestParam("name");
        if (string.IsNullOrWhiteSpace(nzbUrl))
            errors.Add("name", "The nzb url is required.");
        else if (!Uri.TryCreate(nzbUrl, UriKind.Absolute, out var requestUri)
                 || (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("name", "The nzb url must be an absolute HTTP or HTTPS URL.");
        }

        if (!AddFileRequest.TryMapPriorityOption(context.GetRequestParam("priority"), out _))
            errors.Add("priority", "Invalid priority");
        if (!AddFileRequest.TryMapPostProcessingOption(context.GetRequestParam("pp"), out _))
            errors.Add("pp", "Invalid pp param");
        errors.ThrowIfAny();

        var nzbName = context.GetRequestParam("nzbname");
        var indexerConfig = configManager.GetIndexerConfig();
        var matchedIndexer = MatchIndexerByHost(nzbUrl, indexerConfig);

        var userAgent = IndexerConfig.PerIndexerRetrieveUserAgent(matchedIndexer) ?? configManager.GetUserAgent();
        var proxyUrl = StringUtil.EmptyToNull(matchedIndexer?.ProxyUrl) ?? indexerConfig.ProxyUrl;
        var skipTlsVerification = matchedIndexer?.SkipTlsVerification == true;
        var trustedHosts = TrustedHostsMatcher.Parse(configManager.GetAddUrlTrustedHosts());

        if (matchedIndexer is not null)
        {
            var hitCheck = await hitTracker
                .CheckAsync(matchedIndexer.Name, IndexerApiHit.HitType.Download, matchedIndexer.DownloadLimit, matchedIndexer.HitLimitResetTime, context.RequestAborted)
                .ConfigureAwait(false);
            if (hitCheck is { Allowed: false })
            {
                var reason = IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Download);
                Log.Warning("AddUrl rejected for {Indexer}: {Reason}", matchedIndexer.Name, reason);
                throw new BadHttpRequestException($"Indexer {matchedIndexer.Name} {reason}");
            }
        }

        var nzbFile = await GetNzbFile(
            nzbUrl,
            nzbName,
            userAgent,
            proxyUrl,
            skipTlsVerification,
            trustedHosts,
            context.RequestAborted).ConfigureAwait(false);
        if (matchedIndexer is not null)
            _ = hitTracker.RecordAsync(matchedIndexer.Name, IndexerApiHit.HitType.Download, CancellationToken.None);
        return new AddUrlRequest()
        {
            FileName = nzbFile.FileName,
            ContentType = nzbFile.ContentType,
            NzbFileStream = nzbFile.FileStream,
            Category = SabCategoryResolver.GetCategory(context, configManager)
                       ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetRequestParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetRequestParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    private static IndexerConfig.ConnectionDetails? MatchIndexerByHost(string? url, IndexerConfig config)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri)) return null;
        var host = requestUri.Host;
        if (string.IsNullOrEmpty(host)) return null;

        foreach (var indexer in config.Indexers)
        {
            if (!indexer.Enabled) continue;
            if (!Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri)) continue;
            if (string.Equals(indexerUri.Host, host, StringComparison.OrdinalIgnoreCase))
                return indexer;
        }
        return null;
    }

    private static async Task<NzbFileResponse> GetNzbFile(
        string? url,
        string? nzbName,
        string userAgent,
        string? proxyUrl,
        bool skipTlsVerification,
        TrustedHostsMatcher trustedHosts,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException($"The url is invalid.");

            var response = await GetAsync(
                url, userAgent, proxyUrl, skipTlsVerification, trustedHosts, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new InvalidOperationException($"Received status code {response.StatusCode}.");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;

            var resolvedFileName = nzbName
                                   ?? GetFilenameFromResponseHeader(response)
                                   ?? GetFilenameFromUrl(url)
                                   ?? throw new InvalidOperationException("Nzb filename could not be determined.");
            var fileName = NzbStreamUtil.NormalizeFileName(resolvedFileName);

            var fileStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return new NzbFileResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileStream = fileStream
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            throw new BadHttpRequestException($"Failed to fetch nzb-file url `{url}`: {ex.Message}");
        }
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null : NzbStreamUtil.NormalizeFileName(nzbName);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        string url,
        string userAgent,
        string? proxyUrl,
        bool skipTlsVerification,
        TrustedHostsMatcher trustedHosts,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FetchTimeout);
        var ct = timeoutCts.Token;
        var currentUri = ValidateHttpUri(url);
        var httpClient = string.IsNullOrWhiteSpace(proxyUrl)
            ? (skipTlsVerification ? PermissiveDirectHttpClient : StrictDirectHttpClient)
            : ProxyHttpClientPool.GetClient(proxyUrl, skipTlsVerification);

        for (var redirectCount = 0; ; redirectCount++)
        {
            // Validate the destination before every request, including requests sent through
            // an indexer proxy. The direct client repeats this check at connect time to
            // protect against DNS rebinding between validation and socket creation.
            await EnsurePublicHostAsync(currentUri, trustedHosts, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Options.Set(TrustedHostsOptionKey, trustedHosts);
            if (!string.IsNullOrWhiteSpace(userAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            ).ConfigureAwait(false);

            if (!IsRedirect(response) || response.Headers.Location is null)
                return response;

            if (redirectCount >= MaxAutomaticRedirections)
            {
                response.Dispose();
                throw new HttpRequestException($"The maximum number of redirects ({MaxAutomaticRedirections}) was exceeded.");
            }

            Uri redirectUri;
            try
            {
                redirectUri = response.Headers.Location.IsAbsoluteUri
                    ? ValidateHttpUri(response.Headers.Location)
                    : ValidateHttpUri(new Uri(currentUri, response.Headers.Location));

                if (
                    currentUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && redirectUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !EnvironmentUtil.IsVariableTrue("ALLOW_HTTPS_TO_HTTP_REDIRECTS")
                )
                {
                    throw new HttpRequestException("Redirecting from HTTPS to HTTP is not allowed.");
                }
            }
            catch
            {
                response.Dispose();
                throw;
            }
            response.Dispose();
            currentUri = redirectUri;
        }
    }

    private static HttpClient InitializeHttpClient(bool skipTlsVerification)
    {
#pragma warning disable CA2000 // handler is owned by the static HttpClient (process lifetime by design)
        var handler = new SocketsHttpHandler
#pragma warning restore CA2000
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectToValidatedAddress,
        };
        if (skipTlsVerification)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
#pragma warning disable CA5359 // behind the explicit per-request/per-provider skip-TLS-verification setting for hosts with broken/self-signed certs
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
#pragma warning restore CA5359
            };
        }
        return new HttpClient(handler)
        {
            Timeout = FetchTimeout,
        };
    }

    private static async Task EnsurePublicHostAsync(
        Uri uri,
        TrustedHostsMatcher trustedHosts,
        CancellationToken cancellationToken)
    {
        if (trustedHosts.IsTrustedHost(uri.Host))
            return;

        var addresses = IPAddress.TryParse(uri.Host, out var literalAddress)
            ? [literalAddress]
            : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new HttpRequestException($"The host `{uri.Host}` did not resolve to an IP address.");
        if (addresses.Any(address => !IsPublicAddress(address) && !trustedHosts.IsTrustedAddress(address)))
            throw new HttpRequestException($"The host `{uri.Host}` resolved to a non-public IP address.");
    }

    private static async ValueTask<Stream> ConnectToValidatedAddress(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var host = context.DnsEndPoint.Host;
        var trustedHosts = TrustedHostsMatcher.Empty;
        if (context.InitialRequestMessage.Options.TryGetValue(TrustedHostsOptionKey, out var requestTrustedHosts)
            && requestTrustedHosts is not null)
        {
            trustedHosts = requestTrustedHosts;
        }

        if (trustedHosts.IsTrustedHost(host))
        {
            return await ConnectToAnyAddressAsync(host, context.DnsEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);
        }

        IPAddress[] addresses = IPAddress.TryParse(host, out var literalAddress)
            ? [literalAddress]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new HttpRequestException($"The host `{host}` did not resolve to an IP address.");

        if (addresses.Any(address => !IsPublicAddress(address) && !trustedHosts.IsTrustedAddress(address)))
            throw new HttpRequestException($"The host `{host}` resolved to a non-public IP address.");

        return await ConnectToAddressesAsync(host, addresses, context.DnsEndPoint.Port, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<Stream> ConnectToAnyAddressAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literalAddress)
            ? [literalAddress]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new HttpRequestException($"The host `{host}` did not resolve to an IP address.");

        return await ConnectToAddressesAsync(host, addresses, port, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Stream> ConnectToAddressesAsync(
        string host,
        IPAddress[] addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        foreach (var address in addresses.Distinct())
        {
#pragma warning disable CA2000 // socket is disposed on connect failure; on success the returned NetworkStream owns it (ownsSocket: true)
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
#pragma warning restore CA2000
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, port),
                    cancellationToken
                ).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                lastException = ex;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"Could not connect to the host `{host}`.", lastException);
    }

    private static Uri ValidateHttpUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new HttpRequestException("The URL is invalid.");

        return ValidateHttpUri(uri);
    }

    private static Uri ValidateHttpUri(Uri uri)
    {
        if (
            !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new HttpRequestException("Only HTTP and HTTPS URLs are allowed.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.HostNameType == UriHostNameType.Unknown)
            throw new HttpRequestException("The URL host is invalid.");

        return uri;
    }

    private static bool IsRedirect(HttpResponseMessage response)
    {
        return (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 0 && bytes[2] == 0 => bytes[3] is 9 or 10,
                192 when bytes[1] == 0 && bytes[2] == 2 => false,
                192 when bytes[1] == 88 && bytes[2] == 99 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        // Globally routable IPv6 unicast is currently allocated from 2000::/3.
        if ((bytes[0] & 0xe0) != 0x20)
            return false;

        if (HasPrefix(bytes, [0x20, 0x01, 0x00, 0x00], 32)) // Teredo
            return false;
        if (HasPrefix(bytes, [0x20, 0x01, 0x00, 0x02, 0x00, 0x00], 48)) // Benchmarking
            return false;
        if (HasPrefix(bytes, [0x20, 0x01, 0x00, 0x10], 28)) // ORCHID
            return false;
        if (HasPrefix(bytes, [0x20, 0x01, 0x00, 0x20], 28)) // ORCHIDv2
            return false;
        if (HasPrefix(bytes, [0x20, 0x01, 0x0d, 0xb8], 32)) // Documentation
            return false;
        if (HasPrefix(bytes, [0x20, 0x02], 16)) // 6to4
            return false;
        if (HasPrefix(bytes, [0x3f, 0xff, 0x00], 20)) // Documentation
            return false;

        return true;
    }

    private static bool HasPrefix(byte[] address, byte[] prefix, int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (address[index] != prefix[index])
                return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xff << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }

    private static string? GetFilenameFromResponseHeader(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var filename = contentDisposition?.FileName?.Trim('"');
        return StringUtil.EmptyToNull(filename);
    }

    private static string? GetFilenameFromUrl(string url)
    {
        try
        {
            var filename = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(filename)) return null;
            filename = Uri.UnescapeDataString(filename);
            filename = AddNzbExtension(filename);
            return filename;
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    private class NzbFileResponse
    {
        public required string FileName { get; init; }
        public required string? ContentType { get; init; }
        public required Stream FileStream { get; init; }
    }
}
