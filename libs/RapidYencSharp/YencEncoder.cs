using System;
using System.Buffers;

namespace RapidYencSharp;

/// <summary>
/// Provides yEnc encoding functionality
/// </summary>
public static class YencEncoder
{
    private static readonly Lazy<bool> Init = new(() =>
    {
        NativeMethods.EnsureResolverRegistered();
        NativeMethods.rapidyenc_encode_init();
        return true;
    });

    /// <summary>
    /// Ensures the encoder is initialized. This method is thread-safe and idempotent.
    /// </summary>
    public static void EnsureInitialized()
    {
        _ = Init.Value;
    }

    /// <summary>
    /// Gets the kernel/ISA level used for encoding
    /// </summary>
    public static int Kernel
    {
        get
        {
            EnsureInitialized();
            return NativeMethods.rapidyenc_encode_kernel();
        }
    }

    /// <summary>
    /// Calculates the maximum possible length of yEnc encoded output for a given input length
    /// </summary>
    /// <param name="inputLength">The length of the input data in bytes</param>
    /// <param name="lineSize">The target number of bytes for each line (default: 128)</param>
    /// <returns>The maximum possible encoded length</returns>
    public static nuint GetMaxEncodedLength(nuint inputLength, int lineSize = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineSize);
        NativeMethods.EnsureResolverRegistered();
        return NativeMethods.rapidyenc_encode_max_length(inputLength, lineSize);
    }

    /// <summary>
    /// Encodes data using yEnc encoding directly into the provided output buffer
    /// </summary>
    /// <param name="input">The input data to encode</param>
    /// <param name="output">The output buffer to write encoded data to. Must be at least GetMaxEncodedLength(input.Length) bytes.</param>
    /// <param name="lineSize">The target number of bytes for each line (default: 128)</param>
    /// <returns>The number of bytes written to the output buffer</returns>
    /// <exception cref="ArgumentException">Thrown when output buffer is too small</exception>
    public static int Encode(ReadOnlySpan<byte> input, Span<byte> output, int lineSize = 128)
    {
        EnsureInitialized();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineSize);

        if (input.IsEmpty)
            return 0;

        nuint maxLength = GetMaxEncodedLength((nuint)input.Length, lineSize);
        if ((nuint)output.Length < maxLength)
            throw new ArgumentException($"Output buffer too small. Required: {maxLength}, Provided: {output.Length}",
                nameof(output));
        if (input.Overlaps(output))
            throw new ArgumentException("Input and output buffers must not overlap when encoding.", nameof(output));

        unsafe
        {
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                nuint encodedLength = lineSize == 128
                    ? NativeMethods.rapidyenc_encode(
                        new IntPtr(inputPtr),
                        new IntPtr(outputPtr),
                        (nuint)input.Length)
                    : NativeMethods.rapidyenc_encode_ex_without_column(
                        lineSize,
                        IntPtr.Zero,
                        new IntPtr(inputPtr),
                        new IntPtr(outputPtr),
                        (nuint)input.Length,
                        1);

                if (encodedLength > maxLength)
                    throw new InvalidOperationException("Encoded length exceeds maximum expected length");

                return (int)encodedLength;
            }
        }
    }

    /// <summary>
    /// Encodes data using yEnc encoding and returns a new array
    /// </summary>
    /// <param name="input">The input data to encode</param>
    /// <returns>The encoded data</returns>
    public static byte[] Encode(ReadOnlySpan<byte> input)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return Array.Empty<byte>();

        nuint maxLength = GetMaxEncodedLength((nuint)input.Length);

        // Use ArrayPool for temporary buffer to reduce allocations
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(checked((int)maxLength));
        try
        {
            int bytesWritten = Encode(input, pooledBuffer);

            // Only allocate the exact size needed
            byte[] result = new byte[bytesWritten];
            pooledBuffer.AsSpan(0, bytesWritten).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }

    /// <summary>
    /// Encodes data using yEnc encoding with custom line size and incremental processing support
    /// </summary>
    /// <param name="input">The input data to encode</param>
    /// <param name="output">The output buffer to write encoded data to. Must be at least GetMaxEncodedLength(input.Length, lineSize) bytes.</param>
    /// <param name="column">The column in the line to start at. This will be updated with the column position after encoding. Pass null to not track the column.</param>
    /// <param name="lineSize">The target number of bytes for each line (default: 128)</param>
    /// <param name="isEnd">If true, this is the final chunk of the article. Setting this ensures that trailing whitespace is properly escaped.</param>
    /// <returns>The number of bytes written to the output buffer</returns>
    /// <exception cref="ArgumentException">Thrown when output buffer is too small</exception>
    public static int EncodeEx(ReadOnlySpan<byte> input, Span<byte> output, ref int? column, int lineSize = 128,
        bool isEnd = true)
    {
        EnsureInitialized();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineSize);

        if (input.IsEmpty)
            return 0;

        nuint maxLength = GetMaxEncodedLength((nuint)input.Length, lineSize);
        if ((nuint)output.Length < maxLength)
            throw new ArgumentException($"Output buffer too small. Required: {maxLength}, Provided: {output.Length}",
                nameof(output));
        if (input.Overlaps(output))
            throw new ArgumentException("Input and output buffers must not overlap when encoding.", nameof(output));

        unsafe
        {
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                nuint encodedLength;
                int col = column ?? 0;

                if (column.HasValue)
                {
                    encodedLength = NativeMethods.rapidyenc_encode_ex(
                        lineSize,
                        ref col,
                        new IntPtr(inputPtr),
                        new IntPtr(outputPtr),
                        (nuint)input.Length,
                        isEnd ? 1 : 0);
                    column = col;
                }
                else
                {
                    encodedLength = NativeMethods.rapidyenc_encode_ex_without_column(
                        lineSize,
                        IntPtr.Zero,
                        new IntPtr(inputPtr),
                        new IntPtr(outputPtr),
                        (nuint)input.Length,
                        isEnd ? 1 : 0);
                }

                if (encodedLength > maxLength)
                    throw new InvalidOperationException("Encoded length exceeds maximum expected length");

                return (int)encodedLength;
            }
        }
    }

    /// <summary>
    /// Encodes data using yEnc encoding with custom line size and incremental processing support, returning a new array
    /// </summary>
    /// <param name="input">The input data to encode</param>
    /// <param name="column">The column in the line to start at. This will be updated with the column position after encoding. Pass null to not track the column.</param>
    /// <param name="lineSize">The target number of bytes for each line (default: 128)</param>
    /// <param name="isEnd">If true, this is the final chunk of the article. Setting this ensures that trailing whitespace is properly escaped.</param>
    /// <returns>The encoded data</returns>
    public static byte[] EncodeEx(ReadOnlySpan<byte> input, ref int? column, int lineSize = 128, bool isEnd = true)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return Array.Empty<byte>();

        nuint maxLength = GetMaxEncodedLength((nuint)input.Length, lineSize);

        // Use ArrayPool for temporary buffer to reduce allocations
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(checked((int)maxLength));
        try
        {
            int bytesWritten = EncodeEx(input, pooledBuffer, ref column, lineSize, isEnd);

            // Only allocate the exact size needed
            byte[] result = new byte[bytesWritten];
            pooledBuffer.AsSpan(0, bytesWritten).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }
}
