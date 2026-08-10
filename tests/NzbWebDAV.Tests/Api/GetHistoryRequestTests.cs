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
    public void ClampsNegativePageSizeToZero()
    {
        // A negative Take() becomes a negative SQLite LIMIT (= unbounded); must clamp.
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?pageSize=-1");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal(0, request.Limit);
    }

    [Fact]
    public void MalformedNzoId_ThrowsBadRequest()
    {
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?nzo_ids=not-a-guid");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetHistoryRequest(context, config));
        Assert.Contains("not-a-guid", ex.Message);
    }

    [Fact]
    public void MixedValidAndInvalidNzoIds_ThrowsBadRequestNamingOnlyInvalidTokens()
    {
        var validId = Guid.NewGuid();
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?nzo_ids={validId},bad-one,also-bad");

        var ex = Assert.Throws<BadHttpRequestException>(() => new GetHistoryRequest(context, config));
        Assert.Contains("bad-one", ex.Message);
        Assert.Contains("also-bad", ex.Message);
        Assert.DoesNotContain(validId.ToString(), ex.Message);
    }

    [Fact]
    public void ValidNzoIdsWithWhitespaceAndEmptyEntries_ParsesExpectedGuids()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?nzo_ids= {id1} ,,{id2} ");

        var request = new GetHistoryRequest(context, config);

        Assert.Equal([id1, id2], request.NzoIds);
    }

    [Fact]
    public void MissingNzoIds_KeepsEmptyList()
    {
        var config = CreateConfig(ignoreLimit: true);
        var context = new DefaultHttpContext();

        var request = new GetHistoryRequest(context, config);

        Assert.Empty(request.NzoIds);
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
