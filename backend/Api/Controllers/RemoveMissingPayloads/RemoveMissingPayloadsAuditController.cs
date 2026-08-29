using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Tasks;

namespace NzbWebDAV.Api.Controllers.RemoveMissingPayloads;

[ApiController]
[Route("api/remove-missing-payloads/audit")]
public sealed class RemoveMissingPayloadsAuditController : GetOnlyApiController
{
    protected override Task<IActionResult> HandleRequest() =>
        Task.FromResult<IActionResult>(Ok(new RemoveMissingPayloadsAuditResponse
        {
            Status = true,
            Report = RemoveMissingPayloadsTask.GetAuditReport(),
        }));
}
