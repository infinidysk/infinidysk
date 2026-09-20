namespace NzbWebDAV.Api.Controllers.UpdateConfig;

public class UpdateConfigResponse : BaseApiResponse
{
    public string ActiveCacheMode { get; init; } = "off";
    public string ConfiguredCacheMode { get; init; } = "off";
    public bool RestartRequired { get; init; }
}
