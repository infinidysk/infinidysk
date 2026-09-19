using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestMediaServerConnection;

public sealed class TestMediaServerConnectionRequest
{
    public MediaServerType Type { get; }
    public string BaseUrl { get; }
    public string Token { get; }

    public TestMediaServerConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        var typeRaw = context.Request.Form["type"].FirstOrDefault();
        BaseUrl = context.Request.Form["baseUrl"].FirstOrDefault() ?? "";
        var submittedToken = context.Request.Form["token"].FirstOrDefault();

        if (!Enum.TryParse<MediaServerType>(typeRaw, ignoreCase: true, out var type)
            || !Enum.IsDefined(type))
        {
            errors.Add("type", "A supported media-server type is required");
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
            errors.Add("baseUrl", "Media-server base URL is required");
        if (submittedToken is null)
            errors.Add("token", "Media-server token/API key is required");

        errors.ThrowIfAny();

        Type = type;
        Token = MediaServerTokenResolver.Resolve(submittedToken!, configManager);

        MediaServerConfig.Validate(
            ConfigKeys.MediaServersInstances,
            new MediaServerConfig
            {
                Instances =
                [
                    new MediaServerInstance
                    {
                        Id = Guid.NewGuid(),
                        Type = Type,
                        Name = "Connection test",
                        BaseUrl = BaseUrl,
                        Token = Token,
                        Enabled = true,
                        PathMappings = [],
                    },
                ],
            });
    }
}
