using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.Diagnostics;

namespace NzbWebDAV.Api.Controllers.GetMemoryComponents;

/// <summary>
/// Authenticated low-overhead owner-attribution snapshot for benchmark sampling.
/// </summary>
[ApiController]
[Route("api/memory-components")]
[ProducesResponseType(typeof(MemoryComponentSnapshot), StatusCodes.Status200OK)]
public sealed class GetMemoryComponentsController(
    MemoryComponentSnapshotBuilder snapshotBuilder) : GetOnlyApiController
{
    protected override Task<IActionResult> HandleRequest() =>
        Task.FromResult<IActionResult>(Ok(snapshotBuilder.Capture()));
}
