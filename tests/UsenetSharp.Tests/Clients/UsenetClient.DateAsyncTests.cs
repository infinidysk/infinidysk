using UsenetSharp.Clients;
using UsenetSharp.Exceptions;

namespace UsenetSharpTest.Clients;

public class DateAsyncTests
{
    [Test]
    public async Task DateAsync_WithValidConnection_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var dateResult = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111), "Response code should be 111");
        Assert.That(dateResult.DateTime, Is.Not.Null, "DateTime should be populated");
    }

    [Test]
    public async Task DateAsync_ReturnsUtcDateTime()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var dateResult = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111), "Response code should be 111");
        Assert.That(dateResult.DateTime, Is.Not.Null, "DateTime should be populated");
        Assert.That(dateResult.DateTime.Value.Offset, Is.EqualTo(TimeSpan.Zero), "DateTime should be UTC");
    }

    [Test]
    public async Task DateAsync_ReturnsReasonableDateTime()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var dateResult = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111), "Response code should be 111");
        Assert.That(dateResult.DateTime, Is.Not.Null, "DateTime should be populated");

        var now = DateTimeOffset.UtcNow;
        var timeDifference = (now - dateResult.DateTime.Value).Duration();

        // Server time should be within 5 minutes of current time
        Assert.That(timeDifference, Is.LessThan(TimeSpan.FromMinutes(5)),
            "Server time should be reasonably close to current time");
    }

    [Test]
    public void DateAsync_WithoutConnection_ThrowsException()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Act & Assert
        var exception = Assert.ThrowsAsync<UsenetNotConnectedException>(
            async () => await client.DateAsync(cancellationToken));

        Assert.That(exception.Message, Does.Contain("Not connected"),
            "Exception message should indicate not connected");
    }

    [Test]
    public async Task DateAsync_WithSslConnection_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect with SSL and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var dateResult = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111), "Response code should be 111");
        Assert.That(dateResult.DateTime, Is.Not.Null, "DateTime should be populated");
    }

    [Test]
    public async Task DateAsync_WithoutSslConnection_ReturnsSuccess()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect without SSL and authenticate
        await client.ConnectAsync(Credentials.Host, 119, false, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act
        var dateResult = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(dateResult.ResponseCode, Is.EqualTo(111), "Response code should be 111");
        Assert.That(dateResult.DateTime, Is.Not.Null, "DateTime should be populated");
    }

    [Test]
    public async Task DateAsync_MultipleCallsReturnDifferentTimes()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Call DATE twice with a small delay
        var dateResult1 = await client.DateAsync(cancellationToken);
        await Task.Delay(1000); // Wait 1 second
        var dateResult2 = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(dateResult1.ResponseCode, Is.EqualTo(111), "First DATE command should succeed");
        Assert.That(dateResult2.ResponseCode, Is.EqualTo(111), "Second DATE command should succeed");
        Assert.That(dateResult1.DateTime, Is.Not.Null, "First DateTime should be populated");
        Assert.That(dateResult2.DateTime, Is.Not.Null, "Second DateTime should be populated");

        // Second time should be equal to or greater than the first
        Assert.That(dateResult2.DateTime.Value, Is.GreaterThanOrEqualTo(dateResult1.DateTime.Value),
            "Second DATE should be later than or equal to the first");
    }

    [Test]
    public async Task DateAsync_WithoutAuthentication_ReturnsAuthenticationRequired()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect but don't authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        // Act
        var result = await client.DateAsync(cancellationToken);

        // Assert
        Assert.That(result.ResponseCode, Is.EqualTo(480), "Should have 480 Authentication Required response code");
    }
}
