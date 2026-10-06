using System.Net;
using System.Text;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Clients;

[Collection(nameof(RcloneClientCollection))]
public class RcloneMountApiTests : IDisposable
{
    private readonly CapturingHandler _handler = new();

    public RcloneMountApiTests()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://rclone.test" },
        ]);
        RcloneClient.Initialize(config);
        RcloneClient.TestHandler = _handler;
    }

    [Fact]
    public async Task ListMounts_ReturnsWhatIsMounted()
    {
        _handler.Respond("/mount/listmounts", """
            {"mountPoints":[{"Fs":"infinidysk:","MountPoint":"/mnt/remote","MountedOn":"2026-09-08T20:39:55Z"}]}
            """);

        var result = await RcloneClient.Current!.ListMounts();

        Assert.True(result.Success);
        var mount = Assert.Single(result.MountPoints!);
        Assert.Equal("infinidysk:", mount.Fs);
        Assert.Equal("/mnt/remote", mount.MountPoint);
    }

    [Fact]
    public async Task ListMounts_TreatsAnAbsentListAsNothingMounted()
    {
        _handler.Respond("/mount/listmounts", "{}");

        var result = await RcloneClient.Current!.ListMounts();

        Assert.True(result.Success);
        Assert.Empty(result.MountPoints ?? []);
    }

    [Fact]
    public async Task MountFs_SendsTheMountAndVfsOptions()
    {
        _handler.Respond("/mount/mount", """{"mountPoint":"/mnt/remote"}""");

        var result = await RcloneClient.Current!.MountFs(
            "infinidysk:/",
            "/mnt/remote",
            new Dictionary<string, object?> { ["AllowOther"] = true },
            new Dictionary<string, object?> { ["CacheMode"] = "full", ["ReadAhead"] = 536870912 });

        Assert.True(result.Success);
        var body = _handler.LastBody("/mount/mount");
        Assert.Contains("\"fs\":\"infinidysk:/\"", body, StringComparison.Ordinal);
        Assert.Contains("\"mountPoint\":\"/mnt/remote\"", body, StringComparison.Ordinal);
        Assert.Contains("\"AllowOther\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"CacheMode\":\"full\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MountFs_SurfacesTheRcloneErrorMessage()
    {
        _handler.Respond("/mount/mount", """{"error":"mount point is not empty"}""", HttpStatusCode.InternalServerError);

        var result = await RcloneClient.Current!.MountFs("infinidysk:/", "/mnt/remote", null, null);

        Assert.False(result.Success);
        Assert.Equal("mount point is not empty", result.Error);
    }

    [Fact]
    public async Task UnmountFs_TargetsTheMountPoint()
    {
        _handler.Respond("/mount/unmount", "{}");

        var result = await RcloneClient.Current!.UnmountFs("/mnt/remote");

        Assert.True(result.Success);
        Assert.Contains("\"mountPoint\":\"/mnt/remote\"", _handler.LastBody("/mount/unmount"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRemote_AsksRcloneToObscureThePassword()
    {
        _handler.Respond("/config/create", "{}");

        var result = await RcloneClient.Current!.CreateRemote(
            "infinidysk",
            "webdav",
            new Dictionary<string, string>
            {
                ["url"] = "http://localhost:8080/",
                ["vendor"] = "other",
                ["user"] = "dav",
                ["pass"] = "plaintext",
            });

        Assert.True(result.Success);
        var body = _handler.LastBody("/config/create");
        Assert.Contains("\"type\":\"webdav\"", body, StringComparison.Ordinal);
        // rclone stores an obscured password; sending obscure=true means we never
        // have to implement (or get wrong) rclone's own obscure algorithm.
        Assert.Contains("\"obscure\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRemotes_ReturnsConfiguredRemoteNames()
    {
        _handler.Respond("/config/listremotes", """{"remotes":["infinidysk"]}""");

        var result = await RcloneClient.Current!.ListRemotes();

        Assert.True(result.Success);
        Assert.Equal(["infinidysk"], result.Remotes);
    }

    // The built-in daemon is a second rclone at a different address with its own
    // generated credentials. It gets its own client instance rather than
    // overwriting the user's saved external settings.
    [Fact]
    public async Task ForEndpoint_TalksToTheGivenAddressWithoutTouchingSavedConfig()
    {
        _handler.Respond("/config/listremotes", """{"remotes":["infinidysk"]}""");

        using var builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "infinidysk", "generated");

        Assert.Equal("http://127.0.0.1:5572", builtin.Host);
        Assert.True(builtin.IsRemoteControlEnabled);

        var result = await builtin.ListRemotes();
        Assert.True(result.Success);
    }

    [Fact]
    public void ForEndpoint_DoesNotDisturbTheConfiguredExternalClient()
    {
        using var builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "infinidysk", "generated");

        Assert.Equal("http://rclone.test", RcloneClient.Current!.Host);
    }

    [Fact]
    public async Task CreateRemote_DoesNotReturnRclonesErrorBody()
    {
        // rclone includes the request it could not process in some error bodies,
        // and that request carries the plaintext WebDAV password. The body must
        // reach neither the log nor the admin UI.
        _handler.Respond(
            "/config/create",
            """
            {"error":"couldn't create remote: pass=hunter2 user=dav","input":{"parameters":{"pass":"hunter2"}}}
            """,
            HttpStatusCode.InternalServerError);

        var result = await RcloneClient.Current!.CreateRemote(
            "infinidysk",
            "webdav",
            new Dictionary<string, string> { ["pass"] = "hunter2" });

        Assert.False(result.Success);
        Assert.DoesNotContain("hunter2", result.Error!, StringComparison.Ordinal);
        Assert.Equal("HTTP InternalServerError", result.Error);
    }

    [Fact]
    public async Task CreateRemote_StillReportsAnAuthenticationFailure()
    {
        _handler.Respond("/config/create", "{}", HttpStatusCode.Unauthorized);

        var result = await RcloneClient.Current!.CreateRemote(
            "infinidysk",
            "webdav",
            new Dictionary<string, string> { ["pass"] = "hunter2" });

        Assert.False(result.Success);
        Assert.Equal("Authentication failed", result.Error);
    }

    public void Dispose()
    {
        RcloneClient.TestHandler = null;
        RcloneClient.Current?.Dispose();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _requestBodies = new(StringComparer.Ordinal);

        public void Respond(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses[path] = (body, status);

        public string LastBody(string path) => _requestBodies[path];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            _requestBodies[path] = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (!_responses.TryGetValue(path, out var response))
                throw new InvalidOperationException($"Unexpected request: {path}");

            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
