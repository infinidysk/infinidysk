namespace RapidYencSharp.Tests;

public sealed class Crc32Tests
{
    [Test]
    public void ComputeMatchesStandardCheckVector()
    {
        Assert.That(Crc32.Compute("123456789"u8), Is.EqualTo(0xCBF43926u));
    }

    [Test]
    public void EmptyInputReturnsInitialCrc()
    {
        Assert.That(Crc32.Compute([], 0x12345678u), Is.EqualTo(0x12345678u));
    }

    [Test]
    public void IncrementalComputeMatchesSinglePass()
    {
        byte[] first = "first chunk"u8.ToArray();
        byte[] second = "second chunk"u8.ToArray();
        byte[] combined = [.. first, .. second];

        uint firstCrc = Crc32.Compute(first);
        uint incrementalCrc = Crc32.Compute(second, firstCrc);

        Assert.That(incrementalCrc, Is.EqualTo(Crc32.Compute(combined)));
    }

    [Test]
    public void CombineMatchesConcatenatedData()
    {
        byte[] first = "first chunk"u8.ToArray();
        byte[] second = "second chunk"u8.ToArray();
        byte[] combined = [.. first, .. second];

        uint actual = Crc32.Combine(Crc32.Compute(first), Crc32.Compute(second), (ulong)second.Length);

        Assert.That(actual, Is.EqualTo(Crc32.Compute(combined)));
    }

    [TestCase(0UL)]
    [TestCase(1UL)]
    [TestCase(4096UL)]
    [TestCase(1_000_000UL)]
    public void UnzeroReversesComputeZeros(ulong length)
    {
        const uint initial = 0x89ABCDEFu;

        uint withZeros = Crc32.ComputeZeros(initial, length);

        Assert.That(Crc32.Unzero(withZeros, length), Is.EqualTo(initial));
    }

    [TestCase(0UL)]
    [TestCase(1UL)]
    [TestCase(17UL)]
    [TestCase(1024UL)]
    public void PowerOf256MatchesPowerOf2(ulong exponent)
    {
        Assert.That(Crc32.PowerOf256(exponent), Is.EqualTo(Crc32.PowerOf2(checked((long)exponent * 8))));
    }
}
