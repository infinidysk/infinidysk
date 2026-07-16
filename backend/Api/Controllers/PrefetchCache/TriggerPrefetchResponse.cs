namespace NzbWebDAV.Api.Controllers.PrefetchCache;

public class TriggerPrefetchResponse : BaseApiResponse
{
    public required string Result { get; init; }
}
