using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Api.Controllers.Prefetch;

[ApiController]
[Route("api/prefetch")]
public sealed class PrefetchController(PrefetchRuntime runtime, PlexPrefetchService policies, DavDatabaseClient database) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await runtime.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        IReadOnlyList<PrefetchJob> jobs = [];
        var paused = true;
        try
        {
            if (runtime.Healthy && runtime.Jobs is { } store)
            {
                jobs = store.List();
                paused = store.Paused;
            }
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        { runtime.ReportMetadataFailure(); }
        var items = (await database.GetItemsByIdsBatchedAsync(jobs.Select(job => job.ItemId).Distinct().ToArray(),
            ct: HttpContext.RequestAborted).ConfigureAwait(false)).ToDictionary(item => item.Id);
        return Ok(new {
        available = runtime.Jobs is not null, runtime.InitializationError, runtime.RuntimeError, runtime.Healthy,
        paused, jobs = jobs.Select(job => job with
        {
            DisplayName = items.GetValueOrDefault(job.ItemId)?.Name ?? "Removed media",
            FileSize = items.GetValueOrDefault(job.ItemId)?.FileSize,
            Source = PlexPrefetchService.SourceLabel(job.Trigger),
            Reason = PlexPrefetchService.RangeReason(job.Trigger, job.Length)
        }),
        settings = runtime.Settings(), policies.LastSuccess, policies.LastError
        });
    }
}

[ApiController]
[Route("api/prefetch/preview")]
public sealed class PrefetchPreviewController(PlexPrefetchService policies) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest() => Ok(new
    {
        predictions = await policies.PreviewAsync(HttpContext.RequestAborted).ConfigureAwait(false),
        warning = policies.LastError
    });
}

[ApiController]
[Route("api/prefetch/operations")]
[RequestSizeLimit(64 * 1024)]
public sealed class PrefetchOperationController(PrefetchRuntime runtime, DavDatabaseClient database, PlexPrefetchService policies) : PostOnlyApiController
{
    public sealed record OperationRequest(string Operation, Guid[]? ItemIds, string? JobId, int? Priority, long Start = 0, long Length = 0);
    public sealed record ItemOutcome(Guid ItemId, string Status, string? JobId, long Start, long Length, string? Reason);
    protected override async Task<IActionResult> HandleRequest()
    {
        try { return await HandleOperationAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            runtime.ReportMetadataFailure();
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = runtime.RuntimeError });
        }
    }
    private async Task<IActionResult> HandleOperationAsync()
    {
        await runtime.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (runtime.RuntimeError is not null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = runtime.RuntimeError });
        var request = await HttpContext.Request.ReadFromJsonAsync<OperationRequest>(HttpContext.RequestAborted).ConfigureAwait(false)
            ?? throw new ArgumentException("A prefetch operation is required.");
        var jobs = runtime.Jobs ?? throw new ArgumentException("Activate Native cache and restart before warming media.");
        var coordinator = runtime.Coordinator!;
        if (request.Operation == "sync")
        {
            policies.RequestSync();
            return Accepted(value: new { status = true });
        }
        if (request.Operation is "pause-all" or "resume-all")
        {
            coordinator.SetPaused(request.Operation == "pause-all");
            return Ok(new { status = true });
        }
        if (request.Operation != "warm")
        {
            coordinator.Change(request.JobId ?? "", request.Operation, request.Priority);
            return Ok(new { status = true });
        }
        if (request.ItemIds is not { Length: > 0 and <= 32 }) throw new ArgumentException("Select 1–32 imported media files.");
        if (request.Priority is < -100 or > 100 || request.Start < 0 || request.Length < 0
            || request.Start > long.MaxValue - request.Length) throw new ArgumentException("Invalid warming priority or range.");
        var selected = request.ItemIds.Distinct().ToArray();
        var items = (await database.GetItemsByIdsBatchedAsync(selected, ct: HttpContext.RequestAborted).ConfigureAwait(false)).ToDictionary(item => item.Id);
        var accepted = new List<PrefetchJob>();
        var rejected = new List<Guid>();
        var outcomes = new List<ItemOutcome>();
        foreach (var id in selected)
        {
            var item = items.GetValueOrDefault(id);
            string? reason = item is null || item.FileBlobId is null || item.FileSize is not > 0
                || item.SubType is not (NzbWebDAV.Database.Models.DavItem.ItemSubType.NzbFile or NzbWebDAV.Database.Models.DavItem.ItemSubType.RarFile or NzbWebDAV.Database.Models.DavItem.ItemSubType.MultipartFile)
                ? "Not an available imported streamable file."
                : item.FileSize > runtime.Settings().MaxBytesPerItem ? "File exceeds the configured per-item warming cap."
                : request.Start >= item.FileSize || request.Length > item.FileSize - request.Start ? "Requested range is outside this file." : null;
            if (reason is null)
            {
                try
                {
                    var result = jobs.EnqueueWithOutcome(id, "manual", request.Priority ?? 50, request.Start, request.Length);
                    accepted.Add(result.Job);
                    outcomes.Add(new(id, result.Created ? "accepted" : "deduplicated", result.Job.Id, result.Job.Start, result.Job.Length, null));
                    continue;
                }
                catch (ArgumentException) { reason = "Queue or ownership capacity reached; retry after existing work finishes."; }
            }
            rejected.Add(id);
            outcomes.Add(new(id, "rejected", null, request.Start, request.Length, reason));
        }
        return Accepted(value: new { jobs = accepted, rejected, outcomes });
    }
}
