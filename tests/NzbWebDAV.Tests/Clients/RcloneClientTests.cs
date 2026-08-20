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
}
