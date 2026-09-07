using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class DisposableOwnerTests
{
    [Fact]
    public void OwnedResource_IsDisposedWhenScopeExits()
    {
        using var resource = new MemoryStream();
        using (var owner = new DisposableOwner<MemoryStream>())
        {
            owner.TakeOwnership(() => resource);
            Assert.Same(resource, owner.Value);
        }
        Assert.False(resource.CanRead);
    }

    [Fact]
    public void TransferredResource_SurvivesScopeExit()
    {
        using var resource = new MemoryStream();
        using (var owner = new DisposableOwner<MemoryStream>(() => resource))
            Assert.Same(resource, owner.ReleaseOwnership());
        Assert.True(resource.CanRead);
    }

    [Fact]
    public void Cancellation_DisposesResourceBeforeOwnershipTransfers()
    {
        using var resource = new MemoryStream();
        Assert.Throws<OperationCanceledException>((Action)(() =>
        {
            using var owner = new DisposableOwner<MemoryStream>(() => resource);
            throw new OperationCanceledException();
        }));
        Assert.False(resource.CanRead);
    }

    [Fact]
    public void Dispose_IsIdempotentAndClearsOwnership()
    {
        using var resource = new MemoryStream();
        using var owner = new DisposableOwner<MemoryStream>(() => resource);
        owner.Dispose();
        owner.Dispose();
        Assert.Null(owner.ReleaseOwnership());
        Assert.Throws<InvalidOperationException>(() => owner.Value);
    }
}