using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace NzbWebDAV.UsenetMigration.Symlinks;

/// <summary>
/// A restore <c>.tar.gz</c> whose single entry is a
/// JSON manifest of symlinks' prior state (path → original Altmount target).
/// The wizard writes this <b>before</b> any rewrite or orphan removal so the exact
/// prior state can be replayed. A manifest (rather than raw symlink tar entries)
/// keeps entry names safe and restore fully deterministic and testable.
/// </summary>
public static class SymlinkBackup
{
    private const string ManifestEntryName = "symlinks.json";
    private const long MaxManifestBytes = 64L * 1024 * 1024;

    public const string OrphanRemovalOperation = "remove-orphan";

    public sealed record Entry(
        string Path,
        string Target,
        string? ReplacementTarget = null,
        string? Operation = null);

    public static async Task WriteAsync(
        string backupFilePath, IReadOnlyList<Entry> entries, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(backupFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.SerializeToUtf8Bytes(entries);

        await using (var fs = new FileStream(
                         backupFilePath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         FileOptions.Asynchronous))
        {
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true))
            await using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
            {
                var manifest = new PaxTarEntry(TarEntryType.RegularFile, ManifestEntryName)
                {
                    DataStream = new MemoryStream(json),
                };
                await tar.WriteEntryAsync(manifest, ct).ConfigureAwait(false);
            }

            await fs.FlushAsync(ct).ConfigureAwait(false);
            #pragma warning disable CA1849 // FlushAsync does not flush to disk; backup durability requires the synchronous Flush(flushToDisk: true)
            fs.Flush(flushToDisk: true);
            #pragma warning restore CA1849
        }
    }

    /// <summary>Reads the manifest back out of a backup (does not touch the filesystem).</summary>
    public static async Task<IReadOnlyList<Entry>> ReadAsync(
        string backupFilePath, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(backupFilePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.Name != ManifestEntryName || entry.DataStream is null)
                continue;
            using var ms = new MemoryStream();
            await CopyBoundedAsync(entry.DataStream, ms, MaxManifestBytes, ct).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<Entry?>>(ms.ToArray()) ?? [];
            return list.Where(e => e is not null).Cast<Entry>().ToList();
        }
        throw new InvalidDataException("The archive does not contain a symlink manifest.");
    }

    private static async Task CopyBoundedAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
                return;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException(
                    $"The symlink backup manifest exceeds the {maxBytes} byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}
