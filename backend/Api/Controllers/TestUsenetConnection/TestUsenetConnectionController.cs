using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Api.Controllers.TestUsenetConnection;

[ApiController]
[Route("api/test-usenet-connection")]
public class TestUsenetConnectionController(ConfigManager configManager) : BaseApiController
{
    private async Task<TestUsenetConnectionResponse> TestUsenetConnection(TestUsenetConnectionRequest request)
    {
        try
        {
            using var connection = await UsenetStreamingClient.CreateNewConnection(
                request.ToConnectionDetails(),
                configManager.GetNntpReadTimeout(),
                HttpContext.RequestAborted).ConfigureAwait(false);
            return new TestUsenetConnectionResponse { Status = true, Connected = true };
        }
        catch (CouldNotConnectToUsenetException e)
        {
            // Prefer known outer/cause messages; InnerException alone is often a bare OCE
            // ("A task was canceled") when CreateNewConnection wraps a handshake timeout.
            var error = e.TryGetKnownErrorMessage(out var reason) ? reason : e.Message;
            Log.Warning(
                "Test connection failed for {Host}:{Port} (ssl={UseSsl}, user={User}): connect error: {Error}",
                request.Host, request.Port, request.UseSsl, request.User, error);
            return new TestUsenetConnectionResponse { Status = true, Connected = false };
        }
        catch (CouldNotLoginToUsenetException e)
        {
            var error = e.TryGetKnownErrorMessage(out var reason) ? reason : e.Message;
            Log.Warning(
                "Test connection failed for {Host}:{Port} (ssl={UseSsl}, user={User}): login error: {Error}",
                request.Host, request.Port, request.UseSsl, request.User, error);
            return new TestUsenetConnectionResponse { Status = true, Connected = false };
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Warning(e,
                "Test connection failed for {Host}:{Port} (ssl={UseSsl}, user={User}): unexpected error",
                request.Host, request.Port, request.UseSsl, request.User);
            throw;
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestUsenetConnectionRequest(HttpContext, configManager);
        var response = await TestUsenetConnection(request).ConfigureAwait(false);
        return Ok(response);
    }
}
