using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class GetHistoryRequestTests
{
    [Fact]
    public void CapsUnboundedLimitWhenIgnoreHistoryLimitEnabled()
    {
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        // Arrs send limit=60 but ignore-history-limit discards it → would be int.MaxValue
        context.Request.QueryString = new QueryString("?limit=60");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(10_000, request.Limit);
    }

    [Fact]
    public void PreservesSmallPageSizeBelowCeiling()
    {
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?pageSize=100");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(100, request.Limit);
    }

    [Fact]
    public void HonorsArrLimitWhenIgnoreDisabledAndBelowCeiling()
    {
        var config = CreateConfig(ignoreLimit: false);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=60");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(60, request.Limit);
    }

    [Fact]
    public void CapsPageSizeAboveConfiguredCeiling()
    {
        var config = CreateConfig(ignoreLimit: true, maxPageSize: "500");
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?pageSize=2000");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(500, request.Limit);
    }

    [Fact]
    public void TreatsNegativePageSizeAsUnlimitedCappedAtCeiling()
    {
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?pageSize=-1");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(10_000, request.Limit);
    }

    [Fact]
    public void TreatsLimitZeroAsUnlimitedCappedAtCeiling()
    {
        var config = CreateConfig(ignoreLimit: false);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=0");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(10_000, request.Limit);
    }

    [Fact]
    public void TreatsPageSizeZeroAsUnlimitedCappedAtCeiling()
    {
        var config = CreateConfig(ignoreLimit: false);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?pageSize=0");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(10_000, request.Limit);
    }

    [Fact]
    public void PageSizeZeroOverridesLimitWhenBothPresent()
    {
        var config = CreateConfig(ignoreLimit: false);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=60&pageSize=0");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(10_000, request.Limit);
    }

    [Fact]
    public void RejectsNonIntegerLimit()
    {
        var config = CreateConfig(ignoreLimit: false);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?limit=abc");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetHistoryRequest(context, config));
        Assert.Equal("Invalid limit parameter", ex.Message);
    }

    [Fact]
    public void RejectsNonIntegerPageSize()
    {
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?pageSize=abc");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetHistoryRequest(context, config));
        Assert.Equal("Invalid pageSize parameter", ex.Message);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-100, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    public void ClampsNegativeStartToZero(int startParam, int expectedStart)
    {
        var config = CreateConfig(ignoreLimit: false);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?start={startParam}");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(expectedStart, request.Start);
    }

    [Fact]
    public void GetHistoryMaxPageSize_DefaultsAndClamps()
    {
        Assert.Equal(10_000, new ConfigManager().GetHistoryMaxPageSize());

        var custom = CreateConfig(ignoreLimit: true, maxPageSize: "2500");
        Assert.Equal(2500, custom.GetHistoryMaxPageSize());

        var clamped = CreateConfig(ignoreLimit: true, maxPageSize: "999999");
        Assert.Equal(100_000, clamped.GetHistoryMaxPageSize());
    }

    private static ConfigManager CreateConfig(bool ignoreLimit, string? maxPageSize = null)
    {
        var items = new List<ConfigItem>
        {
            new()
            {
                ConfigName = ConfigKeys.ApiIgnoreHistoryLimit,
                ConfigValue = ignoreLimit ? "true" : "false",
            },
        };
        if (maxPageSize is not null)
        {
            items.Add(new ConfigItem
            {
                ConfigName = ConfigKeys.ApiHistoryMaxPageSize,
                ConfigValue = maxPageSize,
            });
        }

        var config = new ConfigManager();
        config.UpdateValues(items);
        return config;
    }
}
