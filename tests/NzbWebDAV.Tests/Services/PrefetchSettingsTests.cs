using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchSettingsTests
{
    [Fact]
    public void ConfidenceAndCooldown_AreExplicitConfigurableControls()
    {
        var settings = PrefetchSettings.Parse("{\"ConfidenceThreshold\":0.8,\"CooldownMinutes\":3}");
        Assert.Equal(0.8, settings.ConfidenceThreshold);
        Assert.Equal(3, settings.CooldownMinutes);
        var limits = PrefetchSettings.Parse("{\"QueueCapacity\":8,\"MaxRetries\":0,\"IntentTtlHours\":2,\"VerifiedSessionExpirySeconds\":45}");
        Assert.Equal(8, limits.QueueCapacity);
        Assert.Equal(0, limits.MaxRetries);
        Assert.Equal(2, limits.IntentTtlHours);
        Assert.Equal(45, limits.VerifiedSessionExpirySeconds);
    }

    [Fact]
    public void DefaultPolicies_AreOffAndBounded()
    {
        var settings = PrefetchSettings.Parse(null);
        Assert.False(settings.Enabled);
        Assert.Empty(settings.DisabledLibraries);
        Assert.Equal(10_000_000_000, settings.DailyByteBudget);
        Assert.True(settings.FullFileWarming);
        Assert.InRange(settings.MaxConcurrentJobs, 1, 4);
    }

    [Fact]
    public void DisabledLibraries_RoundTripStableIdentity()
    {
        var settings = PrefetchSettings.Parse("""{"DisabledLibraries":[{"ServerId":"plex","LibraryId":"2","Type":"show"}]}""");

        Assert.Equal(new PrefetchLibraryIdentity("plex", "2", "show"), Assert.Single(settings.DisabledLibraries));
    }

    [Fact]
    public void DisabledLibraries_AreBounded()
    {
        var libraries = string.Join(',', Enumerable.Range(0, 129)
            .Select(index => $$"""{"ServerId":"plex","LibraryId":"{{index}}","Type":"movie"}"""));

        Assert.Throws<ArgumentException>(() => PrefetchSettings.Parse($$"""{"DisabledLibraries":[{{libraries}}]}"""));
    }

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("show", "show")]
    [InlineData("episode", "show")]
    [InlineData("clip", "show")]
    public void SourceLibraryType_NormalizesSchedulerIdentity(string sourceType, string expected) =>
        Assert.Equal(expected, PrefetchSettings.LibraryType(new PrefetchSource { Type = sourceType }));

    [Theory]
    [InlineData("{\"MaxConcurrentJobs\":100}")]
    [InlineData("{\"RealtimeCheckIntervalSeconds\":0}")]
    [InlineData("{\"Sources\":[{\"ServerId\":\"one\",\"Key\":\"https://other-host/steal\"}]}")]
    [InlineData("{\"Sources\":null}")]
    [InlineData("{\"Users\":null}")]
    [InlineData("{\"DisabledLibraries\":null}")]
    [InlineData("{\"DisabledLibraries\":[{\"ServerId\":\"\",\"LibraryId\":\"2\",\"Type\":\"show\"}]}")]
    [InlineData("{\"DisabledLibraries\":[{\"ServerId\":\"plex\",\"LibraryId\":\"2\",\"Type\":\"episode\"}]}")]
    [InlineData("{\"DisabledLibraries\":[{\"ServerId\":\"plex\",\"LibraryId\":\"2\",\"Type\":\"show\"},{\"ServerId\":\"plex\",\"LibraryId\":\"2\",\"Type\":\"show\"}]}")]
    [InlineData("{\"ConfidenceThreshold\":1.1}")]
    [InlineData("{\"CooldownMinutes\":0}")]
    public void UnsafeOrUnboundedPolicies_AreRejected(string json) =>
        Assert.Throws<ArgumentException>(() => PrefetchSettings.Parse(json));
}
