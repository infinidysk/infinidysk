using System.Text;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Models;

public class NzbDocumentTests
{
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
