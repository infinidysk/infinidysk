using System;
using System.IO;
using System.Linq;
using SharpCompress.Common.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Rar;

/// <summary>
/// Covers the <see cref="RarHeaderReadException"/> contract (issue #119).
/// </summary>
public class RarHeaderReadExceptionTests : TestBase
{
    private static RarHeaderFactory NewFactory() =>
        new(
            StreamingMode.Seekable,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = true,
            }
        );

    [Fact]
    public void TruncatedStream_OnSignature_ThrowsTruncated()
    {
        using var stream = new MemoryStream([0x52, 0x61]); // incomplete "Rar!" signature
        var factory = NewFactory();

        var ex = Assert.Throws<RarHeaderReadException>(() =>
        {
            foreach (var _ in factory.ReadHeaders(stream)) { }
        });

        Assert.True(ex.Truncated);
    }

    [Fact]
    public void NonRarBytes_ThrowsNotTruncated()
    {
        using var stream = new MemoryStream([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        var factory = NewFactory();

        var ex = Assert.Throws<RarHeaderReadException>(() =>
        {
            foreach (var _ in factory.ReadHeaders(stream)) { }
        });

        Assert.False(ex.Truncated);
    }

    [Fact]
    public void TruncatedAfterMark_MidHeader_EndsEnumerationGracefully()
    {
        // Valid RAR5 mark (8 bytes) then abrupt EOF before the next header block.
        // Soft-end preserves pre-#119 behavior for archives without EndArchive.
        using var stream = new MemoryStream([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);
        var factory = NewFactory();

        var headers = factory.ReadHeaders(stream).ToList();
        Assert.Single(headers);
        Assert.Equal(HeaderType.Mark, headers[0].HeaderType);
    }

    [Fact]
    public void SuccessfulParse_Unchanged()
    {
        using var stream = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, "Rar5.none.rar"));
        var factory = NewFactory();

        var count = 0;
        foreach (var header in factory.ReadHeaders(stream))
        {
            count++;
            Assert.NotNull(header);
        }

        Assert.True(count >= 2);
    }

    [Fact]
    public void SeekPastEnd_OnDeferredSkip_ThrowsTruncated()
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, "Rar.none.rar");
        var bytes = File.ReadAllBytes(path);

        long skipTarget = 0;
        using (var probe = new MemoryStream(bytes))
        {
            foreach (var header in NewFactory().ReadHeaders(probe))
            {
                if (
                    header.HeaderType == HeaderType.File
                    && header is IRarFileHeader fh
                    && fh.CompressedSize > 0
                )
                {
                    skipTarget = fh.DataStartPosition + fh.CompressedSize;
                    break;
                }
            }
        }

        Assert.True(skipTarget > 1, "expected a stored file header with packed data");

        // MemoryStream allows Position > Length; use a Length-enforcing stream to mimic
        // FileStream / remote Usenet streams that throw ArgumentOutOfRangeException.
        using var inner = new MemoryStream(bytes);
        using var stream = new LengthEnforcingStream(inner, skipTarget - 1);
        using var enumerator = NewFactory().ReadHeaders(stream).GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (
                enumerator.Current.HeaderType == HeaderType.File
                && enumerator.Current is IRarFileHeader
            )
            {
                var ex = Assert.Throws<RarHeaderReadException>(() => enumerator.MoveNext());
                Assert.True(ex.Truncated);
                Assert.Contains(
                    "seek past stream end",
                    ex.Message,
                    StringComparison.OrdinalIgnoreCase
                );
                return;
            }
        }

        Assert.Fail("expected to observe a file header before the deferred skip");
    }

    /// <summary>
    /// Seekable stream whose <see cref="Stream.Position"/> setter rejects values outside
    /// <c>[0, length]</c> with <see cref="ArgumentOutOfRangeException"/> (MemoryStream does not).
    /// </summary>
    private sealed class LengthEnforcingStream(Stream inner, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                if (value < 0 || value > length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        value,
                        "Seek position is outside stream bounds."
                    );
                }

                inner.Position = value;
            }
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - Position;
            if (remaining <= 0)
            {
                return 0;
            }

            return inner.Read(buffer, offset, (int)Math.Min(count, remaining));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = newPos;
            return newPos;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
