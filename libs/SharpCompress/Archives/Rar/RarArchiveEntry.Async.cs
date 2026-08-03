using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors.Rar;
using SharpCompress.Readers;

namespace SharpCompress.Archives.Rar;

public partial class RarArchiveEntry
{
    public async ValueTask<Stream> OpenEntryStreamAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Prefer sync IsSolid: Archive API streams are seekable, and mixed sync Entries +
        // async OpenEntryStream must not re-parse headers via a second async volume set
        // (corrupt-but-openable archives can fail a second parse).
        var isSolidArchive = archive.IsSolid;

        // Fast path: stored (m0), RAR5 encrypted, non-solid entries over seekable volumes.
        if (
            !isSolidArchive
            && EncryptedStoredRarEntryStream.TryCreate(parts, out var encryptedStoredStream)
        )
        {
            return encryptedStoredStream;
        }

        // Fast path: stored (m0), non-encrypted, non-solid entries over seekable volumes.
        if (!isSolidArchive && StoredRarEntryStream.TryCreate(parts, out var storedStream))
        {
            return storedStream;
        }

        var unpack = archive.AcquireUnpackForEntry(IsRarV3, isSolidArchive, out var ownsUnpack);
        Action? onDispose = isSolidArchive ? archive.ReleaseSolidEntryStream : null;
        MultiVolumeReadOnlyAsyncStream? readStream = null;
        RarStream? stream = null;
        try
        {
            readStream = await MultiVolumeReadOnlyAsyncStream.Create(parts).ConfigureAwait(false);
            stream = new RarStream(unpack, FileHeader, readStream, ownsUnpack, onDispose);

            await stream.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (readStream is not null)
                {
                    await readStream.DisposeAsync().ConfigureAwait(false);
                }

                if (ownsUnpack && unpack is IDisposable disposableUnpack)
                {
                    disposableUnpack.Dispose();
                }

                onDispose?.Invoke();
            }

            throw;
        }
    }
}
