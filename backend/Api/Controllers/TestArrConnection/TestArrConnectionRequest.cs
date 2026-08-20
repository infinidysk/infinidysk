using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestArrConnection;

public class TestArrConnectionRequest
{
    public string Host { get; init; }
    public string ApiKey { get; init; }

    public TestArrConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        Host = context.Request.Form["host"].FirstOrDefault() ?? "";
        var submittedApiKey = context.Request.Form["apiKey"].FirstOrDefault();
        if (string.IsNullOrEmpty(Host))
            errors.Add("host", "Arr host is required");
        if (submittedApiKey is null)
            errors.Add("apiKey", "Arr apiKey is required");
        errors.ThrowIfAny();
        ApiKey = ArrApiKeyResolver.Resolve(submittedApiKey!, configManager);
    }
}
