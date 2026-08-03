using System;
using SharpCompress.Algorithms;

namespace SharpCompress.Compressors.Rar;

internal static class RarCRC
{
    public static uint CheckCrc(uint startCrc, byte b) => Crc32Helper.Append(startCrc, b);

    public static uint CheckCrc(uint startCrc, ReadOnlySpan<byte> data, int offset, int count)
    {
        var size = Math.Min(data.Length - offset, count);
        if (size <= 0)
        {
            return startCrc;
        }

        return Crc32Helper.Append(startCrc, data.Slice(offset, size));
    }
}
