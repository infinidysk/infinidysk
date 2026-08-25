using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers;

internal static class SabPathResolver
{
    internal static string GetCompletedDir(ConfigManager configManager)
    {
        return configManager.GetImportStrategy() == "strm"
            ? configManager.GetStrmCompletedDownloadDir()
            : configManager.GetSymlinkCompletedDownloadDir();
    }
}
