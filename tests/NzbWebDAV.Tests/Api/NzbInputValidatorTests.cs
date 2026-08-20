using System.Text;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Models.Nzb;

namespace NzbWebDAV.Tests.Api;

public class NzbInputValidatorTests
{
    [Fact]
    public void AcceptsDocumentAtSegmentLimit()
    {
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 2);
        using var stream = Bytes(xml);
        var limits = new NzbInputLimits { MaxFiles = 1, MaxSegmentsPerFile = 2, MaxTotalSegments = 2 };

        var bytes = NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits);

        Assert.Equal(30, bytes);
    }

    [Fact]
    public void RejectsOneFilePastLimit()
    {
        var xml = BuildNzb(fileCount: 2, segmentsPerFile: 1);
        using var stream = Bytes(xml);
        var limits = new NzbInputLimits { MaxFiles = 1, MaxSegmentsPerFile = 10, MaxTotalSegments = 10 };

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits));
        Assert.Contains("too many files", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsOneSegmentPastPerFileLimit()
    {
        var xml = BuildNzb(fileCount: 1, segmentsPerFile: 2);
        using var stream = Bytes(xml);
        var limits = new NzbInputLimits { MaxFiles = 5, MaxSegmentsPerFile = 1, MaxTotalSegments = 10 };

        var ex = Assert.Throws<ApiValidationException>(
            () => NzbInputValidator.ValidateAndSumSegmentBytes(stream, limits));
        Assert.Contains("too many segments", ex.Errors["nzb"][0], StringComparison.OrdinalIgnoreCase);
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

    private static string BuildNzb(int fileCount, int segmentsPerFile)
    {
        var builder = new StringBuilder("<nzb>");
        for (var file = 1; file <= fileCount; file++)
        {
            builder.Append($"<file subject=\"file-{file}\"><segments>");
            for (var segment = 1; segment <= segmentsPerFile; segment++)
            {
                builder.Append(
                    $"<segment bytes=\"15\" number=\"{segment}\">id-{file}-{segment}@example</segment>");
            }

            builder.Append("</segments></file>");
        }

        builder.Append("</nzb>");
        return builder.ToString();
    }

    private static MemoryStream Bytes(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
