using UsenetSharp.Clients;
using UsenetSharp.Exceptions;

namespace UsenetSharpTest.Clients;

[TestFixture]
public class UsenetClientHeadAsyncTests
{
    [Test]
    public async Task HeadAsync_WithValidSegmentId_ReturnsResponseWithHeaders()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.HeadAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.SegmentId, Is.EqualTo(segmentId));
        Assert.That(result.ResponseCode, Is.EqualTo(221));
        Assert.That(result.ArticleHeaders, Is.Not.Null);
        Assert.That(result.ArticleHeaders.Headers, Is.Not.Empty);
    }

    [Test]
    public async Task HeadAsync_ValidSegmentId_ParsesCommonHeaders()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.HeadAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.ArticleHeaders, Is.Not.Null);

        // Verify we can access headers
        var headers = result.ArticleHeaders;
        Assert.That(headers.Headers, Is.Not.Empty);

        // Most articles should have these common headers
        // Note: we're not asserting they exist, just that if they do, they're not empty
        if (headers.Subject != null)
        {
            Assert.That(headers.Subject, Is.Not.Empty);
        }

        if (headers.MessageId != null)
        {
            Assert.That(headers.MessageId, Is.Not.Empty);
        }

        Assert.That(headers.Date, Is.Not.Default);
    }

    [Test]
    public async Task HeadAsync_WithInvalidSegmentId_ReturnsNullHeaders()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var invalidSegmentId = "invalid-segment-id-does-not-exist@test.com";
        var result = await client.HeadAsync(invalidSegmentId, cancellationToken);

        // Assert
        Assert.That(result.SegmentId, Is.EqualTo(invalidSegmentId));
        Assert.That(result.ArticleHeaders, Is.Null);
        Assert.That(result.ResponseCode, Is.EqualTo(430)); // No article with that message-id
    }

    [Test]
    public async Task HeadAsync_WithoutConnection_ThrowsException()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();

        // Act & Assert
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var exception = Assert.ThrowsAsync<UsenetNotConnectedException>(async () =>
            await client.HeadAsync(segmentId, cancellationToken));

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("Not connected"));
    }

    [Test]
    public async Task HeadAsync_WithoutAuthentication_ReturnsAuthenticationRequired()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        // Act (no authentication)
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.HeadAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(480)); // Authentication required
        Assert.That(result.ArticleHeaders, Is.Null);
    }

    [Test]
    public async Task HeadAsync_ReleasesConnectionForNextCommand()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Execute HEAD command
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var headResult = await client.HeadAsync(segmentId, cancellationToken);

        Assert.That(headResult.ArticleHeaders, Is.Not.Null);

        // Assert - Should be able to execute another command immediately
        var dateResult = await client.DateAsync(cancellationToken);
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111),
            "DATE command should succeed after HEAD command completes");
    }

    [Test]
    public async Task HeadAsync_MultipleSegments_CanBeReadSequentially()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentIds = new[]
        {
            "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
            "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
            "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV"
        };

        // Act & Assert
        foreach (var segmentId in segmentIds)
        {
            var result = await client.HeadAsync(segmentId, cancellationToken);
            Assert.That(result.ArticleHeaders, Is.Not.Null);
            Assert.That(result.ArticleHeaders.Headers, Is.Not.Empty);
            Assert.That(result.ResponseCode, Is.EqualTo(221));
        }
    }

    [Test]
    public async Task HeadAsync_HeadersDictionary_IsCaseInsensitive()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.HeadAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.ArticleHeaders, Is.Not.Null);

        // If any header exists, verify case-insensitive access
        if (result.ArticleHeaders.Headers.Count > 0)
        {
            var firstHeader = result.ArticleHeaders.Headers.First();
            var headerName = firstHeader.Key;
            var headerValue = firstHeader.Value;

            // Access with different cases
            Assert.That(result.ArticleHeaders.Headers[headerName.ToLower()], Is.EqualTo(headerValue));
            Assert.That(result.ArticleHeaders.Headers[headerName.ToUpper()], Is.EqualTo(headerValue));
            Assert.That(result.ArticleHeaders.Headers[headerName], Is.EqualTo(headerValue));
        }
    }

    [Test]
    public async Task HeadAsync_ReturnsImmediately()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.HeadAsync(segmentId, cancellationToken);

        // Assert - Headers should be available immediately
        Assert.That(result.ArticleHeaders, Is.Not.Null);
        Assert.That(result.ArticleHeaders.Headers, Is.Not.Empty);
        Assert.That(result.ResponseCode, Is.EqualTo(221));
    }

    [Test]
    public async Task HeadAsync_ComparedToArticleAsync_ReturnsSameHeaders()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";

        // Act - Get headers using HEAD
        var headResult = await client.HeadAsync(segmentId, cancellationToken);

        // Get headers using ARTICLE
        var articleResult = await client.ArticleAsync(segmentId, cancellationToken);

        // Read and discard article stream
        using var streamReader = new StreamReader(articleResult.Stream);
        await streamReader.ReadToEndAsync(cancellationToken);

        // Assert - Headers should be the same
        Assert.That(headResult.ArticleHeaders, Is.Not.Null);
        Assert.That(articleResult.ArticleHeaders, Is.Not.Null);

        // Compare common headers
        Assert.That(headResult.ArticleHeaders.Subject, Is.EqualTo(articleResult.ArticleHeaders.Subject));
        Assert.That(headResult.ArticleHeaders.MessageId, Is.EqualTo(articleResult.ArticleHeaders.MessageId));
        Assert.That(headResult.ArticleHeaders.Date, Is.EqualTo(articleResult.ArticleHeaders.Date));
        Assert.That(headResult.ArticleHeaders.From, Is.EqualTo(articleResult.ArticleHeaders.From));

        // Compare header counts
        Assert.That(headResult.ArticleHeaders.Headers.Count, Is.EqualTo(articleResult.ArticleHeaders.Headers.Count));
    }
}
