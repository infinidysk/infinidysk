using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors.ZStandard.Unsafe;

namespace SharpCompress.Compressors.ZStandard;

public partial class CompressionStream
{
    public override async ValueTask DisposeAsync()
    {
        if (compressor == null)
        {
            await base.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await FlushInternalAsync(ZSTD_EndDirective.ZSTD_e_end).ConfigureAwait(false);
        }
        finally
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override async Task FlushAsync(CancellationToken cancellationToken) =>
        await FlushInternalAsync(ZSTD_EndDirective.ZSTD_e_flush, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask FlushInternalAsync(
        ZSTD_EndDirective directive,
        CancellationToken cancellationToken = default
    ) => await WriteInternalAsync(null, directive, cancellationToken).ConfigureAwait(false);

    private async ValueTask WriteInternalAsync(
        ReadOnlyMemory<byte>? buffer,
        ZSTD_EndDirective directive,
        CancellationToken cancellationToken = default
    )
    {
        EnsureNotDisposed();

        var input = new ZSTD_inBuffer_s
        {
            pos = 0,
            size = buffer.HasValue ? (nuint)buffer.Value.Length : 0,
        };
        nuint remaining;
        do
        {
            output.pos = 0;
            remaining = CompressStream(
                ref input,
                buffer.HasValue ? buffer.Value.Span : null,
                directive
            );

            var written = (int)output.pos;
            if (written > 0)
            {
                await innerStream
                    .WriteAsync(outputBuffer.AsMemory(0, written), cancellationToken)
                    .ConfigureAwait(false);
            }
        } while (
            directive == ZSTD_EndDirective.ZSTD_e_continue ? input.pos < input.size : remaining > 0
        );
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default
    ) =>
        await WriteInternalAsync(buffer, ZSTD_EndDirective.ZSTD_e_continue, cancellationToken)
            .ConfigureAwait(false);
}
