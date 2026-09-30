using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Tests.Streams;

public sealed class NativeSharedGenerationTests
{
    [Fact]
    public async Task BufferedBytes_AreNotServedOrAttachedAfterSourceInvalidation()
    {
        var source = new GenerationStream("old");
        await using var entry = Start(source);
        await using var reader = entry.TryAttach(0, (_, _) => throw new InvalidOperationException("Unexpected fallback"), out _)!;
        Assert.NotNull(reader);
        Assert.Equal(1, await reader.ReadAsync(new byte[1]));
        source.IsSourceCurrent = false;
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1]).AsTask());
        Assert.False(entry.IsAttachable);
        Assert.Null(entry.TryAttach(0, (_, _) => throw new InvalidOperationException("Unexpected fallback"), out _));
    }

    [Theory]
    [InlineData("old", true)]
    [InlineData("new", false)]
    public async Task PrivateFallback_AfterOldPrefixRequiresSameGeneration(string fallbackGeneration, bool accepted)
    {
        var source = new GenerationStream("old");
        await using var entry = Start(source);
        await using var reader = entry.TryAttach(0, (offset, _) =>
        {
            Stream fallback = new GenerationStream(fallbackGeneration) { Position = offset };
            return Task.FromResult(fallback);
        }, out _)!;
        Assert.NotNull(reader);
        Assert.Equal(1, await reader.ReadAsync(new byte[1]));
        reader.Position = 1000; // Outside the tiny retained ring: force private reopening.
        if (accepted) Assert.Equal(1, await reader.ReadAsync(new byte[1]));
        else await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(new byte[1]).AsTask());
    }

    private static SharedStreamEntry Start(Stream source)
    {
        var entry = new SharedStreamEntry("/movie", 0, source.Length, 32, TimeSpan.FromSeconds(10),
            CancellationToken.None, chunkSize: 8, leadBytes: 16);
        entry.BindAndStart(new DetachedStreamLease { Stream = source, Ownership = new EmptyOwner(),
            ContentIdentity = new SharedContentIdentity("same-item", null, source.Length) });
        return entry;
    }

    private sealed class GenerationStream(string generation) : MemoryStream(new byte[2048]), IStreamGenerationEvidence
    {
        public string GenerationIdentity => generation;
        public bool IsSourceCurrent { get; set; } = true;
    }
    private sealed class EmptyOwner : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
