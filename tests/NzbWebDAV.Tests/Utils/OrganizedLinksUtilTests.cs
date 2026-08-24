using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class OrganizedLinksUtilTests
{
    [Fact]
    public void GetLink_WithoutLibraryDirectory_ReturnsNullWithoutScanning()
    {
        var configManager = new ConfigManager();
        var id = Guid.NewGuid();
        var item = new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            Name = "movie.mkv",
            Path = "/content/movie.mkv",
        };

        Assert.Null(OrganizedLinksUtil.GetLink(item, configManager));
        Assert.Empty(OrganizedLinksUtil.GetLibraryDavItemLinks(configManager));
    }

    [Fact]
    public void GetDavItemLink_Symlink_SkipsNonGuidTarget()
    {
        var symlink = new SymlinkAndStrmUtil.SymlinkInfo
        {
            SymlinkPath = "/library/movie.mkv",
            TargetPath = "/mnt/nzbdav/.ids/not-a-guid.mkv",
        };

        var link = OrganizedLinksUtil.GetDavItemLink(symlink, "/mnt/nzbdav");

        Assert.Null(link);
    }

    [Fact]
    public void GetDavItemLink_Symlink_ParsesGuidTarget()
    {
        var id = Guid.NewGuid();
        var symlink = new SymlinkAndStrmUtil.SymlinkInfo
        {
            SymlinkPath = "/library/movie.mkv",
            TargetPath = $"/mnt/nzbdav/.ids/{id}.mkv",
        };

        var link = OrganizedLinksUtil.GetDavItemLink(symlink, "/mnt/nzbdav");

        Assert.NotNull(link);
        var parsed = link ?? throw new InvalidOperationException("expected link");
        Assert.Equal(id, parsed.DavItemId);
        Assert.Equal("/library/movie.mkv", parsed.LinkPath);
    }

    [Fact]
    public void GetDavItemLink_Strm_SkipsMalformedUrl()
    {
        var strm = new SymlinkAndStrmUtil.StrmInfo
        {
            StrmPath = "/library/movie.strm",
            TargetUrl = "not a url",
        };

        var link = OrganizedLinksUtil.GetDavItemLink(strm);

        Assert.Null(link);
    }

    [Fact]
    public void GetDavItemLink_Strm_SkipsNonGuidTarget()
    {
        var strm = new SymlinkAndStrmUtil.StrmInfo
        {
            StrmPath = "/library/movie.strm",
            TargetUrl = "http://localhost:3000/view/.ids/not-a-guid.mkv",
        };

        var link = OrganizedLinksUtil.GetDavItemLink(strm);

        Assert.Null(link);
    }

    [Fact]
    public void GetDavItemLink_Strm_ParsesGuidTarget()
    {
        var id = Guid.NewGuid();
        var strm = new SymlinkAndStrmUtil.StrmInfo
        {
            StrmPath = "/library/movie.strm",
            TargetUrl = $"http://localhost:3000/view/.ids/{id}.mkv",
        };

        var link = OrganizedLinksUtil.GetDavItemLink(strm);

        Assert.NotNull(link);
        var parsed = link ?? throw new InvalidOperationException("expected link");
        Assert.Equal(id, parsed.DavItemId);
        Assert.Equal("/library/movie.strm", parsed.LinkPath);
    }
}
