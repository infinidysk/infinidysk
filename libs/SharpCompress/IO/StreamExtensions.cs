using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.IO;

namespace SharpCompress;

public static class StreamExtensions
{
    extension(Stream stream)
    {
        public void Skip(long advanceAmount)
        {
            if (stream is SharpCompressStream sharpCompressStream)
            {
                if (sharpCompressStream.TrySkipForward(advanceAmount))
                {
                    return;
                }
            }
            else if (stream.CanSeek)
            {
                stream.Position += advanceAmount;
                return;
            }

            using var readOnlySubStream = new ReadOnlySubStream(stream, advanceAmount);
            readOnlySubStream.CopyTo(Stream.Null);
        }

        public void Skip() => stream.CopyTo(Stream.Null);

        public async ValueTask SkipAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
    }
}
