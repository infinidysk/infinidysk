using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Queue.PostProcessors;

public class BlocklistedFilePostProcessor(ConfigManager configManager, DavDatabaseClient dbClient)
{
    public void RemoveBlocklistedFiles()
    {
        var blocklistPatterns = configManager.GetBlocklistedFiles();
        var blocklistedFiles = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Where(x => MatchesAnyPattern(x.Name, blocklistPatterns));

        foreach (var blocklistedFile in blocklistedFiles)
            RemoveBlocklistedFile(blocklistedFile);
    }

    public static bool MatchesAnyPattern(string fileName, HashSet<string> patterns)
    {
        var lowerFileName = fileName.ToLower();
        return patterns.Any(pattern => MatchesPattern(lowerFileName, pattern));
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        // Convert pattern to regex:
        // 1. Escape all regex special characters (this escapes * to \*)
        // 2. Replace \* with .* to support greedy wildcard matching
        var regexPattern = Regex.Escape(pattern).Replace("\\*", ".*");
        return Regex.IsMatch(fileName, $"^{regexPattern}$");
    }

    private void RemoveBlocklistedFile(DavItem davItem)
    {
        DavItemRemover.Remove(dbClient, davItem);
    }
}
