using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpCompress.Common.Zip.Headers;

internal abstract partial class ZipFileEntry
{
    internal async ValueTask<PkwareTraditionalEncryptionData> ComposeEncryptionDataAsync(
        Stream archiveStream,
        CancellationToken cancellationToken = default
    )
    {
        ThrowHelper.ThrowIfNull(archiveStream);

        var buffer = new byte[12];
        await archiveStream
            .ReadAtLeastAsync(
                buffer.AsMemory(0, 12),
                12,
                throwOnEndOfStream: false,
                cancellationToken
            )
            .ConfigureAwait(false);

        var encryptionData = PkwareTraditionalEncryptionData.ForRead(Password!, this, buffer);

        return encryptionData;
    }
}
