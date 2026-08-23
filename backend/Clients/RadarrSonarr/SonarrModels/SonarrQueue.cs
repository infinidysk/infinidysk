using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Clients.RadarrSonarr.SonarrModels;

public class SonarrQueue : ArrQueue<SonarrQueueRecord>
{
    public ArrQueue<ArrQueueRecord> ToGeneric()
    {
        return new ArrQueue<ArrQueueRecord>()
        {
            Page = Page,
            PageSize = PageSize,
            TotalRecords = TotalRecords,
            Records = Records.Select(x => (ArrQueueRecord)x).ToList()
        };
    }
}
