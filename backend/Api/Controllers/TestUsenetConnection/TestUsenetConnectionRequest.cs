using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;
using NzbWebDAV.Models;

namespace NzbWebDAV.Api.Controllers.TestUsenetConnection;

public class TestUsenetConnectionRequest
{
    public string Host { get; init; }
    public string User { get; init; }
    public string Pass { get; init; }
    public int Port { get; init; }
    public bool UseSsl { get; init; }
    public bool SkipTlsVerification { get; init; }

    public TestUsenetConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        Host = context.Request.Form["host"].FirstOrDefault() ?? "";
        User = context.Request.Form["user"].FirstOrDefault() ?? "";
        var submittedPass = context.Request.Form["pass"].FirstOrDefault();
        var port = context.Request.Form["port"].FirstOrDefault();
        var useSsl = context.Request.Form["use-ssl"].FirstOrDefault();

        if (string.IsNullOrEmpty(Host))
            errors.Add("host", "Usenet host is required");
        if (string.IsNullOrEmpty(User))
            errors.Add("user", "Usenet user is required");
        if (submittedPass is null)
            errors.Add("pass", "Usenet pass is required");
        if (port is null)
            errors.Add("port", "Usenet port is required");
        else if (!int.TryParse(port, out var portValue))
            errors.Add("port", "Invalid usenet port");
        else
            Port = portValue;

        if (useSsl is null)
            errors.Add("use-ssl", "Usenet use-ssl is required");
        else if (!bool.TryParse(useSsl, out var useSslValue))
            errors.Add("use-ssl", "Invalid use-ssl value");
        else
            UseSsl = useSslValue;

        errors.ThrowIfAny();
        Pass = UsenetPassResolver.Resolve(submittedPass!, configManager);

        var skipTlsVerification = context.Request.Form["skip-tls-verification"].FirstOrDefault();
        SkipTlsVerification = bool.TryParse(skipTlsVerification, out var skipTlsVerificationValue)
                              && skipTlsVerificationValue;
    }

    public UsenetProviderConfig.ConnectionDetails ToConnectionDetails()
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            Host = Host,
            User = User,
            Pass = Pass,
            Port = Port,
            UseSsl = UseSsl,
            SkipTlsVerification = UseSsl && SkipTlsVerification,
            MaxConnections = 1,
            Type = ProviderType.Disabled
        };
    }
}
