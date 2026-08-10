using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;

namespace NzbWebDAV.Api.SabControllers.GetWarnings;

public class GetWarningsController(
    HttpContext httpContext,
    ConfigManager configManager,
    WarningLogBuffer warningLogBuffer
) : SabApiController.BaseController(httpContext, configManager)
{
    protected override Task<IActionResult> Handle()
    {
        var response = BuildResponse(Context.GetRequestParam("name"));
        return Task.FromResult<IActionResult>(Ok(response));
    }

    internal GetWarningsResponse BuildResponse(string? name)
    {
        if (name is not null and not ("show" or "clear"))
            throw new BadHttpRequestException($"Invalid name parameter '{name}'; expected 'show' or 'clear'");

        var snapshot = warningLogBuffer.Sink.Snapshot(
            50,
            levels: null,
            source: null,
            search: null,
            beforeSequence: null);

        return new GetWarningsResponse
        {
            Warnings = snapshot.Entries.Select(MapWarning).ToList(),
        };
    }

    internal static GetWarningsResponse.WarningItem MapWarning(LogEntry entry) =>
        new()
        {
            Type = entry.Level.ToUpperInvariant(),
            Text = entry.Exception is not null
                ? entry.Message + "\n" + entry.Exception
                : entry.Message,
            Time = entry.TimestampUnixMs / 1000,
        };
}
