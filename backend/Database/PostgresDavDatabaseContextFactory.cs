using Microsoft.EntityFrameworkCore.Design;

namespace NzbWebDAV.Database;

/// <summary>
/// Design-time factory for creating the PostgreSQL-only migration baseline.
/// </summary>
public sealed class PostgresDavDatabaseContextFactory
    : IDesignTimeDbContextFactory<PostgresDavDatabaseContext>
{
    public PostgresDavDatabaseContext CreateDbContext(string[] args) =>
        new();
}
