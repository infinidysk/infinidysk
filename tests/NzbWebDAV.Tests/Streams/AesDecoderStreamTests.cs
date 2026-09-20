using System.Security.Cryptography;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class AesDecoderStreamTests
{
    [Fact]
    public async Task NativeReadBudget_IncludesThePreviousCipherBlockUsedForSeekIv()
    {
        const int block = 4 * 1024 * 1024;
        var plaintext = Enumerable.Range(0, 2 * block).Select(index => (byte)(index % 251)).ToArray();
        var (ciphertext, parameters) = Encrypt(plaintext);
        var ranges = new LongRange[] { new(0, block - 16), new(block - 16, block),
            new(block, 2L * block - 16), new(2L * block - 16, 2L * block) };
        var ids = new[] { "head", "iv", "body", "tail" };
        using var client = new NzbWebDAV.Tests.Fakes.FakeNntpClient(ids.Select((id, index) =>
                (id, bytes: ciphertext[(int)ranges[index].StartInclusive..(int)ranges[index].EndExclusive]))
            .ToDictionary(item => item.id, item => item.bytes), useCachedYencStreams: true,
            segmentRanges: ids.Select((id, index) => (id, range: ranges[index]))
                .ToDictionary(item => item.id, item => item.range));
        await using var source = new NzbFileStream(ids, ciphertext.Length, client, 4, ranges,
            segmentByteRangesTrusted: true);
        await using var stream = new AesDecoderStream(source, parameters);
        using var native = new NativeCacheReadContext();
        stream.Position = block;
        var result = new byte[block];
        await stream.ReadExactlyAsync(result);
        Assert.Equal(plaintext.AsSpan(block).ToArray(), result);
        Assert.True(Assert.IsAssignableFrom<ICacheReadEvidence>(stream).LastReadCacheable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CacheEvidence_UnsafeCiphertextAndSeekIvRemainTainted(bool unsafeFirstRead, bool seek)
    {
        var plaintext = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        var (ciphertext, parameters) = Encrypt(plaintext);
        await using var stream = new AesDecoderStream(new EvidenceCiphertextStream(ciphertext, unsafeFirstRead), parameters);
        if (seek) stream.Seek(17, SeekOrigin.Begin);
        var evidence = Assert.IsAssignableFrom<ICacheReadEvidence>(stream);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Equal(!unsafeFirstRead, evidence.LastReadCacheable);
        // This read may return already-decrypted bytes without reading any ciphertext.
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Equal(!unsafeFirstRead, evidence.LastReadCacheable);
        stream.Seek(32, SeekOrigin.Begin);
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        Assert.Equal(!unsafeFirstRead, evidence.LastReadCacheable);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.False(evidence.LastReadCacheable);
    }

    private sealed class EvidenceCiphertextStream(byte[] bytes, bool unsafeFirstRead)
        : MemoryStream(bytes), ICacheReadEvidence
    {
        private int _reads;
        public bool LastReadCacheable { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            LastReadCacheable = false;
            // Force a mixed-evidence IV/block assembly, rather than only testing the last read.
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], ct);
            LastReadCacheable = read > 0 && !(unsafeFirstRead && _reads++ == 0);
            return read;
        }
    }

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
