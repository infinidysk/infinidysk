using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Compressors.Rar;

namespace SharpCompress.Readers.Rar;

public abstract partial class RarReader
{
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            DisposeUnpackInstances();
            _disposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the compressed read stream for the current entry.
    /// Multi-volume readers override this to load additional volumes asynchronously.
    /// </summary>
    internal virtual ValueTask<MultiVolumeReadOnlyAsyncStream> CreateMultiVolumeReadStreamAsync() =>
        MultiVolumeReadOnlyAsyncStream.Create(Entry.Parts.Cast<RarFilePart>());

    /// <summary>
    /// Asynchronously creates an entry stream for the current entry.
    /// Supports both RAR v3 and v5 archives with proper CRC verification.
    /// </summary>
    protected override async ValueTask<EntryStream> GetEntryStreamAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Entry.IsRedir)
        {
            throw new ArchiveOperationException("no stream for redirect entry");
        }

        var stream = await CreateMultiVolumeReadStreamAsync().ConfigureAwait(false);
        if (Entry.IsRarV3)
        {
            return CreateEntryStream(
                await RarCrcStream
                    .CreateAsync(UnpackV1.Value, Entry.FileHeader, stream, cancellationToken)
                    .ConfigureAwait(false)
            );
        }

        if (Entry.FileHeader.FileCrc?.Length > 5)
        {
            return CreateEntryStream(
                await RarBLAKE2spStream
                    .CreateAsync(UnpackV2017.Value, Entry.FileHeader, stream, cancellationToken)
                    .ConfigureAwait(false)
            );
        }

        return CreateEntryStream(
            await RarCrcStream
                .CreateAsync(UnpackV2017.Value, Entry.FileHeader, stream, cancellationToken)
                .ConfigureAwait(false)
        );
    }
}
