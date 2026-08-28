namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public sealed record ArrMediaFileMatch(
    ArrMediaKind Kind,
    int FileId,
    IReadOnlyList<int> MediaIds)
{
    public IReadOnlyList<string> MediaKeys =>
        MediaIds.Select(id => $"{Kind.ToString().ToLowerInvariant()}:{id}").ToArray();
}

public enum ArrMediaKind
{
    Movie,
    Episode,
}

public enum ArrMissingPayloadCleanupOutcome
{
    RemovedSearchRequested,
    RemovedSearchWithheld,
    RemovedSearchFailed,
    RemovedNoSearchTargets,
    MediaItemNotFound,
}
