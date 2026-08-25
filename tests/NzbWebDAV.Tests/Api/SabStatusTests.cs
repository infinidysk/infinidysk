using System.Text.Json;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class SabStatusTests
{
    [Fact]
    public void StatusResponse_SerializesStatusAsObject()
    {
        var response = new GetStatusResponse
        {
            Status = new SabStatusObject { CompleteDir = "/downloads/complete" },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));

        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("status").ValueKind);
        var status = json.RootElement.GetProperty("status");
        Assert.Equal(
            "/downloads/complete",
            status.GetProperty("completedir").GetString());
        Assert.False(status.GetProperty("paused").GetBoolean());
        Assert.Equal("0", status.GetProperty("speedlimit").GetString());
        Assert.Equal("0", status.GetProperty("speedlimit_abs").GetString());
        Assert.False(status.TryGetProperty("speed", out _));
        Assert.False(status.TryGetProperty("kbpersec", out _));
    }

    [Fact]
    public void StatusResponse_SerializesPauseAndSpeedLimit()
    {
        var response = new GetStatusResponse
        {
            Status = new SabStatusObject
            {
                CompleteDir = "/downloads/complete",
                Paused = true,
                SpeedLimit = "400",
                SpeedLimitAbs = "400",
            },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        var status = json.RootElement.GetProperty("status");
        Assert.True(status.GetProperty("paused").GetBoolean());
        Assert.Equal("400", status.GetProperty("speedlimit").GetString());
        Assert.Equal("400", status.GetProperty("speedlimit_abs").GetString());
        Assert.False(status.TryGetProperty("speed", out _));
        Assert.False(status.TryGetProperty("kbpersec", out _));
    }

    [Fact]
    public void CompletedDir_UsesStrmDirectoryForStrmImports()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiImportStrategy,
                ConfigValue = "strm",
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.ApiCompletedDownloadsDir,
                ConfigValue = "/data/strm",
            },
        ]);

        Assert.Equal("/data/strm", SabPathResolver.GetCompletedDir(config));
    }

    [Fact]
    public void CompletedDir_UsesSymlinkFolderForSymlinkImports()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.RcloneMountDir,
                ConfigValue = "/mnt/nzbdav",
            },
        ]);

        Assert.Equal(
            Path.Join("/mnt/nzbdav", DavItem.SymlinkFolder.Name),
            SabPathResolver.GetCompletedDir(config));
    }
}
