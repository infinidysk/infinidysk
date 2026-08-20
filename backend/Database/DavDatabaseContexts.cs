using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database;

/// <summary>
/// Creates <see cref="DavDatabaseContext"/> instances while honoring the
/// optional injection overrides used by tests and migration runners.
/// Centralized because CA2000 cannot follow the inline
/// `factory?.Invoke() ?? new DavDatabaseContext()` coalescing pattern and
/// false-flags every such site; the early returns here are clean ownership
/// transfers.
/// </summary>
internal static class DavDatabaseContexts
{
    public static DavDatabaseContext Create(Func<DavDatabaseContext>? factoryOverride)
        => Create(factoryOverride, dbContextFactory: null);

    public static DavDatabaseContext Create(
        Func<DavDatabaseContext>? factoryOverride,
        IDbContextFactory<DavDatabaseContext>? dbContextFactory)
    {
        var overridden = factoryOverride?.Invoke();
        if (overridden is not null) return overridden;
        if (dbContextFactory is not null) return dbContextFactory.CreateDbContext();
        return new DavDatabaseContext();
    }
}
