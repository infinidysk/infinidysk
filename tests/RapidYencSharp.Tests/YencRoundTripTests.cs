using System.Buffers;

namespace RapidYencSharp.Tests;

public sealed class YencRoundTripTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(127)]
    [TestCase(128)]
    [TestCase(8192)]
    [TestCase(1_048_576)]
    public void AllocatingApisRoundTripDeterministicData(int length)
    {
        byte[] input = CreateData(length);

        byte[] encoded = YencEncoder.Encode(input);
        byte[] decoded = YencDecoder.Decode(encoded);

        Assert.That(decoded, Is.EqualTo(input));
    }

    [TestCase(1)]
    [TestCase(128)]
    [TestCase(8192)]
    public void SpanApisRoundTripWithoutManagedOutputAllocation(int length)
    {
        byte[] input = CreateData(length);
        int maxEncodedLength = checked((int)YencEncoder.GetMaxEncodedLength((nuint)input.Length));
        byte[] encoded = ArrayPool<byte>.Shared.Rent(maxEncodedLength);
        byte[] decoded = ArrayPool<byte>.Shared.Rent(maxEncodedLength);

        try
        {
            int encodedLength = YencEncoder.Encode(input, encoded);
            int decodedLength = YencDecoder.Decode(encoded.AsSpan(0, encodedLength), decoded);

            Assert.That(decodedLength, Is.EqualTo(input.Length));
            Assert.That(decoded.AsSpan(0, decodedLength).ToArray(), Is.EqualTo(input));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encoded);
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(127)]
    public void IncrementalEncodingRoundTripsAcrossChunkBoundaries(int chunkSize)
    {
        byte[] input = CreateData(4096);
        using var encoded = new MemoryStream();
        int? column = 0;

        for (int offset = 0; offset < input.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, input.Length - offset);
            bool isEnd = offset + count == input.Length;
            int maxLength = checked((int)YencEncoder.GetMaxEncodedLength((nuint)count));
            byte[] output = new byte[maxLength];
            int written = YencEncoder.EncodeEx(
                input.AsSpan(offset, count),
                output,
                ref column,
                isEnd: isEnd);
            encoded.Write(output, 0, written);
        }

        byte[] decoded = YencDecoder.Decode(encoded.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(decoded, Is.EqualTo(input));
            Assert.That(column, Is.Not.Null);
        });
    }

    [TestCase(1)]
    [TestCase(31)]
    [TestCase(257)]
    public void IncrementalDecodingPreservesStateAcrossChunkBoundaries(int chunkSize)
    {
        byte[] input = CreateData(4096);
        byte[] encoded = YencEncoder.Encode(input);
        byte[] decoded = new byte[encoded.Length];
        RapidYencDecoderState? state = RapidYencDecoderState.RYDEC_STATE_CRLF;
        int totalWritten = 0;

        for (int offset = 0; offset < encoded.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, encoded.Length - offset);
            int written = YencDecoder.DecodeEx(
                encoded.AsSpan(offset, count),
                decoded.AsSpan(totalWritten),
                ref state,
                isRaw: false);
            totalWritten += written;
        }

        Assert.That(totalWritten, Is.EqualTo(input.Length));
        Assert.That(decoded.AsSpan(0, totalWritten).ToArray(), Is.EqualTo(input));
    }

    [Test]
    public void SpanApisRejectUndersizedDestinations()
    {
        byte[] input = CreateData(128);
        byte[] encoded = YencEncoder.Encode(input);

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => YencEncoder.Encode(input, new byte[1]));
            Assert.Throws<ArgumentException>(() => YencDecoder.Decode(encoded, new byte[encoded.Length - 1]));
        });
    }

    [TestCase(64)]
    [TestCase(128)]
    [TestCase(256)]
    public void SpanEncoderHonorsCustomLineSize(int lineSize)
    {
        byte[] input = CreateData(4096);
        int maxLength = checked((int)YencEncoder.GetMaxEncodedLength((nuint)input.Length, lineSize));
        byte[] output = new byte[maxLength];
        int? column = null;

        int actualLength = YencEncoder.Encode(input, output, lineSize);
        byte[] expected = YencEncoder.EncodeEx(input, ref column, lineSize);

        Assert.That(output.AsSpan(0, actualLength).ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void EncoderRejectsInvalidLineSizesAndOverlappingBuffers()
    {
        byte[] buffer = CreateData(512);

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => YencEncoder.GetMaxEncodedLength(1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => YencEncoder.Encode(buffer, new byte[1024], -1));
            Assert.Throws<ArgumentException>(() => YencEncoder.Encode(buffer.AsSpan(0, 128), buffer));
        });
    }

    [Test]
    public void ConcurrentFirstUseIsSafe()
    {
        byte[] input = CreateData(1024);

        Assert.DoesNotThrow(() => Parallel.For(0, 100, _ =>
        {
            byte[] encoded = YencEncoder.Encode(input);
            byte[] decoded = YencDecoder.Decode(encoded);
            uint crc = Crc32.Compute(decoded);

            if (!decoded.AsSpan().SequenceEqual(input) || crc == 0)
                throw new InvalidOperationException("Concurrent native operation produced invalid output.");
        }));
    }

    private static byte[] CreateData(int length)
    {
        var random = new Random(0x5EED + length);
        byte[] data = new byte[length];
        random.NextBytes(data);
        return data;
    }
}
