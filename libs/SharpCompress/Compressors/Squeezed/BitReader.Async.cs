using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;

namespace SharpCompress.Compressors.Squeezed;

public partial class BitReader
{
    public async ValueTask<bool> ReadBitAsync(CancellationToken cancellationToken = default)
    {
        if (_bitCount == 0)
        {
            byte[] buffer = _singleByteBuffer;
            int bytesRead = await _stream
                .ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new IncompleteArchiveException("Unexpected end of stream.");
            }

            _bitBuffer = buffer[0];
            _bitCount = 8;
        }

        bool bit = (_bitBuffer & 1) != 0;
        _bitBuffer >>= 1;
        _bitCount--;
        return bit;
    }
}
