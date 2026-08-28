using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.BenchmarkUsenetConnection;
using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Api;

public class BenchmarkUsenetConnectionRequestTests
{
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Constructor_RejectsInvalidProviderConnectionLimit(string providerLimit)
    {
        var context = CreateContext(
            ("max-connections", providerLimit));

        Assert.Throws<BadHttpRequestException>(
            () => new BenchmarkUsenetConnectionRequest(context, new ConfigManager()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Constructor_RejectsInvalidTransferTestConnections(string transferConnections)
    {
        var context = CreateContext(
            ("max-connections", "5"),
            ("transfer-connections", transferConnections));

        Assert.Throws<BadHttpRequestException>(
            () => new BenchmarkUsenetConnectionRequest(context, new ConfigManager()));
    }

    [Fact]
    public void Constructor_RejectsTransferTestConnectionsAboveProviderLimit()
    {
        var context = CreateContext(
            ("max-connections", "5"),
            ("transfer-connections", "10"));

        var error = Assert.Throws<BadHttpRequestException>(
            () => new BenchmarkUsenetConnectionRequest(context, new ConfigManager()));

        Assert.Contains("must not exceed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_PipeliningOnlyKeepsProviderAndTransferLimitsSeparate()
    {
        var context = CreateContext(
            ("max-connections", "20"),
            ("transfer-connections", "8"),
            ("pipelining-only", "true"));

        var request = new BenchmarkUsenetConnectionRequest(context, new ConfigManager());

        Assert.True(request.PipeliningOnly);
        Assert.Equal(20, request.MaxConnections);
        Assert.Equal(8, request.TransferTestConnections);
    }

    [Fact]
    public void Constructor_LegacyRequestUsesProviderLimitAsTransferFallback()
    {
        var context = CreateContext(("max-connections", "8"));

        var request = new BenchmarkUsenetConnectionRequest(context, new ConfigManager());

        Assert.Equal(8, request.MaxConnections);
        Assert.Equal(8, request.TransferTestConnections);
    }

    [Fact]
    public void Constructor_CancelSkipsConnectionValidation()
    {
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["cancel"] = "true",
            });

        var request = new BenchmarkUsenetConnectionRequest(context, new ConfigManager());

        Assert.True(request.Cancel);
        Assert.Equal(1, request.MaxConnections);
        Assert.Equal(1, request.TransferTestConnections);
    }

    [Fact]
    public void Constructor_BlankTransferTestConnectionsUseProviderLimit()
    {
        var context = CreateContext(
            ("max-connections", "8"),
            ("transfer-connections", ""),
            ("pipelining-only", "true"));

        var request = new BenchmarkUsenetConnectionRequest(context, new ConfigManager());

        Assert.Equal(8, request.TransferTestConnections);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("6")]
    public void Constructor_RejectsInvalidVerificationConnections(string verifyConnections)
    {
        var context = CreateContext(
            ("max-connections", "5"),
            ("verify-connections", verifyConnections));

        Assert.Throws<BadHttpRequestException>(
            () => new BenchmarkUsenetConnectionRequest(context, new ConfigManager()));
    }

    private static DefaultHttpContext CreateContext(params (string Key, string Value)[] overrides)
    {
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["host"] = "news.example",
            ["user"] = "user",
            ["pass"] = "pass",
            ["port"] = "563",
            ["use-ssl"] = "true",
        };
        foreach (var (key, value) in overrides)
            fields[key] = value;

        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(fields);
        return context;
    }
}
