using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.GetOverviewStats;
using NzbWebDAV.Api.Errors;

namespace NzbWebDAV.Tests.Api;

public class GetOverviewStatsRequestTests
{
    [Theory]
    [InlineData(null, GetOverviewStatsRequest.OverviewSections.All)]
    [InlineData("", GetOverviewStatsRequest.OverviewSections.All)]
    [InlineData("all", GetOverviewStatsRequest.OverviewSections.All)]
    [InlineData("window", GetOverviewStatsRequest.OverviewSections.Window)]
    [InlineData("detail", GetOverviewStatsRequest.OverviewSections.Detail)]
    [InlineData("static", GetOverviewStatsRequest.OverviewSections.Static)]
    [InlineData("window,detail", GetOverviewStatsRequest.OverviewSections.Window | GetOverviewStatsRequest.OverviewSections.Detail)]
    [InlineData("window, static", GetOverviewStatsRequest.OverviewSections.Window | GetOverviewStatsRequest.OverviewSections.Static)]
    public void ParsesSectionsQueryParam(string? sections, GetOverviewStatsRequest.OverviewSections expected)
    {
        var context = new DefaultHttpContext();
        if (sections is not null)
            context.Request.QueryString = new QueryString($"?sections={sections}");

        var request = new GetOverviewStatsRequest(context);

        Assert.Equal(expected, request.Sections);
    }

    [Fact]
    public void RejectsUnknownSection()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?sections=window,nope");

        Assert.Throws<ApiValidationException>(() => new GetOverviewStatsRequest(context));
    }

    [Theory]
    [InlineData("1h", GetOverviewStatsRequest.OverviewWindow.Last1Hour)]
    [InlineData("24h", GetOverviewStatsRequest.OverviewWindow.Last24Hours)]
    [InlineData("7d", GetOverviewStatsRequest.OverviewWindow.Last7Days)]
    [InlineData("30d", GetOverviewStatsRequest.OverviewWindow.Last30Days)]
    [InlineData("all", GetOverviewStatsRequest.OverviewWindow.AllTime)]
    public void ParsesWindowQueryParam(string window, GetOverviewStatsRequest.OverviewWindow expected)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?window={window}&sections=window");

        var request = new GetOverviewStatsRequest(context);

        Assert.Equal(expected, request.Window);
        Assert.Equal(GetOverviewStatsRequest.OverviewSections.Window, request.Sections);
    }
}
