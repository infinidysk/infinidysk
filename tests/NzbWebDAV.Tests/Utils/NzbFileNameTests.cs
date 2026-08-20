using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class NzbFileNameTests
{
    [Fact]
    public void Resolve_ThrowsArgumentExceptionWhenNeitherNameIsUsable()
    {
        Assert.Throws<ArgumentException>(() => NzbFileName.Resolve(null, null));
        Assert.Throws<ArgumentException>(() => NzbFileName.Resolve("  ", ""));
    }
}
