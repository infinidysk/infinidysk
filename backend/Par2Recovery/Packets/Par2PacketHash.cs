using System.Security.Cryptography;

namespace NzbWebDAV.Par2Recovery.Packets;

#pragma warning disable CA5351 // PAR 2.0 packet integrity uses MD5 per spec

/// <summary>
/// PAR 2.0 packet MD5: hash of Recovery Set ID + packet type + body (bytes 32..packet end).
/// Matches par2-rs / par2cmdline wire format (no separate length suffix).
/// </summary>
internal static class Par2PacketHash
{
    public static bool Verify(byte[] packetBytes, ReadOnlySpan<byte> expectedHash)
    {
        if (expectedHash.Length != 16) return false;
        var computed = Compute(packetBytes);
        return computed.AsSpan().SequenceEqual(expectedHash);
    }

    public static byte[] Compute(byte[] packetBytes)
    {
        const int hashStart = 32;
        if (packetBytes.Length < hashStart)
            throw new InvalidDataException("PAR2 packet too short for hash verification.");

        return MD5.HashData(packetBytes.AsSpan(hashStart));
    }

    public static void WriteHash(byte[] packetBytes)
    {
        var hash = Compute(packetBytes);
        hash.CopyTo(packetBytes, 16);
    }
}

#pragma warning restore CA5351
