using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Tasks;

namespace NzbWebDAV.Api.Controllers.RemoveSampleFiles;

[ApiController]
[Route("api/remove-sample-files/audit")]
public class RemoveSampleFilesAuditController(
) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var report = RemoveSampleFilesTask.GetAuditReport();
        return Task.FromResult<IActionResult>(Ok(report));
    }
}
