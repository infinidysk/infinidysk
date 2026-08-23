namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public enum ArrRepairOutcome
{
    RemoveAndBlocklistSucceeded,
    RemoveAndBlocklistSucceededSearchWithheld,
    MediaItemNotFound,
    DownloadHistoryNotFound,
}
