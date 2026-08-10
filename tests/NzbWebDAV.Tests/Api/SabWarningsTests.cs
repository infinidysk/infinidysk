using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.GetWarnings;
using NzbWebDAV.Config;
using NzbWebDAV.Logging;
using Serilog;
using Serilog.Events;

namespace NzbWebDAV.Tests.Api;

public sealed class SabWarningsTests
{
    [Fact]
    public void Response_SerializesWarningsArray()
    {
        var response = new GetWarningsResponse
        {
            Warnings =
            [
                new GetWarningsResponse.WarningItem
                {
                    Type = "WARNING",
                    Text = "Provider timeout",
                    Time = 1_700_000_000,
                },
            ],
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var warnings = json.RootElement.GetProperty("warnings");
        Assert.Equal(JsonValueKind.Array, warnings.ValueKind);
        Assert.Equal("WARNING", warnings[0].GetProperty("type").GetString());
        Assert.Equal("Provider timeout", warnings[0].GetProperty("text").GetString());
        Assert.Equal(1_700_000_000, warnings[0].GetProperty("time").GetInt64());
    }

    [Fact]
    public void MapWarning_UppercasesLevelAndConvertsTimestampToSeconds()
    {
        var item = GetWarningsController.MapWarning(new LogEntry
        {
            Sequence = 1,
            TimestampUnixMs = 1_700_000_123_000,
            Level = "Warning",
            Message = "Streaming stalled",
        });

        Assert.Equal("WARNING", item.Type);
        Assert.Equal("Streaming stalled", item.Text);
        Assert.Equal(1_700_000_123, item.Time);
    }

    [Fact]
    public void MapWarning_AppendsExceptionText()
    {
        var item = GetWarningsController.MapWarning(new LogEntry
        {
            Sequence = 1,
            TimestampUnixMs = 1_000,
            Level = "Error",
            Message = "Download failed",
            Exception = "System.IO.IOException: broken pipe",
        });

        Assert.Equal("ERROR", item.Type);
        Assert.Equal("Download failed\nSystem.IO.IOException: broken pipe", item.Text);
    }

    [Fact]
    public void BuildResponse_ReturnsWarningsFromBuffer()
    {
        var buffer = CreateBufferWithWarning("Queue paused by operator");
        var controller = CreateController(buffer);

        var response = controller.BuildResponse(name: null);

        Assert.Single(response.Warnings);
        Assert.Equal("Queue paused by operator", response.Warnings[0].Text);
    }

    [Fact]
    public void BuildResponse_NameClear_ReturnsWarningsWithoutClearingBuffer()
    {
        var buffer = CreateBufferWithWarning("Keep this warning");
        var controller = CreateController(buffer);

        var first = controller.BuildResponse("clear");
        var second = controller.BuildResponse("clear");

        Assert.Single(first.Warnings);
        Assert.Single(second.Warnings);
        Assert.Equal("Keep this warning", second.Warnings[0].Text);
    }

    [Fact]
    public void BuildResponse_InvalidName_ThrowsBadHttpRequest()
    {
        var buffer = new WarningLogBuffer(new LogBufferSink(10));
        var controller = CreateController(buffer);

        Assert.Throws<BadHttpRequestException>(() => controller.BuildResponse("purge"));
    }

    private static WarningLogBuffer CreateBufferWithWarning(string message)
    {
        var sink = new LogBufferSink(10);
        var buffer = new WarningLogBuffer(sink);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink, restrictedToMinimumLevel: LogEventLevel.Verbose)
            .CreateLogger();
        logger.Warning(message);
        return buffer;
    }

    private static GetWarningsController CreateController(WarningLogBuffer buffer) =>
        new(new DefaultHttpContext(), new ConfigManager(), buffer);
}
