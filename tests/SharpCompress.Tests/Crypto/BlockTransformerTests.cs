using System;
using System.Security.Cryptography;
using AwesomeAssertions;
using SharpCompress.Crypto;
using Xunit;

namespace SharpCompress.Test.Crypto;

public class BlockTransformerTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(4096)]
    [InlineData(4096 + 16)]
    public void Process_Run_Matches_PerBlock_Loop(int length)
    {
        var key = new byte[16];
        var iv = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            key[i] = (byte)i;
            iv[i] = (byte)(16 - i);
        }

        var plain = new byte[length];
        new Random(42).NextBytes(plain);

        var cipher = Encrypt(plain, key, iv);
        var expected = DecryptPerBlock(cipher, key, iv);

        var actual = new byte[length];
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        using var transformer = new BlockTransformer(decryptor);
        transformer.Process(cipher, 0, length, actual, 0);

        actual.Should().Equal(expected);
        actual.Should().Equal(plain);
    }

    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        var cipher = new byte[plain.Length];
        encryptor.TransformBlock(plain, 0, plain.Length, cipher, 0);
        return cipher;
    }

    private static byte[] DecryptPerBlock(byte[] cipher, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var output = new byte[cipher.Length];
        for (var i = 0; i < cipher.Length; i += 16)
        {
            decryptor.TransformBlock(cipher, i, 16, output, i);
        }

        return output;
    }
}
