using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Extensions;

public class NWebDavOptionsFilterTests
{
    private static bool ClaimedByWebDav(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return new NWebDavOptions().GetFilter()(context);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/get-config")]
    [InlineData("/view")]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/ws")]
    [InlineData("/p")]
    [InlineData("/openapi/admin.json")]
    [InlineData("/scalar/")]
    [InlineData("/adapters")]
    [InlineData("/adapters/addon/token/manifest.json")]
    public void LeavesApplicationRoutesAlone(string path)
    {
        Assert.False(ClaimedByWebDav(path));
    }

    [Theory]
    [InlineData("/content/movie.mkv")]
    [InlineData("/completed-symlinks/movie")]
    [InlineData("/.ids/abc")]
    [InlineData("/nzbs/file.nzb")]
    [InlineData("/")]
    public void ClaimsWebDavRoutes(string path)
    {
        Assert.True(ClaimedByWebDav(path));
    }

    [Theory]
    [InlineData("/readyfoo")]
    [InlineData("/healthfoo")]
    public void MatchesOnSegmentBoundariesOnly(string path)
    {
        Assert.True(ClaimedByWebDav(path));
    }
}
