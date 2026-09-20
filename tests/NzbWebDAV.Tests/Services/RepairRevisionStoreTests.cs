using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class RepairRevisionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repair-revisions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Revisions_AffectOnlyDependentSources_AndSurviveRestart()
    {
        var path = Path.Combine(_root, "revisions.db");
        string fingerprint;
        using (var revisions = new RepairRevisionStore(path))
        {
            var first = revisions.Capture(["movie", "fallback"]);
            var other = revisions.Capture(["another-movie"]);
            using (revisions.BeginPublication([RepairRevisionStore.HashSegmentId("fallback")]))
            {
                Assert.False(first.IsCurrent);
                Assert.False(revisions.Capture(["fallback"]).IsCurrent);
            }
            Assert.False(first.IsCurrent);
            Assert.True(other.IsCurrent);
            fingerprint = revisions.Capture(["movie", "fallback"]).Fingerprint;
        }
        using var reopened = new RepairRevisionStore(path);
        var snapshot = reopened.Capture(["movie", "fallback"]);
        Assert.Equal(fingerprint, snapshot.Fingerprint);
        using (reopened.BeginPublication([RepairRevisionStore.HashSegmentId("fallback")])) { }
        Assert.False(snapshot.IsCurrent);
        Assert.NotEqual(fingerprint, reopened.Capture(["movie", "fallback"]).Fingerprint);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
