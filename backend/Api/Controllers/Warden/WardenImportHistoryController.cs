using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

/// <summary>
/// Turns this install's own failed imports into Warden fingerprints. <c>dryRun</c> reports what
/// would be added without writing, so the counts can be shown before committing.
/// </summary>
[ApiController]
[Route("api/warden-import-history")]
public class WardenImportHistoryController(WardenHistoryImporter importer) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        // Default to a dry run: the write path is explicit, so a stray request can never add verdicts.
        var dryRun = HttpContext.Request.Query["dryRun"].ToString() != "false";
        var result = await importer.ImportAsync(dryRun, HttpContext.RequestAborted).ConfigureAwait(false);
        if (result is null)
            return Conflict(new BaseApiResponse
            {
                Status = false,
                Error = "A history scan is already running.",
            });

        return Ok(new WardenImportHistoryResponse
        {
            Status = true,
            DryRun = result.DryRun,
            Scanned = result.Scanned,
            Eligible = result.Eligible,
            Distinct = result.Distinct,
            Added = result.Added,
            SkippedMissingNzb = result.SkippedMissingNzb,
            SkippedUnparsableNzb = result.SkippedUnparsableNzb,
            SkippedNoFingerprint = result.SkippedNoFingerprint,
        });
    }
}

public class WardenImportHistoryResponse : BaseApiResponse
{
    [JsonPropertyName("dryRun")] public required bool DryRun { get; init; }
    [JsonPropertyName("scanned")] public required int Scanned { get; init; }
    [JsonPropertyName("eligible")] public required int Eligible { get; init; }
    [JsonPropertyName("distinct")] public required int Distinct { get; init; }
    [JsonPropertyName("added")] public required int Added { get; init; }
    [JsonPropertyName("skippedMissingNzb")] public required int SkippedMissingNzb { get; init; }
    [JsonPropertyName("skippedUnparsableNzb")] public required int SkippedUnparsableNzb { get; init; }
    [JsonPropertyName("skippedNoFingerprint")] public required int SkippedNoFingerprint { get; init; }
}
