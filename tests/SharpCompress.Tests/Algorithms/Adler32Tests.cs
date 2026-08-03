using System;
using System.Runtime.Intrinsics.Arm;
using SharpCompress.Algorithms;
using Xunit;

namespace SharpCompress.Test.Algorithms;

public class Adler32Tests
{
    public static TheoryData<uint, int> LengthCases =>
        new()
        {
            { 1U, 0 },
            { 1U, 1 },
            { 1U, 15 },
            { 1U, 16 },
            { 1U, 17 },
            { 1U, 5552 },
            { 1U, 5553 },
            { 0x1234_5678U, 1024 },
        };

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void Calculate_MatchesScalarReference(uint seed, int length)
    {
        var data = CreateTestData(length);
        var expected = Adler32.CalculateScalarReference(seed, data);
        var actual = Adler32.Calculate(seed, data);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Calculate_OneMegabyteRandom_MatchesScalarReference()
    {
        var data = CreateTestData(1_048_576);
        var expected = Adler32.CalculateScalarReference(Adler32.SeedValue, data);
        var actual = Adler32.Calculate(Adler32.SeedValue, data);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Calculate_EmptyBuffer_ReturnsSeed()
    {
        Assert.Equal(Adler32.SeedValue, Adler32.Calculate(ReadOnlySpan<byte>.Empty));
        Assert.Equal(42U, Adler32.Calculate(42U, ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [MemberData(nameof(LengthCases))]
    public void CalculateAdvSimd_MatchesScalarReference_WhenHardwareAvailable(uint seed, int length)
    {
        if (!AdvSimd.IsSupported)
        {
            return;
        }

        var data = CreateTestData(length);
        var expected = Adler32.CalculateScalarReference(seed, data);
        var actual = Adler32.CalculateAdvSimdReference(seed, data);

        Assert.Equal(expected, actual);
    }

    private static byte[] CreateTestData(int length)
    {
        var data = new byte[length];
        var state = 0x9E3779B9U;

        for (var i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            data[i] = (byte)state;
        }

        return data;
    }
}
