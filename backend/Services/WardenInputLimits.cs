using NzbWebDAV.Utils;

namespace NzbWebDAV.Services;

internal static class WardenInputLimits
{
    internal const long MaxDecompressedBytes = 512L * 1024 * 1024;
    internal const int MaxRecordCharacters = 64 * 1024;
    internal const int MaxRecords = 4_000_000;

    internal static LimitedReadStream CreateLimitedStream(Stream body) =>
        new(body, MaxDecompressedBytes, static () => new InvalidOperationException(
            $"Warden source exceeds the {MaxDecompressedBytes:N0}-byte decompressed limit."));
}
