using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Api.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected virtual bool RequiresAuthentication => true;
    protected abstract Task<IActionResult> HandleRequest();

    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> HandleApiRequest()
    {
        try
        {
            if (RequiresAuthentication)
            {
                var configManager = HttpContext.RequestServices.GetRequiredService<ConfigManager>();
                ApiKeyValidator.Validate(HttpContext, configManager);
            }

            return await HandleRequest().ConfigureAwait(false);
        }
        catch (Exception e) when (e is BadHttpRequestException or ArgumentException)
        {
            return BadRequest(new BaseApiResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (UnauthorizedAccessException e)
        {
            return Unauthorized(new BaseApiResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (Exception e) when ((e is not OperationCanceledException ||
                                   !HttpContext.RequestAborted.IsCancellationRequested) &&
                                  e is not OutOfMemoryException)
        {
            Log.Error(e, "Unhandled admin API request failure");
            // Once headers/body have started (e.g. a streamed zip), writing a JSON
            // 500 would corrupt the response and surface as a browser network error.
            if (HttpContext.Response.HasStarted)
                return new EmptyResult();

            return StatusCode(500, new BaseApiResponse()
            {
                Status = false,
                Error = "An internal server error occurred."
            });
        }
    }
}
