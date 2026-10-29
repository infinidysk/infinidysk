using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;

namespace NzbWebDAV.Tests.Clients;

public class ArrUnlinkedDownloadClassifierTests
{
    private const string FileName = "episode-a.mkv";
    private const string ImportedPath = "/tv/Series/Season 01/Series - S01E01.mkv";
    private static readonly DateTimeOffset ImportedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = ImportedAt.AddDays(7);

    [Fact]
    public void ArrJson_DeserializesFieldsUsedAsProof()
    {
        var history = JsonSerializer.Deserialize<ArrHistory>(
            """
            {"totalRecords":2,"records":[
              {"eventType":"episodeFileDeleted","episodeId":7,"sourceTitle":"Season 01/Series - S01E01.mkv","data":{"reason":"Upgrade"}},
              {"eventType":"downloadFailed","movieId":9,"data":{"droppedPath":"/dl/job/episode-a.mkv"}}
            ]}
            """)!;

        Assert.Equal(7, history.Records[0].EpisodeId);
        Assert.Equal("Upgrade", history.Records[0].Data!.Reason);
        Assert.Equal(4, history.Records[1].EventType);
        Assert.Equal(9, history.Records[1].MovieId);
        Assert.Equal("/dl/job/episode-a.mkv", history.Records[1].Data!.DroppedPath);
    }

    [Theory]
    [InlineData("/dl/job/episode-a.mkv", true)]
    [InlineData("/dl/job/episode-a.mkv.strm", true)]
    [InlineData("C:\\dl\\job\\episode-a.mkv", true)]
    [InlineData("/dl/job/episode-a (2).mkv", false)]
    [InlineData("/dl/job/Episode-A.mkv", false)]
    public void FindFileImport_MatchesDroppedFileNameExactly(string droppedPath, bool matches)
    {
        var found = ArrUnlinkedDownloadClassifier.FindFileImport([Import(droppedPath)], FileName);

        Assert.Equal(matches, found is not null);
    }

    [Theory]
    [InlineData("Season 01/Series - S01E01.mkv", "Upgrade", 1, true)]
    [InlineData(ImportedPath, "Upgrade", 1, true)]
    [InlineData("Season 01/Series - S01E01.mkv", "upgrade", 1, true)]
    [InlineData("Season 01/Series - S01E01.mkv", "Manual", 1, false)]
    [InlineData("Season 01/Series - S01E01.mkv", "MissingFromDisk", 1, false)]
    [InlineData("Season 01/Series - S01E01.mkv", "Upgrade", -1, false)]
    [InlineData("Season 01/Series - S01E02.mkv", "Upgrade", 1, false)]
    [InlineData("01/Series - S01E01.mkv", "Upgrade", 1, false)]
    [InlineData("/other/Season 01/Series - S01E01.mkv", "Upgrade", 1, false)]
    public void IsSupersededByUpgrade_RequiresLaterUpgradeDeletionOfTheImportedPath(
        string deletedPath, string reason, int hoursAfterImport, bool superseded)
    {
        var deletion = new ArrHistoryRecord
        {
            Date = ImportedAt.AddHours(hoursAfterImport),
            SourceTitle = deletedPath,
            Data = new ArrHistoryData { Reason = reason },
        };

        Assert.Equal(
            superseded,
            ArrUnlinkedDownloadClassifier.IsSupersededByUpgrade(Import("/dl/job/episode-a.mkv"), [deletion]));
    }

    [Fact]
    public void FailedDownloadWithoutImports_IsDownloadFailed()
    {
        var verdict = ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(
            History(Grab(), new ArrHistoryRecord { EventType = 4, Date = ImportedAt }),
            Now);

        Assert.Equal(ArrUnlinkedFileVerdict.DownloadFailed, verdict);
    }

    [Fact]
    public void SettledImportOfASiblingFile_IsNotImported()
    {
        var verdict = ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(
            History(Grab(), Import("/dl/job/episode-a (2).mkv")),
            Now);

        Assert.Equal(ArrUnlinkedFileVerdict.NotImported, verdict);
    }

    [Fact]
    public void UnsettledImportOfASiblingFile_IsUnknown()
    {
        var verdict = ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(
            History(Grab(), Import("/dl/job/episode-a (2).mkv")),
            ImportedAt.AddHours(1));

        Assert.Equal(ArrUnlinkedFileVerdict.Unknown, verdict);
    }

    [Fact]
    public void ImportWithoutDroppedPath_IsUnknown()
    {
        var verdict = ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(
            History(Grab(), Import(droppedPath: null)),
            Now);

        Assert.Equal(ArrUnlinkedFileVerdict.Unknown, verdict);
    }

    [Fact]
    public void GrabOnly_IsUnknown()
    {
        Assert.Equal(
            ArrUnlinkedFileVerdict.Unknown,
            ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(History(Grab()), Now));
    }

    [Fact]
    public void EmptyHistory_IsUnknown()
    {
        Assert.Equal(
            ArrUnlinkedFileVerdict.Unknown,
            ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(History(), Now));
    }

    [Fact]
    public void TruncatedHistory_IsUnknown()
    {
        var history = History(new ArrHistoryRecord { EventType = 4, Date = ImportedAt });
        history.TotalRecords = 2;

        Assert.Equal(
            ArrUnlinkedFileVerdict.Unknown,
            ArrUnlinkedDownloadClassifier.ClassifyWithoutFileImport(history, Now));
    }

    private static ArrHistory History(params ArrHistoryRecord[] records) =>
        new() { Records = [.. records], TotalRecords = records.Length };

    private static ArrHistoryRecord Grab() => new() { EventType = 1, Date = ImportedAt.AddHours(-1) };

    private static ArrHistoryRecord Import(string? droppedPath) =>
        new()
        {
            EventType = 3,
            Date = ImportedAt,
            EpisodeId = 7,
            Data = new ArrHistoryData { DroppedPath = droppedPath, ImportedPath = ImportedPath },
        };
}
