using NzbWebDAV.Api.Controllers.TestRcloneConnection;

namespace NzbWebDAV.Tests.Api;

public class TestRcloneConnectionControllerTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5572")]
    [InlineData("http://localhost:5572")]
    [InlineData("http://[::1]:5572")]
    public void DescribeConnectionError_ForLoopbackHost_AddsContainerGuidance(string host)
    {
        var result = TestRcloneConnectionController.DescribeConnectionError(
            host,
            "Connection refused");

        Assert.Contains("use the rclone service name", result);
    }

    [Fact]
    public void DescribeConnectionError_ForServiceHost_PreservesReason()
    {
        var result = TestRcloneConnectionController.DescribeConnectionError(
            "http://nzbdav_rclone:5572",
            "Connection refused");

        Assert.Equal("Connection refused", result);
    }
}