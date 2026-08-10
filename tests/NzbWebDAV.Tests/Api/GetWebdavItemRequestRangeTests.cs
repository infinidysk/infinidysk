using NzbWebDAV.Api.Controllers.GetWebdavItem;

namespace NzbWebDAV.Tests.Api;

public class GetWebdavItemRequestRangeTests
{
    [Theory]
    [InlineData("bytes=0-99", 0L, 99L, null)]
    [InlineData("bytes=100-", 100L, null, null)]
    [InlineData("bytes=-65536", null, null, 65536L)]
    public void TryParseRangeHeader_ParsesValidForms(
        string header, long? start, long? end, long? suffix)
    {
        Assert.True(GetWebdavItemRequest.TryParseRangeHeader(header, out var s, out var e, out var suf));
        Assert.Equal(start, s);
        Assert.Equal(end, e);
        Assert.Equal(suffix, suf);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=999999999999999999999-")]
    [InlineData("bytes=0-99,200-299")]
    [InlineData("bytes=0-xx")]
    [InlineData("items=0-10")]
    [InlineData("npt=0.000-")]
    [InlineData("bytes=-")]
    public void TryParseRangeHeader_IgnoresMalformed(string header)
    {
        Assert.False(GetWebdavItemRequest.TryParseRangeHeader(header, out var s, out var e, out var suf));
        Assert.Null(s);
        Assert.Null(e);
        Assert.Null(suf);
    }

    [Theory]
    [InlineData(1_048_575L, 50_000L, 49_999L)] // end past EOF → last byte
    [InlineData(49_999L, 50_000L, 49_999L)] // exact last byte → unchanged
    [InlineData(null, 50_000L, 49_999L)] // open-ended → last byte
    [InlineData(1_023L, 50_000L, 1_023L)] // in-bounds end → unchanged
    public void ResolveRangeEnd_ClampsPastEofAndPreservesInBounds(
        long? rangeEnd, long fileSize, long expected)
    {
        Assert.Equal(expected, GetWebdavItemController.ResolveRangeEnd(rangeEnd, fileSize));
    }

    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("abc123")]
    [InlineData("player_session-1")]
    public void NormalizePlayerSession_AcceptsShortOpaqueTokens(string raw)
    {
        Assert.Equal(raw, GetWebdavItemRequest.NormalizePlayerSession(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has?query&chars")]
    [InlineData("newline\ninjection")]
    public void NormalizePlayerSession_RejectsMissingOrUnsafeValues(string? raw)
    {
        Assert.Null(GetWebdavItemRequest.NormalizePlayerSession(raw));
    }

    [Fact]
    public void NormalizePlayerSession_RejectsOverlongValues()
    {
        Assert.Null(GetWebdavItemRequest.NormalizePlayerSession(new string('a', 65)));
        Assert.NotNull(GetWebdavItemRequest.NormalizePlayerSession(new string('a', 64)));
    }
}
