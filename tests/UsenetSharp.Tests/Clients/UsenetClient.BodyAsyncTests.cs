using UsenetSharp.Clients;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharpTest.Clients;

[TestFixture]
public class UsenetClientBodyAsyncTests
{
    [Test]
    public async Task BodyAsync_WithValidSegmentId_ReturnsResponseWithStream()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.BodyAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.SegmentId, Is.EqualTo(segmentId));
        Assert.That(result.Stream, Is.Not.Null);

        // Read some data from the stream to verify it works
        using var streamReader = new StreamReader(result.Stream);
        var firstLine = await streamReader.ReadLineAsync(cancellationToken);
        Assert.That(firstLine, Is.Not.Null);
        Assert.That(firstLine, Is.Not.Empty);

        // Read the rest to ensure the background task completes
        await streamReader.ReadToEndAsync(cancellationToken);
    }

    [Test]
    public async Task BodyAsync_WithInvalidSegmentId_ReturnsNullStream()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var invalidSegmentId = "invalid-segment-id-does-not-exist@test.com";
        var result = await client.BodyAsync(invalidSegmentId, cancellationToken);

        // Assert
        Assert.That(result.SegmentId, Is.EqualTo(invalidSegmentId));
        Assert.That(result.Stream, Is.Null);
        Assert.That(result.ResponseCode, Is.EqualTo(430)); // No article with that message-id
    }

    [Test]
    public async Task BodyAsync_WithoutConnection_ThrowsException()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();

        // Act & Assert
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var exception = Assert.ThrowsAsync<UsenetNotConnectedException>(async () =>
            await client.BodyAsync(segmentId, cancellationToken));

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("Not connected"));
    }

    [Test]
    public async Task BodyAsync_WithoutAuthentication_ReturnsAuthenticationRequired()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        // Act (no authentication)
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.BodyAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(480)); // Authentication required
        Assert.That(result.Stream, Is.Null);
    }

    [Test]
    public async Task BodyAsync_StreamCanBeReadCompletely()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var result = await client.BodyAsync(segmentId, cancellationToken);

        // Assert
        Assert.That(result.Stream, Is.Not.Null);

        // Read entire stream
        using var streamReader = new StreamReader(result.Stream);
        var content = await streamReader.ReadToEndAsync(cancellationToken);

        Assert.That(content, Is.Not.Null);
        Assert.That(content.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task BodyAsync_ReleasesConnectionForNextCommand()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Start BODY command (returns immediately)
        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";
        var bodyResult = await client.BodyAsync(segmentId, cancellationToken);

        Assert.That(bodyResult.Stream, Is.Not.Null);

        // Read stream to completion
        using var streamReader = new StreamReader(bodyResult.Stream);
        var content = await streamReader.ReadToEndAsync(cancellationToken);
        Assert.That(content.Length, Is.GreaterThan(0));

        // Wait a bit for the background task to complete and release the semaphore
        await Task.Delay(100, cancellationToken);

        // Assert - Should be able to execute another command after stream completes
        var dateResult = await client.DateAsync(cancellationToken);
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111), "DATE command should succeed after BODY stream completes");
    }

    [Test]
    public async Task BodyAsync_MultipleSegments_CanBeReadSequentially()
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
            var result = await client.BodyAsync(segmentId, cancellationToken);
            Assert.That(result.Stream, Is.Not.Null);

            // Read stream completely
            using var streamReader = new StreamReader(result.Stream);
            var content = await streamReader.ReadToEndAsync(cancellationToken);
            Assert.That(content.Length, Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task BodyAsync_OnConnectionReadyAgainCallback_IsInvokedAfterStreamCompletes()
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
        var result = await client.BodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

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
    public async Task BodyAsync_OnConnectionReadyAgainCallback_IsInvokedWhenArticleNotFound()
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
        var result = await client.BodyAsync(invalidSegmentId, onConnectionReadyAgain, cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(430));
        Assert.That(result.Stream, Is.Null);

        // Callback should be invoked even when article doesn't exist
        Assert.That(callbackInvoked, Is.True, "Callback should be invoked even when article doesn't exist");
    }

    [Test]
    public async Task BodyAsync_ReturnsSameStreamDataAsArticleAsync()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = cts.Token;

        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);
        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        var segmentId = "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo";

        // Act - Get data from BodyAsync
        var bodyResult = await client.BodyAsync(segmentId, cancellationToken);
        Assert.That(bodyResult.Stream, Is.Not.Null);

        using var bodyMs = new MemoryStream();
        await bodyResult.Stream.CopyToAsync(bodyMs, cancellationToken);
        var bodyBytes = bodyMs.ToArray();

        // Act - Get data from ArticleAsync
        var articleResult = await client.ArticleAsync(segmentId, cancellationToken);
        Assert.That(articleResult.Stream, Is.Not.Null);

        using var articleMs = new MemoryStream();
        await articleResult.Stream.CopyToAsync(articleMs, cancellationToken);
        var articleBytes = articleMs.ToArray();

        // Assert - Both streams should contain identical data byte-for-byte
        Assert.That(bodyBytes.Length, Is.GreaterThan(0), "BodyAsync stream should contain data");
        Assert.That(articleBytes.Length, Is.GreaterThan(0), "ArticleAsync stream should contain data");
        Assert.That(bodyBytes, Is.EqualTo(articleBytes), "BodyAsync and ArticleAsync should return identical stream data");
    }
}
