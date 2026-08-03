using System;
using System.Security.Cryptography;

namespace SharpCompress.Crypto;

/// <summary>
/// Holds at most one AES block of leftover plaintext between decrypt reads.
/// </summary>
internal struct RarAesDecryptCarry
{
    private readonly byte[] _carry;
    private int _carryLength;
    private int _carryOffset;

    public RarAesDecryptCarry()
    {
        _carry = new byte[16];
        _carryLength = 0;
        _carryOffset = 0;
    }

    public int Count => _carryLength - _carryOffset;

    public void Clear()
    {
        if (_carryLength > 0)
        {
            CryptographicOperations.ZeroMemory(_carry.AsSpan(0, _carryLength));
        }

        _carryLength = 0;
        _carryOffset = 0;
    }

    public int Read(Span<byte> destination)
    {
        var available = Count;
        if (available == 0 || destination.IsEmpty)
        {
            return 0;
        }

        var toCopy = Math.Min(destination.Length, available);
        _carry.AsSpan(_carryOffset, toCopy).CopyTo(destination);
        _carryOffset += toCopy;
        if (_carryOffset == _carryLength)
        {
            Clear();
        }

        return toCopy;
    }

    public void Store(ReadOnlySpan<byte> decryptedOvershoot)
    {
        if (decryptedOvershoot.Length > _carry.Length)
        {
            throw new ArgumentException(
                "AES decrypt carry can hold at most one block.",
                nameof(decryptedOvershoot)
            );
        }

        decryptedOvershoot.CopyTo(_carry);
        _carryLength = decryptedOvershoot.Length;
        _carryOffset = 0;
    }
}
