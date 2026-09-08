using System.Net;
using System.Text;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients;

/// <summary>
/// Deleting or renaming an item tells rclone to drop the directory from its VFS
/// cache. With the built-in mount that has to reach the daemon InfiniDysk runs
/// itself; an installation that switched to built-in mode usually has the
/// external remote control switched off, so aiming at the external client alone
/// leaves the mount showing files that are gone.
/// </summary>
[Collection(nameof(RcloneClientCollection))]
public sealed class RcloneVfsForgetTargetTests : IDisposable
{
    private readonly RecordingHandler _handler = new();

    public RcloneVfsForgetTargetTests()
    {
        RcloneClient.TestHandler = _handler;
    }

    [Fact]
    public async Task RcloneVfsForget_ReachesTheBuiltinDaemon_WhenItIsRunning()
    {
        // The external remote control is off, which is the normal state once an
        // operator has moved to the built-in mount.
        RcloneClient.Initialize(new ConfigManager());
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "infinidysk", "secret");

        await DavDatabaseContext.RcloneVfsForget(["/content"], CancellationToken.None);

        Assert.Contains("http://127.0.0.1:5572/vfs/forget", _handler.Urls);
    }

    [Fact]
    public async Task RcloneVfsForget_PrefersTheBuiltinDaemon_OverAConfiguredExternalRclone()
    {
        // Both can be reachable during a migration. The daemon serving the mount
        // people are actually reading through is the one whose cache matters.
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://rclone.test" },
            new ConfigItem { ConfigName = ConfigKeys.RcloneRcEnabled, ConfigValue = "true" },
        ]);
        RcloneClient.Initialize(config);
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "infinidysk", "secret");

        await DavDatabaseContext.RcloneVfsForget(["/content"], CancellationToken.None);

        Assert.Contains("http://127.0.0.1:5572/vfs/forget", _handler.Urls);
        Assert.DoesNotContain("http://rclone.test/vfs/forget", _handler.Urls);
    }

    [Fact]
    public async Task RcloneVfsForget_StillUsesTheExternalRclone_WhenNoDaemonIsRunning()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://rclone.test" },
            new ConfigItem { ConfigName = ConfigKeys.RcloneRcEnabled, ConfigValue = "true" },
        ]);
        RcloneClient.Initialize(config);

        await DavDatabaseContext.RcloneVfsForget(["/content"], CancellationToken.None);

        Assert.Contains("http://rclone.test/vfs/forget", _handler.Urls);
    }

    [Fact]
    public async Task RcloneVfsForget_DoesNothing_WhenNeitherRcloneIsAvailable()
    {
        RcloneClient.Initialize(new ConfigManager());

        await DavDatabaseContext.RcloneVfsForget(["/content"], CancellationToken.None);

        Assert.Empty(_handler.Urls);
    }

    public void Dispose()
    {
        RcloneClient.Builtin = null;
        RcloneClient.TestHandler = null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"forgotten":["/content"]}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
