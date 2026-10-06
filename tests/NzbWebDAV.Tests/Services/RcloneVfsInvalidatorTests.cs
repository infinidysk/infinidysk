using System.Net;
using System.Text;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Imports and deletions are only visible through a mount once rclone drops the
/// directory it cached. With polling off and a week-long directory cache, an
/// invalidation that does not arrive is not a delay -- it is a library that
/// shows the wrong thing until somebody restarts something.
/// </summary>
[Collection(nameof(RcloneClientCollection))]
public sealed class RcloneVfsInvalidatorTests : IDisposable
{
    private readonly RecordingHandler _handler = new();

    public RcloneVfsInvalidatorTests()
    {
        RcloneClient.TestHandler = _handler;
    }

    [Fact]
    public async Task ForgetAsync_TellsBothTheBuiltinDaemonAndAStillMountedSidecar()
    {
        // Starting the built-in daemon does not unmount the sidecar. Until the
        // operator finishes moving across, both are serving the same library, and
        // telling only one leaves the other showing what was deleted.
        _handler.Mounts["127.0.0.1:5572"] = ["infinidysk:"];
        _handler.Mounts["rclone:5572"] = ["nzbdav:"];
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "u", "p");
        var external = new ConfigManager();
        external.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://rclone:5572" },
            new ConfigItem { ConfigName = ConfigKeys.RcloneRcEnabled, ConfigValue = "true" },
        ]);
        RcloneClient.Initialize(external);

        await RcloneVfsInvalidator.ForgetAsync(["/content/Movies"], CancellationToken.None);

        Assert.Contains(_handler.Forgets, f => f.Host == "127.0.0.1:5572");
        Assert.Contains(_handler.Forgets, f => f.Host == "rclone:5572");

        // One VFS each, so neither request names one: rclone documents the
        // unqualified form for a single VFS, and naming it adds a way to fail.
        Assert.All(_handler.Forgets, f => Assert.Null(f.Fs));
    }

    [Fact]
    public async Task ForgetAsync_NamesTheVfsAndUsesItsOwnPaths_WhenSeveralAreMounted()
    {
        // rclone refuses an unqualified vfs/forget once more than one VFS is
        // active, and a VFS rooted at /content reads "/content/Movies" as
        // "/content/content/Movies" because it joins onto its own root.
        _handler.Mounts["127.0.0.1:5572"] = ["infinidysk:", "infinidysk:/content"];
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "u", "p");

        await RcloneVfsInvalidator.ForgetAsync(["/content/Movies", "/.ids"], CancellationToken.None);

        var root = Assert.Single(_handler.Forgets, f => f.Fs == "infinidysk:");
        Assert.Equal(["/content/Movies", "/.ids"], root.Dirs);

        var subtree = Assert.Single(_handler.Forgets, f => f.Fs == "infinidysk:/content");
        Assert.Equal(["Movies"], subtree.Dirs);
    }

    [Fact]
    public async Task ForgetAsync_TranslatesPaths_ForALoneSubtreeMount()
    {
        // rclone joins what it is given onto the VFS root, so an untranslated
        // "/content/Movies" would forget "/content/content/Movies" here.
        _handler.Mounts["127.0.0.1:5572"] = ["infinidysk:/content"];
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "u", "p");

        await RcloneVfsInvalidator.ForgetAsync(["/content/Movies"], CancellationToken.None);

        var forget = Assert.Single(_handler.Forgets);
        Assert.Equal(["Movies"], forget.Dirs);
    }

    [Fact]
    public async Task ForgetAsync_SendsEachNameOnce_WhenTwoMountsShareOneRemote()
    {
        // Two mounts of one remote are two VFS instances under one name. The
        // built-in mount list rejects that, but an external rclone can still be
        // set up this way. Counting distinct remotes would see two here and send
        // the unqualified request, which rclone refuses whenever more than one
        // VFS is active. Each name goes out once, qualified, and the VFS that
        // does not share its name still has its own paths forgotten.
        _handler.Mounts["127.0.0.1:5572"] = ["infinidysk:", "infinidysk:", "infinidysk:/content"];
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "u", "p");

        await RcloneVfsInvalidator.ForgetAsync(["/content/Movies"], CancellationToken.None);

        Assert.Equal(2, _handler.Forgets.Count);
        Assert.DoesNotContain(_handler.Forgets, f => f.Fs is null);

        var shared = Assert.Single(_handler.Forgets, f => f.Fs == "infinidysk:");
        Assert.Equal(["/content/Movies"], shared.Dirs);

        var subtree = Assert.Single(_handler.Forgets, f => f.Fs == "infinidysk:/content");
        Assert.Equal(["Movies"], subtree.Dirs);
    }

    [Fact]
    public async Task ForgetAsync_SendsOneUnqualifiedRequest_WhenTheMountsCannotBeListed()
    {
        // An unreadable mount listing is not an answer. The single-VFS request is
        // what every install with one mount has always used, so it stays the
        // fallback rather than skipping invalidation entirely.
        _handler.FailListMounts = true;
        RcloneClient.Builtin = RcloneClient.ForEndpoint("http://127.0.0.1:5572", "u", "p");

        await RcloneVfsInvalidator.ForgetAsync(["/content/Movies"], CancellationToken.None);

        var forget = Assert.Single(_handler.Forgets);
        Assert.Null(forget.Fs);
        Assert.Equal(["/content/Movies"], forget.Dirs);
    }

    [Theory]
    [InlineData("/", "/content/Movies", "/content/Movies")]
    [InlineData("/content", "/content/Movies", "Movies")]
    [InlineData("/content", "/content", "")]
    public void Relativize_RewritesPathsIntoTheVfsOwnNamespace(
        string remotePath,
        string path,
        string expected)
    {
        Assert.Equal([expected], RcloneVfsInvalidator.Relativize([path], remotePath));
    }

    [Fact]
    public void Relativize_DropsPathsTheVfsCannotSee()
    {
        // rclone joins what it is given onto the VFS root and reports nothing
        // when the path is not there, so an untranslated path forgets a
        // directory that does not exist while the one that changed stays
        // cached.
        Assert.Empty(RcloneVfsInvalidator.Relativize(["/.ids/abc"], "/content"));
    }

    public void Dispose()
    {
        RcloneClient.TestHandler = null;
        RcloneClient.Builtin = null;
        RcloneClient.Initialize(new ConfigManager());
    }

    private sealed record ForgetCall(string Host, string? Fs, List<string> Dirs);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, string[]> Mounts { get; } = [];
        public List<ForgetCall> Forgets { get; } = [];
        public bool FailListMounts { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var host = request.RequestUri!.Authority;
            var path = request.RequestUri.AbsolutePath;

            if (path == "/mount/listmounts")
            {
                if (FailListMounts) return Json(HttpStatusCode.InternalServerError, "{}");

                var entries = Mounts.TryGetValue(host, out var list) ? list : [];
                var json = string.Join(
                    ",",
                    entries.Select(fs => $$"""{"Fs":"{{fs}}","MountPoint":"/mnt/{{fs.GetHashCode()}}"}"""));
                return Json(HttpStatusCode.OK, $$"""{"mountPoints":[{{json}}]}""");
            }

            if (path == "/vfs/forget")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var parsed = System.Text.Json.JsonDocument.Parse(body);
                var root = parsed.RootElement;

                var fs = root.TryGetProperty("fs", out var fsValue) ? fsValue.GetString() : null;
                var dirs = root.EnumerateObject()
                    .Where(p => p.Name.StartsWith("dir", StringComparison.Ordinal))
                    .OrderBy(p => p.Name.Length)
                    .ThenBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => p.Value.GetString() ?? "")
                    .ToList();

                Forgets.Add(new ForgetCall(host, fs, dirs));
            }

            return Json(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
