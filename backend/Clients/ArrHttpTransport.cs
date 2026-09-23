using System.Net;

namespace NzbWebDAV.Clients;

/// <summary>
/// Shared transport for Radarr/Sonarr/Prowlarr management API calls. Single-label
/// hostnames (Docker service names such as <c>sonarr</c>) always connect directly,
/// even when a process-wide proxy is configured; every other destination keeps the
/// default proxy policy, including NO_PROXY rules and proxy credentials.
/// </summary>
public static class ArrHttpTransport
{
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    public static HttpMessageHandler CreateHandler() => new RoutingHandler();

    internal static SocketsHttpHandler CreateSocketHandler(bool useProxy) => new()
    {
        PooledConnectionLifetime = PooledConnectionLifetime,
        UseProxy = useProxy,
        AllowAutoRedirect = false,
    };

    public static bool IsSingleLabelHost(Uri destination) =>
        destination.HostNameType == UriHostNameType.Dns && !destination.IdnHost.Contains('.', StringComparison.Ordinal);

    public static string DescribeRouting(Uri destination) =>
        IsSingleLabelHost(destination) ? "direct (single-label hostname)" : "default proxy policy";

    internal sealed class RoutingHandler(HttpMessageHandler? directHandler = null, HttpMessageHandler? defaultHandler = null)
        : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _direct = new(directHandler ?? CreateSocketHandler(useProxy: false), disposeHandler: true);
        private readonly HttpMessageInvoker _default = new(defaultHandler ?? CreateSocketHandler(useProxy: true), disposeHandler: true);

        private HttpMessageInvoker Select(HttpRequestMessage request) =>
            IsSingleLabelHost(request.RequestUri!) ? _direct : _default;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var redirects = 0; ; redirects++)
            {
                var response = Select(request).Send(request, cancellationToken);
                if (!PrepareRedirect(request, response, redirects)) return response;
                response.Dispose();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var redirects = 0; ; redirects++)
            {
                var response = await Select(request).SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!PrepareRedirect(request, response, redirects)) return response;
                response.Dispose();
            }
        }

        private static bool PrepareRedirect(HttpRequestMessage request, HttpResponseMessage response, int redirects)
        {
            if (redirects >= 50 || response.StatusCode is not (
                    HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                || response.Headers.Location is not { } location)
                return false;

            var previous = request.RequestUri!;
            var destination = location.IsAbsoluteUri ? location : new Uri(previous, location);
            if (destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps)
                return false;
            if (previous.Scheme == Uri.UriSchemeHttps && destination.Scheme != Uri.UriSchemeHttps)
                return false;
            if (string.IsNullOrEmpty(destination.Fragment) && !string.IsNullOrEmpty(previous.Fragment))
                destination = new UriBuilder(destination) { Fragment = previous.Fragment }.Uri;

            var forceGet = response.StatusCode switch
            {
                HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found =>
                    request.Method == HttpMethod.Post,
                HttpStatusCode.SeeOther => request.Method != HttpMethod.Get && request.Method != HttpMethod.Head,
                _ => false,
            };
            request.Headers.Authorization = null;
            if (Uri.Compare(previous, destination, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
                request.Headers.Remove("X-Api-Key");
            request.RequestUri = destination;
            if (forceGet)
            {
                request.Method = HttpMethod.Get;
                request.Content = null;
                if (request.Headers.TransferEncodingChunked == true)
                    request.Headers.TransferEncodingChunked = false;
            }
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _direct.Dispose();
                _default.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
