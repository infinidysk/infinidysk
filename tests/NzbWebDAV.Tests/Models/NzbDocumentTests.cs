using System.Globalization;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Tests.TestUtils;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Models;

public class NzbDocumentTests
{
    [Fact]
    public async Task LoadAsync_SparseLabels_AreNotPadded()
    {
        var file = await ParseSparseFileAsync("1,3");
        Assert.Equal(["part-1@example", "part-3@example"], file.GetSegmentIds());
        Assert.Equal(["alternate@example"], file.Segments[0].FallbackMessageIds);
    }

    [Theory]
    [InlineData("1,2,4", 4, true, 12, 4, 9, 3, true)]
    [InlineData("1,3", 2, true, 6, 2, 3, 3, false)]
    [InlineData("1,3", 3, true, 9, 3, 6, 3, true)]
    [InlineData("1,3", 0, false, 9, 3, 6, 3, true)]
    [InlineData("1,3", 0, false, 9, 2, 3, 6, false)]
    [InlineData("1,3", 3, true, 6, 3, 3, 3, false)]
    [InlineData("2,3,4", 4, true, 12, 4, 9, 3, false)]
    [InlineData("0,1,3", 3, true, 9, 3, 6, 3, false)]
    [InlineData("1,?,3", 3, true, 9, 3, 6, 3, false)]
    [InlineData("1,1000000", 1000000, true, 3000000, 1000000, 2999997, 3, false)]
    [InlineData("1,3", 0, true, 9, 3, 6, 3, false)]
    [InlineData("1,3", 0, null, 9, 3, 6, 3, false)]
    [InlineData("1,3", 4, true, 9, 3, 6, 3, false)]
    [InlineData("1,2,3", 3, true, 9, 3, 6, 3, false)]
    [InlineData("1,3", -1, true, 9, 3, 6, 3, false)]
    public async Task TryFillOmittedSegmentsAsync_RequiresCorroboratingHeaders(
        string numbers, int total, bool? presence, int size,
        int lastPart, int lastOffset, int lastSize, bool expected)
    {
        var file = await ParseSparseFileAsync(numbers);
        var originals = file.Segments.ToArray();
        var ids = file.GetSegmentIds();
        var fallbacks = file.GetSegmentFallbackIds();
        file.Segments[0].ByteRange = new LongRange(0, 3);
        var ranges = file.Segments.Select(segment => segment.ByteRange).ToArray();
        using var client = new OmittedHeaderClient(OmittedHeader(total, presence, size) with
        {
            PartNumber = lastPart, PartOffset = lastOffset, PartSize = lastSize,
        });

        Assert.Equal(expected, await file.TryFillOmittedSegmentsAsync(
            OmittedHeader(total, presence, size), client, CancellationToken.None));

        Assert.Equal(presence == false && total == 0 ? [ids[^1]] : Array.Empty<string>(), client.Probes);
        if (!expected)
        {
            Assert.Equal(originals, file.Segments);
            Assert.Equal(ids, file.GetSegmentIds());
            Assert.Equal(fallbacks, file.GetSegmentFallbackIds());
            Assert.Equal(ranges, file.Segments.Select(segment => segment.ByteRange));
        }
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("offset")]
    [InlineData("empty")]
    [InlineData("fileSize")]
    public async Task TryFillOmittedSegmentsAsync_RejectsInvalidFirstGeometry(string invalid)
    {
        var file = await ParseSparseFileAsync("1,3");
        var header = OmittedHeader(3, true, 9);
        header = invalid switch
        {
            "ordinal" => header with { PartNumber = 2 },
            "offset" => header with { PartOffset = 3 },
            "empty" => header with { PartSize = 0 },
            _ => header with { FileSize = 2 },
        };
        using var client = new OmittedHeaderClient(header);
        Assert.False(await file.TryFillOmittedSegmentsAsync(header, client, CancellationToken.None));
        Assert.Equal(2, file.Segments.Count);
        Assert.Empty(client.Probes);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("total")]
    [InlineData("explicitZero")]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData("cancelled")]
    public async Task TryFillOmittedSegmentsAsync_FailedConfirmationPreservesLayout(string failure)
    {
        var file = await ParseSparseFileAsync("1,3");
        var originals = file.Segments.ToArray();
        var lastHeader = OmittedHeader(0, false, 9) with { PartNumber = 3, PartOffset = 6 };
        lastHeader = failure switch
        {
            "size" => lastHeader with { FileSize = 10 },
            "offset" => lastHeader with { PartOffset = 5 },
            "length" => lastHeader with { PartSize = 2 },
            "total" => lastHeader with { TotalParts = 2, HasTotalParts = true },
            "explicitZero" => lastHeader with { HasTotalParts = true },
            "unknown" => lastHeader with { HasTotalParts = null },
            _ => lastHeader,
        };
        using var client = new OmittedHeaderClient(lastHeader)
        {
            Failure = failure switch
            {
                "unavailable" => new IOException("confirmation unavailable"),
                "cancelled" => new OperationCanceledException(),
                _ => null,
            },
        };
        if (failure == "cancelled")
            await Assert.ThrowsAsync<OperationCanceledException>(() => file.TryFillOmittedSegmentsAsync(
                OmittedHeader(0, false, 9), client, CancellationToken.None));
        else
            Assert.False(await file.TryFillOmittedSegmentsAsync(
                OmittedHeader(0, false, 9), client, CancellationToken.None));
        Assert.Equal(originals, file.Segments);
        Assert.Equal(["part-3@example"], client.Probes);
    }

    [Fact]
    public async Task TryFillOmittedSegmentsAsync_IsDeterministicAndIdempotent()
    {
        var first = await ParseSparseFileAsync("1,2,4");
        var second = await ParseSparseFileAsync("1,2,4");
        var originals = first.Segments.ToArray();
        first.Segments[0].ByteRange = new LongRange(0, 3);
        using var client = new OmittedHeaderClient(OmittedHeader(4, true, 12));
        Assert.True(await first.TryFillOmittedSegmentsAsync(OmittedHeader(4, true, 12), client, CancellationToken.None));
        Assert.True(await second.TryFillOmittedSegmentsAsync(OmittedHeader(4, true, 12), client, CancellationToken.None));
        Assert.Equal(first.GetSegmentIds(), second.GetSegmentIds());
        Assert.Same(originals[0], first.Segments[0]);
        Assert.Same(originals[1], first.Segments[1]);
        Assert.Same(originals[2], first.Segments[3]);
        Assert.Equal(["alternate@example"], first.Segments[0].FallbackMessageIds);
        Assert.Equal(new LongRange(0, 3), first.Segments[0].ByteRange);
        var marker = first.Segments[2];
        Assert.Equal(3, marker.Number);
        Assert.Equal(originals[1].Bytes, marker.Bytes);
        Assert.Null(marker.ByteRange);
        Assert.Empty(marker.FallbackMessageIds);
        Assert.False(await first.TryFillOmittedSegmentsAsync(OmittedHeader(4, true, 12), client, CancellationToken.None));
        Assert.Empty(client.Probes);
    }

    [Fact]
    public async Task ProbeSecondSegmentRangeAsync_AdjacentOmissionsDoNotTrustInferredRanges()
    {
        var file = await ParseSparseFileAsync("1,4,5");
        using var client = new OmittedHeaderClient(OmittedHeader(5, true, 15) with
        {
            PartNumber = 4, PartOffset = 9,
        });
        Assert.True(await file.TryFillOmittedSegmentsAsync(OmittedHeader(5, true, 15), client, CancellationToken.None));
        file.Segments[0].ByteRange = new LongRange(0, 3);
        file.Segments[^1].ByteRange = new LongRange(12, 15);

        await file.ProbeSecondSegmentRangeAsync(client, 15, CancellationToken.None);

        Assert.Null(file.GetSegmentByteRangeIndex().Ranges);
        Assert.Equal(["part-4@example"], client.Probes);
        Assert.Equal(new LongRange(9, 12), file.Segments[3].ByteRange);
    }

    [Fact]
    public void OmittedSegmentId_HasExactRecognizableShape()
    {
        var marker = NzbFile.CreateOmittedSegmentId("part-1@example", 3);
        Assert.Equal(marker, NzbFile.CreateOmittedSegmentId("<part-1@example>", 3));
        Assert.True(NntpClient.IsValidSegmentId(marker));
        Assert.True(NzbFile.IsOmittedSegmentId(marker));
        Assert.True(NzbFile.IsOmittedSegmentId($"<{marker}>"));
        Assert.False(NzbFile.IsOmittedSegmentId("ordinary@" + NzbFile.OmittedSegmentDomain));
        Assert.False(NzbFile.IsOmittedSegmentId(marker.Replace("omitted-3-", "omitted-0-", StringComparison.Ordinal)));
        Assert.False(NzbFile.IsOmittedSegmentId(marker.Remove(12, 1)));
        Assert.Matches(@"^omitted-3-[0-9A-F]{64}@omitted\.nzbdav\.invalid$", marker);
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("legacy", false)]
    [InlineData("unrelated", false)]
    [InlineData("missingReal", false)]
    [InlineData("missingMarker", false)]
    public async Task RestoreStoredOmittedSegments_RequiresTheWholeExpandedLayout(string layout, bool expected)
    {
        var file = await ParseSparseFileAsync("1,2,5");
        var originals = file.Segments.ToArray();
        var stored = file.GetSegmentIds().ToHashSet(StringComparer.Ordinal);
        if (layout != "legacy")
        {
            stored.Add(NzbFile.CreateOmittedSegmentId(layout == "unrelated" ? "other@example" : "part-1@example", 3));
            stored.Add(NzbFile.CreateOmittedSegmentId("part-1@example", 4));
        }
        if (layout == "missingReal") stored.Remove("part-2@example");
        if (layout == "missingMarker") stored.Remove(NzbFile.CreateOmittedSegmentId("part-1@example", 4));
        long charged = 0;
        Assert.Equal(expected, file.RestoreStoredOmittedSegments(stored, bytes =>
        {
            Assert.Equal(originals, file.Segments);
            charged += bytes;
        }));
        Assert.Equal(expected ? 256L + 5 * 512L : 0, charged);
        if (expected)
        {
            Assert.Equal(5, file.Segments.Count);
            Assert.Equal(stored, file.GetSegmentIds().ToHashSet(StringComparer.Ordinal));
            Assert.Same(originals[0], file.Segments[0]);
            Assert.Same(originals[1], file.Segments[1]);
            Assert.Same(originals[2], file.Segments[4]);
            Assert.False(file.RestoreStoredOmittedSegments(stored, _ => Assert.Fail("Idempotent replay must not charge")));
        }
        else Assert.Equal(originals, file.Segments);
    }

    [Fact]
    public async Task RestoreStoredOmittedSegments_BudgetFailurePrecedesMutation()
    {
        var file = await ParseSparseFileAsync("1,3");
        var originals = file.Segments.ToArray();
        var stored = file.GetSegmentIds().Append(NzbFile.CreateOmittedSegmentId("part-1@example", 2)).ToHashSet();
        Assert.Throws<InvalidOperationException>(() => file.RestoreStoredOmittedSegments(stored,
            _ => throw new InvalidOperationException("budget exhausted")));
        Assert.Equal(originals, file.Segments);
    }

    private static async Task<NzbFile> ParseSparseFileAsync(string numbers)
    {
        var entries = numbers.Split(',').Select(number => number == "?"
            ? "<segment bytes='7'>unnumbered@example</segment>"
            : $"<segment bytes='{int.Parse(number, CultureInfo.InvariantCulture) + 10}' number='{number}'>part-{number}@example</segment>");
        var duplicate = numbers.StartsWith("1,", StringComparison.Ordinal)
            ? "<segment bytes='11' number='1'>alternate@example</segment>" : "";
        var xml = "<nzb><file subject='file'><segments>" + string.Concat(entries) + duplicate + "</segments></file></nzb>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return Assert.Single((await NzbDocument.LoadAsync(stream)).Files);
    }

    private static UsenetYencHeader OmittedHeader(int total, bool? presence, long size) => new()
    {
        FileName = "file", FileSize = size, LineLength = 128, PartNumber = 1,
        TotalParts = total, HasTotalParts = presence, PartSize = 3, PartOffset = 0,
    };

    private sealed class OmittedHeaderClient(UsenetYencHeader header)
        : WrappingNntpClient(new FakeNntpClient(new Dictionary<string, byte[]>()))
    {
        public List<string> Probes { get; } = [];
        public Exception? Failure { get; init; }

        public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken cancellationToken)
        {
            Probes.Add(segmentId);
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is { } failure ? Task.FromException<UsenetYencHeader>(failure) : Task.FromResult(header);
        }
    }

    [Theory]
    [InlineData(1, 8, "<file/><file/>")]
    [InlineData(8, 1, "<file><segments><segment>one</segment><segment>two</segment></segments></file>")]
    [InlineData(8, 8, "<file subject='toolong'/>")]
    [InlineData(8, 8, "<file><segments><segment>toolong</segment></segments></file>")]
    public async Task LoadAsync_OptInLimitsRejectBeforeRetainingMoreEntries(int files, int segments, string body)
    {
        var budget = new Par2MemoryBudget(1024 * 1024);
        var options = new NzbReadOptions(4096, budget.Charge, budget.Reserve)
        {
            MaxFiles = files, MaxSegments = segments, MaxSubjectLength = 4, MaxMessageIdLength = 4,
        };
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<nzb>" + body + "</nzb>"));

        await Assert.ThrowsAsync<InvalidDataException>(() => NzbDocument.LoadAsync(stream, options, CancellationToken.None));
        Assert.True(budget.PeakBytes <= budget.Limit);
    }

    [Fact]
    public async Task LoadAsync_OptInBudgetStopsSmallLegalDocument()
    {
        var budget = new Par2MemoryBudget(100_000);
        var options = new NzbReadOptions(4096, budget.Charge, budget.Reserve);
        var xml = "<nzb>" + string.Concat(Enumerable.Repeat("<file subject='file'/>", 20)) + "</nzb>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        await Assert.ThrowsAsync<Par2BudgetExceededException>(() => NzbDocument.LoadAsync(stream, options, CancellationToken.None));
        Assert.True(budget.ReservedBytes < 100_000);
    }

    [Theory]
    [InlineData(" date='1071674882'", 1071674882L)]
    [InlineData("", null)]
    [InlineData(" date='0'", null)]
    [InlineData(" date='-5'", null)]
    [InlineData(" date='soon'", null)]
    [InlineData(" date='99999999999999999'", null)]
    public async Task LoadAsync_FileDateAttribute_SetsPostedDate(string attribute, long? expectedSeconds)
    {
        var xml = $"<nzb><file subject='file'{attribute}/></nzb>";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var file = Assert.Single((await NzbDocument.LoadAsync(stream)).Files);

        Assert.Equal(
            expectedSeconds is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
            file.PostedDate);
    }

    [Fact]
    public async Task LoadAsync_OptInLimitsPreserveMetadataAndFallbacks()
    {
        const string xml = "<nzb><head><meta type='category'>movies</meta></head><file subject='file'><segments><segment number='1'>a</segment><segment number='1'>b</segment></segments></file></nzb>";
        var budget = new Par2MemoryBudget(1024 * 1024);
        var options = new NzbReadOptions(4096, budget.Charge, budget.Reserve);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream, options, CancellationToken.None);

        Assert.Equal("movies", document.Metadata["category"]);
        Assert.Equal(["b"], Assert.Single(Assert.Single(document.Files).Segments).FallbackMessageIds);
        Assert.InRange(budget.ReservedBytes, 1, 4096);
    }

    [Fact]
    public async Task LoadAsync_ParsesMetadataFilesAndSegments()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <head>
                <meta type="category">movies</meta>
                <meta type="password">secret</meta>
              </head>
              <file subject="example.mkv">
                <segments>
                  <segment bytes="123" number="1">segment-1@example</segment>
                  <segment bytes="456" number="2">segment-2@example</segment>
                </segments>
              </file>
            </nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream);

        Assert.Equal("movies", document.Metadata["category"]);
        Assert.Equal("secret", document.Metadata["password"]);
        var file = Assert.Single(document.Files);
        Assert.Equal("example.mkv", file.Subject);
        Assert.Collection(
            file.Segments,
            segment =>
            {
                Assert.Equal(123, segment.Bytes);
                Assert.Equal("segment-1@example", segment.MessageId);
            },
            segment =>
            {
                Assert.Equal(456, segment.Bytes);
                Assert.Equal("segment-2@example", segment.MessageId);
            });
    }

    [Fact]
    public async Task LoadAsync_TrimsWhitespaceAroundSegmentMessageIds()
    {
        const string xml = """
            <nzb><file subject="file"><segments>
              <segment bytes="10">
                padded@example.com
              </segment>
              <segment bytes="20">  spaced@example.com  </segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream);

        Assert.Collection(
            Assert.Single(document.Files).Segments,
            segment => Assert.Equal("padded@example.com", segment.MessageId),
            segment => Assert.Equal("spaced@example.com", segment.MessageId));
    }

    [Fact]
    public async Task LoadAsync_UsesZeroForInvalidSegmentSize()
    {
        const string xml = """
            <nzb><file subject="file"><segments>
              <segment bytes="invalid">segment</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream);

        Assert.Equal(0, Assert.Single(Assert.Single(document.Files).Segments).Bytes);
    }

    [Fact]
    public async Task LoadAsync_WrapsMalformedXml()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<nzb><file>"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => NzbDocument.LoadAsync(stream));

        Assert.Equal("Could not parse the nzb document (malformed nzb)", exception.Message);
        Assert.IsType<System.Xml.XmlException>(exception.InnerException);
    }

    [Fact]
    public async Task LoadAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        const string xml = """
            <nzb><file subject="file"><segments>
              <segment bytes="10" number="1">a@example</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NzbDocument.LoadAsync(stream, cts.Token));
    }

    [Fact]
    public async Task LoadAsync_CancelMidSegments_ThrowsOperationCanceledUnwrapped()
    {
        // ~3 MB single-file document; cancelling after 64 KiB of reads must
        // propagate as OCE, not as InvalidDataException("malformed nzb").
        var builder = new StringBuilder("<nzb><file subject=\"huge\"><segments>");
        for (var i = 1; i <= 50_000; i++)
            builder.Append($"<segment bytes=\"15\" number=\"{i}\">id-{i}@example</segment>");
        builder.Append("</segments></file></nzb>");
        using var cts = new CancellationTokenSource();
        await using var stream = TestStreams.CancelAfterBytes(
            new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString())),
            cancelAfterBytes: 64 * 1024,
            cts);

        // ThrowsAsync requires the exact type: an OCE wrapped as
        // InvalidDataException("malformed nzb") would fail this assertion.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NzbDocument.LoadAsync(stream, cts.Token));
    }

    [Fact]
    public async Task LoadAsync_DedupesDuplicateSegmentNumbersKeepingFirst()
    {
        const string xml = """
            <nzb><file subject="dup"><segments>
              <segment bytes="10" number="1">a@example</segment>
              <segment bytes="20" number="2">b-first@example</segment>
              <segment bytes="21" number="2">b-second@example</segment>
              <segment bytes="30" number="3">c@example</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream);
        var file = Assert.Single(document.Files);

        Assert.Equal(3, file.Segments.Count);
        Assert.Equal(["a@example", "b-first@example", "c@example"], file.GetSegmentIds());
        Assert.Equal([1, 2, 3], file.Segments.Select(s => s.Number!.Value).ToArray());
        Assert.Equal([[], ["b-second@example"], []], file.GetSegmentFallbackIds());
    }

    [Fact]
    public async Task LoadAsync_SortsSegmentsByNumber()
    {
        const string xml = """
            <nzb><file subject="shuffled"><segments>
              <segment bytes="30" number="3">c@example</segment>
              <segment bytes="10" number="1">a@example</segment>
              <segment bytes="20" number="2">b@example</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream);

        Assert.Equal(["a@example", "b@example", "c@example"],
            Assert.Single(document.Files).GetSegmentIds());
    }

    [Fact]
    public async Task LoadAsync_WithoutNumbers_DedupesDuplicateMessageIds()
    {
        const string xml = """
            <nzb><file subject="ids"><segments>
              <segment bytes="10">a@example</segment>
              <segment bytes="20">b@example</segment>
              <segment bytes="20">b@example</segment>
              <segment bytes="30">c@example</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var file = Assert.Single((await NzbDocument.LoadAsync(stream)).Files);

        Assert.Equal(["a@example", "b@example", "c@example"], file.GetSegmentIds());
        // Same MessageId has nothing to fall back to — drop only.
        Assert.All(file.GetSegmentFallbackIds(), fallbacks => Assert.Empty(fallbacks));
    }

    [Fact]
    public async Task LoadAsync_DuplicateNumbers_KeepOrderedFallbacksOnPrimary()
    {
        const string xml = """
            <nzb><file subject="fallbacks"><segments>
              <segment bytes="10" number="1">a@example</segment>
              <segment bytes="20" number="2">b-primary@example</segment>
              <segment bytes="21" number="2">b-alt1@example</segment>
              <segment bytes="22" number="2">b-alt2@example</segment>
              <segment bytes="30" number="3">c@example</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await NzbDocument.LoadAsync(stream);
        var file = Assert.Single(document.Files);

        Assert.Equal(["a@example", "b-primary@example", "c@example"], file.GetSegmentIds());
        Assert.Equal(
            [[], ["b-alt1@example", "b-alt2@example"], []],
            file.GetSegmentFallbackIds());
        Assert.Empty(file.Segments[0].FallbackMessageIds);
        Assert.Equal(["b-alt1@example", "b-alt2@example"], file.Segments[1].FallbackMessageIds);
    }

    [Fact]
    public async Task GetSegmentByteRanges_RemainsContiguousAfterDedup()
    {
        const string xml = """
            <nzb><file subject="ranges"><segments>
              <segment bytes="100" number="1">a@example</segment>
              <segment bytes="100" number="2">b-dup1@example</segment>
              <segment bytes="100" number="2">b-dup2@example</segment>
              <segment bytes="100" number="3">c@example</segment>
            </segments></file></nzb>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var file = Assert.Single((await NzbDocument.LoadAsync(stream)).Files);

        file.Segments[0].ByteRange = new LongRange(0, 100);
        file.Segments[^1].ByteRange = new LongRange(200, 300);

        var ranges = file.GetSegmentByteRanges();
        Assert.NotNull(ranges);
        Assert.Equal(3, ranges.Length);
        Assert.Equal(0, ranges[0].StartInclusive);
        Assert.Equal(100, ranges[0].EndExclusive);
        Assert.Equal(100, ranges[1].StartInclusive);
        Assert.Equal(200, ranges[1].EndExclusive);
        Assert.Equal(200, ranges[2].StartInclusive);
        Assert.Equal(300, ranges[2].EndExclusive);
    }
}
