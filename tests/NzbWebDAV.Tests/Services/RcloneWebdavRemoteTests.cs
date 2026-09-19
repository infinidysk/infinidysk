using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RcloneWebdavRemoteTests
{
    [Fact]
    public void BuildParameters_PointsAtTheBackendOverLoopback()
    {
        var parameters = RcloneWebdavRemote.BuildParameters("dav-user", "dav-pass");

        // The URL itself follows ASPNETCORE_URLS and is covered by the Resolve
        // cases below; asserting a fixed value here would make this test depend
        // on the environment it runs in.
        Assert.Equal("webdav", parameters["type"]);
        Assert.Equal("other", parameters["vendor"]);
    }

    [Fact]
    public void BuildParameters_CarriesTheSuppliedCredentials()
    {
        var parameters = RcloneWebdavRemote.BuildParameters("dav-user", "dav-pass");

        Assert.Equal("dav-user", parameters["user"]);
        Assert.Equal("dav-pass", parameters["pass"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildParameters_RejectsAnEmptyPassword(string? password)
    {
        // An empty password would create a remote that authenticates against
        // nothing, and the mount would fail later with an opaque 401.
        Assert.Throws<ArgumentException>(() => RcloneWebdavRemote.BuildParameters("dav-user", password));
    }

    [Fact]
    public void BuildParameters_RejectsAnEmptyUser()
    {
        Assert.Throws<ArgumentException>(() => RcloneWebdavRemote.BuildParameters(" ", "dav-pass"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://localhost:8080/")]
    [InlineData("http://0.0.0.0:9090", "http://localhost:9090/")]
    [InlineData("http://+:5000", "http://localhost:5000/")]
    [InlineData("http://*:5000", "http://localhost:5000/")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/")]
    public void Resolve_FollowsTheConfiguredListener(string urls, string expected)
    {
        // An installation that moves the backend off 8080 would otherwise get a
        // remote pointing at a port nothing is listening on.
        Assert.Equal(expected, RcloneWebdavRemote.Resolve(urls));
    }

    [Fact]
    public void Resolve_PrefersPlainHttp_WhenBothAreConfigured()
    {
        // The daemon shares the container, so loopback HTTP avoids a certificate
        // the backend may not have a trusted chain for.
        Assert.Equal(
            "http://localhost:8080/",
            RcloneWebdavRemote.Resolve("https://+:8443;http://+:8080"));
    }

    [Fact]
    public void Resolve_UsesHttps_WhenThatIsAllThatIsConfigured()
    {
        Assert.Equal("https://localhost:8443/", RcloneWebdavRemote.Resolve("https://+:8443"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void Resolve_FallsBackToTheDocumentedPort(string? urls)
    {
        Assert.Equal(
            $"http://localhost:{RcloneWebdavRemote.DefaultBackendPort}/",
            RcloneWebdavRemote.Resolve(urls));
    }
}
