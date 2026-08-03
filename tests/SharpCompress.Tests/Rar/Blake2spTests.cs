using System;
using AwesomeAssertions;
using SharpCompress.Compressors.Rar;
using Xunit;

namespace SharpCompress.Test.Rar;

public class Blake2spTests
{
    // Digests from the BLAKE2sp implementation (also covered by RAR5 -htb archive tests).
    private const string EmptyDigest =
        "DD0E891776933F43C7D032B08A917E25741F8AA9A12C12E1CAC8801500F2CA4F";
    private const string AbcDigest =
        "70F75B58F1FECAB821DB43C88AD84EDDE5A52600616CD22517B7BB14D440A7D5";
    private const string OneMbSeed42Digest =
        "F5539CAC70C4C0BDD0964216FC925687A359DB5F22C2AA7C2DA1448544E17D03";

    [Fact]
    public void HashBlake2sp_Empty_HasExpectedDigest()
    {
        Convert
            .ToHexString(RarBLAKE2spStream.HashBlake2sp(ReadOnlySpan<byte>.Empty))
            .Should()
            .Be(EmptyDigest);
    }

    [Fact]
    public void HashBlake2sp_Abc_HasExpectedDigest()
    {
        Convert.ToHexString(RarBLAKE2spStream.HashBlake2sp("abc"u8)).Should().Be(AbcDigest);
    }

    [Fact]
    public void HashBlake2sp_OneMegabyteSeed42_HasExpectedDigest()
    {
        var data = new byte[1024 * 1024];
        new Random(42).NextBytes(data);
        Convert.ToHexString(RarBLAKE2spStream.HashBlake2sp(data)).Should().Be(OneMbSeed42Digest);
    }
}
