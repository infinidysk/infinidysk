using System.Security.Cryptography;
using System.Text;
using RapidYencSharp;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Immutable deterministic yEnc corpus used by the loopback NNTP performance report.
/// The server only writes these precomputed bytes so encoding work is not charged to
/// the benchmark client process.
/// </summary>
internal sealed class NntpLoopbackCorpus
{
    private readonly Dictionary<string, NntpLoopbackArticle> _articles;

    private NntpLoopbackCorpus(
        IReadOnlyList<NntpLoopbackArticle> articles,
        byte[] decodedFile,
        string sha256)
    {
        Articles = articles;
        DecodedFile = decodedFile;
        ExpectedSha256 = sha256;
        _articles = articles.ToDictionary(article => article.SegmentId, StringComparer.Ordinal);
    }

    public IReadOnlyList<NntpLoopbackArticle> Articles { get; }
    public byte[] DecodedFile { get; }
    public string ExpectedSha256 { get; }
    public long ExpectedBytes => DecodedFile.LongLength;

    public bool TryGetArticle(string segmentId, out NntpLoopbackArticle article) =>
        _articles.TryGetValue(NormalizeSegmentId(segmentId), out article!);

    public static NntpLoopbackCorpus Create(int articleCount, int decodedArticleBytes, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(articleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodedArticleBytes);

        YencEncoder.EnsureInitialized();
        Crc32.EnsureInitialized();

        var articles = new NntpLoopbackArticle[articleCount];
        var decodedFile = GC.AllocateUninitializedArray<byte>(
            checked(articleCount * decodedArticleBytes));
        var random = new Random(seed);

        for (var index = 0; index < articleCount; index++)
        {
            var payload = decodedFile.AsSpan(index * decodedArticleBytes, decodedArticleBytes);
            random.NextBytes(payload);
            var segmentId = $"bench-{index:D4}@loopback";
            var crc32 = Crc32.Compute(payload);
            articles[index] = new NntpLoopbackArticle(
                segmentId,
                payload.ToArray(),
                EncodeWireBody(payload, index, articleCount, decodedFile.Length, crc32),
                crc32);
        }

        return new NntpLoopbackCorpus(
            articles,
            decodedFile,
            Convert.ToHexString(SHA256.HashData(decodedFile)).ToLowerInvariant());
    }

    private static byte[] EncodeWireBody(
        ReadOnlySpan<byte> payload,
        int index,
        int articleCount,
        int fileLength,
        uint crc32)
    {
        int? column = 0;
        var encoded = YencEncoder.EncodeEx(payload, ref column, 128, true);
        var stuffed = DotStuff(encoded);
        using var output = new MemoryStream(stuffed.Length + 256);
        WriteAscii(
            output,
            $"=ybegin part={index + 1} total={articleCount} line=128 size={fileLength} name=loopback.bin\r\n");
        WriteAscii(output, $"=ypart begin={index * payload.Length + 1} end={(index + 1) * payload.Length}\r\n");
        output.Write(stuffed);
        if (stuffed.Length > 0)
            WriteAscii(output, "\r\n");
        WriteAscii(output, $"=yend size={payload.Length} part={index + 1} pcrc32={crc32:x8}\r\n.\r\n");
        return output.ToArray();
    }

    private static byte[] DotStuff(ReadOnlySpan<byte> source)
    {
        using var output = new MemoryStream(source.Length);
        var atLineStart = true;
        foreach (var value in source)
        {
            if (atLineStart && value == (byte)'.')
                output.WriteByte(value);
            output.WriteByte(value);
            atLineStart = value == (byte)'\n';
        }
        return output.ToArray();
    }

    private static void WriteAscii(Stream stream, string value) =>
        stream.Write(Encoding.ASCII.GetBytes(value));

    public static string NormalizeSegmentId(string value) =>
        value.Trim().Trim('<', '>');
}

internal sealed record NntpLoopbackArticle(
    string SegmentId,
    byte[] Decoded,
    byte[] WireBody,
    uint Crc32);
