using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Api.SabControllers.AddUrl;
using NzbWebDAV.Api.SabControllers.GetCategories;
using NzbWebDAV.Api.SabControllers.GetConfig;
using NzbWebDAV.Api.SabControllers.GetFullStatus;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Api.SabControllers.GetServerStats;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Api.SabControllers.GetVersion;
using NzbWebDAV.Api.SabControllers.GetWarnings;
using NzbWebDAV.Api.SabControllers.MoveInQueue;
using NzbWebDAV.Api.SabControllers.Pause;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Api.SabControllers.RemoveFromQueue;
using NzbWebDAV.Api.SabControllers.Resume;
using NzbWebDAV.Api.SabControllers.SetQueueCategory;
using NzbWebDAV.Api.SabControllers.SetQueuePriority;
using NzbWebDAV.Api.SabControllers.RetryHistory;
using NzbWebDAV.Api.SabControllers.SpeedLimit;
using NzbWebDAV.Api.SabControllers.SwitchQueue;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.SabControllers;

[ApiController]
[Route("api")]
public class SabApiController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    NzbWebDAV.Services.ProviderUsageTracker providerUsageTracker,
    NzbWebDAV.Services.IndexerHitTracker hitTracker,
    WarningLogBuffer warningLogBuffer
) : ControllerBase
{
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> HandleApiRequests()
    {
        try
        {
            var controller = GetController();
            return await controller.HandleRequest().ConfigureAwait(false);
        }
        catch (BadHttpRequestException e)
        {
            return BadRequest(new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (UnauthorizedAccessException e)
        {
            return Unauthorized(new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (Exception e)
        {
            e.LogWarningKnownOrStack("Unhandled SAB API request failure");
            return StatusCode(500, new SabBaseResponse()
            {
                Status = false,
                Error = "An internal server error occurred."
            });
        }
    }

    public BaseController GetController()
    {
        switch (HttpContext.GetRequestParam("mode"))
        {
            case "version":
                return new GetVersionController(
                    HttpContext, configManager);
            case "status":
                return new GetStatusController(
                    HttpContext, configManager);
            case "get_cats":
                return new GetCategoriesController(
                    HttpContext, configManager);
            case "get_config":
                return new GetConfigController(
                    HttpContext, configManager);
            case "fullstatus":
                return new GetFullStatusController(
                    HttpContext, configManager);
            case "server_stats":
                return new GetServerStatsController(HttpContext, configManager);
            case "warnings":
                return new GetWarningsController(HttpContext, configManager, warningLogBuffer);
            case "addfile":
                return new AddFileController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);
            case "addurl":
                return new AddUrlController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager, hitTracker);

            case "pause":
                return new PauseController(HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "resume":
                return new ResumeController(HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "speedlimit":
                return new SpeedLimitController(HttpContext, dbClient, configManager);

            case "queue" when HttpContext.GetRequestParam("name") == "delete":
                return new RemoveFromQueueController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);
            case "queue" when HttpContext.GetRequestParam("name") == "move":
                return new MoveInQueueController(
                    HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "queue" when HttpContext.GetRequestParam("name") == "priority":
                return new SetQueuePriorityController(
                    HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "queue" when HttpContext.GetRequestParam("name") == "pause":
                return new PauseController(HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "queue" when HttpContext.GetRequestParam("name") == "resume":
                return new ResumeController(HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "queue":
                return new GetQueueController(
                    HttpContext, dbClient, queueManager, configManager, providerUsageTracker);

            case "switch":
                return new SwitchQueueController(
                    HttpContext, dbClient, configManager, queueManager, websocketManager);

            case "history" when HttpContext.GetRequestParam("name") == "delete":
                return new RemoveFromHistoryController(
                    HttpContext, dbClient, configManager, websocketManager);
            case "history":
                return new GetHistoryController(
                    HttpContext, dbClient, configManager, providerUsageTracker);

            case "change_cat":
                return new SetQueueCategoryController(
                    HttpContext, dbClient, configManager, queueManager, websocketManager);
            case "retry":
                return new RetryHistoryController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);

            default:
                throw new BadHttpRequestException("Invalid mode");
        }
    }

    public abstract class BaseController(HttpContext httpContext, ConfigManager configManager) : ControllerBase
    {
        // Derived controllers must use these properties instead of capturing
        // the primary-constructor parameters (CS9107 double-capture).
        protected HttpContext Context => httpContext;
        protected ConfigManager Config => configManager;

        public Task<IActionResult> HandleRequest()
        {
            if (RequiresAuthentication)
                ApiKeyValidator.Validate(httpContext, configManager);

            return Handle();
        }

        protected virtual bool RequiresAuthentication => true;
        protected abstract Task<IActionResult> Handle();
    }
}
