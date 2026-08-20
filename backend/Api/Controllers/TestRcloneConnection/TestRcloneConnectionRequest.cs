using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

public class TestRcloneConnectionRequest
{
    public string Host { get; init; }
    public string? User { get; init; }
    public string? Pass { get; init; }

    public TestRcloneConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        Host = context.Request.Form["host"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(Host))
            errors.Add("host", "Rclone host is required");
        errors.ThrowIfAny();

        User = context.Request.Form["user"].FirstOrDefault();
        Pass = RclonePassResolver.Resolve(context.Request.Form["pass"].FirstOrDefault(), configManager);
    }
}
