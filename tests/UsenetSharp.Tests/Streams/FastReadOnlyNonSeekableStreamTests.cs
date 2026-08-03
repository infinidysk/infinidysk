using UsenetSharp.Streams;

namespace UsenetSharpTest.Streams;

[TestFixture]
public class FastReadOnlyNonSeekableStreamTests
{
    private class TestFastReadOnlyStream : FastReadOnlyNonSeekableStream
    {
        private readonly byte[] _data;
        private int _position;

        public TestFastReadOnlyStream(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            // Simulate async operation
            await Task.Yield();

            var bytesToRead = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, bytesToRead).CopyTo(buffer.Span);
            _position += bytesToRead;
            return bytesToRead;
        }
    }

    [Test]
    public void CanRead_ReturnsTrue()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Assert
        Assert.That(stream.CanRead, Is.True);
    }

    [Test]
    public void CanWrite_ReturnsFalse()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Assert
        Assert.That(stream.CanWrite, Is.False);
    }

    [Test]
    public void CanSeek_ReturnsFalse()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Assert
        Assert.That(stream.CanSeek, Is.False);
    }

    [Test]
    public void Length_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() =>
        {
            var _ = stream.Length;
        });
    }

    [Test]
    public void Position_Get_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() =>
        {
            var _ = stream.Position;
        });
    }

    [Test]
    public void Position_Set_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
    }

    [Test]
    public void Seek_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Test]
    public async Task ReadAsync_Memory_ReadsDataCorrectly()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var stream = new TestFastReadOnlyStream(data);
        var buffer = new byte[3];

        // Act
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(3));
        Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task ReadAsync_Memory_ReadsMultipleTimes()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var stream = new TestFastReadOnlyStream(data);
        var buffer1 = new byte[2];
        var buffer2 = new byte[3];

        // Act
        var bytesRead1 = await stream.ReadAsync(buffer1.AsMemory(), CancellationToken.None);
        var bytesRead2 = await stream.ReadAsync(buffer2.AsMemory(), CancellationToken.None);

        // Assert
        Assert.That(bytesRead1, Is.EqualTo(2));
        Assert.That(buffer1, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(bytesRead2, Is.EqualTo(3));
        Assert.That(buffer2, Is.EqualTo(new byte[] { 3, 4, 5 }));
    }

    [Test]
    public async Task ReadAsync_Memory_ReturnsZeroAtEndOfStream()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };
        var stream = new TestFastReadOnlyStream(data);
        var buffer = new byte[10];

        // Act
        await stream.ReadAsync(buffer.AsMemory(0, 3), CancellationToken.None);
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public void Read_ByteArray_DelegatesToReadAsync()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var stream = new TestFastReadOnlyStream(data);
        var buffer = new byte[3];

        // Act
        var bytesRead = stream.Read(buffer, 0, 3);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(3));
        Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void Read_Span_DelegatesToReadAsync()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var stream = new TestFastReadOnlyStream(data);
        Span<byte> buffer = stackalloc byte[3];

        // Act
        var bytesRead = stream.Read(buffer);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(3));
        Assert.That(buffer.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task ReadAsync_ByteArray_DelegatesToReadAsyncMemory()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var stream = new TestFastReadOnlyStream(data);
        var buffer = new byte[3];

        // Act
        var bytesRead = await stream.ReadAsync(buffer, 0, 3, CancellationToken.None);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(3));
        Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void ReadByte_DelegatesToRead()
    {
        // Arrange
        var data = new byte[] { 42, 43, 44 };
        var stream = new TestFastReadOnlyStream(data);

        // Act
        var byte1 = stream.ReadByte();
        var byte2 = stream.ReadByte();
        var byte3 = stream.ReadByte();
        var byte4 = stream.ReadByte();

        // Assert
        Assert.That(byte1, Is.EqualTo(42));
        Assert.That(byte2, Is.EqualTo(43));
        Assert.That(byte3, Is.EqualTo(44));
        Assert.That(byte4, Is.EqualTo(-1)); // End of stream
    }

    [Test]
    public void Read_Span_HandlesZeroLengthBuffer()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };
        var stream = new TestFastReadOnlyStream(data);
        Span<byte> buffer = Span<byte>.Empty;

        // Act
        var bytesRead = stream.Read(buffer);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public async Task ReadAsync_Memory_HandlesZeroLengthBuffer()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3 };
        var stream = new TestFastReadOnlyStream(data);

        // Act
        var bytesRead = await stream.ReadAsync(Memory<byte>.Empty, CancellationToken.None);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(0));
    }

    [Test]
    public void Read_ConsecutiveCalls_MaintainPosition()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        var stream = new TestFastReadOnlyStream(data);
        var buffer1 = new byte[2];
        var buffer2 = new byte[2];
        var buffer3 = new byte[2];

        // Act
        var read1 = stream.Read(buffer1, 0, 2);
        var read2 = stream.Read(buffer2, 0, 2);
        var read3 = stream.Read(buffer3, 0, 2);

        // Assert
        Assert.That(read1, Is.EqualTo(2));
        Assert.That(buffer1, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(read2, Is.EqualTo(2));
        Assert.That(buffer2, Is.EqualTo(new byte[] { 3, 4 }));
        Assert.That(read3, Is.EqualTo(2));
        Assert.That(buffer3, Is.EqualTo(new byte[] { 5, 6 }));
    }

    [Test]
    public void Write_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Write(buffer, 0, 5));
    }

    [Test]
    public void SetLength_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestFastReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
    }
}
