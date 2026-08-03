using System;
using System.Buffers;

namespace RapidYencSharp;

/// <summary>
/// Provides yEnc decoding functionality
/// </summary>
public static class YencDecoder
{
    private static readonly Lazy<bool> Init = new(() =>
    {
        NativeMethods.EnsureResolverRegistered();
        NativeMethods.rapidyenc_decode_init();
        return true;
    });

    /// <summary>
    /// Ensures the decoder is initialized. This method is thread-safe and idempotent.
    /// </summary>
    public static void EnsureInitialized()
    {
        _ = Init.Value;
    }

    /// <summary>
    /// Gets the kernel/ISA level used for decoding
    /// </summary>
    public static int Kernel
    {
        get
        {
            EnsureInitialized();
            return NativeMethods.rapidyenc_decode_kernel();
        }
    }

    /// <summary>
    /// Decodes yEnc encoded data directly into the provided output buffer
    /// </summary>
    /// <param name="input">The yEnc encoded data to decode</param>
    /// <param name="output">The output buffer to write decoded data to. Must be at least as large as input.</param>
    /// <returns>The number of bytes written to the output buffer</returns>
    /// <exception cref="ArgumentException">Thrown when output buffer is too small</exception>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return 0;

        if (output.Length < input.Length)
            throw new ArgumentException($"Output buffer too small. Required: {input.Length}, Provided: {output.Length}",
                nameof(output));

        unsafe
        {
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                nuint decodedLength = NativeMethods.rapidyenc_decode(
                    new IntPtr(inputPtr),
                    new IntPtr(outputPtr),
                    (nuint)input.Length);

                if (decodedLength > (nuint)input.Length)
                    throw new InvalidOperationException("Decoded length exceeds input length");

                return (int)decodedLength;
            }
        }
    }

    /// <summary>
    /// Decodes yEnc encoded data and returns a new array
    /// </summary>
    /// <param name="input">The yEnc encoded data to decode</param>
    /// <returns>The decoded data</returns>
    public static byte[] Decode(ReadOnlySpan<byte> input)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return Array.Empty<byte>();

        // Use ArrayPool for temporary buffer to reduce allocations
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(input.Length);
        try
        {
            int bytesWritten = Decode(input, pooledBuffer);

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
    /// Decodes yEnc encoded data with optional NNTP dot unstuffing and state tracking
    /// </summary>
    /// <param name="input">The yEnc encoded data to decode</param>
    /// <param name="output">The output buffer to write decoded data to. Must be at least as large as input.</param>
    /// <param name="state">The decoder state for incremental decoding. Pass null if not tracking state.</param>
    /// <param name="isRaw">If true, will also handle NNTP dot unstuffing</param>
    /// <returns>The number of bytes written to the output buffer</returns>
    /// <exception cref="ArgumentException">Thrown when output buffer is too small</exception>
    public static int DecodeEx(ReadOnlySpan<byte> input, Span<byte> output, ref RapidYencDecoderState? state,
        bool isRaw = true)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return 0;

        if (output.Length < input.Length)
            throw new ArgumentException($"Output buffer too small. Required: {input.Length}, Provided: {output.Length}",
                nameof(output));

        unsafe
        {
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                nuint decodedLength;
                RapidYencDecoderState decoderState = state ?? RapidYencDecoderState.RYDEC_STATE_CRLF;

                if (state.HasValue)
                {
                    decodedLength = NativeMethods.rapidyenc_decode_ex(
                        isRaw ? 1 : 0,
                        new IntPtr(inputPtr),
                        new IntPtr(outputPtr),
                        (nuint)input.Length,
                        ref decoderState);
                    state = decoderState;
                }
                else
                {
                    decodedLength = NativeMethods.rapidyenc_decode_ex_without_state(
                        isRaw ? 1 : 0,
                        new IntPtr(inputPtr),
                        new IntPtr(outputPtr),
                        (nuint)input.Length,
                        IntPtr.Zero);
                }

                if (decodedLength > (nuint)input.Length)
                    throw new InvalidOperationException("Decoded length exceeds input length");

                return (int)decodedLength;
            }
        }
    }

    /// <summary>
    /// Decodes yEnc encoded data with optional NNTP dot unstuffing and state tracking, returning a new array
    /// </summary>
    /// <param name="input">The yEnc encoded data to decode</param>
    /// <param name="state">The decoder state for incremental decoding. Pass null if not tracking state.</param>
    /// <param name="isRaw">If true, will also handle NNTP dot unstuffing</param>
    /// <returns>The decoded data</returns>
    public static byte[] DecodeEx(ReadOnlySpan<byte> input, ref RapidYencDecoderState? state, bool isRaw = true)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return Array.Empty<byte>();

        // Use ArrayPool for temporary buffer to reduce allocations
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(input.Length);
        try
        {
            int bytesWritten = DecodeEx(input, pooledBuffer, ref state, isRaw);

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
    /// Decodes yEnc data incrementally, stopping when a yEnc/NNTP end sequence is found
    /// </summary>
    /// <param name="input">The yEnc encoded data to decode</param>
    /// <param name="output">The output buffer to write decoded data to. Must be at least as large as input.</param>
    /// <param name="state">The decoder state for incremental decoding</param>
    /// <param name="endState">The end state indicating if and what kind of end sequence was found</param>
    /// <returns>The number of bytes written to the output buffer</returns>
    /// <exception cref="ArgumentException">Thrown when output buffer is too small</exception>
    public static int DecodeIncremental(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        ref RapidYencDecoderState state,
        out RapidYencDecoderEnd endState)
    {
        EnsureInitialized();

        endState = RapidYencDecoderEnd.RYDEC_END_NONE;

        if (input.IsEmpty)
            return 0;

        if (output.Length < input.Length)
            throw new ArgumentException($"Output buffer too small. Required: {input.Length}, Provided: {output.Length}",
                nameof(output));

        unsafe
        {
            fixed (byte* inputPtr = input)
            fixed (byte* outputPtr = output)
            {
                IntPtr srcPtr = new IntPtr(inputPtr);
                IntPtr destPtr = new IntPtr(outputPtr);

                endState = NativeMethods.rapidyenc_decode_incremental(
                    ref srcPtr,
                    ref destPtr,
                    (nuint)input.Length,
                    ref state);

                // Calculate decoded length
                nuint decodedLength = (nuint)(destPtr.ToInt64() - new IntPtr(outputPtr).ToInt64());

                if (decodedLength > (nuint)input.Length)
                    throw new InvalidOperationException("Decoded length exceeds input length");

                return (int)decodedLength;
            }
        }
    }

    /// <summary>
    /// Decodes yEnc data incrementally, stopping when a yEnc/NNTP end sequence is found, returning a new array
    /// </summary>
    /// <param name="input">The yEnc encoded data to decode</param>
    /// <param name="state">The decoder state for incremental decoding</param>
    /// <returns>A tuple containing the decoded data and the end state</returns>
    public static (byte[] DecodedData, RapidYencDecoderEnd EndState) DecodeIncremental(
        ReadOnlySpan<byte> input,
        ref RapidYencDecoderState state)
    {
        EnsureInitialized();

        if (input.IsEmpty)
            return (Array.Empty<byte>(), RapidYencDecoderEnd.RYDEC_END_NONE);

        // Use ArrayPool for temporary buffer to reduce allocations
        byte[] pooledBuffer = ArrayPool<byte>.Shared.Rent(input.Length);
        try
        {
            int bytesWritten = DecodeIncremental(input, pooledBuffer, ref state, out RapidYencDecoderEnd endState);

            // Only allocate the exact size needed
            byte[] result = new byte[bytesWritten];
            pooledBuffer.AsSpan(0, bytesWritten).CopyTo(result);
            return (result, endState);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }
}
