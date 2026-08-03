using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AwesomeAssertions;
using SharpCompress.Crypto;
using Xunit;

namespace SharpCompress.Test.Crypto;

[SuppressMessage(
    "Security",
    "CA5350:Do Not Use Weak Cryptographic Algorithms",
    Justification = "Tests compare against SHA-1 which RAR3 mandates."
)]
public class CopyableSha1Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1_000_000)]
    public void FinalizeTo_MatchesSystemSha1(int length)
    {
        var data = new byte[length];
        if (length > 0)
        {
            var rng = new Random(length);
            rng.NextBytes(data);
        }

        var expected = SHA1.HashData(data);

        var sha = CopyableSha1.Create();
        sha.Append(data);
        Span<byte> actual = stackalloc byte[20];
        sha.FinalizeTo(actual);

        actual.ToArray().Should().Equal(expected);
    }

    [Fact]
    public void CheckpointCopy_DoesNotMutateRunningState()
    {
        var data = new byte[100];
        new Random(42).NextBytes(data);

        var sha = CopyableSha1.Create();
        sha.Append(data.AsSpan(0, 40));

        var checkpoint = sha;
        Span<byte> checkpointDigest = stackalloc byte[20];
        checkpoint.FinalizeTo(checkpointDigest);

        sha.Append(data.AsSpan(40));
        Span<byte> fullDigest = stackalloc byte[20];
        sha.FinalizeTo(fullDigest);

        var expectedCheckpoint = SHA1.HashData(data.AsSpan(0, 40));
        var expectedFull = SHA1.HashData(data);

        checkpointDigest.ToArray().Should().Equal(expectedCheckpoint);
        fullDigest.ToArray().Should().Equal(expectedFull);
    }
}
