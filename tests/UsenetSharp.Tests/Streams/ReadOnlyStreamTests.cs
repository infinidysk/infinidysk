using UsenetSharp.Streams;

namespace UsenetSharpTest.Streams;

[TestFixture]
public class ReadOnlyStreamTests
{
    private class TestReadOnlyStream : ReadOnlyStream
    {
        private readonly byte[] _data;
        private int _position;

        public TestReadOnlyStream(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public override bool CanSeek => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => _position = (int)value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesToRead = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, bytesToRead);
            _position += bytesToRead;
            return bytesToRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }
    }

    [Test]
    public void CanRead_ReturnsTrue()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });

        // Assert
        Assert.That(stream.CanRead, Is.True);
    }

    [Test]
    public void CanWrite_ReturnsFalse()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });

        // Assert
        Assert.That(stream.CanWrite, Is.False);
    }

    [Test]
    public void Write_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Write(buffer, 0, 5));
    }

    [Test]
    public void WriteSpan_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.Write(buffer.AsSpan()));
    }

    [Test]
    public async Task WriteAsync_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        // Act & Assert
        Assert.ThrowsAsync<NotSupportedException>(
            async () => await stream.WriteAsync(buffer, 0, 5, CancellationToken.None));
    }

    [Test]
    public async Task WriteAsyncMemory_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });
        var buffer = new byte[10];

        // Act & Assert
        var exception = Assert.ThrowsAsync<NotSupportedException>(
            async () => await stream.WriteAsync(buffer.AsMemory(), CancellationToken.None));
        Assert.That(exception, Is.Not.Null);
    }

    [Test]
    public void WriteByte_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(42));
    }

    [Test]
    public void SetLength_ThrowsNotSupportedException()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
    }

    [Test]
    public void Flush_DoesNotThrow()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.DoesNotThrow(() => stream.Flush());
    }

    [Test]
    public async Task FlushAsync_DoesNotThrow()
    {
        // Arrange
        var stream = new TestReadOnlyStream(new byte[] { 1, 2, 3 });

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await stream.FlushAsync(CancellationToken.None));
    }

    [Test]
    public void Read_WorksCorrectly()
    {
        // Arrange
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var stream = new TestReadOnlyStream(data);
        var buffer = new byte[3];

        // Act
        var bytesRead = stream.Read(buffer, 0, 3);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(3));
        Assert.That(buffer, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }
}
