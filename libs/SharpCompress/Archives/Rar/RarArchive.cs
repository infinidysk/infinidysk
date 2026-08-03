using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors.Rar;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

namespace SharpCompress.Archives.Rar;

public interface IRarArchiveCommon
{
    int MinVersion { get; }
    int MaxVersion { get; }
}

public interface IRarArchive : IArchive, IRarArchiveCommon { }

public interface IRarAsyncArchive : IAsyncArchive, IRarArchiveCommon { }

/// <summary>
/// RAR archive with random-access entry streams.
/// </summary>
/// <remarks>
/// <para>
/// Non-solid archives may open multiple entry streams concurrently; each stream owns a private
/// unpacker instance with a pool-rented decompression window (up to ~4 MB+ for RAR5).
/// </para>
/// <para>
/// Solid archives share a single unpacker and do not support concurrent entry streams. Open at most
/// one entry stream at a time via <c>OpenEntryStream</c> / <c>OpenEntryStreamAsync</c>, or extract
/// sequentially with <see cref="IArchive.ExtractAllEntries"/>.
/// </para>
/// </remarks>
public partial class RarArchive
    : AbstractArchive<RarArchiveEntry, RarVolume>,
        IRarArchive,
        IRarAsyncArchive
{
    private bool _disposed;
    private int _activeSolidEntryStreams;

    internal Lazy<IRarUnpack> UnpackV2017 { get; } =
        new(() => new Compressors.Rar.UnpackV2017.Unpack());
    internal Lazy<IRarUnpack> UnpackV1 { get; } = new(() => new Compressors.Rar.UnpackV1.Unpack());

    private RarArchive(SourceStream sourceStream)
        : base(ArchiveType.Rar, sourceStream) { }

    /// <summary>
    /// Acquires an unpacker for an entry stream. Non-solid archives get a private instance
    /// (pool-rented window, up to ~4 MB+ for RAR5); solid archives reuse the shared instance and
    /// enforce a single active stream.
    /// </summary>
    /// <param name="isRarV3">Whether the entry uses the RAR3 unpacker.</param>
    /// <param name="isSolidArchive">Archive-level solid flag (use sync or async IsSolid consistently with the open path).</param>
    /// <param name="ownsUnpack">True when the caller must dispose the returned unpacker with the stream.</param>
    internal IRarUnpack AcquireUnpackForEntry(
        bool isRarV3,
        bool isSolidArchive,
        out bool ownsUnpack
    )
    {
        if (!isSolidArchive)
        {
            ownsUnpack = true;
            return isRarV3
                ? new Compressors.Rar.UnpackV1.Unpack()
                : new Compressors.Rar.UnpackV2017.Unpack();
        }

        if (System.Threading.Interlocked.CompareExchange(ref _activeSolidEntryStreams, 1, 0) != 0)
        {
            throw new ArchiveOperationException(
                "Solid RAR archives do not support concurrent entry streams; extract sequentially with ExtractAllEntries()."
            );
        }

        ownsUnpack = false;
        return isRarV3 ? UnpackV1.Value : UnpackV2017.Value;
    }

    internal void ReleaseSolidEntryStream() =>
        System.Threading.Interlocked.Exchange(ref _activeSolidEntryStreams, 0);

    public override void Dispose()
    {
        if (!_disposed)
        {
            if (UnpackV1.IsValueCreated && UnpackV1.Value is IDisposable unpackV1)
            {
                unpackV1.Dispose();
            }
            if (UnpackV2017.IsValueCreated && UnpackV2017.Value is IDisposable unpackV2017)
            {
                unpackV2017.Dispose();
            }

            _disposed = true;
            base.Dispose();
        }
    }

    protected override IEnumerable<RarArchiveEntry> LoadEntries(IEnumerable<RarVolume> volumes) =>
        RarArchiveEntryFactory.GetEntries(this, volumes, ReaderOptions);

    // Simple async property - kept in original file
    protected override IAsyncEnumerable<RarArchiveEntry> LoadEntriesAsync(
        IAsyncEnumerable<RarVolume> volumes
    ) => RarArchiveEntryFactory.GetEntriesAsync(this, volumes, ReaderOptions);

    protected override IEnumerable<RarVolume> LoadVolumes(SourceStream sourceStream)
    {
        sourceStream.LoadAllParts();
        var streams = sourceStream.Streams.ToArray();
        var i = 0;
        if (streams.Length > 1 && IsRarFile(streams[1], ReaderOptions))
        {
            sourceStream.IsVolumes = true;
            streams[1].Position = 0;
            sourceStream.Position = 0;

            return sourceStream.Streams.Select(a => new StreamRarArchiveVolume(
                a,
                ReaderOptions,
                i++
            ));
        }

        return [new StreamRarArchiveVolume(sourceStream, ReaderOptions, i++)];
    }

    protected override IReader CreateReaderForSolidExtraction()
    {
        if (this.IsMultipartVolume())
        {
            var streams = Volumes.Select(volume =>
            {
                volume.Stream.Position = 0;
                return volume.Stream;
            });
            return (RarReader)RarReader.OpenReader(streams, ReaderOptions);
        }

        var stream = Volumes.First().Stream;
        stream.Position = 0;
        return (RarReader)RarReader.OpenReader(stream, ReaderOptions);
    }

    private bool? _isSolid;
    private bool? _isEncrypted;
    private int? _minVersion;
    private int? _maxVersion;

    public override bool IsSolid
    {
        get
        {
            if (_isSolid is null)
            {
                _isSolid = Volumes.First().IsSolidArchive;
            }

            return _isSolid.Value;
        }
    }

    public override bool IsEncrypted
    {
        get
        {
            if (_isEncrypted is null)
            {
                // Do not cache failures (e.g. no non-directory entries) so behavior is unchanged.
                _isEncrypted = Entries.First(x => !x.IsDirectory).IsEncrypted;
            }

            return _isEncrypted.Value;
        }
    }

    public virtual int MinVersion
    {
        get
        {
            _minVersion ??= Volumes.First().MinVersion;
            return _minVersion.Value;
        }
    }

    public virtual int MaxVersion
    {
        get
        {
            _maxVersion ??= Volumes.First().MaxVersion;
            return _maxVersion.Value;
        }
    }
}
