using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common;
using SharpCompress.IO;

namespace SharpCompress;

internal static partial class Utility
{
    /// <summary>
    /// Gets the appropriate StringComparison for path checks based on the file system.
    /// Windows uses case-insensitive file systems, while Unix-like systems use case-sensitive file systems.
    /// </summary>
    internal static StringComparison PathComparison =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly HashSet<char> invalidChars = new(Path.GetInvalidFileNameChars());

    public static DateTime DosDateToDateTime(ushort iDate, ushort iTime)
    {
        var year = (iDate / 512) + 1980;
        var month = iDate % 512 / 32;
        var day = iDate % 512 % 32;
        var hour = iTime / 2048;
        var minute = iTime % 2048 / 32;
        var second = iTime % 2048 % 32 * 2;

        if (iDate == ushort.MaxValue || month == 0 || day == 0)
        {
            year = 1980;
            month = 1;
            day = 1;
        }

        if (iTime == ushort.MaxValue)
        {
            hour = minute = second = 0;
        }

        if (
            month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || hour > 23
            || minute > 59
            || second > 59
        )
        {
            return new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }

    public static uint DateTimeToDosTime(this DateTime? dateTime)
    {
        if (dateTime is null)
        {
            return 0;
        }

        var localDateTime = dateTime.Value.ToLocalTime();

        return (uint)(
            (localDateTime.Second / 2)
            | (localDateTime.Minute << 5)
            | (localDateTime.Hour << 11)
            | (localDateTime.Day << 16)
            | (localDateTime.Month << 21)
            | ((localDateTime.Year - 1980) << 25)
        );
    }

    public static DateTime DosDateToDateTime(uint iTime) =>
        DosDateToDateTime((ushort)(iTime / 65536), (ushort)(iTime % 65536));

    /// <summary>
    /// Convert Unix time value to a DateTime object.
    /// </summary>
    /// <param name="unixtime">The Unix time stamp you want to convert to DateTime.</param>
    /// <returns>Returns a DateTime object that represents value of the Unix time.</returns>
    public static DateTime UnixTimeToDateTime(long unixtime)
    {
        var sTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return sTime.AddSeconds(unixtime);
    }

    extension(Stream source)
    {
        public long TransferTo(Stream destination, long maxLength, int? bufferSize)
        {
            // Use ReadOnlySubStream to limit reading and leverage framework's CopyTo
            using var limitedStream = new IO.ReadOnlySubStream(source, maxLength);
            limitedStream.CopyTo(destination, bufferSize ?? Constants.BufferSize);
            return limitedStream.Position;
        }

        public async ValueTask SkipAsync(
            long advanceAmount,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (source is SharpCompressStream sharpCompressStream)
            {
                if (sharpCompressStream.TrySkipForward(advanceAmount))
                {
                    return;
                }
            }
            else if (source.CanSeek)
            {
                source.Position += advanceAmount;
                return;
            }

            var array = ArrayPool<byte>.Shared.Rent(Constants.BufferSize);
            try
            {
                while (advanceAmount > 0)
                {
                    var toRead = (int)Math.Min(array.Length, advanceAmount);
                    var read = await source
                        .ReadAsync(array.AsMemory(0, toRead), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    advanceAmount -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }

    public static string TrimNulls(this string source) => source.Replace('\0', ' ').Trim();

    /// <summary>
    /// Insert a little endian UINT32 into a byte array
    /// </summary>
    /// <param name="buffer">The buffer to insert into</param>
    /// <param name="number">The UINT32 to insert</param>
    /// <param name="offset">Offset of the buffer to insert into</param>
    public static void SetLittleUInt32(byte[] buffer, uint number, long offset) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan((int)offset), number);

    /// <summary>
    /// Insert a big endian UINT32 into a byte array
    /// </summary>
    /// <param name="buffer">The buffer to insert into</param>
    /// <param name="number">The UINT32 to insert</param>
    /// <param name="offset">Offset of the buffer to insert into</param>
    public static void SetBigUInt32(byte[] buffer, uint number, long offset) =>
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan((int)offset), number);

    public static string ReplaceInvalidFileNameChars(string fileName)
    {
        var sb = new StringBuilder(fileName.Length);
        foreach (var c in fileName)
        {
            var newChar = invalidChars.Contains(c) ? '_' : c;
            sb.Append(newChar);
        }

        return sb.ToString();
    }
}
