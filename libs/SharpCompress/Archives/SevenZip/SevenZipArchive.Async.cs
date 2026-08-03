using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;

namespace SharpCompress.Archives.SevenZip;

public partial class SevenZipArchive
{
    // Async counterpart to the synchronous SaveTo: rebuilds the archive from the surviving source
    // entries and pending additions using the async SevenZipWriter write/finalize path.
    protected override async ValueTask SaveToAsync(
        Stream stream,
        SevenZipWriterOptions options,
        IAsyncEnumerable<SevenZipArchiveEntry> oldEntries,
        IEnumerable<SevenZipArchiveEntry> newEntries,
        CancellationToken cancellationToken = default
    )
    {
        await using var writer = new SevenZipWriter(stream, options);
        await foreach (
            var entry in oldEntries.WithCancellation(cancellationToken).ConfigureAwait(false)
        )
        {
            await WriteEntryAsync(writer, entry, cancellationToken).ConfigureAwait(false);
        }
        foreach (var entry in newEntries)
        {
            await WriteEntryAsync(writer, entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteEntryAsync(
        SevenZipWriter writer,
        SevenZipArchiveEntry entry,
        CancellationToken cancellationToken
    )
    {
        if (entry.IsDirectory)
        {
            await writer
                .WriteDirectoryAsync(
                    entry.Key.NotNull("Entry Key is null"),
                    entry.LastModifiedTime,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else
        {
            using var entryStream = await entry
                .OpenEntryStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await writer
                .WriteAsync(
                    entry.Key.NotNull("Entry Key is null"),
                    entryStream,
                    entry.LastModifiedTime,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private async ValueTask LoadFactoryAsync(
        Stream stream,
        CancellationToken cancellationToken = default
    )
    {
        if (_database is null)
        {
            stream.Position = 0;
            var reader = new ArchiveReader();
            await reader
                .OpenAsync(stream, lookForHeader: ReaderOptions.LookForHeader, cancellationToken)
                .ConfigureAwait(false);
            _database = await reader
                .ReadDatabaseAsync(new PasswordProvider(ReaderOptions.Password), cancellationToken)
                .ConfigureAwait(false);
            _database.SolidFolderDecodedCacheMaxBytes =
                ReaderOptions.SolidFolderDecodedCacheMaxBytes;
        }
    }

    protected override async IAsyncEnumerable<SevenZipArchiveEntry> LoadEntriesAsync(
        IAsyncEnumerable<SevenZipVolume> volumes
    )
    {
        var stream = (await volumes.SingleAsync().ConfigureAwait(false)).Stream;
        await LoadFactoryAsync(stream).ConfigureAwait(false);
        if (_database is null)
        {
            yield break;
        }
        var entries = new SevenZipArchiveEntry[_database._files.Count];
        for (var i = 0; i < _database._files.Count; i++)
        {
            var file = _database._files[i];
            entries[i] = new SevenZipArchiveEntry(
                this,
                new SevenZipFilePart(stream, _database, i, file, ReaderOptions.ArchiveEncoding),
                ReaderOptions
            );
        }
        foreach (var group in entries.Where(x => !x.IsDirectory).GroupBy(x => x.FilePart.Folder))
        {
            var isSolid = false;
            foreach (var entry in group)
            {
                entry.IsSolid = isSolid;
                isSolid = true;
            }
        }

        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    protected override ValueTask<IAsyncReader> CreateReaderForSolidExtractionAsync() =>
        new(new SevenZipReader(ReaderOptions, this));

    public override async ValueTask<bool> IsSolidAsync()
    {
        var entries = await EntriesAsync.ToListAsync().ConfigureAwait(false);
        if (_isSolid is not null && !HasPendingWritableEntries(entries))
        {
            return _isSolid.Value;
        }

        _isSolid = ComputeIsSolid(entries);
        return _isSolid.Value;
    }

    public override async ValueTask DisposeAsync()
    {
        _database?.DisposeFolderStreamCache();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
