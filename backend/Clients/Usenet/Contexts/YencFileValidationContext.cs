using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Contexts;

internal sealed class YencFileValidationContext : IDisposable
{
    private static readonly AsyncLocal<int?> ExpectedTotalParts = new();
    private readonly int? _previous = ExpectedTotalParts.Value;

    private YencFileValidationContext(int expectedTotalParts)
    {
        ExpectedTotalParts.Value = expectedTotalParts;
    }

    public static int? CurrentExpectedTotalParts => ExpectedTotalParts.Value;

    public static bool MatchesExpectedFile(UsenetYencHeader header) =>
        CurrentExpectedTotalParts is not { } expectedTotalParts
        || (expectedTotalParts == 1 && header.TotalParts == 0)
        || header.TotalParts == expectedTotalParts;

    public static IDisposable Begin(int expectedTotalParts) =>
        new YencFileValidationContext(expectedTotalParts);

    public void Dispose() => ExpectedTotalParts.Value = _previous;
}