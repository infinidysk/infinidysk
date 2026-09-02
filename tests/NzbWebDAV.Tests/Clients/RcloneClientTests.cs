using System.Net;
using System.Text;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Clients;

public class RcloneClientTests : IDisposable
{
    public RcloneClientTests()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://rclone.test" },
        ]);
        RcloneClient.Initialize(config);
        RcloneClient.BackoffOverride = _ => TimeSpan.Zero;
    }

    [Fact]
    public async Task ForgetVfsPaths_RetriesUntilSuccess()
    {
        RcloneClient.TestHandler = CreateHandler(
            ("POST /vfs/forget", FailResponse("transient failure")),
            ("POST /vfs/forget", FailResponse("transient failure")),
            ("POST /vfs/forget", SuccessResponse()));

        var result = await RcloneClient.Current!.ForgetVfsPaths(["/content/test"]);

        Assert.True(result.Success);
        Assert.Null(RcloneClient.Current.LastForgetError);
    }

    [Fact]
    public async Task ForgetVfsPaths_DoesNotRetryAuthenticationFailure()
    {
        RcloneClient.TestHandler = CreateHandler(
            ("POST /vfs/forget", UnauthorizedResponse()));

        var result = await RcloneClient.Current!.ForgetVfsPaths(["/content/test"]);

        Assert.False(result.Success);
        Assert.Equal("Authentication failed", result.Error);
        Assert.Null(RcloneClient.Current.LastForgetError);
    }

    [Fact]
    public async Task ForgetVfsPaths_AuthenticationFailureClearsPriorError()
    {
        RcloneClient.TestHandler = CreateHandler(
            ("POST /vfs/forget", FailResponse("transient failure")),
            ("POST /vfs/forget", FailResponse("transient failure")),
            ("POST /vfs/forget", FailResponse("transient failure")),
            ("POST /vfs/forget", FailResponse("transient failure")),
            ("POST /vfs/forget", UnauthorizedResponse()));

        await RcloneClient.Current!.ForgetVfsPaths(["/content/seed-error"]);
        Assert.NotNull(RcloneClient.Current.LastForgetError);

        var result = await RcloneClient.Current.ForgetVfsPaths(["/content/test"]);

        Assert.False(result.Success);
        Assert.Equal("Authentication failed", result.Error);
        Assert.Null(RcloneClient.Current.LastForgetError);
    }

    [Fact]
    public async Task ForgetVfsPaths_SetsLastForgetErrorAfterAllAttemptsFail()
    {
        RcloneClient.TestHandler = CreateHandler(
            ("POST /vfs/forget", FailResponse("persistent failure")),
            ("POST /vfs/forget", FailResponse("persistent failure")),
            ("POST /vfs/forget", FailResponse("persistent failure")),
            ("POST /vfs/forget", FailResponse("persistent failure")));

        var result = await RcloneClient.Current!.ForgetVfsPaths(["/content/test"]);

        Assert.False(result.Success);
        Assert.NotNull(RcloneClient.Current.LastForgetError);
        Assert.Equal("persistent failure", RcloneClient.Current.LastForgetError!.Value.Message);
    }

    [Fact]
    public async Task ForgetVfsPaths_EmptyPathList_DoesNotCallHttp()
    {
        RcloneClient.TestHandler = CreateHandler(
            ("POST /vfs/forget", SuccessResponse()));

        var result = await RcloneClient.Current!.ForgetVfsPaths([]);

        Assert.True(result.Success);
        Assert.Empty(result.Forgotten ?? []);
    }

    [Fact]
    public async Task TestConnection_PropagatesCancellation()
    {
        RcloneClient.TestHandler = new HangUntilCancelledHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RcloneClient.TestConnection("http://rclone.test", null, null, cts.Token));
    }

    [Fact]
    public async Task GetVfsStats_WithSubmittedCredentials_ReturnsReadAheadAndCacheMode()
    {
        RcloneClient.TestHandler = CreateHandler(
            ("POST /vfs/stats", new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"opt\":{\"ReadAhead\":536870912,\"CacheMode\":\"full\"}}",
                    Encoding.UTF8,
                    "application/json"),
            }));

        var result = await RcloneClient.GetVfsStats(
            "http://rclone.test",
            "rclone",
            "secret");

        Assert.True(result.Success);
        Assert.Equal(536_870_912, result.Options?.ReadAhead);
        Assert.Equal("full", result.Options?.CacheMode);
    }

    [Fact]
    public void SharedHttpClient_HasExplicitTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RcloneClient.RequestTimeout);
    }

    public void Dispose()
    {
        RcloneClient.TestHandler = null;
        RcloneClient.BackoffOverride = null;
        RcloneClient.Current?.Dispose();
    }

    private static HttpResponseMessage SuccessResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage FailResponse(string error) =>
        new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent($"{{\"error\":\"{error}\"}}", Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage UnauthorizedResponse() =>
        new(HttpStatusCode.Unauthorized);

    private static ResponseQueueHandler CreateHandler(
        params (string request, HttpResponseMessage response)[] responses) =>
        new(responses
            .GroupBy(x => x.request)
            .ToDictionary(
                x => x.Key,
                x => new Queue<HttpResponseMessage>(x.Select(y => y.response))));

    private sealed class ResponseQueueHandler(
        Dictionary<string, Queue<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            if (!responses.TryGetValue(key, out var queuedResponses) || !queuedResponses.TryDequeue(out var response))
                throw new InvalidOperationException($"Unexpected request: {key}");

            return Task.FromResult(response);
        }
    }

    private sealed class HangUntilCancelledHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected cancellation before a response.");
        }
    }
}
