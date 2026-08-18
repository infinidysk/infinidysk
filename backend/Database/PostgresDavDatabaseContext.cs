using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database;

/// <summary>
/// Migration-only context for a fresh PostgreSQL main database. Runtime services
/// continue to use <see cref="DavDatabaseContext"/> so provider selection remains
/// transparent to the application.
/// </summary>
public sealed class PostgresDavDatabaseContext : DavDatabaseContext
{
    public PostgresDavDatabaseContext()
        : base(CreateOptions())
    {
    }

    public PostgresDavDatabaseContext(DbContextOptions<PostgresDavDatabaseContext> options)
        : base(options)
    {
    }

    private static DbContextOptions<PostgresDavDatabaseContext> CreateOptions()
    {
        if (!DatabaseProviderConfig.IsPostgres)
        {
            throw new InvalidOperationException(
                "PostgresDavDatabaseContext requires DATABASE_PROVIDER=postgres.");
        }

        var builder = new DbContextOptionsBuilder<PostgresDavDatabaseContext>();
        builder.UseNpgsql(DatabaseProviderConfig.PostgresConnectionString);
        return builder.Options;
    }
}
