using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue;
using NzbWebDAV.Queue.Inspection;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Clients.Usenet;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

/// <summary>
/// Inspection runs the queue import planner without importing. These tests pin that the
/// report agrees with what a real import of the same NZB does, that inspection has no
/// import side effects, and that completeness, encryption and serviceability are
/// reported as separate facts.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class NzbInspectorTests : IAsyncLifetime
{
    private const string Category = "other";
    private const int VolumeSize = 4096;
    private const string Rar5Password = "test";
    private const string MissingPrimaryHost = "missing.example";
    private static readonly byte[] MatroskaMagic = [0x1A, 0x45, 0xDF, 0xA3];

    private readonly string _root = Path.Join(Path.GetTempPath(), "nzb-inspect-" + Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];
    private string? _previous;

    public async Task InitializeAsync()
    {
        _previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        DavDatabaseContext.ResetOptionsForTests();
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previous);
        DavDatabaseContext.ResetOptionsForTests();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    // ---- agreement with a real import of the same NZB ----

    public static TheoryData<string> CompletingCases =>
        ["direct-media", "split-video", "single-rar-lazy", "single-rar-eager", "multi-set", "nested", "sevenzip-stored"];

    [Theory]
    [MemberData(nameof(CompletingCases))]
    public async Task Inspect_AgreesWithImport_WhenImportCompletes(string fixture)
    {
        var (files, lazyRar) = Fixture(fixture);
        var report = await InspectAsync(fixture, files, lazyRar);
        var import = await ImportAsync(fixture, files, lazyRar);

        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, import.Status);
        Assert.True(report.ManifestComplete);
        Assert.Null(report.Diagnostics.Failure);

        // Every mounted file is a planned output with the same name and size...
        var planned = report.Files.Select(x => (x.PlannedName, x.Size)).ToHashSet();
        Assert.All(import.Mounted, mounted => Assert.Contains(mounted, planned));
        // ...and the planned media is exactly the mounted media (import also drops blocklisted extras such as .nfo).
        Assert.Equal(
            import.Mounted.Where(x => FilenameUtil.IsVideoFile(x.Name)).OrderBy(x => x.Name),
            report.Files.Where(x => FilenameUtil.IsVideoFile(x.PlannedName)).Select(x => (x.PlannedName, x.Size)).OrderBy(x => x.PlannedName));
    }

    [Fact]
    public async Task CompressedSevenZip_ReportsUnsupportedLikeImportFails()
    {
        var files = new List<PostedFile> { new("movie.7z", CompressedSevenZip()) };
        var report = await InspectAsync("sevenzip-compressed", files);
        var import = await ImportAsync("sevenzip-compressed", files);

        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, import.Status);
        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.Unsupported);
        Assert.Equal(import.FailMessage, report.Diagnostics.Failure!.Message);
        var archive = Assert.Single(report.Archives);
        Assert.Equal("7z", archive.ArchiveType);
        Assert.Equal(InspectionContentAccess.Unsupported, archive.ContentAccess);
        Assert.False(archive.StreamSupported);
    }

    [Fact]
    public async Task MalformedArchive_ReportsTheImportFailure()
    {
        var garbage = new byte[VolumeSize];
        Encoding.ASCII.GetBytes("this is not a rar archive").CopyTo(garbage, 0);
        var files = new List<PostedFile> { new("movie.rar", garbage) };
        var report = await InspectAsync("malformed", files);
        var import = await ImportAsync("malformed", files);

        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, import.Status);
        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.Failed);
        Assert.Equal(import.FailMessage, report.Diagnostics.Failure!.Message);
    }

    [Fact]
    public async Task MissingFirstSegment_IsObservedEvidence_AndNotRemembered()
    {
        var files = new List<PostedFile> { new("movie.mkv", Media(VolumeSize), Served: false) };
        var report = await InspectAsync("missing-first", files);

        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.MissingArticles);
        Assert.Equal(["movie.mkv"], report.Diagnostics.MissingFirstSegments);
        // Inspection must not write the process-wide missing-article cache an import consults.
        HealthCheckService.CheckCachedMissingSegmentIds([SegmentId("missing-first", "movie.mkv")]);

        var import = await ImportAsync("missing-first", files);
        Assert.Equal(HistoryItem.DownloadStatusOption.Failed, import.Status);
        Assert.Equal(import.FailMessage, report.Diagnostics.Failure!.Message);
        // Positive control: an import does remember it, so the check above is meaningful.
        Assert.Throws<UsenetArticleNotFoundException>(() =>
            HealthCheckService.CheckCachedMissingSegmentIds([SegmentId("missing-first", "movie.mkv")]));
    }

    [Fact]
    public async Task TransientProviderFailure_IsUnknown_NeverANegativeManifest()
    {
        var files = RarSet("movie", "movie.mkv");
        // Every fetch of part2 times out (a per-inspection article cache could otherwise serve re-reads).
        static bool Fault(string segmentId, int fetch) => segmentId.Contains(".part2.rar", StringComparison.Ordinal);
        var report = await InspectAsync("transient", files, lazyRar: false, faultOnFetch: Fault);
        var import = await ImportAsync("transient", files, lazyRar: false, faultOnFetch: Fault);

        Assert.True(import.StillQueued);
        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.ProviderUnavailable);
        Assert.All(report.Archives, x => Assert.Equal(InspectionContentAccess.Unknown, x.ContentAccess));
    }

    [Fact]
    public async Task DefinitiveProviderMiss_IsNotRememberedByInspection_ButIsByImport()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var missDb = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using (var context = new DavDatabaseContext(missDb))
            await context.Database.EnsureCreatedAsync();
        using var missCache = new ArticleMissNegativeCache(new ConfigManager(), () => new DavDatabaseContext(missDb));
        await missCache.StartAsync(CancellationToken.None);
        var (files, lazyRar) = Fixture("multi-set");

        // A primary provider that definitively misses (430) every article, ahead of a backup that serves them.
        var inspectionPrimary = new FakeNntpClient(new Dictionary<string, byte[]>());
        var report = await InspectAsync("provider-miss", files, lazyRar,
            providerStack: served => MissingPrimaryThen(served, inspectionPrimary, missCache));

        Assert.True(report.ManifestComplete, report.Diagnostics.Failure?.Message);
        Assert.True(inspectionPrimary.BodyRequestCount > 0, "the primary provider was never asked, so no miss was observed");
        await missCache.FlushPersistenceForTestsAsync();
        Assert.Equal(0, missCache.Entries);
        Assert.Equal(0, await PersistedMissCountAsync(missDb));

        // Positive control: the same NZB imported through the same provider stack records the misses.
        var importPrimary = new FakeNntpClient(new Dictionary<string, byte[]>());
        var import = await ImportAsync("provider-miss", files, lazyRar,
            providerStack: served => MissingPrimaryThen(served, importPrimary, missCache));

        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, import.Status);
        await missCache.FlushPersistenceForTestsAsync();
        Assert.True(missCache.Entries > 0, "an import did not record the primary provider's misses");
        Assert.True(await PersistedMissCountAsync(missDb) > 0, "an import did not persist the primary provider's misses");
        await missCache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ArticleMissCacheReads_StayEnabledDuringInspection()
    {
        // Misses an earlier import or playback recorded for the primary provider.
        using var missCache = new ArticleMissNegativeCache(new ConfigManager());
        var (files, lazyRar) = Fixture("direct-media");
        foreach (var file in files)
        {
            foreach (var operation in Enum.GetValues<ArticleMissNegativeCache.ArticleMissOperation>())
            {
                missCache.MarkMissing(ArticleMissNegativeCache.BuildKey(
                    SegmentId("cached-miss", file.Name), MissingPrimaryHost, null, operation));
            }
        }
        var recorded = missCache.Entries;

        var primary = new FakeNntpClient(new Dictionary<string, byte[]>());
        var report = await InspectAsync("cached-miss", files, lazyRar,
            providerStack: served => MissingPrimaryThen(served, primary, missCache));

        Assert.True(report.ManifestComplete, report.Diagnostics.Failure?.Message);
        Assert.True(missCache.Hits > 0, "inspection did not consult the recorded misses");
        Assert.Equal(recorded, missCache.Entries);
    }

    // ---- no import side effects ----

    [Theory]
    [InlineData("multi-set")]
    [InlineData("missing-first")]
    public async Task Inspect_LeavesQueueHistoryAndMountStateUntouched(string fixture)
    {
        var files = fixture == "missing-first"
            ? [new PostedFile("movie.mkv", Media(VolumeSize), Served: false)]
            : Fixture(fixture).Files;
        var before = await StateAsync();
        await InspectAsync(fixture, files);
        Assert.Equal(before, await StateAsync());
    }

    // ---- structure ----

    [Fact]
    public async Task LazyAndEagerRar_ReportTheSameManifest()
    {
        var files = RarSet("movie", "movie.mkv");
        var lazy = await InspectAsync("rar", files, lazyRar: true);
        var eager = await InspectAsync("rar", files, lazyRar: false);

        Assert.True(lazy.ManifestComplete);
        Assert.True(eager.ManifestComplete);
        Assert.Equal(
            lazy.Files.Select(x => (x.RawPath, x.PlannedName, x.Size, x.ArchiveSetId, x.NameSource)),
            eager.Files.Select(x => (x.RawPath, x.PlannedName, x.Size, x.ArchiveSetId, x.NameSource)));
    }

    [Fact]
    public async Task MultipleArchiveSets_StayDistinct()
    {
        var report = await InspectAsync("multi-set", Fixture("multi-set").Files);

        Assert.Equal(2, report.Archives.Count);
        Assert.Equal(2, report.Archives.Select(x => x.ArchiveSetId).Distinct().Count());
        var episodeOne = Assert.Single(report.Files, x => x.RawPath == "Show.S01E01.mkv");
        var episodeTwo = Assert.Single(report.Files, x => x.RawPath == "Show.S01E02.mkv");
        Assert.NotEqual(episodeOne.ArchiveSetId, episodeTwo.ArchiveSetId);
        Assert.All(report.Files, x => Assert.Equal("archiveHeader", x.NameSource));
        Assert.All(report.Archives, x => Assert.Equal(InspectionContentAccess.Plain, x.ContentAccess));
    }

    [Fact]
    public async Task NestedRar_ReportsNestingDepth()
    {
        var report = await InspectAsync("nested", Fixture("nested").Files);

        Assert.True(report.ManifestComplete);
        var movie = Assert.Single(report.Files);
        Assert.Equal("movie.mkv", movie.RawPath);
        Assert.Equal(1, movie.NestingDepth);
        Assert.Contains(report.Archives, x => x.NestingDepth == 0 && x.ContentAccess == InspectionContentAccess.Plain);
        Assert.Contains(report.Archives, x => x.NestingDepth == 1 && x.ArchiveSetId == movie.ArchiveSetId);
    }

    [Fact]
    public async Task UnexpandedNestedArchive_LeavesTheManifestIncomplete()
    {
        // Inner archive without an end-of-archive block cannot be expanded over NNTP and stays opaque.
        var inner = Rar4TestArchiveBuilder.BuildRar4Volume(
            "movie.mkv", VolumeSize, payloadPrefix: MatroskaMagic, isVolume: false);
        var outer = Rar4TestArchiveBuilder.BuildRar4Volume(
            "inner.rar", inner.Length, payloadPrefix: inner, isVolume: false, endOfArchive: true);
        var report = await InspectAsync("opaque", [new PostedFile("outer.rar", outer)]);

        Assert.False(report.ManifestComplete);
        Assert.Null(report.Diagnostics.Failure);
        Assert.Contains(report.Files, x => x.RawPath == "inner.rar");
        Assert.Contains(report.Diagnostics.Warnings, x => x.Code == InspectionWarningCodes.NestedArchiveOpaque);
    }

    [Fact]
    public async Task DirectMedia_ReportsNameSource()
    {
        var report = await InspectAsync("direct-media", Fixture("direct-media").Files);

        Assert.True(report.ManifestComplete);
        Assert.Empty(report.Archives);
        var movie = Assert.Single(report.Files, x => x.RawPath == "movie.mkv");
        Assert.Equal("subject", movie.NameSource);
        Assert.Null(movie.ArchiveSetId);
        Assert.Equal(".mkv", movie.MediaExtension);
    }

    // ---- encryption is reported separately from completeness ----

    [Fact]
    public async Task EncryptedMember_WithoutPassword_IsCompleteButPasswordRequired()
    {
        var volume = Rar4TestArchiveBuilder.BuildRar4Volume(
            "movie.mkv", VolumeSize, firstVolume: true, payloadPrefix: MatroskaMagic, encrypted: true);
        var report = await InspectAsync("encrypted", [new PostedFile("movie.rar", volume)]);

        Assert.True(report.ManifestComplete);
        Assert.Contains(report.Files, x => x.RawPath == "movie.mkv");
        var archive = Assert.Single(report.Archives);
        Assert.Equal(InspectionEncryption.Data, archive.Encryption);
        Assert.Equal(InspectionContentAccess.PasswordRequired, archive.ContentAccess);
    }

    [Fact]
    public async Task Rar5DataEncryption_WithCorrectPassword_IsValidated()
    {
        var report = await InspectAsync("rar5-ok", Rar5EncryptedVolumes(), password: Rar5Password);

        Assert.True(report.ManifestComplete);
        Assert.NotEmpty(report.Files);
        var archive = Assert.Single(report.Archives);
        Assert.Equal(InspectionEncryption.Data, archive.Encryption);
        Assert.Equal(InspectionContentAccess.PasswordValidated, archive.ContentAccess);
    }

    [Fact]
    public async Task Rar5DataEncryption_WithoutPassword_IsCompleteButPasswordRequired()
    {
        var report = await InspectAsync("rar5-none", Rar5EncryptedVolumes());

        Assert.True(report.ManifestComplete);
        Assert.NotEmpty(report.Files);
        Assert.Equal(InspectionContentAccess.PasswordRequired, Assert.Single(report.Archives).ContentAccess);
    }

    [Fact]
    public async Task Rar5DataEncryption_WithWrongPassword_IsRejected()
    {
        var report = await InspectAsync("rar5-wrong", Rar5EncryptedVolumes(), password: "wrong");

        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.Password);
        Assert.Equal(InspectionContentAccess.PasswordRequired, Assert.Single(report.Archives).ContentAccess);
    }

    [Fact]
    public async Task Rar5HeaderEncryption_WithoutPassword_LeavesTheManifestIncomplete()
    {
        var report = await InspectAsync("rar5-headers",
            [new PostedFile("release.rar", TestArchive("Rar5.encrypted_filesAndHeader.rar"))]);

        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        Assert.NotNull(report.Diagnostics.Failure);
    }

    // ---- limits ----

    [Fact]
    public async Task TooManyNzbFiles_StopsBeforeAnyProviderTraffic()
    {
        var limits = new NzbInspectionLimits { MaxNzbFiles = 1 };
        var report = await InspectAsync("direct-media", Fixture("direct-media").Files, limits: limits);

        AssertLimitExceeded(report);
        Assert.Equal(0, report.Diagnostics.ArticleRequests);
    }

    [Fact]
    public async Task ArticleBudget_StopsInspection()
    {
        var limits = new NzbInspectionLimits { MaxArticleRequests = 1 };
        var report = await InspectAsync("multi-set", Fixture("multi-set").Files, limits: limits);

        AssertLimitExceeded(report);
        Assert.Contains("article_requests", report.Diagnostics.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveSetCap_StopsInspection()
    {
        var limits = new NzbInspectionLimits { MaxArchiveSets = 1 };
        var report = await InspectAsync("multi-set", Fixture("multi-set").Files, limits: limits);

        AssertLimitExceeded(report);
        Assert.Contains("archive_sets", report.Diagnostics.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestFileCap_StopsInspection()
    {
        var limits = new NzbInspectionLimits { MaxManifestFiles = 1 };
        var report = await InspectAsync("direct-media", Fixture("direct-media").Files, limits: limits);

        AssertLimitExceeded(report);
    }

    [Fact]
    public async Task Timeout_IsReportedAsIncomplete()
    {
        var limits = new NzbInspectionLimits { Timeout = TimeSpan.FromMilliseconds(250) };
        var report = await InspectAsync("hang", Fixture("direct-media").Files, limits: limits, hang: true);

        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.Timeout);
    }

    [Fact]
    public void Admission_AllowsOneInspectionAtATime()
    {
        using var inspector = new NzbInspector(new FakeNntpClient(new Dictionary<string, byte[]>()), new ConfigManager());
        var first = inspector.TryBeginInspection();
        Assert.NotNull(first);
        Assert.Null(inspector.TryBeginInspection());

        first.Dispose();
        first.Dispose();

        // Positive control: releasing the lease admits exactly one more inspection.
        using var next = inspector.TryBeginInspection();
        Assert.NotNull(next);
        Assert.Null(inspector.TryBeginInspection());
    }

    private static void AssertLimitExceeded(NzbInspectionReport report)
    {
        Assert.False(report.ManifestComplete);
        Assert.Empty(report.Files);
        AssertFailure(report, InspectionFailureKinds.LimitExceeded);
    }

    private static void AssertFailure(NzbInspectionReport report, string kind)
    {
        var failure = report.Diagnostics.Failure;
        Assert.True(failure?.Kind == kind,
            $"expected {kind}; got kind={failure?.Kind} stage={failure?.Stage} message={failure?.Message}");
    }

    // ---- fixtures ----

    private static (List<PostedFile> Files, bool LazyRar) Fixture(string name)
    {
        var media = Media(2 * VolumeSize);
        return name switch
        {
            "direct-media" => ([new("movie.mkv", media), new("movie.nfo", Encoding.ASCII.GetBytes("release notes"))], true),
            "split-video" => ([new("movie.mkv.001", media[..VolumeSize]), new("movie.mkv.002", media[VolumeSize..])], true),
            "single-rar-lazy" => (RarSet("movie", "movie.mkv"), true),
            "single-rar-eager" => (RarSet("movie", "movie.mkv"), false),
            "multi-set" => ([.. RarSet("Show.S01E01", "Show.S01E01.mkv"), .. RarSet("Show.S01E02", "Show.S01E02.mkv")], true),
            "nested" => ([new("outer.rar", NestedRar())], true),
            "sevenzip-stored" => ([new("movie.7z", Convert.FromBase64String(StoredSevenZipBase64))], true),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    // Stored (copy) 7z with dir/before.txt, dir/data.bin and dir/after.txt; same fixture as SevenZipProcessorTests.
    private const string StoredSevenZipBase64 =
        "N3q8ryccAARu4/hlFQAAAAAAAACEAAAAAAAAACp/hIpzdG9yZWQtc2V2ZW56aXAtZW50cnkBBAYAAQkVAAcLAQABAQAMFQAICgHuw9/ZAAAFBA4B0A8BYBFdAGQAaQByAAAAZABpAHIALwBiAGUAZgBvAHIAZQAuAHQAeAB0AAAAZABpAHIALwBkAGEAdABhAC4AYgBpAG4AAABkAGkAcgAvAGEAZgB0AGUAcgAuAHQAeAB0AAAAAAA=";

    private static byte[] Media(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        MatroskaMagic.CopyTo(bytes, 0);
        return bytes;
    }

    private static List<PostedFile> RarSet(string baseName, string memberName) =>
    [
        new($"{baseName}.part1.rar", Rar4TestArchiveBuilder.BuildRar4SplitFirstVolume(
            memberName, VolumeSize, 2 * VolumeSize, MatroskaMagic)),
        new($"{baseName}.part2.rar", Rar4TestArchiveBuilder.BuildRar4ContinuationVolume(
            memberName, VolumeSize, uncompressedSize: 2 * VolumeSize)),
    ];

    private static byte[] NestedRar()
    {
        var inner = Rar4TestArchiveBuilder.BuildRar4Volume(
            "movie.mkv", VolumeSize, payloadPrefix: MatroskaMagic, isVolume: false, endOfArchive: true);
        return Rar4TestArchiveBuilder.BuildRar4Volume(
            "inner.rar", inner.Length, payloadPrefix: inner, isVolume: false, endOfArchive: true);
    }

    private static byte[] CompressedSevenZip()
    {
        using var archive = new MemoryStream();
        using (var writer = new SevenZipWriter(archive, new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            LeaveStreamOpen = true,
            CompressHeader = false,
            Solid = false,
        }))
        {
            using var payload = new MemoryStream(Media(VolumeSize));
            writer.Write("movie.mkv", payload, DateTime.UnixEpoch);
        }

        return archive.ToArray();
    }

    private static List<PostedFile> Rar5EncryptedVolumes() =>
    [
        new("release.part01.rar", TestArchive("Rar5.multi.none.encrypted.part01.rar")),
        new("release.part02.rar", TestArchive("Rar5.multi.none.encrypted.part02.rar")),
        new("release.part03.rar", TestArchive("Rar5.multi.none.encrypted.part03.rar")),
    ];

    private static byte[] TestArchive(string name) =>
        File.ReadAllBytes(Path.Join(RepoPaths.FindRepoRoot(), "tests", "TestArchives", "Archives", name));

    private sealed record PostedFile(string Name, byte[] Bytes, bool Served = true);

    private sealed record ImportOutcome(
        HistoryItem.DownloadStatusOption? Status,
        string? FailMessage,
        bool StillQueued,
        List<(string Name, long Size)> Mounted);

    private string SegmentId(string jobName, string fileName) => $"{jobName}-{fileName}-{_run}@example.invalid";

    private (byte[] Nzb, FakeNntpClient Client) Post(
        string jobName,
        IReadOnlyList<PostedFile> files,
        Func<string, int, bool>? faultOnFetch)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var headers = new Dictionary<string, UsenetYencHeader>(StringComparer.Ordinal);
        var nzbFiles = new List<XElement>();
        foreach (var file in files)
        {
            var segmentId = SegmentId(jobName, file.Name);
            if (file.Served)
            {
                payloads[segmentId] = file.Bytes;
                headers[segmentId] = new UsenetYencHeader
                {
                    FileName = file.Name,
                    FileSize = file.Bytes.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    HasTotalParts = true,
                    PartOffset = 0,
                    PartSize = file.Bytes.Length,
                };
            }

            nzbFiles.Add(new XElement("file",
                new XAttribute("subject", $"\"{file.Name}\" yEnc (1/1)"),
                new XElement("segments",
                    new XElement("segment",
                        new XAttribute("bytes", file.Bytes.Length),
                        new XAttribute("number", 1),
                        segmentId))));
        }

        var fetches = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Func<string, byte[], Stream>? decodedStreamFactory = faultOnFetch is null
            ? null
            : (segmentId, bytes) =>
            {
                var fetch = fetches.AddOrUpdate(segmentId, 1, (_, previous) => previous + 1);
                if (faultOnFetch(segmentId, fetch))
                    throw new TimeoutException("simulated provider timeout");
                return new MemoryStream(bytes, writable: false);
            };
        var nzb = Encoding.UTF8.GetBytes(new XElement("nzb", nzbFiles).ToString(SaveOptions.DisableFormatting));
        return (nzb, new FakeNntpClient(
            payloads,
            useCachedYencStreams: true,
            decodedStreamFactory: decodedStreamFactory,
            yencHeaders: headers));
    }

    private static ConfigManager Config(bool lazyRar)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiRenameSingleVideoToRelease, ConfigValue = "false" },
            new ConfigItem { ConfigName = ConfigKeys.ApiLazyRarParsing, ConfigValue = lazyRar ? "true" : "false" },
        ]);
        return config;
    }

    private async Task<NzbInspectionReport> InspectAsync(
        string jobName,
        IReadOnlyList<PostedFile> files,
        bool lazyRar = true,
        string? password = null,
        Func<string, int, bool>? faultOnFetch = null,
        NzbInspectionLimits? limits = null,
        bool hang = false,
        Func<INntpClient, INntpClient>? providerStack = null)
    {
        var (nzb, fake) = Post(jobName, files, faultOnFetch);
        using var served = new ArticleFromBodyNntpClient(fake, hang);
        using var stacked = providerStack?.Invoke(served);
        using var inspector = new NzbInspector(stacked ?? served, Config(lazyRar), limits);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await inspector.InspectAsync(nzb, password, jobName, cancellation.Token);
    }

    /// <summary>
    /// The production provider stack: a <see cref="MultiProviderNntpClient"/> with a primary
    /// provider that answers every request with a definitive miss (430), then a backup provider
    /// serving <paramref name="served"/>, recording misses in <paramref name="missCache"/>.
    /// </summary>
    private static MultiProviderNntpClient MissingPrimaryThen(
        INntpClient served,
        FakeNntpClient missingPrimary,
        ArticleMissNegativeCache missCache) =>
        new(
        [
            MultiProviderNntpClientTests.CreateProvider(
                new ArticleFromBodyNntpClient(missingPrimary), host: MissingPrimaryHost),
            MultiProviderNntpClientTests.CreateProvider(served, host: "backup.example"),
        ], articleMissCache: missCache);

    private static async Task<int> PersistedMissCountAsync(DbContextOptions<DavDatabaseContext> options)
    {
        await using var context = new DavDatabaseContext(options);
        return await context.ArticleMissCacheEntries.CountAsync();
    }

    private async Task<ImportOutcome> ImportAsync(
        string jobName,
        IReadOnlyList<PostedFile> files,
        bool lazyRar = true,
        Func<string, int, bool>? faultOnFetch = null,
        Func<INntpClient, INntpClient>? providerStack = null)
    {
        var (nzb, fake) = Post(jobName, files, faultOnFetch);
        var config = Config(lazyRar);
        await using var context = new DavDatabaseContext();
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = $"{jobName}.nzb",
            JobName = jobName,
            NzbFileSize = nzb.Length,
            TotalSegmentBytes = files.Sum(file => (long)file.Bytes.Length),
            Category = Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();

        using var served = new ArticleFromBodyNntpClient(fake);
        using var stacked = providerStack?.Invoke(served);
        using var gate = new HealthCheckConnectionGate(config);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var nzbStream = new MemoryStream(nzb);
        await new QueueItemProcessor(queueItem, nzbStream, new DavDatabaseClient(context), stacked ?? served, config,
            new WebsocketManager(), new Progress<int>(), gate, cancellation.Token).ProcessAsync();

        context.ChangeTracker.Clear();
        var history = await context.HistoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == queueItem.Id);
        var queued = await context.QueueItems.AsNoTracking().AnyAsync(x => x.Id == queueItem.Id);
        var mountPath = $"{DavItem.ContentFolder.Path.TrimEnd('/')}/{Category}/{jobName}";
        var mounted = await context.Items.AsNoTracking()
            .Where(x => x.Path.StartsWith(mountPath + "/") && x.Type != DavItem.ItemType.Directory)
            .Select(x => new { x.Name, x.FileSize })
            .ToListAsync();
        return new ImportOutcome(
            history?.DownloadStatus,
            history?.FailMessage,
            queued,
            mounted.Select(x => (x.Name, x.FileSize ?? 0)).ToList());
    }

    private async Task<string> StateAsync()
    {
        await using var context = new DavDatabaseContext();
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(x => !x.Contains(".sqlite", StringComparison.Ordinal) && !x.EndsWith("-wal", StringComparison.Ordinal)
                        && !x.EndsWith("-shm", StringComparison.Ordinal))
            .Select(x => Path.GetRelativePath(_root, x))
            .Order(StringComparer.Ordinal);
        var queue = await context.QueueItems.CountAsync();
        var history = await context.HistoryItems.CountAsync();
        var items = await context.Items.CountAsync();
        return string.Create(CultureInfo.InvariantCulture,
            $"queue={queue} history={history} items={items} files=[{string.Join(",", files)}]");
    }
}
