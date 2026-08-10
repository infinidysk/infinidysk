namespace NzbWebDAV.Utils;

public static class VideoSignatureUtil
{
    public const int First16KBLength = 16 * 1024;

    public static async Task<byte[]> ReadFirst16KBAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[First16KBLength];
        if (!stream.CanSeek)
            throw new InvalidOperationException("Stream must be seekable to read the first 16 KiB.");

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (count == 0)
                    break;
                read += count;
            }

            return read == buffer.Length ? buffer : buffer[..read];
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static ReadOnlySpan<byte> AsfGuid =>
        [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];

    private static ReadOnlySpan<byte> Rar4Magic =>
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];

    private static ReadOnlySpan<byte> Rar5Magic =>
        [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01];

    private static ReadOnlySpan<byte> SevenZipMagic =>
        [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>
    /// Sniffs a video container from the leading bytes. Returns a lowercase
    /// extension including the dot, or null when no known signature matches.
    /// </summary>
    public static string? GuessVideoExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4
            && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            return ".mkv";

        if (data.Length >= 12
            && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            return ".mp4";

        if (data.Length >= 12
            && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'A' && data[9] == (byte)'V' && data[10] == (byte)'I' && data[11] == (byte)' ')
            return ".avi";

        if (data.Length >= 16 && data[..16].SequenceEqual(AsfGuid))
            return ".wmv";

        if (data.Length >= 4
            && data[0] == 0x46 && data[1] == 0x4C && data[2] == 0x56 && data[3] == 0x01)
            return ".flv";

        if (data.Length >= 4
            && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0xBA)
            return ".mpg";

        if (IsMpegTransportStream(data))
            return ".ts";

        return null;
    }

    public static bool LooksLikeArchiveMagic(ReadOnlySpan<byte> data)
    {
        if (data.Length >= Rar4Magic.Length && data[..Rar4Magic.Length].SequenceEqual(Rar4Magic))
            return true;
        if (data.Length >= Rar5Magic.Length && data[..Rar5Magic.Length].SequenceEqual(Rar5Magic))
            return true;
        return data.Length >= SevenZipMagic.Length && data[..SevenZipMagic.Length].SequenceEqual(SevenZipMagic);
    }

    /// <summary>
    /// Sniffs archive member content that starts within the first 16 KiB of the
    /// parent file. Returns null when encrypted, out of range, or archive magic.
    /// </summary>
    public static string? SniffMemberFromFirst16KB(byte[] first16KB, long dataStart, bool encrypted)
    {
        if (encrypted || dataStart < 0 || dataStart + 16 > first16KB.Length || dataStart + 16 > First16KBLength)
            return null;

        var slice = first16KB.AsSpan((int)dataStart);
        if (LooksLikeArchiveMagic(slice))
            return null;

        return GuessVideoExtension(slice);
    }

    private static bool IsMpegTransportStream(ReadOnlySpan<byte> data)
    {
        if (data.Length < 377 || data[0] != 0x47 || data[188] != 0x47 || data[376] != 0x47)
            return false;

        return data.Length < 565 || data[564] == 0x47;
    }
}
