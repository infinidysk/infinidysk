using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.GetLibraryFileDetails;

[ApiController]
[Route("api/get-library-file-details")]
public class GetLibraryFileDetailsController(DavDatabaseClient dbClient, ConfigManager config) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!config.IsMediaLibraryEnabled())
            return NotFound(new BaseApiResponse { Status = false, Error = "Media Library is disabled." });
        var request = new GetLibraryFileDetailsRequest(HttpContext);
        var ct = request.CancellationToken;

        var item = await dbClient.Ctx.Items.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.DavItemId, ct)
            .ConfigureAwait(false);
        if (item is null)
            return NotFound(new BaseApiResponse { Status = false, Error = "Item not found." });

        var latest = await dbClient.Ctx.HealthCheckResults.AsNoTracking()
            .Where(x => x.DavItemId == request.DavItemId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var mappings = await dbClient.Ctx.LinkMaps.AsNoTracking()
            .Where(m => m.DavItemId == request.DavItemId)
            .OrderBy(m => m.LinkPath)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(new GetLibraryFileDetailsResponse
        {
            DavItemId = item.Id.ToString("D"),
            Name = item.Name,
            ContentPath = item.Path,
            Size = item.FileSize,
            ReleaseDate = item.ReleaseDate,
            LastHealthCheck = item.LastHealthCheck,
            NextHealthCheck = item.NextHealthCheck,
            HealthRepairPending = item.HealthRepairPending,
            HistoryItemId = item.HistoryItemId?.ToString("D"),
            LatestHealth = latest is null ? null : new GetLibraryFileDetailsResponse.LibraryFileDetailsHealth
            {
                Result = latest.Result.ToString().ToLowerInvariant(),
                RepairStatus = latest.RepairStatus.ToString().ToLowerInvariant(),
                Message = latest.Message,
                CreatedAt = latest.CreatedAt,
            },
            Mappings = mappings.Select(m => new GetLibraryFileDetailsResponse.LibraryFileDetailsMapping
            {
                LinkPath = m.LinkPath,
                TargetText = m.TargetText,
                MappingType = m.MappingType.ToString().ToLowerInvariant(),
                Status = m.Status.ToString().ToLowerInvariant(),
            }).ToList(),
        });
    }
}
