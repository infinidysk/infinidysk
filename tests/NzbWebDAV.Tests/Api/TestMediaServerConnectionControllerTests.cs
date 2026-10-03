using System.Net;
using NzbWebDAV.Api.Controllers.TestMediaServerConnection;

namespace NzbWebDAV.Tests.Api;

public class TestMediaServerConnectionControllerTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.BadGateway, "HTTP 502")]
    public void DescribeHttpError_WithStatus_UsesBoundedStatusOnly(
        HttpStatusCode status,
        string expected)
    {
        Assert.Equal(expected, TestMediaServerConnectionController.DescribeHttpError(status));
    }

    [Fact]
    public void DescribeHttpError_WithoutStatus_DoesNotEchoExceptionDetails()
    {
        Assert.Equal(
            "Connection failed",
            TestMediaServerConnectionController.DescribeHttpError(null));
    }
}
