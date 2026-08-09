using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class CreateNewConnectionTests
{
    [Fact]
    public async Task CreateNewConnection_DisposesConnectionWhenAuthFails()
    {
        var fake = new HandshakeNntpClient
        {
            AuthenticateException = new CouldNotLoginToUsenetException("bad credentials"),
        };
        var details = MakeDetails();

        await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(async () =>
            await UsenetStreamingClient.CreateNewConnection(details, () => fake, CancellationToken.None));

        Assert.Equal(1, fake.DisposeCount);
        Assert.True(fake.Connected);
    }

    [Fact]
    public async Task CreateNewConnection_TimesOutHungConnect()
    {
        var previous = UsenetStreamingClient.ConnectTimeout;
        UsenetStreamingClient.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var fake = new HandshakeNntpClient { HangConnect = true };
            var details = MakeDetails();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var ex = await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(async () =>
                await UsenetStreamingClient.CreateNewConnection(details, () => fake, CancellationToken.None));

            sw.Stop();
            Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nntp.example:563", ex.Message, StringComparison.Ordinal);
            Assert.True(ex.InnerException!.IsCancellationException());
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                $"Expected timeout well under OS connect default; elapsed={sw.Elapsed}");
            Assert.Equal(1, fake.DisposeCount);
        }
        finally
        {
            UsenetStreamingClient.ConnectTimeout = previous;
        }
    }

    [Fact]
    public async Task CreateNewConnection_TimesOutHungAuthenticate()
    {
        var previous = UsenetStreamingClient.ConnectTimeout;
        UsenetStreamingClient.ConnectTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var fake = new HandshakeNntpClient { HangAuthenticate = true };
            var details = MakeDetails();

            var ex = await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(async () =>
                await UsenetStreamingClient.CreateNewConnection(details, () => fake, CancellationToken.None));

            Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("nntp.example:563", ex.Message, StringComparison.Ordinal);
            Assert.True(ex.InnerException!.IsCancellationException());
            Assert.True(fake.Connected);
            Assert.Equal(1, fake.DisposeCount);
        }
        finally
        {
            UsenetStreamingClient.ConnectTimeout = previous;
        }
    }

    [Fact]
    public async Task CreateNewConnection_PropagatesCallerCancellationAsOperationCanceled()
    {
        var previous = UsenetStreamingClient.ConnectTimeout;
        UsenetStreamingClient.ConnectTimeout = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource();
        try
        {
            var fake = new HandshakeNntpClient { HangConnect = true };
            var details = MakeDetails();
            var connectTask = UsenetStreamingClient.CreateNewConnection(details, () => fake, cts.Token);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connectTask);
            Assert.Equal(1, fake.DisposeCount);
        }
        finally
        {
            UsenetStreamingClient.ConnectTimeout = previous;
        }
    }

    [Fact]
    public async Task CreateNewConnection_DoesNotTypeUnrelatedCancellationAsConnectTimeout()
    {
        var previous = UsenetStreamingClient.ConnectTimeout;
        UsenetStreamingClient.ConnectTimeout = TimeSpan.FromSeconds(30);
        try
        {
            var fake = new HandshakeNntpClient
            {
                ConnectException = new OperationCanceledException("internal cancel"),
            };
            var details = MakeDetails();

            var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await UsenetStreamingClient.CreateNewConnection(details, () => fake, CancellationToken.None));

            Assert.Equal("internal cancel", ex.Message);
            Assert.Equal(1, fake.DisposeCount);
        }
        finally
        {
            UsenetStreamingClient.ConnectTimeout = previous;
        }
    }

    [Fact]
    public async Task CreateNewConnection_ReturnsLiveConnectionOnSuccess()
    {
        var fake = new HandshakeNntpClient();
        var details = MakeDetails();

        var connection = await UsenetStreamingClient.CreateNewConnection(
            details, () => fake, CancellationToken.None);

        Assert.Same(fake, connection);
        Assert.Equal(0, fake.DisposeCount);
        Assert.Equal(1, fake.AuthenticateCount);
        Assert.Equal("u", fake.LastAuthUser);
        Assert.Equal("p", fake.LastAuthPass);
        connection.Dispose();
    }

    [Fact]
    public async Task CreateNewConnection_SkipsAuthenticateWhenCredentialsEmpty()
    {
        var fake = new HandshakeNntpClient();
        var details = MakeDetails();
        details.User = "";
        details.Pass = "";

        var connection = await UsenetStreamingClient.CreateNewConnection(
            details, () => fake, CancellationToken.None);

        Assert.Same(fake, connection);
        Assert.True(fake.Connected);
        Assert.Equal(0, fake.AuthenticateCount);
        connection.Dispose();
    }

    [Fact]
    public async Task CreateNewConnection_AuthenticatesWhenOnlyPasswordSet()
    {
        var fake = new HandshakeNntpClient();
        var details = MakeDetails();
        details.User = "";
        details.Pass = "secret";

        await UsenetStreamingClient.CreateNewConnection(details, () => fake, CancellationToken.None);

        Assert.Equal(1, fake.AuthenticateCount);
        Assert.Equal("", fake.LastAuthUser);
        Assert.Equal("secret", fake.LastAuthPass);
    }

    [Fact]
    public async Task CreateNewConnection_RejectsControlCharactersInCredentials()
    {
        var fake = new HandshakeNntpClient();
        var details = MakeDetails();
        details.User = "user\r\nQUIT";

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await UsenetStreamingClient.CreateNewConnection(details, () => fake, CancellationToken.None));

        Assert.False(fake.Connected);
        Assert.Equal(0, fake.DisposeCount);
    }

    private static UsenetProviderConfig.ConnectionDetails MakeDetails() =>
        new()
        {
            Type = ProviderType.Pooled,
            Host = "nntp.example",
            Port = 563,
            UseSsl = true,
            User = "u",
            Pass = "p",
            MaxConnections = 1,
        };

    private sealed class HandshakeNntpClient : NntpClient
    {
        public bool HangConnect { get; init; }
        public bool HangAuthenticate { get; init; }
        public Exception? ConnectException { get; init; }
        public Exception? AuthenticateException { get; init; }
        public bool Connected { get; private set; }
        public int DisposeCount { get; private set; }
        public int AuthenticateCount { get; private set; }
        public string? LastAuthUser { get; private set; }
        public string? LastAuthPass { get; private set; }

        public override async Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken)
        {
            if (HangConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            if (ConnectException is not null)
                throw ConnectException;

            cancellationToken.ThrowIfCancellationRequested();
            Connected = true;
        }

        public override async Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken)
        {
            if (HangAuthenticate)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return default!;
            }

            cancellationToken.ThrowIfCancellationRequested();
            AuthenticateCount++;
            LastAuthUser = user;
            LastAuthPass = pass;
            if (AuthenticateException is not null)
                throw AuthenticateException;
            return new UsenetResponse
            {
                ResponseCode = (int)UsenetResponseType.AuthenticationAccepted,
                ResponseMessage = "281 Ok",
            };
        }

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose() => DisposeCount++;
    }
}
