using System;
using System.Security.Cryptography;

namespace SharpCompress.Crypto;

internal sealed class BlockTransformer : IDisposable
{
    private readonly ICryptoTransform _transformer;

    public BlockTransformer(ICryptoTransform transformer) => _transformer = transformer;

    /// <summary>
    /// Decrypts a run of ciphertext whose length is a multiple of the AES block size (16).
    /// CBC chaining state is preserved across calls via <see cref="ICryptoTransform"/>.
    /// </summary>
    public void Process(byte[] input, int inputOffset, int count, byte[] output, int outputOffset)
    {
        if (count == 0)
        {
            return;
        }

        _transformer.TransformBlock(input, inputOffset, count, output, outputOffset);
    }

    public void Dispose() => _transformer.Dispose();
}
