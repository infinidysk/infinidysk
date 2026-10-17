using NzbWebDAV.Queue.Inspection;

namespace NzbWebDAV.Api.Controllers.InspectNzb;

public sealed class InspectNzbResponse : BaseApiResponse
{
    public required NzbInspectionReport Inspection { get; init; }
}
