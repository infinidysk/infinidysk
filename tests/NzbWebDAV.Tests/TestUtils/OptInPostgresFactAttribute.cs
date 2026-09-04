namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// A [Fact] that only runs when explicitly opted into via the
/// RUN_NPGSQL_CONCURRENCY_TESTS=1 environment variable, and is otherwise skipped at
/// xUnit's discovery stage.
///
/// This is deliberately not the [SkippableFact]/Skip.IfNot() pattern used elsewhere in
/// this project (e.g. PostgresMigrationTests): Skip.IfNot() is a body-level check, which
/// works fine for tests that open their own connection inside the test method after the
/// check runs. It does NOT help for a test class that implements IAsyncLifetime, because
/// xUnit calls InitializeAsync() before the test body executes -- by the time a
/// Skip.IfNot() in the body would run, InitializeAsync() has already tried (and, without
/// Postgres available, already failed) to connect.
///
/// FactAttribute.Skip is evaluated by xUnit at test discovery, before the test class is
/// ever constructed or any IAsyncLifetime method runs. Setting it here is the only way to
/// keep an opted-out run from attempting a connection at all.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OptInPostgresFactAttribute : Xunit.FactAttribute
{
    public OptInPostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_NPGSQL_CONCURRENCY_TESTS") != "1")
        {
            Skip = "Opt-in only: set RUN_NPGSQL_CONCURRENCY_TESTS=1 with a Postgres " +
                   "instance on 127.0.0.1:15432 to run this test.";
        }
    }
}
