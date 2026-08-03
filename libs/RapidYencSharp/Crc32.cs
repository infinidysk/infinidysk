using System;

namespace RapidYencSharp;

/// <summary>
/// Provides CRC32 computation functionality
/// </summary>
public static class Crc32
{
    private static readonly Lazy<bool> Init = new(() =>
    {
        NativeMethods.EnsureResolverRegistered();
        NativeMethods.rapidyenc_crc_init();
        return true;
    });

    /// <summary>
    /// Ensures the CRC32 module is initialized. This method is thread-safe and idempotent.
    /// </summary>
    public static void EnsureInitialized()
    {
        _ = Init.Value;
    }

    /// <summary>
    /// Gets the kernel/ISA level used for CRC32 computation
    /// </summary>
    public static int Kernel
    {
        get
        {
            EnsureInitialized();
            return NativeMethods.rapidyenc_crc_kernel();
        }
    }

    /// <summary>
    /// Computes the CRC32 hash of the input data
    /// </summary>
    /// <param name="input">The input data</param>
    /// <param name="initCrc">The initial CRC32 value (default: 0). Use this for incremental hashing.</param>
    /// <returns>The CRC32 hash</returns>
    public static uint Compute(ReadOnlySpan<byte> input, uint initCrc = 0)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return initCrc;

        unsafe
        {
            fixed (byte* inputPtr = input)
            {
                return NativeMethods.rapidyenc_crc(
                    new IntPtr(inputPtr),
                    (nuint)input.Length,
                    initCrc);
            }
        }
    }

    /// <summary>
    /// Combines two CRC32 hashes. Given crc1 = CRC32(data1) and crc2 = CRC32(data2), returns CRC32(data1 + data2)
    /// </summary>
    /// <param name="crc1">The CRC32 of the first data block</param>
    /// <param name="crc2">The CRC32 of the second data block</param>
    /// <param name="length2">The length of the second data block</param>
    /// <returns>The combined CRC32 hash</returns>
    public static uint Combine(uint crc1, uint crc2, ulong length2)
    {
        EnsureInitialized();
        return NativeMethods.rapidyenc_crc_combine(crc1, crc2, length2);
    }

    /// <summary>
    /// Computes the CRC32 of a sequence of zero bytes
    /// </summary>
    /// <param name="initCrc">The initial CRC32 value</param>
    /// <param name="length">The number of zero bytes</param>
    /// <returns>The CRC32 hash of the zero sequence</returns>
    public static uint ComputeZeros(uint initCrc, ulong length)
    {
        EnsureInitialized();
        return NativeMethods.rapidyenc_crc_zeros(initCrc, length);
    }

    /// <summary>
    /// Performs the inverse of ComputeZeros: Given initCrc = CRC32(data + [0]*length), returns CRC32(data)
    /// </summary>
    /// <param name="initCrc">The CRC32 value that includes trailing zeros</param>
    /// <param name="length">The number of trailing zero bytes to remove</param>
    /// <returns>The CRC32 hash without the trailing zeros</returns>
    public static uint Unzero(uint initCrc, ulong length)
    {
        EnsureInitialized();
        return NativeMethods.rapidyenc_crc_unzero(initCrc, length);
    }

    /// <summary>
    /// Returns the product of a and b in the CRC32 field
    /// </summary>
    /// <param name="a">First value</param>
    /// <param name="b">Second value</param>
    /// <returns>The product in the CRC32 field</returns>
    public static uint Multiply(uint a, uint b)
    {
        EnsureInitialized();
        return NativeMethods.rapidyenc_crc_multiply(a, b);
    }

    /// <summary>
    /// Returns 2**n in the CRC32 field. n can be negative
    /// </summary>
    /// <param name="n">The exponent</param>
    /// <returns>2**n in the CRC32 field</returns>
    public static uint PowerOf2(long n)
    {
        EnsureInitialized();
        return NativeMethods.rapidyenc_crc_2pow(n);
    }

    /// <summary>
    /// Returns 2**(8n) in the CRC32 field. Similar to PowerOf2(8*n), but avoids overflow and n cannot be negative
    /// </summary>
    /// <param name="n">The exponent</param>
    /// <returns>2**(8n) in the CRC32 field</returns>
    public static uint PowerOf256(ulong n)
    {
        EnsureInitialized();
        return NativeMethods.rapidyenc_crc_256pow(n);
    }
}
