using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.Diagnostics;

namespace NzbWebDAV.Api.Controllers.GetMemoryComponents;

/// <summary>
/// Authenticated low-overhead owner-attribution snapshot for benchmark sampling.
/// </summary>
[ApiController]
[Route("api/memory-components")]
public sealed class GetMemoryComponentsController(
    MemoryComponentSnapshotBuilder snapshotBuilder) : GetOnlyApiController
{
    protected override Task<IActionResult> HandleRequest() =>
        Task.FromResult<IActionResult>(Ok(snapshotBuilder.Capture()));
}
