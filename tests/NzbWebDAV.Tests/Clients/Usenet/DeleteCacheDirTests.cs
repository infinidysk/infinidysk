using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DeleteCacheDirTests
{
    [Fact]
    public async Task DeleteCacheDir_NonexistentPath_ReturnsPromptly()
    {
        var path = Path.Join(Path.GetTempPath(), "nzbdav-missing-" + Guid.NewGuid().ToString("N"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ArticleCachingNntpClient.DeleteCacheDir(path);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DeleteCacheDir_UndeletablePath_StopsAfterMaxAttempts()
    {
        var previous = ArticleCachingNntpClient.DeleteCacheDirInitialDelayMs;
        ArticleCachingNntpClient.DeleteCacheDirInitialDelayMs = 1;
        var blocker = Path.Join(Path.GetTempPath(), "nzbdav-block-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A file at the target path makes Directory.Delete throw IOException.
            await File.WriteAllTextAsync(blocker, "not-a-directory");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ArticleCachingNntpClient.DeleteCacheDir(blocker);
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"Expected capped retries; elapsed={sw.Elapsed}");
            Assert.True(File.Exists(blocker));
        }
        finally
        {
            ArticleCachingNntpClient.DeleteCacheDirInitialDelayMs = previous;
            try { File.Delete(blocker); } catch { /* ignore */ }
        }
    }
}
