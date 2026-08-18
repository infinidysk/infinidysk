using NzbWebDAV.Utils;

namespace NzbWebDAV.Database;

public enum DatabaseProvider
{
    Sqlite,
    Postgres
}

/// <summary>
/// Selects the operational database provider. SQLite remains the default so
/// existing bind-mounted <c>/config</c> installs require no configuration.
/// </summary>
public static class DatabaseProviderConfig
{
    public static DatabaseProvider Provider
    {
        get
        {
            var value = EnvironmentUtil.GetEnvironmentVariable("DATABASE_PROVIDER");
            return value?.Trim().ToLowerInvariant() switch
            {
                null or "" or "sqlite" => DatabaseProvider.Sqlite,
                "postgres" or "postgresql" => DatabaseProvider.Postgres,
                _ => throw new InvalidOperationException(
                    "DATABASE_PROVIDER must be either 'sqlite' or 'postgres'.")
            };
        }
    }

    public static bool IsPostgres => Provider == DatabaseProvider.Postgres;

    public static string PostgresConnectionString
    {
        get
        {
            var value = EnvironmentUtil.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "DATABASE_CONNECTION_STRING is required when DATABASE_PROVIDER=postgres.");
            }

            return value;
        }
    }
}
