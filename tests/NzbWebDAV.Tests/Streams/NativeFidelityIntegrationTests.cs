using NzbWebDAV.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

public sealed class NativeFidelityIntegrationTests
{
    [Theory]
    [InlineData("complete", 15)]
    [InlineData("missing", 0)]
    [InlineData("short", 0)]
    [InlineData("wrong-offset", 0)]
    public async Task NativeStorage_AdmitsOnlyCompleteVerifiedFinalFileBytes(string middleKind, long coverage)
    {
        var directory = Directory.CreateTempSubdirectory("native-fidelity-");
        try
        {
            var data = new Dictionary<string, byte[]>
            {
                ["one"] = "abcde"u8.ToArray(), ["three"] = "klmno"u8.ToArray(),
            };
            if (middleKind != "missing")
                data["two"] = middleKind == "short" ? "fg"u8.ToArray() : "fghij"u8.ToArray();
            var ranges = new LongRange[] { new(0, 5), new(5, 10), new(10, 15) };
            using var client = new FakeNntpClient(data, useCachedYencStreams: true,
                segmentRanges: new Dictionary<string, LongRange>
                {
                    ["one"] = ranges[0],
                    ["two"] = middleKind == "wrong-offset" ? new LongRange(50, 55) : ranges[1],
                    ["three"] = ranges[2],
                });
            await using var store = new NativeCacheStore(Path.Combine(directory.FullName, "index.db"),
                [new NativeCacheFolder { Path = directory.FullName, MinFreeBytes = 0 }]);
            var identity = new NativeCacheIdentity("file", "generation", 15);
            await using (var stream = new NativeCachedStream(store, identity,
                _ => Task.FromResult<Stream>(new NzbFileStream(["one", "two", "three"], 15, client, 0,
                    ranges, fileName: "native-fidelity-" + Guid.NewGuid().ToString("N"),
                    segmentByteRangesTrusted: true)), () => true))
            {
                var bytes = new byte[15];
                await stream.ReadExactlyAsync(bytes);
                Assert.Equal("abcde"u8.ToArray(), bytes[..5]);
                Assert.Equal("klmno"u8.ToArray(), bytes[10..]);
            }
            Assert.Equal(coverage, await store.GetCoverageAsync(identity));
            if (coverage > 0)
            {
                await using var hit = new NativeCachedStream(store, identity,
                    _ => throw new InvalidOperationException("Verified cache hit opened the source."), () => true);
                var bytes = new byte[15];
                await hit.ReadExactlyAsync(bytes);
                Assert.Equal("abcdefghijklmno"u8.ToArray(), bytes);
            }
        }
        finally { directory.Delete(recursive: true); }
    }
}
