using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Api.Controllers.GetLibraryCatalog;

[ApiController]
[Route("api/get-library-catalog")]
public class GetLibraryCatalogController(DavDatabaseClient dbClient, ConfigManager config) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetLibraryCatalogRequest(HttpContext);
        var scanner = HttpContext.RequestServices.GetService<LibraryCatalogScanner>();
        var service = new LibraryCatalogService(dbClient.Ctx, config.IsMediaLibraryVideoOnly());
        var result = await service
            .QueryAsync(request.Query, scanner, request.CancellationToken)
            .ConfigureAwait(false);
        return Ok(new GetLibraryCatalogResponse
        {
            Items = result.Items.Select(i => new GetLibraryCatalogResponse.LibraryCatalogItem
            {
                Kind = i.Kind,
                DavItemId = i.DavItemId?.ToString(),
                DisplayName = i.DisplayName,
                ContentPath = i.ContentPath,
                Size = i.Size,
                MappingCount = i.MappingCount,
                Health = i.Health,
                Mappings = i.Mappings.Select(m => new GetLibraryCatalogResponse.LibraryCatalogMapping
                {
                    LinkPath = m.LinkPath,
                    TargetText = m.TargetText,
                    MappingType = m.MappingType,
                    Status = m.Status,
                }).ToList(),
            }).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
            IndexScannedAt = result.IndexScannedAt,
            IndexWarning = result.IndexWarning,
        });
    }
}
