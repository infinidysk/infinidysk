using UsenetSharp.Clients;
using UsenetSharp.Exceptions;

namespace UsenetSharpTest.Clients;

public class StatAsyncTests
{
    private static readonly string[] ValidSegmentIds = new[]
    {
        "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
        "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
        "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV",
        "njX6awmG5Rl0lZbBbfll8WtA@M6zC3hmaiMoK.w5x",
        "vAOEczfxpsXMjg0bUPUGO7Bb@KDqE994Bw3O0.BG5"
    };

    [Test]
    public async Task StatAsync_WithValidSegmentId_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var statResult = await client.StatAsync(ValidSegmentIds[0], cancellationToken);

        // Assert
        Assert.That(statResult.ArticleExists, Is.True, "Article should exist");
        Assert.That(statResult.ResponseCode, Is.EqualTo(223), "Response code should be 223");
        Assert.That(statResult.ResponseType, Is.EqualTo(UsenetSharp.Models.UsenetResponseType.ArticleExists),
            "Response type should be ArticleExists");
    }

    [Test]
    public async Task StatAsync_WithMultipleValidSegmentIds_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act & Assert - Test all valid segment IDs
        foreach (var segmentId in ValidSegmentIds)
        {
            var statResult = await client.StatAsync(segmentId, cancellationToken);

            Assert.That(statResult.ArticleExists, Is.True, $"Article should exist for {segmentId}");
            Assert.That(statResult.ResponseCode, Is.EqualTo(223), "Response code should be 223");
            Assert.That(statResult.ResponseType, Is.EqualTo(UsenetSharp.Models.UsenetResponseType.ArticleExists),
                $"Response type should be ArticleExists for {segmentId}");
        }
    }

    [Test]
    public async Task StatAsync_WithInvalidSegmentId_ReturnsFailure()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;
        var invalidSegmentId = "invalid@segment.id";

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var statResult = await client.StatAsync(invalidSegmentId, cancellationToken);

        // Assert
        Assert.That(statResult.ArticleExists, Is.False, "Article should not exist");
        Assert.That(statResult.ResponseCode, Is.EqualTo(430), "Response code should be 430");
        Assert.That(statResult.ResponseType, Is.EqualTo(UsenetSharp.Models.UsenetResponseType.NoArticleWithThatMessageId),
            "Response type should be NoArticleWithThatMessageId");
    }

    [Test]
    public async Task StatAsync_WithoutConnection_ThrowsException()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = Assert.ThrowsAsync<UsenetNotConnectedException>(async () =>
            await client.StatAsync(ValidSegmentIds[0], cancellationToken));

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("Not connected"));
    }

    [Test]
    public async Task StatAsync_WithoutAuthentication_ReturnsAuthenticationRequired()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect but don't authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        // Act
        var result = await client.StatAsync(ValidSegmentIds[0], cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(480)); // Authentication required
        Assert.That(result.ArticleExists, Is.False);
    }

    [Test]
    public async Task StatAsync_ArticleExists_ReturnsExistsTrue()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var statResult = await client.StatAsync(ValidSegmentIds[0], cancellationToken);

        // Assert
        Assert.That(statResult.ArticleExists, Is.True, "ArticleExists should be true for valid segment");
        Assert.That(statResult.ResponseCode, Is.EqualTo(223));
        Assert.That(statResult.ResponseType, Is.EqualTo(UsenetSharp.Models.UsenetResponseType.ArticleExists));
    }
}
