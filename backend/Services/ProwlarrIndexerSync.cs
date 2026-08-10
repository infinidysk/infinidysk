using NzbWebDAV.Clients.Prowlarr;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public static class ProwlarrIndexerSync
{
    public static ProwlarrSyncMergeResult Merge(
        IndexerConfig indexerConfig,
        ProfileConfig profileConfig,
        IReadOnlyList<ProwlarrIndexer> remoteIndexers,
        string prowlarrUrl,
        string prowlarrApiKey)
    {
        var result = new ProwlarrSyncMergeResult(indexerConfig, profileConfig)
        {
            RemoteIndexerCount = remoteIndexers.Count,
        };

        var managedById = new Dictionary<int, IndexerConfig.ConnectionDetails>();
        foreach (var managed in indexerConfig.Indexers.Where(x => x.ProwlarrIndexerId is not null))
        {
            // A copied config can contain duplicate ownership metadata. The first entry
            // wins; any later duplicate is treated as stale and removed below.
            managedById.TryAdd(managed.ProwlarrIndexerId!.Value, managed);
        }

        var reservedNames = indexerConfig.Indexers
            .Where(x => x.ProwlarrIndexerId is null)
            .Select(x => x.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedManagedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptManagedIds = new HashSet<int>();
        var seenRemoteIds = new HashSet<int>();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var remote in remoteIndexers
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Id))
        {
            if (!seenRemoteIds.Add(remote.Id))
            {
                result.Skipped++;
                result.SkippedDetails.Add($"{remote.Name}: duplicate Prowlarr indexer ID {remote.Id}");
                continue;
            }

            managedById.TryGetValue(remote.Id, out var existing);
            if (!IsSupported(remote))
            {
                // Existing managed entries are removed below. New unsupported entries are skipped.
                if (existing is null)
                {
                    result.Skipped++;
                    result.SkippedDetails.Add($"{remote.Name}: not a searchable Usenet indexer");
                }
                continue;
            }

            // A disabled Prowlarr indexer updates an existing managed entry to disabled,
            // but is not newly imported until it is enabled upstream.
            if (!remote.Enable && existing is null)
            {
                result.Skipped++;
                result.SkippedDetails.Add($"{remote.Name}: disabled in Prowlarr");
                continue;
            }

            var remoteName = remote.Name!.Trim();
            var finalName = remoteName;
            var nameSkipped = false;
            if (reservedNames.Contains(remoteName) || usedManagedNames.Contains(remoteName))
            {
                if (existing is null)
                {
                    result.Skipped++;
                    result.SkippedDetails.Add($"{remoteName}: conflicts with an existing indexer name");
                    continue;
                }

                // Keep the prior local name rather than introducing an ambiguous
                // name-based search-profile reference. Other owned fields still sync.
                finalName = existing.Name;
                nameSkipped = true;
            }

            keptManagedIds.Add(remote.Id);
            usedManagedNames.Add(finalName);

            if (existing is null)
            {
                indexerConfig.Indexers.Add(new IndexerConfig.ConnectionDetails
                {
                    Name = finalName,
                    Url = ProwlarrClient.BuildIndexerApiUrl(prowlarrUrl, remote.Id),
                    ApiKey = prowlarrApiKey,
                    Enabled = remote.Enable,
                    ProwlarrIndexerId = remote.Id,
                });
                result.Added++;
                continue;
            }

            var changed = false;
            if (!string.Equals(existing.Name, finalName, StringComparison.Ordinal))
            {
                renames[existing.Name] = finalName;
                existing.Name = finalName;
                changed = true;
            }

            var nextUrl = ProwlarrClient.BuildIndexerApiUrl(prowlarrUrl, remote.Id);
            if (!string.Equals(existing.Url, nextUrl, StringComparison.Ordinal))
            {
                existing.Url = nextUrl;
                changed = true;
            }

            if (!string.Equals(existing.ApiKey, prowlarrApiKey, StringComparison.Ordinal))
            {
                existing.ApiKey = prowlarrApiKey;
                changed = true;
            }

            if (existing.Enabled != remote.Enable)
            {
                existing.Enabled = remote.Enable;
                changed = true;
            }

            if (nameSkipped)
            {
                result.Skipped++;
                result.SkippedDetails.Add($"{remoteName}: name conflicts with an existing indexer; kept {finalName}");
            }

            if (changed) result.Updated++;
        }

        var removedNames = new List<string>();
        foreach (var existing in indexerConfig.Indexers
                     .Where(x => x.ProwlarrIndexerId is { } id && !keptManagedIds.Contains(id))
                     .ToList())
        {
            removedNames.Add(existing.Name);
            indexerConfig.Indexers.Remove(existing);
            result.Removed++;
        }

        ApplyProfileChanges(profileConfig, renames, removedNames, result);
        result.ManagedIndexerCount = indexerConfig.Indexers.Count(x => x.ProwlarrIndexerId is not null);
        return result;
    }

    private static bool IsSupported(ProwlarrIndexer indexer) =>
        string.Equals(indexer.Protocol, "usenet", StringComparison.OrdinalIgnoreCase)
        && indexer.SupportsSearch;

    private static void ApplyProfileChanges(
        ProfileConfig profileConfig,
        Dictionary<string, string> renames,
        List<string> removedNames,
        ProwlarrSyncMergeResult result)
    {
        if (renames.Count == 0 && removedNames.Count == 0) return;
        var removed = removedNames.ToHashSet(StringComparer.Ordinal);

        foreach (var profile in profileConfig.Profiles)
        {
            var next = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var oldName in profile.IndexerNames.Where(x => !removed.Contains(x)))
            {
                var name = renames.GetValueOrDefault(oldName, oldName);
                if (seen.Add(name)) next.Add(name);
            }

            if (!next.SequenceEqual(profile.IndexerNames, StringComparer.Ordinal))
            {
                profile.IndexerNames = next;
                result.ProfilesChanged = true;
            }
        }
    }
}

public sealed class ProwlarrSyncMergeResult(IndexerConfig indexerConfig, ProfileConfig profileConfig)
{
    public IndexerConfig IndexerConfig { get; } = indexerConfig;
    public ProfileConfig ProfileConfig { get; } = profileConfig;
    public int RemoteIndexerCount { get; set; }
    public int ManagedIndexerCount { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Skipped { get; set; }
    public List<string> SkippedDetails { get; } = [];
    public bool ProfilesChanged { get; set; }
    public bool IndexersChanged => Added > 0 || Updated > 0 || Removed > 0;
}
