using System;
using System.Buffers;
using System.Text;

namespace RapidYencSharp;

/// <summary>
/// Example usage of RapidYencSharp
/// </summary>
public static class Examples
{
    /// <summary>
    /// Example: Basic encoding and decoding
    /// </summary>
    public static void BasicEncodeDecode()
    {
        // Encode some data
        byte[] original = Encoding.UTF8.GetBytes("Hello, World! This is a test.");
        byte[] encoded = YencEncoder.Encode(original);

        Console.WriteLine($"Original: {Encoding.UTF8.GetString(original)}");
        Console.WriteLine($"Encoded length: {encoded.Length} bytes");

        // Decode it back
        byte[] decoded = YencDecoder.Decode(encoded);
        Console.WriteLine($"Decoded: {Encoding.UTF8.GetString(decoded)}");
        Console.WriteLine($"Match: {original.SequenceEqual(decoded)}");
    }

    /// <summary>
    /// Example: Incremental encoding with column tracking
    /// </summary>
    public static void IncrementalEncode()
    {
        byte[] data = Encoding.UTF8.GetBytes("This is a longer piece of data that will be encoded incrementally.");

        // Split into chunks
        int chunkSize = 20;
        int? column = 0;

        for (int i = 0; i < data.Length; i += chunkSize)
        {
            int remaining = Math.Min(chunkSize, data.Length - i);
            ReadOnlySpan<byte> chunk = data.AsSpan(i, remaining);
            bool isLast = (i + chunkSize >= data.Length);

            byte[] encoded = YencEncoder.EncodeEx(chunk, ref column, lineSize: 128, isEnd: isLast);
            Console.WriteLine($"Chunk {i / chunkSize + 1}: {encoded.Length} bytes, column: {column}");
        }
    }

    /// <summary>
    /// Example: CRC32 computation
    /// </summary>
    public static void Crc32Example()
    {
        byte[] data1 = Encoding.UTF8.GetBytes("First part");
        byte[] data2 = Encoding.UTF8.GetBytes("Second part");

        // Compute CRC32 for each part
        uint crc1 = Crc32.Compute(data1);
        uint crc2 = Crc32.Compute(data2);

        Console.WriteLine($"CRC32 of part 1: 0x{crc1:X8}");
        Console.WriteLine($"CRC32 of part 2: 0x{crc2:X8}");

        // Combine them
        uint combined = Crc32.Combine(crc1, crc2, (ulong)data2.Length);
        Console.WriteLine($"Combined CRC32: 0x{combined:X8}");

        // Verify it matches computing on the full data
        byte[] fullData = new byte[data1.Length + data2.Length];
        Array.Copy(data1, fullData, data1.Length);
        Array.Copy(data2, 0, fullData, data1.Length, data2.Length);
        uint fullCrc = Crc32.Compute(fullData);
        Console.WriteLine($"Full data CRC32: 0x{fullCrc:X8}");
        Console.WriteLine($"Match: {combined == fullCrc}");
    }

    /// <summary>
    /// Example: Version and kernel information
    /// </summary>
    public static void VersionInfo()
    {
        int version = Version.GetVersion();
        Console.WriteLine($"rapidyenc version: {Version.Major}.{Version.Minor}.{Version.Patch} (0x{version:X6})");
        Console.WriteLine($"Encode kernel: 0x{YencEncoder.Kernel:X}");
        Console.WriteLine($"Decode kernel: 0x{YencDecoder.Kernel:X}");
        Console.WriteLine($"CRC32 kernel: 0x{Crc32.Kernel:X}");
    }

    /// <summary>
    /// Example: Zero-allocation encoding using stackalloc and Span
    /// This is the most performant way to encode data when you can allocate on the stack
    /// </summary>
    public static void ZeroAllocationEncode()
    {
        ReadOnlySpan<byte> data = "Hello, World!"u8;

        // Calculate the maximum encoded size
        nuint maxEncodedSize = YencEncoder.GetMaxEncodedLength((nuint)data.Length);

        // Use stackalloc for small data (< ~1KB) or ArrayPool for larger data
        Span<byte> encodedBuffer = stackalloc byte[(int)maxEncodedSize];

        int bytesWritten = YencEncoder.Encode(data, encodedBuffer);
        Console.WriteLine($"Encoded {data.Length} bytes to {bytesWritten} bytes (zero allocations)");

        // Now decode it back using zero allocations
        Span<byte> decodedBuffer = stackalloc byte[data.Length];

        int decodedBytes = YencDecoder.Decode(encodedBuffer.Slice(0, bytesWritten), decodedBuffer);
        Console.WriteLine($"Decoded {bytesWritten} bytes to {decodedBytes} bytes (zero allocations)");
        Console.WriteLine($"Match: {data.SequenceEqual(decodedBuffer.Slice(0, decodedBytes))}");
    }

    /// <summary>
    /// Example: High-performance encoding using ArrayPool for larger data
    /// This avoids allocations on the managed heap while handling larger data sizes
    /// </summary>
    public static void HighPerformanceEncode()
    {
        // Simulate larger data
        byte[] largeData = new byte[10_000];
        Random.Shared.NextBytes(largeData);

        // Calculate the maximum encoded size
        nuint maxEncodedSize = YencEncoder.GetMaxEncodedLength((nuint)largeData.Length);

        // Rent from ArrayPool to avoid GC pressure
        byte[] encodedBuffer = ArrayPool<byte>.Shared.Rent((int)maxEncodedSize);
        byte[] decodedBuffer = ArrayPool<byte>.Shared.Rent(largeData.Length);

        try
        {
            // Encode directly into pooled buffer
            int bytesWritten = YencEncoder.Encode(largeData, encodedBuffer);
            Console.WriteLine($"Encoded {largeData.Length} bytes to {bytesWritten} bytes");

            // Decode directly into pooled buffer
            int decodedBytes = YencDecoder.Decode(encodedBuffer.AsSpan(0, bytesWritten), decodedBuffer);
            Console.WriteLine($"Decoded {bytesWritten} bytes to {decodedBytes} bytes");
            Console.WriteLine($"Match: {largeData.AsSpan().SequenceEqual(decodedBuffer.AsSpan(0, decodedBytes))}");
        }
        finally
        {
            // Always return buffers to the pool
            ArrayPool<byte>.Shared.Return(encodedBuffer);
            ArrayPool<byte>.Shared.Return(decodedBuffer);
        }
    }

    /// <summary>
    /// Example: Incremental encoding with zero-allocation using EncodeEx
    /// </summary>
    public static void IncrementalEncodeOptimized()
    {
        ReadOnlySpan<byte> data =
            "This is a longer piece of data that will be encoded incrementally with zero allocations."u8;

        // Calculate max size for the entire output
        nuint maxEncodedSize = YencEncoder.GetMaxEncodedLength((nuint)data.Length, lineSize: 128);

        // Rent a buffer from ArrayPool for the output
        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent((int)maxEncodedSize);

        try
        {
            int totalBytesWritten = 0;
            int chunkSize = 20;
            int? column = 0;

            for (int i = 0; i < data.Length; i += chunkSize)
            {
                int remaining = Math.Min(chunkSize, data.Length - i);
                ReadOnlySpan<byte> chunk = data.Slice(i, remaining);
                bool isLast = (i + chunkSize >= data.Length);

                // Get the remaining space in the output buffer
                Span<byte> outputSlice = outputBuffer.AsSpan(totalBytesWritten);

                int bytesWritten = YencEncoder.EncodeEx(chunk, outputSlice, ref column, lineSize: 128, isEnd: isLast);
                totalBytesWritten += bytesWritten;
                Console.WriteLine($"Chunk {i / chunkSize + 1}: {bytesWritten} bytes written, column: {column}");
            }

            Console.WriteLine($"Total encoded: {totalBytesWritten} bytes with zero heap allocations during encoding");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }
    }

    /// <summary>
    /// Example: Processing streaming data with minimal allocations
    /// </summary>
    public static void StreamingExample()
    {
        // Simulate reading data in chunks (e.g., from a network stream)
        byte[] sourceData = new byte[1000];
        Random.Shared.NextBytes(sourceData);

        // Rent buffers from pool
        byte[] encodeBuffer =
            ArrayPool<byte>.Shared.Rent((int)YencEncoder.GetMaxEncodedLength((nuint)sourceData.Length));
        byte[] decodeBuffer = ArrayPool<byte>.Shared.Rent(sourceData.Length);

        try
        {
            // Encode
            int encodedLength = YencEncoder.Encode(sourceData, encodeBuffer);
            Console.WriteLine($"Encoded {sourceData.Length} bytes to {encodedLength} bytes");

            // Simulate incremental decoding with state tracking
            RapidYencDecoderState? state = RapidYencDecoderState.RYDEC_STATE_CRLF;
            int totalDecoded = 0;
            int chunkSize = 100;

            for (int i = 0; i < encodedLength; i += chunkSize)
            {
                int remaining = Math.Min(chunkSize, encodedLength - i);
                ReadOnlySpan<byte> encodedChunk = encodeBuffer.AsSpan(i, remaining);
                Span<byte> decodeSlice = decodeBuffer.AsSpan(totalDecoded);

                int bytesWritten = YencDecoder.DecodeEx(encodedChunk, decodeSlice, ref state, isRaw: true);
                totalDecoded += bytesWritten;
            }

            Console.WriteLine($"Decoded {encodedLength} bytes to {totalDecoded} bytes incrementally");
            Console.WriteLine($"Match: {sourceData.AsSpan().SequenceEqual(decodeBuffer.AsSpan(0, totalDecoded))}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encodeBuffer);
            ArrayPool<byte>.Shared.Return(decodeBuffer);
        }
    }
}
