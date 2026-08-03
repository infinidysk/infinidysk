using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Archives.Rar;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.IO;
using SharpCompress.Readers;
using Xunit;

namespace SharpCompress.Test.Rar;

/// <summary>
/// Covers the deferred data-skip behavior in seekable header enumeration (issue #118):
/// packed data is not seeked past until the next enumerator advance, so stopping after a
/// match performs no data seek, while full walks land at the same absolute positions.
/// </summary>
public class RarHeaderFactoryDeferredSkipTests : TestBase
{
    private static RarHeaderFactory NewFactory() =>
        new(
            StreamingMode.Seekable,
            ReaderOptions.ForExternalStream with
            {
                LeaveStreamOpen = true,
            }
        );

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void StopAfterFirstFileHeader_PerformsNoDataSeek(string archiveName)
    {
        using var stream = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, archiveName));
        var factory = NewFactory();

        IRarFileHeader? first = null;
        foreach (var header in factory.ReadHeaders(stream))
        {
            if (
                header.HeaderType == HeaderType.File
                && header is IRarFileHeader fh
                && !fh.IsDirectory
                && fh.CompressedSize > 0
            )
            {
                first = fh;
                break;
            }
        }

        Assert.NotNull(first);
        // The stream must still sit at the start of the packed data — no seek past it.
        Assert.Equal(first.DataStartPosition, stream.Position);
    }

    [Theory]
    [InlineData("Rar.none.rar")]
    [InlineData("Rar5.none.rar")]
    public void FullWalk_StoredEntries_DataStartPositionsSliceExactPayload(string archiveName)
    {
        var path = Path.Combine(TEST_ARCHIVES_PATH, archiveName);

        Dictionary<string, byte[]> expected;
        using (var archive = RarArchive.OpenArchive(path))
        {
            expected = archive
                .Entries.Where(e => !e.IsDirectory && e.Size > 0)
                .ToDictionary(e => e.Key!, ReadFully);
        }

        using var stream = File.OpenRead(path);
        var factory = NewFactory();

        var matched = 0;
        foreach (var header in factory.ReadHeaders(stream))
        {
            if (
                header.HeaderType != HeaderType.File
                || header is not IRarFileHeader fh
                || fh.IsDirectory
                || !fh.IsStored
                || fh.CompressedSize == 0
            )
            {
                continue;
            }

            // Seek freely inside the loop; the deferred skip repositions absolutely on advance.
            stream.Position = fh.DataStartPosition;
            var buffer = new byte[fh.CompressedSize];
            stream.ReadExactly(buffer);
            Assert.Equal(expected[fh.FileName], buffer);
            matched++;
        }

        Assert.Equal(expected.Count, matched);
    }

    [Fact]
    public void CommentVolume_FullEnumeration_Succeeds_WithoutManualDrain()
    {
        using var stream = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, "Rar5.comment.rar"));
        var factory = NewFactory();

        var sawComment = false;
        var parsedAfterComment = false;
        var reachedEnd = false;
        foreach (var header in factory.ReadHeaders(stream))
        {
            if (sawComment)
            {
                parsedAfterComment = true;
            }

            if (
                header.HeaderType == HeaderType.Service
                && header is IRarFileHeader fh
                && fh.FileName == "CMT"
            )
            {
                sawComment = true;
            }

            if (header.HeaderType == HeaderType.EndArchive)
            {
                reachedEnd = true;
            }
        }

        Assert.True(sawComment, "expected a CMT service header");
        // The header after the comment parsed without the consumer draining the comment data.
        Assert.True(parsedAfterComment);
        Assert.True(reachedEnd);
    }

    [Fact]
    public void CommentVolume_ArchiveStillReadsComment()
    {
        using var archive = RarArchive.OpenArchive(
            Path.Combine(TEST_ARCHIVES_PATH, "Rar5.comment.rar")
        );
        // Comment is populated while enumerating volume file parts (on the CMT service
        // header); force that load before reading Volumes.Comment.
        _ = archive.Entries.Count();
        var comment = archive.Volumes.Cast<SharpCompress.Common.Rar.RarVolume>().First().Comment;
        Assert.False(string.IsNullOrEmpty(comment));
        Assert.Contains("comment", comment!, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadFully(SharpCompress.Archives.IArchiveEntry entry)
    {
        using var s = entry.OpenEntryStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
