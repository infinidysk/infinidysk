using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(ConfigPathCollection))]
public sealed class DavDatabaseContextFactoryTests
{
    [Fact]
    public async Task HostedServices_CreateContextsFromRegisteredFactory()
    {
        var configRoot = Path.Join(Path.GetTempPath(), $"nzbdav-db-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configRoot);
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", configRoot);
        DavDatabaseContext.ResetOptionsForTests();

        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<DavDatabaseContext>(options =>
                DavDatabaseContext.ConfigureOptions(options));
            services.AddScoped(sp =>
                sp.GetRequiredService<IDbContextFactory<DavDatabaseContext>>().CreateDbContext());
            services.AddSingleton<BlobCleanupService>();

            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();

            await using (var ctx = factory.CreateDbContext())
            {
                await ctx.Database.MigrateAsync();
                Assert.False(ctx.Database.IsNpgsql());
            }

            Assert.NotNull(provider.GetRequiredService<BlobCleanupService>());

            await using (var scope = provider.CreateAsyncScope())
            {
                var scoped = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
                Assert.Equal(0, await scoped.Accounts.CountAsync());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            DavDatabaseContext.ResetOptionsForTests();
            try
            {
                Directory.Delete(configRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for transient SQLite file handles.
            }
        }
    }
}
