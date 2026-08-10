using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ProwlarrSync;

public sealed class ProwlarrSyncResponse : BaseApiResponse
{
    public bool Configured { get; set; }
    public bool SyncEnabled { get; set; }
    public bool IndexersEnvironmentManaged { get; set; }
    public bool ProfilesEnvironmentManaged { get; set; }
    public long? LastAttemptAt { get; set; }
    public long? LastSuccessAt { get; set; }
    public int RemoteIndexerCount { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int Skipped { get; set; }

    public static ProwlarrSyncResponse FromSnapshot(ProwlarrSyncSnapshot snapshot) => new()
    {
        Status = true,
        Error = snapshot.LastError,
        Configured = snapshot.Configured,
        SyncEnabled = snapshot.SyncEnabled,
        IndexersEnvironmentManaged = snapshot.IndexersEnvironmentManaged,
        ProfilesEnvironmentManaged = snapshot.ProfilesEnvironmentManaged,
        LastAttemptAt = snapshot.LastAttemptAt,
        LastSuccessAt = snapshot.LastSuccessAt,
        RemoteIndexerCount = snapshot.RemoteIndexerCount,
        Added = snapshot.Added,
        Updated = snapshot.Updated,
        Removed = snapshot.Removed,
        Skipped = snapshot.Skipped,
    };
}
