using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Compressors.LZMA.Utilities;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Writers.SevenZip;

namespace SharpCompress.Archives.SevenZip;

/// <summary>
/// A 7z archive with random-access read support and rewrite-on-<see cref="SaveTo(Stream, SevenZipWriterOptions)"/>
/// write support. There is no in-place editing: saving re-compresses the surviving source entries
/// together with any pending additions through <see cref="SevenZipWriter"/>.
/// </summary>
public partial class SevenZipArchive
    : AbstractWritableArchive<SevenZipArchiveEntry, SevenZipVolume, SevenZipWriterOptions>
{
    private ArchiveDatabase? _database;
    private bool? _isSolid;
    private bool? _isEncrypted;

    /// <summary>
    /// Constructor with a SourceStream able to handle FileInfo and Streams.
    /// </summary>
    /// <param name="sourceStream"></param>
    private SevenZipArchive(SourceStream sourceStream)
        : base(ArchiveType.SevenZip, sourceStream) { }

    protected override IEnumerable<SevenZipVolume> LoadVolumes(SourceStream sourceStream)
    {
        sourceStream.NotNull("SourceStream is null").LoadAllParts(); //request all streams
        return [new SevenZipVolume(sourceStream, ReaderOptions, 0)]; //simple single volume or split, multivolume not supported
    }

    internal SevenZipArchive()
        : base(ArchiveType.SevenZip) { }

    protected override IEnumerable<SevenZipArchiveEntry> LoadEntries(
        IEnumerable<SevenZipVolume> volumes
    )
    {
        foreach (var volume in volumes)
        {
            LoadFactory(volume.Stream);
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
                    new SevenZipFilePart(
                        volume.Stream,
                        _database,
                        i,
                        file,
                        ReaderOptions.ArchiveEncoding
                    ),
                    ReaderOptions
                );
            }
            foreach (
                var group in entries.Where(x => !x.IsDirectory).GroupBy(x => x.FilePart.Folder)
            )
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
    }

    private void LoadFactory(Stream stream)
    {
        if (_database is null)
        {
            stream.Position = 0;
            var reader = new ArchiveReader();
            reader.Open(stream, lookForHeader: ReaderOptions.LookForHeader);
            _database = reader.ReadDatabase(new PasswordProvider(ReaderOptions.Password));
            _database.SolidFolderDecodedCacheMaxBytes =
                ReaderOptions.SolidFolderDecodedCacheMaxBytes;
        }
    }

    protected override IReader CreateReaderForSolidExtraction() =>
        new SevenZipReader(ReaderOptions, this);

    /// <summary>
    /// Saves the archive using default LZMA2 writer options.
    /// </summary>
    public void SaveTo(Stream stream) =>
        SaveTo(stream, new SevenZipWriterOptions(CompressionType.LZMA2));

    // 7z has no in-place editing, so saving always rebuilds the whole archive: the surviving
    // source entries and any pending additions are re-compressed through SevenZipWriter.
    protected override void SaveTo(
        Stream stream,
        SevenZipWriterOptions options,
        IEnumerable<SevenZipArchiveEntry> oldEntries,
        IEnumerable<SevenZipArchiveEntry> newEntries
    )
    {
        using var writer = new SevenZipWriter(stream, options);
        foreach (var entry in oldEntries.Concat(newEntries))
        {
            if (entry.IsDirectory)
            {
                writer.WriteDirectory(
                    entry.Key.NotNull("Entry Key is null"),
                    entry.LastModifiedTime
                );
            }
            else
            {
                using var entryStream = entry.OpenEntryStream();
                writer.Write(
                    entry.Key.NotNull("Entry Key is null"),
                    entryStream,
                    entry.LastModifiedTime
                );
            }
        }
    }

    protected override SevenZipArchiveEntry CreateEntryInternal(
        string key,
        Stream source,
        long size,
        DateTime? modified,
        bool closeStream
    ) => new SevenZipWritableArchiveEntry(this, source, key, size, modified, closeStream);

    protected override SevenZipArchiveEntry CreateDirectoryEntry(string key, DateTime? modified) =>
        new SevenZipWritableArchiveEntry(this, key, modified);

    public override bool IsSolid
    {
        get
        {
            InvalidatePropertyCacheIfNeeded();
            if (_isSolid is null || HasPendingWritableEntries())
            {
                _isSolid = ComputeIsSolid();
            }

            return _isSolid.Value;
        }
    }

    public override bool IsEncrypted
    {
        get
        {
            InvalidatePropertyCacheIfNeeded();
            if (_isEncrypted is null || HasPendingWritableEntries())
            {
                var firstFile = Entries.FirstOrDefault(x => !x.IsDirectory);
                _isEncrypted = firstFile?.IsEncrypted ?? false;
            }

            return _isEncrypted.Value;
        }
    }

    public override long TotalSize
    {
        get
        {
            if (_database?._packSizes is not { } packSizes)
            {
                return 0;
            }

            long total = 0;
            foreach (var packSize in packSizes)
            {
                total += packSize;
            }

            return total;
        }
    }

    private void InvalidatePropertyCacheIfNeeded()
    {
        if (!HasPendingWritableEntries())
        {
            return;
        }

        _isSolid = null;
        _isEncrypted = null;
    }

    private static bool HasPendingWritableEntries(IEnumerable<SevenZipArchiveEntry> entries) =>
        entries.Any(entry => entry is SevenZipWritableArchiveEntry);

    private bool HasPendingWritableEntries() => HasPendingWritableEntries(Entries);

    private bool ComputeIsSolid() => ComputeIsSolid(Entries);

    private static bool ComputeIsSolid(IEnumerable<SevenZipArchiveEntry> entries)
    {
        var seenFolders = new HashSet<CFolder>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var folder = entry.FilePart.Folder;
            if (folder is null)
            {
                continue;
            }

            if (!seenFolders.Add(folder))
            {
                return true;
            }
        }

        return false;
    }

    public override void Dispose()
    {
        _database?.DisposeFolderStreamCache();
        base.Dispose();
    }

    internal sealed class SevenZipReader : AbstractReader<SevenZipEntry, SevenZipVolume>
    {
        private readonly SevenZipArchive _archive;
        private readonly SevenZipVolume _volume;
        private SevenZipEntry? _currentEntry;
        private Stream? _currentFolderStream;
        private CFolder? _currentFolder;

        /// <summary>
        /// Enables internal diagnostics for tests.
        /// When disabled (default), diagnostics properties return null to avoid exposing internal state.
        /// </summary>
        internal bool DiagnosticsEnabled { get; set; }

        /// <summary>
        /// Current folder instance used to decide whether the solid folder stream should be reused.
        /// Only available when <see cref="DiagnosticsEnabled"/> is true.
        /// </summary>
        internal object? DiagnosticsCurrentFolder => DiagnosticsEnabled ? _currentFolder : null;

        /// <summary>
        /// Current shared folder stream instance.
        /// Only available when <see cref="DiagnosticsEnabled"/> is true.
        /// </summary>
        internal Stream? DiagnosticsCurrentFolderStream =>
            DiagnosticsEnabled ? _currentFolderStream : null;

        internal SevenZipReader(ReaderOptions readerOptions, SevenZipArchive archive)
            : base(readerOptions, ArchiveType.SevenZip, false)
        {
            _archive = archive;
            _volume = archive.Volumes.Single();
        }

        public override SevenZipVolume Volume => _volume;

        protected override IEnumerable<SevenZipEntry> GetEntries(Stream stream)
        {
            var entries = _archive.Entries.ToList();
            stream.Position = 0;
            foreach (var dir in entries.Where(x => x.IsDirectory))
            {
                _currentEntry = dir;
                yield return dir;
            }
            // For solid archives (entries in the same folder share a compressed stream),
            // we must iterate entries sequentially and maintain the folder stream state
            // across entries in the same folder to avoid recreating the decompression
            // stream for each file, which breaks contiguous streaming.
            foreach (var entry in entries.Where(x => !x.IsDirectory))
            {
                _currentEntry = entry;
                yield return entry;
            }
        }

        protected override EntryStream GetEntryStream()
        {
            var entry = _currentEntry.NotNull("currentEntry is not null");
            if (entry.IsDirectory)
            {
                return CreateEntryStream(Stream.Null);
            }

            var folder = entry.FilePart.Folder;

            // If folder is null (empty stream entry), return empty stream
            if (folder is null)
            {
                return CreateEntryStream(Stream.Null);
            }

            // Check if we're starting a new folder - dispose old folder stream if needed
            if (folder != _currentFolder)
            {
                _currentFolderStream?.Dispose();
                _currentFolderStream = null;
                _currentFolder = folder;
            }

            // Create the folder stream once per folder
            if (_currentFolderStream is null)
            {
                _currentFolderStream = _archive._database!.GetFolderStream(
                    _volume.Stream,
                    folder!,
                    _archive._database.PasswordProvider
                );
            }

            return CreateEntryStream(
                new ReadOnlySubStream(_currentFolderStream, entry.Size, leaveOpen: true)
            );
        }

        protected override async ValueTask<EntryStream> GetEntryStreamAsync(
            CancellationToken cancellationToken = default
        )
        {
            var entry = _currentEntry.NotNull("currentEntry is not null");
            if (entry.IsDirectory)
            {
                return CreateEntryStream(Stream.Null);
            }

            var folder = entry.FilePart.Folder;

            // If folder is null (empty stream entry), return empty stream
            if (folder is null)
            {
                return CreateEntryStream(Stream.Null);
            }

            // Check if we're starting a new folder - dispose old folder stream if needed
            if (folder != _currentFolder)
            {
                if (_currentFolderStream is not null)
                {
                    await _currentFolderStream.DisposeAsync().ConfigureAwait(false);
                }
                _currentFolderStream = null;
                _currentFolder = folder;
            }

            // Create the folder stream once per folder (async decoder chain)
            if (_currentFolderStream is null)
            {
                _currentFolderStream = await _archive
                    ._database!.GetFolderStreamAsync(
                        _volume.Stream,
                        folder!,
                        _archive._database.PasswordProvider,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            return CreateEntryStream(
                new ReadOnlySubStream(_currentFolderStream, entry.Size, leaveOpen: true)
            );
        }

        public override void Dispose()
        {
            _currentFolderStream?.Dispose();
            _currentFolderStream = null;
            base.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            if (_currentFolderStream is not null)
            {
                await _currentFolderStream.DisposeAsync().ConfigureAwait(false);
                _currentFolderStream = null;
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private class PasswordProvider : IPasswordProvider
    {
        private readonly string? _password;

        public PasswordProvider(string? password) => _password = password;

        public string? CryptoGetTextPassword() => _password;
    }
}
