using System.Security.Cryptography;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class AesDecoderStreamTests
{
    [Fact]
    public async Task ReadAsync_DecryptsAndHonorsDecodedLength()
    {
        var plaintext = Enumerable.Range(0, 37).Select(index => (byte)index).ToArray();
        var (ciphertext, parameters) = Encrypt(plaintext);
        await using var stream = new AesDecoderStream(
            new MemoryStream(ciphertext), parameters);

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        Assert.Equal(plaintext, destination.ToArray());
        Assert.Equal(plaintext.Length, stream.Position);
    }

    [Fact]
    public void Read_RejectsSynchronousWebDavReads()
    {
        var plaintext = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
        var (ciphertext, parameters) = Encrypt(plaintext);
        using var stream = new AesDecoderStream(
            new MemoryStream(ciphertext), parameters);

        Assert.Throws<NotSupportedException>(
            () => stream.Read(new byte[16], 0, 16));
        Assert.Throws<NotSupportedException>(
            () => stream.Read(new Span<byte>(new byte[16])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(48)]
    public async Task Seek_DecryptsFromArbitraryByteOffset(int offset)
    {
        var plaintext = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var (ciphertext, parameters) = Encrypt(plaintext);
        await using var stream = new AesDecoderStream(
            new MemoryStream(ciphertext), parameters);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(9, plaintext.Length - offset)];

        var read = await stream.ReadAsync(buffer);

        Assert.Equal(plaintext.AsSpan(offset, read).ToArray(), buffer[..read]);
    }

    [Fact]
    public void Constructor_RejectsUnalignedCiphertext()
    {
        var parameters = new AesParams
        {
            Key = new byte[32],
            Iv = new byte[16],
            DecodedSize = 1
        };

        Assert.Throws<NotSupportedException>(
            () => new AesDecoderStream(new MemoryStream(new byte[15]), parameters));
    }

    [Fact]
    public async Task ReadAsync_ReportsPositionWhenCiphertextEndsMidBlock()
    {
        var parameters = new AesParams
        {
            Key = new byte[32],
            Iv = new byte[16],
            DecodedSize = 16
        };
        await using var stream = new AesDecoderStream(
            new DeclaredLengthStream(new byte[15], 16), parameters);

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await stream.ReadExactlyAsync(new byte[16]));

        Assert.Contains("after decoding 0 of 16 bytes", exception.Message);
        Assert.Contains("read 15 ciphertext bytes", exception.Message);
        Assert.Contains("partial block of 15 bytes", exception.Message);
    }

    private static (byte[] Ciphertext, AesParams Parameters) Encrypt(byte[] plaintext)
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var iv = Enumerable.Range(32, 16).Select(index => (byte)index).ToArray();
        var paddedLength = (plaintext.Length + 15) / 16 * 16;
        var padded = new byte[paddedLength];
        plaintext.CopyTo(padded, 0);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key, iv);
        var ciphertext = encryptor.TransformFinalBlock(padded, 0, padded.Length);
        return (ciphertext, new AesParams
        {
            Key = key,
            Iv = iv,
            DecodedSize = plaintext.Length
        });
    }

    private sealed class DeclaredLengthStream(byte[] content, long declaredLength) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => declaredLength;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
