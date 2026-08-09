using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-backup")]
public class WardenBackupController(WardenStore warden) : BaseApiController
{
    private static readonly Regex RepoRx = new("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$");

    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult(HttpContext.Request.Method == HttpMethods.Post ? Save() : Status());
    }

    private IActionResult Status()
    {
        var s = warden.GetBackupSettings();
        return Ok(ToResponse(s));
    }

    private IActionResult Save()
    {
        var form = HttpContext.Request.Form;
        var repo = form["repo"].ToString().Trim();
        if (!string.IsNullOrEmpty(repo) && !RepoRx.IsMatch(repo))
            throw new BadHttpRequestException("Repository must be in owner/name form.");

        var enabled = form["enabled"].ToString() == "true";
        var path = form["path"].ToString();
        var branch = form["branch"].ToString();
        var scope = form["scope"].ToString();
        var interval = int.TryParse(form["intervalHours"], out var n) ? n : 24;

        var tokenRaw = form["token"].ToString();
        string? token = string.IsNullOrEmpty(tokenRaw) ? null : tokenRaw;

        if (enabled && token is null && !warden.GetBackupSettings().HasToken)
            throw new BadHttpRequestException("A GitHub token is required to enable backups.");

        warden.SaveBackupSettings(enabled, repo, path, branch, scope, interval, token);
        return Ok(ToResponse(warden.GetBackupSettings()));
    }

    private static WardenBackupStatusResponse ToResponse(WardenBackupSettings s) => new()
    {
        Enabled = s.Enabled,
        Repo = s.Repo,
        Path = s.Path,
        Branch = s.Branch,
        Scope = s.Scope,
        IntervalHours = s.IntervalHours,
        HasToken = s.HasToken,
        LastAt = s.LastAt,
        LastStatus = s.LastStatus,
        RawUrl = string.IsNullOrWhiteSpace(s.Repo) || string.IsNullOrWhiteSpace(s.Path)
            ? null
            : $"https://raw.githubusercontent.com/{s.Repo}/{s.Branch}/{s.Path}",
    };
}

[ApiController]
[Route("api/warden-backup-now")]
public class WardenBackupNowController(WardenBackupService backup) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var msg = await backup.PushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new WardenBackupMutateResponse { Status = !msg.StartsWith("error", StringComparison.Ordinal), Message = msg });
    }
}

[ApiController]
[Route("api/warden-backup-restore")]
public class WardenBackupRestoreController(WardenBackupService backup) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var msg = await backup.RestoreAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new WardenBackupMutateResponse { Status = true, Message = msg });
    }
}

public class WardenBackupStatusResponse : BaseApiResponse
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("repo")] public string Repo { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("branch")] public string Branch { get; init; } = "";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("intervalHours")] public int IntervalHours { get; init; }
    [JsonPropertyName("hasToken")] public bool HasToken { get; init; }
    [JsonPropertyName("lastAt")] public long LastAt { get; init; }
    [JsonPropertyName("lastStatus")] public string? LastStatus { get; init; }
    [JsonPropertyName("rawUrl")] public string? RawUrl { get; init; }
}

public class WardenBackupMutateResponse : BaseApiResponse
{
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
