using System.Runtime.CompilerServices;
using NzbWebDAV.Api.SabControllers.AddUrl;

namespace NzbWebDAV.Tests.Api;

public class AddUrlRequestStaticInitTests
{
    [Fact]
    public void StaticInitializer_CompletesSuccessfully()
    {
        var exception = Record.Exception(() =>
            RuntimeHelpers.RunClassConstructor(typeof(AddUrlRequest).TypeHandle));

        Assert.Null(exception);
    }
}
