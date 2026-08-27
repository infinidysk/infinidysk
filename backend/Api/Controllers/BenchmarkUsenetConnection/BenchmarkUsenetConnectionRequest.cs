using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Api.Controllers.BenchmarkUsenetConnection;

public class BenchmarkUsenetConnectionRequest
{
    public string Host { get; init; }
    public string User { get; init; }
    public string Pass { get; init; }
    public int Port { get; init; }
    public bool UseSsl { get; init; }
    public bool SkipTlsVerification { get; init; }

    /// <summary>The provider's configured connection ceiling, which the benchmark never exceeds.</summary>
    public int MaxConnections { get; init; }

    /// <summary>The transfer connection count used by a pipelining-only run.</summary>
    public int TransferTestConnections { get; init; }

    public BenchmarkIntensity Intensity { get; init; }

    /// <summary>When true, skip the connection sweep and only tune pipelining depth.</summary>
    public bool PipeliningOnly { get; init; }

    /// <summary>Optional user override for the run's total data budget.</summary>
    public long? DataBudgetBytes { get; init; }

    /// <summary>When set, skip the sweep and just measure this single connection count.</summary>
    public int? VerifyConnections { get; init; }

    /// <summary>When true, cancel the in-flight speed test instead of starting a new one.</summary>
    public bool Cancel { get; init; }

    public BenchmarkUsenetConnectionRequest(HttpContext context, ConfigManager configManager)
    {
        Cancel = string.Equals(
            context.Request.Form["cancel"].FirstOrDefault(),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (Cancel)
        {
            // Cancel requests only need the flag — skip credential validation.
            Host = "";
            User = "";
            Pass = "";
            Port = 0;
            UseSsl = false;
            SkipTlsVerification = false;
            MaxConnections = 1;
            TransferTestConnections = 1;
            Intensity = BenchmarkIntensity.Quick;
            return;
        }

        Host = context.Request.Form["host"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet host is required");

        User = context.Request.Form["user"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet user is required");

        var submittedPass = context.Request.Form["pass"].FirstOrDefault()
               ?? throw new BadHttpRequestException("Usenet pass is required");
        Pass = UsenetPassResolver.Resolve(submittedPass, configManager);

        var port = context.Request.Form["port"].FirstOrDefault()
                   ?? throw new BadHttpRequestException("Usenet port is required");

        var useSsl = context.Request.Form["use-ssl"].FirstOrDefault()
                     ?? throw new BadHttpRequestException("Usenet use-ssl is required");

        Port = !int.TryParse(port, out var portValue)
            ? throw new BadHttpRequestException("Invalid usenet port")
            : portValue;

        UseSsl = !bool.TryParse(useSsl, out var useSslValue)
            ? throw new BadHttpRequestException("Invalid use-ssl value")
            : useSslValue;

        var skipTlsVerification = context.Request.Form["skip-tls-verification"].FirstOrDefault();
        SkipTlsVerification = bool.TryParse(skipTlsVerification, out var skipTlsVerificationValue)
                              && skipTlsVerificationValue;

        var maxConnections = context.Request.Form["max-connections"].FirstOrDefault();
        MaxConnections = !int.TryParse(maxConnections, out var mc) || mc < 1
            ? throw new BadHttpRequestException(
                "Provider connection limit must be a positive whole number")
            : mc;

        var transferConnections =
            context.Request.Form["transfer-connections"].FirstOrDefault();
        TransferTestConnections = string.IsNullOrWhiteSpace(transferConnections)
            ? MaxConnections
            : !int.TryParse(transferConnections, out var transferLimit)
              || transferLimit < 1
                ? throw new BadHttpRequestException(
                    "Transfer test connections must be a positive whole number")
                : transferLimit;
        if (TransferTestConnections > MaxConnections)
        {
            throw new BadHttpRequestException(
                "Transfer test connections must not exceed the provider connection limit");
        }

        var intensity = context.Request.Form["intensity"].FirstOrDefault();
        Intensity = string.Equals(intensity, "thorough", StringComparison.OrdinalIgnoreCase)
            ? BenchmarkIntensity.Thorough
            : BenchmarkIntensity.Quick;

        var pipeliningOnly = context.Request.Form["pipelining-only"].FirstOrDefault();
        PipeliningOnly = bool.TryParse(pipeliningOnly, out var po) && po;

        var budgetMb = context.Request.Form["data-budget-mb"].FirstOrDefault();
        DataBudgetBytes = long.TryParse(budgetMb, out var mb) && mb is >= 50 and <= 100_000
            ? mb * 1_000_000L
            : null;

        var verify = context.Request.Form["verify-connections"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(verify))
        {
            if (!int.TryParse(verify, out var vc) || vc < 1)
                throw new BadHttpRequestException(
                    "Verification connections must be a positive whole number");
            if (vc > MaxConnections)
                throw new BadHttpRequestException(
                    "Verification connections must not exceed the provider connection limit");
            VerifyConnections = vc;
        }
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
