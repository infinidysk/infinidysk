using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public sealed class NativeCachedStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-stream-tests-" + Guid.NewGuid().ToString("N"));
    public NativeCachedStreamTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task VerifiedFill_SubsequentReadNeedsNoSourceOpen()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await using (var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(new EvidenceStream(true)), () => true))
        {
            var bytes = new byte[3];
            Assert.Equal(3, await stream.ReadAsync(bytes));
            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        }
        await using var hit = new NativeCachedStream(store, id, _ => throw new InvalidOperationException("Cache hit opened source"), () => true);
        Assert.Equal(3, await hit.ReadAsync(new byte[3]));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnverifiedOrStaleSource_IsReturnedButNotCached(bool evidence, bool current)
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await using var stream = new NativeCachedStream(store, id, _ => Task.FromResult<Stream>(new EvidenceStream(evidence)), () => current);
        Assert.Equal(3, await stream.ReadAsync(new byte[3]));
        Assert.Equal(0, await store.GetCoverageAsync(id));
    }

    private NativeCacheStore CreateStore() => new(Path.Combine(_root, "index.db"),
        [new NativeCacheFolder { Path = _root, MinFreeBytes = 0 }]);

    [Fact]
    public async Task GenerationChanges_InvalidateAlreadyBufferedCacheHit()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await store.WriteBlockAsync(id, 0, new byte[] { 1, 2, 3 });
        var current = true;
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[] { 9, 9, 9 })), () => current);
        var bytes = new byte[1];
        await stream.ReadAsync(bytes);
        Assert.Equal(1, bytes[0]);
        current = false;
        await stream.ReadAsync(bytes);
        Assert.Equal(9, bytes[0]);
    }

    [Fact]
    public async Task SpeculativeTailFailure_DoesNotBreakReadablePrefix()
    {
        await using var store = CreateStore();
        var id = new NativeCacheIdentity("id", "version", 3);
        await using var stream = new NativeCachedStream(store, id,
            _ => Task.FromResult<Stream>(new FailingTailStream()), () => true);
        var bytes = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(bytes));
        Assert.Equal(1, bytes[0]);
        Assert.Equal(0, await store.GetCoverageAsync(id));
    }

    [Fact]
    public async Task ConcurrentMisses_CoalesceOneVerifiedFill()
    {
        await using var store = CreateStore();
        var identity = new NativeCacheIdentity("item", "v1", 3);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        async Task<Stream> Open(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            opened.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new EvidenceStream(true);
        }
        await using var first = new NativeCachedStream(store, identity, Open, () => true);
        await using var second = new NativeCachedStream(store, identity, Open, () => true);
        var firstRead = first.ReadAsync(new byte[3]).AsTask();
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRead = second.ReadAsync(new byte[3]).AsTask();
        release.SetResult();
        await Task.WhenAll(firstRead, secondRead).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, opens);
    }

    [Fact]
    public void MetadataPath_RejectsSymlinkAncestors()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        Assert.Throws<ArgumentException>(() => NativeFileSystem.RequireLocalMetadata(Path.Combine(link, "index.db")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class EvidenceStream(bool verified) : MemoryStream(new byte[] { 1, 2, 3 }), ICacheReadEvidence
    {
        public bool LastReadCacheable => verified;
    }

    private sealed class FailingTailStream() : MemoryStream(new byte[] { 1, 2, 3 }), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new IOException("Later article unavailable");
            return base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
        }
    }
}
