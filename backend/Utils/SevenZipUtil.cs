using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NzbWebDAV.Utils;

public static class SevenZipUtil
{
    public static async Task<List<SevenZipEntry>> GetSevenZipEntriesAsync
    (
        Stream stream,
        string? password,
        CancellationToken ct
    )
    {
        await using var cancellableStream = new CancellableStream(stream, ct);
        await using var archive = (SevenZipArchive)SevenZipArchive.OpenArchive(
            cancellableStream,
            new ReaderOptions { Password = password, LeaveStreamOpen = true });
        var entries = new List<SevenZipEntry>();
        await foreach (var entry in archive.EntriesAsync
            .WithCancellation(ct).ConfigureAwait(false))
        {
            if (entry.IsDirectory) continue;
            entries.Add(new SevenZipEntry(entry, password));
        }
        return entries;
    }

    public class SevenZipEntry(SevenZipArchiveEntry entry, string? password)
    {
        public SevenZipArchiveEntry Entry => entry;
        public string PathWithinArchive { get; } = entry.Key!;
        public CompressionType CompressionType { get; } = entry.CompressionType;
        public bool IsEncrypted { get; } = entry.IsEncrypted;
        public bool IsSolid { get; } = entry.IsSolid;

        public AesParams? AesParams { get; } =
            AesParams.FromCoderInfo(
                entry.AesCoderProperties is { Length: > 0 } props ? props : null,
                password,
                entry.Size);

        public long FolderStartByteOffset { get; } = entry.FolderStartOffset;

        public LongRange ByteRangeWithinArchive { get; } = GetPackedByteRange(entry);

        private static LongRange GetPackedByteRange(SevenZipArchiveEntry archiveEntry)
        {
            if (!archiveEntry.TryGetPackedByteRange(out var start, out var length))
                throw new InvalidOperationException(
                    $"7z entry '{archiveEntry.Key}' has no packed byte range.");
            return LongRange.FromStartAndSize(start, length);
        }
    }
}
