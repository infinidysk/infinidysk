using System.Text;
using RapidYencSharp;

namespace UsenetSharpTest.Support;

internal sealed record YencWireBody(
    byte[] Wire,
    byte[] Decoded,
    uint Crc32,
    string FileName,
    long FileSize,
    int PartNumber,
    int TotalParts,
    long PartOffset,
    long PartSize,
    int YBeginOffset,
    int YPartOffset,
    int PayloadOffset,
    int YEndOffset,
    int TerminatorOffset,
    int FollowingResponseOffset);

internal static class YencWireBodies
{
    public static YencWireBody SinglePart(
        byte[] decoded,
        string fileName = "test.bin",
        bool includeCrc = true,
        bool corruptCrc = false,
        bool omitYend = false,
        string? preamble = null,
        string? postTrailer = null,
        string? followingResponse = null,
        string extraYendFields = "",
        bool uppercaseCrc = false)
    {
        return Build(
            decoded,
            fileName,
            multipart: false,
            includeCrc,
            corruptCrc,
            omitYend,
            preamble,
            postTrailer,
            followingResponse,
            extraYendFields,
            uppercaseCrc);
    }

    public static YencWireBody Multipart(
        byte[] decoded,
        string fileName = "part.bin",
        int partNumber = 2,
        int totalParts = 2,
        long partOffset = 0,
        long fileSize = -1,
        bool includeCrc = true,
        bool corruptCrc = false,
        bool uppercaseCrc = false)
    {
        return Build(
            decoded,
            fileName,
            multipart: true,
            includeCrc,
            corruptCrc,
            omitYend: false,
            preamble: null,
            postTrailer: null,
            followingResponse: null,
            extraYendFields: "",
            uppercaseCrc,
            partNumber,
            totalParts,
            partOffset,
            fileSize < 0 ? decoded.Length : fileSize);
    }

    private static YencWireBody Build(
        byte[] decoded,
        string fileName,
        bool multipart,
        bool includeCrc,
        bool corruptCrc,
        bool omitYend,
        string? preamble,
        string? postTrailer,
        string? followingResponse,
        string extraYendFields,
        bool uppercaseCrc,
        int partNumber = 0,
        int totalParts = 0,
        long partOffset = 0,
        long fileSize = -1)
    {
        YencEncoder.EnsureInitialized();
        Crc32.EnsureInitialized();
        var crc32 = Crc32.Compute(decoded);
        if (corruptCrc)
        {
            crc32 ^= 1;
        }

        int? column = 0;
        var encoded = decoded.Length == 0
            ? []
            : YencEncoder.EncodeEx(decoded, ref column, 128, true);
        encoded = DotStuff(encoded);

        using var output = new MemoryStream();
        if (!string.IsNullOrEmpty(preamble))
        {
            WriteAscii(output, preamble);
        }

        var ybeginOffset = (int)output.Length;
        var resolvedFileSize = fileSize < 0 ? decoded.Length : fileSize;
        if (multipart)
        {
            WriteAscii(
                output,
                $"=ybegin part={partNumber} total={totalParts} line=128 size={resolvedFileSize} name={fileName}\r\n");
        }
        else
        {
            WriteAscii(output, $"=ybegin line=128 size={resolvedFileSize} name={fileName}\r\n");
        }

        var ypartOffset = -1;
        if (multipart)
        {
            ypartOffset = (int)output.Length;
            WriteAscii(
                output,
                $"=ypart begin={partOffset + 1} end={partOffset + decoded.Length}\r\n");
        }

        var payloadOffset = (int)output.Length;
        output.Write(encoded);
        if (encoded.Length > 0)
        {
            WriteAscii(output, "\r\n");
        }

        var yendOffset = (int)output.Length;
        if (!omitYend)
        {
            var crcField = includeCrc
                ? (multipart
                    ? $" pcrc32={(uppercaseCrc ? crc32.ToString("X8") : crc32.ToString("x8"))}"
                    : $" crc32={(uppercaseCrc ? crc32.ToString("X8") : crc32.ToString("x8"))}")
                : string.Empty;
            var partFields = multipart ? $" part={partNumber}" : string.Empty;
            WriteAscii(
                output,
                $"=yend size={decoded.Length}{partFields}{crcField}{extraYendFields}\r\n");
        }

        if (!string.IsNullOrEmpty(postTrailer))
        {
            WriteAscii(output, postTrailer);
        }

        var terminatorOffset = (int)output.Length;
        WriteAscii(output, ".\r\n");
        var followingOffset = (int)output.Length;
        if (!string.IsNullOrEmpty(followingResponse))
        {
            WriteAscii(output, followingResponse);
        }

        return new YencWireBody(
            output.ToArray(),
            decoded,
            includeCrc && !corruptCrc ? Crc32.Compute(decoded) : crc32,
            fileName,
            resolvedFileSize,
            partNumber,
            totalParts,
            partOffset,
            decoded.Length,
            ybeginOffset,
            ypartOffset,
            payloadOffset,
            yendOffset,
            terminatorOffset,
            followingOffset);
    }

    public static byte[] DotStuff(ReadOnlySpan<byte> data)
    {
        using var stuffed = new MemoryStream(data.Length);
        var atLineStart = true;
        foreach (var value in data)
        {
            if (atLineStart && value == (byte)'.')
            {
                stuffed.WriteByte(value);
            }

            stuffed.WriteByte(value);
            atLineStart = value == (byte)'\n';
        }

        return stuffed.ToArray();
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));
}
