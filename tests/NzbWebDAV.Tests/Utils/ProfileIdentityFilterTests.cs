using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ProfileIdentityFilterTests
{
    private static readonly HashSet<string> Strict = new(StringComparer.Ordinal) { "strict" };

    [Fact]
    public void StrictIndexer_RemovesWrongYearMovie()
    {
        var identity = Identity("Dune", 2024);
        var kept = Apply(
            [
                Result("strict", "Dune.2024.2160p.WEB-DL"),
                Result("strict", "Dune.1984.1080p.BluRay"),
            ],
            identity,
            applyMovieYear: true);

        Assert.Equal(["Dune.2024.2160p.WEB-DL"], kept.Select(x => x.Title));
    }

    [Fact]
    public void NonStrictIndexer_RetainsWrongYearMovie()
    {
        var identity = Identity("Dune", 2024);
        var kept = Apply(
            [Result("open", "Dune.1984.1080p.BluRay")],
            identity,
            applyMovieYear: true,
            strictIndexers: Strict);

        Assert.Single(kept);
    }

    [Fact]
    public void SoleWrongYearStrictResult_IsRemovedWhenCanonicalYearExists()
    {
        var identity = Identity("Dune", 2024);
        var kept = Apply(
            [Result("strict", "Dune.1984.1080p.BluRay")],
            identity,
            applyMovieYear: true);

        Assert.Empty(kept);
    }

    [Fact]
    public void ResolverFailure_LeavesResultsIntact()
    {
        var empty = new ProfileIdentityFilter.ExpectedIdentity(
            new HashSet<string>(StringComparer.Ordinal),
            MovieYear: null);
        var items = new[]
        {
            Result("strict", "Dune.1984.1080p.BluRay"),
        };

        var kept = Apply(items, empty, applyMovieYear: true);

        Assert.Equal(items, kept);
    }

    [Fact]
    public void TvResults_AreNotRejectedByShowPremiereYear()
    {
        var identity = new ProfileIdentityFilter.ExpectedIdentity(
            new HashSet<string>(StringComparer.Ordinal) { FilenameMatcher.NormalizeTitle("The Bear") },
            MovieYear: 2022);
        var kept = Apply(
            [Result("strict", "The.Bear.S03E01.2024.1080p.WEB-DL")],
            identity,
            applyMovieYear: false);

        Assert.Single(kept);
    }

    [Fact]
    public void NonStrictIndexer_PassesThroughUnchanged()
    {
        var identity = Identity("Dune", 2024);
        var items = new[]
        {
            Result("open", "Completely.Different.1999.1080p"),
            Result("strict", "Dune.2024.1080p"),
        };

        var kept = Apply(items, identity, applyMovieYear: true);

        Assert.Equal(["Completely.Different.1999.1080p", "Dune.2024.1080p"], kept.Select(x => x.Title));
    }

    private static ProfileIdentityFilter.ExpectedIdentity Identity(string title, int? year) =>
        new(new HashSet<string>(StringComparer.Ordinal) { FilenameMatcher.NormalizeTitle(title) }, year);

    private static List<Hit> Apply(
        IReadOnlyList<Hit> items,
        ProfileIdentityFilter.ExpectedIdentity identity,
        bool applyMovieYear,
        IReadOnlySet<string>? strictIndexers = null) =>
        ProfileIdentityFilter.ApplyStrictMatching(
            items,
            x => x.Indexer,
            x => x.Title,
            strictIndexers ?? Strict,
            identity,
            applyMovieYear);

    private static Hit Result(string indexer, string title) => new(indexer, title);

    private sealed record Hit(string Indexer, string Title);
}
