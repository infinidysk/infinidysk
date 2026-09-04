using NzbWebDAV.Database;

namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// A [Fact] that only runs when explicitly opted into via the
/// RUN_NPGSQL_CONCURRENCY_TESTS=1 environment variable (with DATABASE_PROVIDER=postgres
/// also set -- see below), and is otherwise skipped at xUnit's discovery stage.
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
///
/// Also requires DatabaseProviderConfig.IsPostgres (DATABASE_PROVIDER=postgres), the same
/// env var PostgresMigrationTests gates on: DavDatabaseContext.OnModelCreating() reads it
/// directly to decide whether to apply the Postgres wall-clock DateTime converters, so the
/// model built without it doesn't match the Postgres migrations at all -- independent of
/// which connection string or context subclass a test uses. Checking it here, rather than
/// setting it at runtime from inside the test, avoids mutating process-wide environment
/// state that could leak into unrelated SQLite tests running concurrently in the same
/// process (xUnit does not isolate environment variables per test).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OptInPostgresFactAttribute : Xunit.FactAttribute
{
    public OptInPostgresFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_NPGSQL_CONCURRENCY_TESTS") != "1")
        {
            Skip = "Opt-in only: set RUN_NPGSQL_CONCURRENCY_TESTS=1, DATABASE_PROVIDER=postgres, " +
                   "and DATABASE_CONNECTION_STRING with a Postgres instance on 127.0.0.1:15432 " +
                   "to run this test.";
        }
        else if (!DatabaseProviderConfig.IsPostgres)
        {
            Skip = "RUN_NPGSQL_CONCURRENCY_TESTS=1 is set, but DATABASE_PROVIDER=postgres is " +
                   "not: DavDatabaseContext builds its model differently depending on this " +
                   "variable, so it must be set for this test's schema to match the Postgres " +
                   "migrations.";
        }
    }
}
