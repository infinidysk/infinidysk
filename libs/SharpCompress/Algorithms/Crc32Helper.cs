using System;
using System.IO.Hashing;
using System.Runtime.CompilerServices;

namespace SharpCompress.Algorithms;

/// <summary>
/// IEEE CRC-32 (poly 0xEDB88320) helpers backed by <see cref="Crc32"/>.
/// Operates on the raw (pre-final-XOR) running state used throughout SharpCompress
/// (initialize with <c>0xFFFFFFFF</c>, finalize with <c>~state</c>).
/// </summary>
internal static class Crc32Helper
{
    [ThreadStatic]
    private static Crc32? t_hasher;

    /// <summary>
    /// Appends <paramref name="data"/> to a raw CRC state and returns the updated raw state.
    /// </summary>
    public static uint Append(uint currentState, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return currentState;
        }

        var hasher = t_hasher ??= new Crc32();
        GetCrc(hasher) = currentState;
        hasher.Append(data);
        return ~hasher.GetCurrentHashAsUInt32();
    }

    /// <summary>
    /// Appends a single byte to a raw CRC state and returns the updated raw state.
    /// </summary>
    public static uint Append(uint currentState, byte value)
    {
        Span<byte> one = stackalloc byte[1];
        one[0] = value;
        return Append(currentState, one);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_crc")]
    private static extern ref uint GetCrc(Crc32 crc);
}
