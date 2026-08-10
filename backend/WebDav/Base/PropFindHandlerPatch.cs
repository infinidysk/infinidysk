using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;

namespace NzbWebDAV.WebDav.Base;

public class PropFindHandlerPatch(PropFindHandler inner) : IRequestHandler
{
    internal const string FiniteDepthErrorBody =
        """<?xml version="1.0" encoding="utf-8"?><D:error xmlns:D="DAV:"><D:propfind-finite-depth/></D:error>""";

    private static readonly XName PropStatName = WebDavNamespaces.DavNs + "propstat";
    private static readonly XName StatusName = WebDavNamespaces.DavNs + "status";
    private const string NotFoundStatusPrefix = "HTTP/1.1 404";

    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        var originalBody = response.Body;
        await using var buffer = new MemoryStream();
        response.Body = buffer;

        bool handled;
        try
        {
            handled = await inner.HandleRequestAsync(httpContext).ConfigureAwait(false);
            await TryWriteFiniteDepthErrorAsync(httpContext).ConfigureAwait(false);
        }
        finally
        {
            response.Body = originalBody;
        }

        if (response.HasStarted)
        {
            buffer.Position = 0;
            await CopyBufferAsync(buffer, originalBody, httpContext.RequestAborted).ConfigureAwait(false);
            return handled;
        }

        if (ShouldSanitizeMultiStatus(response))
        {
            var sanitized = SanitizePropFindMultiStatus(buffer);
            response.ContentLength = sanitized.Length;
            await originalBody.WriteAsync(sanitized, httpContext.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            buffer.Position = 0;
            await CopyBufferAsync(buffer, originalBody, httpContext.RequestAborted).ConfigureAwait(false);
        }

        return handled;
    }

    internal static bool ShouldSanitizeMultiStatus(HttpResponse response)
    {
        return response.StatusCode == StatusCodes.Status207MultiStatus
               && IsXmlContentType(response.ContentType);
    }

    // RFC 4918 allows 404 propstats inside a 207 Multi-Status, but rclone v1.74+
    // treats them as fatal and shows an empty directory. Real clients only need the
    // 200 propstat blocks, so strip the 404 entries before serializing the response.
    internal static byte[] SanitizePropFindMultiStatus(Stream body)
    {
        body.Position = 0;
        var document = XDocument.Load(body, LoadOptions.PreserveWhitespace);
        var removed = false;

        foreach (var propStat in document.Descendants(PropStatName).ToList())
        {
            var status = propStat.Element(StatusName)?.Value;
            if (status is not null
                && status.StartsWith(NotFoundStatusPrefix, StringComparison.Ordinal))
            {
                propStat.Remove();
                removed = true;
            }
        }

        if (!removed)
        {
            body.Position = 0;
            return ((MemoryStream)body).ToArray();
        }

        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }

    internal static async Task<bool> TryWriteFiniteDepthErrorAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        if (response.HasStarted || response.StatusCode != StatusCodes.Status403Forbidden)
            return false;

        response.ContentType = "application/xml; charset=utf-8";
        await response.WriteAsync(FiniteDepthErrorBody, httpContext.RequestAborted).ConfigureAwait(false);
        return true;
    }

    private static bool IsXmlContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        return contentType.StartsWith("application/xml", StringComparison.OrdinalIgnoreCase)
               || contentType.StartsWith("text/xml", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyBufferAsync(
        MemoryStream buffer,
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected before the PROPFIND body was fully written.
        }
    }
}
