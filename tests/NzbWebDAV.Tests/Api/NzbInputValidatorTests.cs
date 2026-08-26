using System.Text;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

public class NzbInputValidatorTests
{
    [Fact]
    public void AcceptsDocumentAtSegmentLimit()
    {
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 2);
        using var stream = Bytes(xml);
        var limits = new NzbInputLimits { MaxFiles = 1, MaxTotalSegments = 2 };

        var bytes = NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits);

        Assert.Equal(30, bytes);
    }

    [Fact]
    public void RejectsOneFilePastLimit()
    {
        var xml = BuildNzb(fileCount: 2, segmentsPerFile: 1);
        using var stream = Bytes(xml);
        var limits = new NzbInputLimits { MaxFiles = 1, MaxTotalSegments = 10 };

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits));
        Assert.Contains("too many files", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2; limit 1", ex.Errors["nzb"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsLargeSingleFileRemux()
    {
        const int segmentCount = 100_001;
        const long segmentBytes = 700_000;
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: segmentCount, segmentBytes);
        using var stream = Bytes(xml);

        var bytes = NzbInputValidator.ValidateAndSumSegmentBytes(stream, NzbInputLimits.Default);

        Assert.Equal(segmentCount * segmentBytes, bytes);
        Assert.True(bytes > 70_000_000_000);
    }

    [Fact]
    public void RejectsOneSegmentPastTotalLimitWithCountAndLimit()
    {
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 2);
        using var stream = Bytes(xml);
        var limits = new NzbInputLimits { MaxTotalSegments = 1 };

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits));

        Assert.Contains("too many segments", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2; limit 1", ex.Errors["nzb"][0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void RejectsSegmentByteTotalOverflow(int fileCount, int segmentsPerFile)
    {
        var xml = BuildNzb(fileCount, segmentsPerFile, long.MaxValue);
        using var stream = Bytes(xml);

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, NzbInputLimits.Default));

        Assert.Contains("total segment byte count", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsInvalidByteCountAndDuplicateNumbers()
    {
        const string xml = """
            <nzb><file subject="file"><segments>
              <segment bytes="nope" number="1">id-one@example</segment>
              <segment bytes="10" number="1">id-two@example</segment>
            </segments></file></nzb>
            """;
        using var stream = Bytes(xml);

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, NzbInputLimits.Default));
        Assert.Contains(ex.Errors["nzb"], message => message.Contains("byte count", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ex.Errors["nzb"], message => message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsOversizedXmlWithoutEchoingContents()
    {
        var payload = "<nzb>" + new string('a', 64) + "</nzb>";
        using var stream = Bytes(payload);
        var limits = new NzbInputLimits { MaxXmlBytes = 16 };

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits));
        Assert.Contains("size", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("aaaa", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsDocumentAbove64MiB()
    {
        // Regression for #1176: padding via an XML comment keeps the document
        // valid without tripping the subject or segment limits.
        var padding = new string('a', 65 * 1024 * 1024);
        var xml = $"<nzb><!-- {padding} --><file subject=\"file-1\"><segments>" +
                  "<segment bytes=\"15\" number=\"1\">id@example</segment>" +
                  "</segments></file></nzb>";
        using var stream = Bytes(xml);

        var bytes = NzbInputValidator.ValidateAndSumSegmentBytes(stream, NzbInputLimits.Default);

        Assert.True(stream.Length > 64 * 1024 * 1024);
        Assert.Equal(15, bytes);
    }

    [Fact]
    public void AcceptsSubjectBeyondFilenameLimit()
    {
        var subject = new string('a', 600);
        var xml = $"<nzb><file subject=\"{subject}\"><segments>" +
                  "<segment bytes=\"15\" number=\"1\">id@example</segment>" +
                  "</segments></file></nzb>";
        using var stream = Bytes(xml);

        var bytes = NzbInputValidator.ValidateAndSumSegmentBytes(stream, NzbInputLimits.Default);

        Assert.Equal(15, bytes);
    }

    [Fact]
    public void RejectsSubjectPastLimitWithLimit()
    {
        var subject = new string('a', 1025);
        var xml = $"<nzb><file subject=\"{subject}\"><segments>" +
                  "<segment bytes=\"15\" number=\"1\">id@example</segment>" +
                  "</segments></file></nzb>";
        using var stream = Bytes(xml);

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, NzbInputLimits.Default));

        Assert.Contains("subject", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1024", ex.Errors["nzb"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void PreCancelledToken_ThrowsOperationCanceled()
    {
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 2);
        using var stream = Bytes(xml);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(
                stream, NzbInputLimits.Default, cts.Token));
    }

    [Fact]
    public void CancelMidDocument_ThrowsOperationCanceledBeforeLastSegment()
    {
        // ~3 MB single-file document; cancelling after 64 KiB of reads must
        // surface as OCE (not ApiValidationException) while segments remain.
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 50_000);
        using var cts = new CancellationTokenSource();
        using var stream = TestStreams.CancelAfterBytes(Bytes(xml), cancelAfterBytes: 64 * 1024, cts);

        Assert.Throws<OperationCanceledException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(
                stream, NzbInputLimits.Default, cts.Token));
    }

    [Fact]
    public void CancelWhileReadingFinalSegment_ThrowsOperationCanceled()
    {
        // Delivery stops one byte into the final segment's content and the token
        // is cancelled when the reader pulls the remainder, so cancellation lands
        // after the per-segment check but before the closing </file> is seen.
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 1);
        var segmentContentStart = xml.IndexOf('>', xml.IndexOf("<segment ", StringComparison.Ordinal)) + 1;
        using var cts = new CancellationTokenSource();
        using var stream = TestStreams.CancelOnReadBeyond(Bytes(xml), byteLimit: segmentContentStart + 1, cts);

        Assert.Throws<OperationCanceledException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(
                stream, NzbInputLimits.Default, cts.Token));
    }

    private static string BuildNzb(int fileCount, int segmentsPerFile, long segmentBytes = 15)
    {
        var builder = new StringBuilder("<nzb>");
        for (var file = 1; file <= fileCount; file++)
        {
            builder.Append($"<file subject=\"file-{file}\"><segments>");
            for (var segment = 1; segment <= segmentsPerFile; segment++)
            {
                builder.Append(
                    $"<segment bytes=\"{segmentBytes}\" number=\"{segment}\">id-{file}-{segment}@example</segment>");
            }

            builder.Append("</segments></file>");
        }

        builder.Append("</nzb>");
        return builder.ToString();
    }

    private static MemoryStream Bytes(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
