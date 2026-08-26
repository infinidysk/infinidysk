using System.IO;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class PersistentCorruptionTrackerTests
{
    [Fact]
    public void NoteOrThrow_SameCrcFromTwoProviders_ThrowsPersistent()
    {
        var tracker = new PersistentCorruptionTracker();
        tracker.NoteOrThrow(Corrupt("a.example", 0xafccdc56, 0xa8e2a630));

        var persistent = Assert.Throws<PersistentUsenetCorruptionException>(
            () => tracker.NoteOrThrow(Corrupt("b.example", 0xafccdc56, 0xa8e2a630)));

        Assert.Equal("seg@example", persistent.SegmentId);
        Assert.Equal(0xafccdc56u, persistent.ActualCrc);
        Assert.Equal(0xa8e2a630u, persistent.ExpectedCrc);
        Assert.True(persistent is NonRetryableDownloadException);
    }

    [Fact]
    public void NoteOrThrow_SameCrcFromOneProvider_StaysRetryable()
    {
        var tracker = new PersistentCorruptionTracker();
        tracker.NoteOrThrow(Corrupt("a.example", 0xafccdc56, 0xa8e2a630));
        tracker.NoteOrThrow(Corrupt("a.example", 0xafccdc56, 0xa8e2a630));
    }

    [Fact]
    public void NoteOrThrow_DifferentCrcPairs_StayRetryable()
    {
        var tracker = new PersistentCorruptionTracker();
        tracker.NoteOrThrow(Corrupt("a.example", 0xafccdc56, 0xa8e2a630));
        tracker.NoteOrThrow(Corrupt("b.example", 0x11111111, 0xa8e2a630));
    }

    [Fact]
    public void TryGetCrcPair_ParsesDecoderMessage()
    {
        var exception = Corrupt("a.example", 0xafccdc56, 0xa8e2a630);
        Assert.True(exception.TryGetCrcPair(out var actual, out var expected));
        Assert.Equal(0xafccdc56u, actual);
        Assert.Equal(0xa8e2a630u, expected);
    }

    private static UsenetCorruptArticleException Corrupt(string provider, uint actual, uint expected) =>
        new(
            "seg@example",
            provider,
            new InvalidDataException(
                $"The decoded yEnc CRC32 was {actual:x8}, but the trailer expected {expected:x8}."));
}
