namespace NzbWebDAV.Clients.RadarrSonarr.BaseModels;

public enum ArrRepairOutcome
{
    RemoveAndBlocklistSucceeded = 0,
    RemoveAndBlocklistSucceededSearchWithheld = 1,
    MediaItemNotFound = 2,
    DownloadHistoryNotFound = 3,
    MediaRemovedBlocklistUnconfirmed = 4,
    MediaRemovedBlocklistConfirmedSearchUnconfirmed = 5,
}
