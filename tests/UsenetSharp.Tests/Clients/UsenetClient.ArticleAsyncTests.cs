using UsenetSharp.Clients;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharpTest.Clients;

[TestFixture]
public class UsenetClientArticleAsyncTests
{
    [Test]
    public async Task ArticleAsync_WithValidSegmentId_ReturnsResponseWithStreamAndHeaders()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.SegmentId, Is.EqualTo(segmentId));
        Assert.That(result.Stream, Is.Not.Null);
        Assert.That(result.ArticleHeaders, Is.Not.Null);
        Assert.That(result.ArticleHeaders.Headers, Is.Not.Empty);

        // Read some data from the stream to verify it works
        using var streamReader = new StreamReader(result.Stream);
        var firstLine = await streamReader.ReadLineAsync(cancellationToken);
        Assert.That(firstLine, Is.Not.Null);

        // Read the rest to ensure the background task completes
        await streamReader.ReadToEndAsync(cancellationToken);
    }

    [Test]
    public async Task ArticleAsync_ValidSegmentId_ParsesCommonHeaders()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, cancellationToken);

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

        // Read stream to completion
        using var streamReader = new StreamReader(result.Stream);
        await streamReader.ReadToEndAsync(cancellationToken);
    }

    [Test]
    public async Task ArticleAsync_WithInvalidSegmentId_ReturnsNullStream()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var invalidSegmentId = "invalid-segment-id-does-not-exist@test.com";
        var result = await client.ArticleAsync(invalidSegmentId, cancellationToken);

        // Assert
        Assert.That(result.SegmentId, Is.EqualTo(invalidSegmentId));
        Assert.That(result.Stream, Is.Null);
        Assert.That(result.ArticleHeaders, Is.Null);
        Assert.That(result.ResponseCode, Is.EqualTo(430)); // No article with that message-id
    }

    [Test]
    public async Task ArticleAsync_WithoutConnection_ThrowsException()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();

        // Act & Assert
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var exception = Assert.ThrowsAsync<UsenetNotConnectedException>(async () =>
            await client.ArticleAsync(segmentId, cancellationToken));

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("Not connected"));
    }

    [Test]
    public async Task ArticleAsync_WithoutAuthentication_ReturnsAuthenticationRequired()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        // Act (no authentication)
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(480)); // Authentication required
        Assert.That(result.Stream, Is.Null);
        Assert.That(result.ArticleHeaders, Is.Null);
    }

    [Test]
    public async Task ArticleAsync_StreamCanBeReadCompletely()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.Stream, Is.Not.Null);
        Assert.That(result.ArticleHeaders, Is.Not.Null);

        // Read entire stream
        using var streamReader = new StreamReader(result.Stream);
        var content = await streamReader.ReadToEndAsync(cancellationToken);

        Assert.That(content, Is.Not.Null);
        Assert.That(content.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task ArticleAsync_ReleasesConnectionForNextCommand()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Start ARTICLE command (returns immediately with headers)
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var articleResult = await client.ArticleAsync(segmentId, cancellationToken);

        Assert.That(articleResult.Stream, Is.Not.Null);
        Assert.That(articleResult.ArticleHeaders, Is.Not.Null);

        // Read stream to completion
        using var streamReader = new StreamReader(articleResult.Stream);
        var content = await streamReader.ReadToEndAsync(cancellationToken);
        Assert.That(content.Length, Is.GreaterThan(0));

        // Wait a bit for the background task to complete and release the semaphore
        await Task.Delay(100, cancellationToken);

        // Assert - Should be able to execute another command after stream completes
        var dateResult = await client.DateAsync(cancellationToken);
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111),
            "DATE command should succeed after ARTICLE stream completes");
    }

    [Test]
    public async Task ArticleAsync_MultipleSegments_CanBeReadSequentially()
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
            var result = await client.ArticleAsync(segmentId, cancellationToken);
            Assert.That(result.Stream, Is.Not.Null);
            Assert.That(result.ArticleHeaders, Is.Not.Null);
            Assert.That(result.ArticleHeaders.Headers, Is.Not.Empty);

            // Read stream completely
            using var streamReader = new StreamReader(result.Stream);
            var content = await streamReader.ReadToEndAsync(cancellationToken);
            Assert.That(content.Length, Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task ArticleAsync_HeadersDictionary_IsCaseInsensitive()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, cancellationToken);

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

        // Read stream to completion
        using var streamReader = new StreamReader(result.Stream);
        await streamReader.ReadToEndAsync(cancellationToken);
    }

    [Test]
    public async Task ArticleAsync_HeadersAreAvailableBeforeStreamRead()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, cancellationToken);

        // Assert - Headers should be available immediately without reading stream
        Assert.That(result.ArticleHeaders, Is.Not.Null);
        Assert.That(result.ArticleHeaders.Headers, Is.Not.Empty);

        // Now read stream to completion
        using var streamReader = new StreamReader(result.Stream);
        await streamReader.ReadToEndAsync(cancellationToken);
    }

    [Test]
    public async Task ArticleAsync_OnConnectionReadyAgainCallback_IsInvokedAfterStreamCompletes()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var callbackInvoked = false;
        Action<ArticleBodyResult> onConnectionReadyAgain = _ => { callbackInvoked = true; };

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.ArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        // Assert - Callback should not be invoked yet (stream not fully read)
        Assert.That(callbackInvoked, Is.False, "Callback should not be invoked before stream is read");

        // Read stream to completion
        using var streamReader = new StreamReader(result.Stream);
        await streamReader.ReadToEndAsync(cancellationToken);

        // Wait a bit for the background task to complete
        await Task.Delay(100, cancellationToken);

        // Assert - Callback should now be invoked
        Assert.That(callbackInvoked, Is.True, "Callback should be invoked after stream completes");
    }

    [Test]
    public async Task ArticleAsync_OnConnectionReadyAgainCallback_IsInvokedWhenArticleNotFound()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var callbackInvoked = false;
        Action<ArticleBodyResult> onConnectionReadyAgain = _ => { callbackInvoked = true; };

        // Act - Try to get an invalid segment
        var invalidSegmentId = "invalid-segment-id-does-not-exist@test.com";
        var result = await client.ArticleAsync(invalidSegmentId, onConnectionReadyAgain, cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(430));
        Assert.That(result.Stream, Is.Null);

        // Callback should be invoked even when article doesn't exist
        Assert.That(callbackInvoked, Is.True, "Callback should be invoked even when article doesn't exist");
    }
}
