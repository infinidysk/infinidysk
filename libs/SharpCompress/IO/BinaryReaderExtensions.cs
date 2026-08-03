using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress;

public static class BinaryReaderExtensions
{
    extension(BinaryReader reader)
    {
        public async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken = default)
        {
            var buffer = new byte[1];
            try
            {
                await reader
                    .BaseStream.ReadExactlyAsync(buffer.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException e)
            {
                throw new IncompleteArchiveException("Unexpected end of stream.", e);
            }
            return buffer[0];
        }

        public async ValueTask<byte[]> ReadBytesAsync(
            int count,
            CancellationToken cancellationToken = default
        )
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");
            }

            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            var bytes = new byte[count];
            try
            {
                await reader
                    .BaseStream.ReadExactlyAsync(bytes.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException e)
            {
                throw new IncompleteArchiveException("Unexpected end of stream.", e);
            }
            return bytes;
        }
    }
}
