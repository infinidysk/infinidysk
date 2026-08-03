using UsenetSharp.Clients;

namespace UsenetSharpTest.Clients;

public class ConnectAsyncTests
{
    [Test]
    public async Task ConnectAsync_WithValidCredentials_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await client.ConnectAsync(
            Credentials.Host,
            563, // Standard NNTP SSL port
            true, // Use SSL
            cancellationToken
        ));
    }

    [Test]
    public async Task ConnectAsync_WithoutSsl_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await client.ConnectAsync(
            Credentials.Host,
            119, // Standard NNTP non-SSL port
            false, // Do not use SSL
            cancellationToken
        ));
    }

    [Test]
    public async Task ConnectAsync_WithInvalidHost_ThrowsException()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () =>
            await client.ConnectAsync(
                "invalid.host.that.does.not.exist.example.com",
                563,
                true,
                cancellationToken
            ));

        Assert.That(exception, Is.Not.Null);
    }

    [Test]
    public async Task ConnectAsync_WithInvalidPort_ThrowsException()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await client.ConnectAsync(
                Credentials.Host,
                99999, // Invalid port
                true,
                cancellationToken
            ));

        Assert.That(exception, Is.Not.Null);
    }
}
