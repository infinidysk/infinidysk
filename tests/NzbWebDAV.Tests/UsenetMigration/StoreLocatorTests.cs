using NzbWebDAV.UsenetMigration.Runner;

namespace NzbWebDAV.Tests.UsenetMigration;

public class StoreLocatorTests
{
    [Fact]
    public void ResolveSourceNzb_FindsRecordedPath()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-storeloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var nzb = Path.Join(dir, "Show.nzb");
            File.WriteAllText(nzb, "<nzb/>");
            Assert.Equal(nzb, StoreLocator.ResolveSourceNzb(nzb, storeRoot: null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveSourceNzb_AppendsGzWhenPlainMissing()
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-storeloc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plain = Path.Join(dir, "Show.nzb");
            var gz = plain + ".gz";
            File.WriteAllBytes(gz, [0x1f, 0x8b]);
            Assert.Equal(gz, StoreLocator.ResolveSourceNzb(plain, storeRoot: null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveSourceNzb_RemapsUnderStoreRootViaNzbsSuffix()
    {
        var root = Path.Join(Path.GetTempPath(), "nzbdav-storeloc-" + Guid.NewGuid().ToString("N"));
        var nzbs = Path.Join(root, ".nzbs", "tv");
        Directory.CreateDirectory(nzbs);
        try
        {
            var local = Path.Join(nzbs, "Show.nzb.gz");
            File.WriteAllBytes(local, [0x1f, 0x8b]);
            var foreign = "/other/host/.nzbs/tv/Show.nzb";
            Assert.Equal(local, StoreLocator.ResolveSourceNzb(foreign, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveUnderRoot_ReturnsNullWhenSuffixAbsent()
    {
        Assert.Null(StoreLocator.ResolveUnderRoot("/tmp/Show.nzb", "/config"));
    }
}
