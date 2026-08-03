using UsenetSharp.Clients;
using UsenetSharp.Exceptions;

namespace UsenetSharpTest.Clients;

public class AuthenticateAsyncTests
{
    [Test]
    public async Task AuthenticateAsync_WithValidCredentials_Succeeds()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // First connect
        await client.ConnectAsync(
            Credentials.Host,
            563,
            true,
            cancellationToken
        );

        // Act & Assert - Should not throw
        Assert.DoesNotThrowAsync(async () =>
            await client.AuthenticateAsync(
                Credentials.Username,
                Credentials.Password,
                cancellationToken
            ));
    }

    [Test]
    public async Task AuthenticateAsync_WithInvalidCredentials_ReturnsErrorResponse()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // First connect
        await client.ConnectAsync(
            Credentials.Host,
            563,
            true,
            cancellationToken
        );

        // Act
        var result = await client.AuthenticateAsync(
            "invalid_username",
            "invalid_password",
            cancellationToken
        );

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ResponseMessage, Is.Not.Null.And.Not.Empty);
        Assert.That(result.ResponseCode, Is.EqualTo(481).Or.EqualTo(482).Or.EqualTo(502),
            "Response code should be 481, 482, or 502 for authentication failure");
    }

    [Test]
    public async Task AuthenticateAsync_WithoutConnection_ThrowsException()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = Assert.ThrowsAsync<UsenetNotConnectedException>(async () =>
            await client.AuthenticateAsync(
                Credentials.Username,
                Credentials.Password,
                cancellationToken
            ));

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("Not connected"),
            "Error message should indicate not connected");
    }

    [Test]
    public async Task AuthenticateAsync_AfterSuccessfulConnection_WithSsl_Succeeds()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect with SSL
        await client.ConnectAsync(
            Credentials.Host,
            563,
            true,
            cancellationToken
        );

        // Act & Assert - Should not throw
        Assert.DoesNotThrowAsync(async () =>
            await client.AuthenticateAsync(
                Credentials.Username,
                Credentials.Password,
                cancellationToken
            ));
    }

    [Test]
    public async Task AuthenticateAsync_AfterSuccessfulConnection_WithoutSsl_Succeeds()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect without SSL
        await client.ConnectAsync(
            Credentials.Host,
            119,
            false,
            cancellationToken
        );

        // Act & Assert - Should not throw
        Assert.DoesNotThrowAsync(async () =>
            await client.AuthenticateAsync(
                Credentials.Username,
                Credentials.Password,
                cancellationToken
            ));
    }
}
