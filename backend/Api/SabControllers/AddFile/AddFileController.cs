using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private readonly NzbSubmissionService _service = new(
        dbClient, queueManager, configManager, websocketManager);

    /// <summary>
    /// Creates a short-lived context for conflict removal without flushing
    /// pending Added entities on the request-scoped context. Tests can override
    /// this to target the same temporary database as the request context.
    /// </summary>
    internal Func<DavDatabaseContext> FreshContextFactory
    {
        get => _service.FreshContextFactory;
        set => _service.FreshContextFactory = value;
    }

    /// <summary>
    /// Test hook invoked after the duplicate pre-check and before the blob is written,
    /// so the UNIQUE retry path can be exercised without a real concurrent request.
    /// </summary>
    internal Func<Task>? AfterDuplicatePreCheckHook
    {
        get => _service.AfterDuplicatePreCheckHook;
        set => _service.AfterDuplicatePreCheckHook = value;
    }

    public async Task<AddFileResponse> AddFileAsync(AddFileRequest request)
    {
        var result = await _service.SubmitAsync(request.ToSubmissionRequest()).ConfigureAwait(false);
        return ToResponse(result);
    }

    internal static bool IsCategoryFileNameUniqueViolation(DbUpdateException ex)
        => NzbSubmissionService.IsCategoryFileNameUniqueViolation(ex);

    internal static string GetSafeBackupFileName(Guid id, string fileName)
        => NzbSubmissionService.GetSafeBackupFileName(id, fileName);

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddFileRequest.New(Context, Config).ConfigureAwait(false);
        return Ok(await AddFileAsync(request).ConfigureAwait(false));
    }

    private static AddFileResponse ToResponse(NzbSubmissionResult result) => new()
    {
        Status = result.Status,
        Error = result.Error,
        NzoIds = result.NzoIds.ToList(),
    };
}
