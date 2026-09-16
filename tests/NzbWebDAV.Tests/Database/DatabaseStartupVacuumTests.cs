using System.Collections;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(ConfigPathCollection))]
public sealed class DatabaseStartupVacuumTests : IDisposable
{
    private readonly string _configPath;
    private readonly string? _previousConfigPath;

    public DatabaseStartupVacuumTests()
    {
        _configPath = Path.Join(Path.GetTempPath(), $"nzbdav-vacuum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configPath);
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
        DavDatabaseContext.ResetOptionsForTests();
    }

    [Fact]
    public async Task IsDatabaseStartupVacuumEnabledAsync_UsesEnvironmentOverlay()
    {
        await using (var databaseContext = new DavDatabaseContext())
            await databaseContext.Database.MigrateAsync();

        var environmentOverlay = ConfigEnvironmentOverlay.LoadFromEnvironment(new Hashtable
        {
            ["NZBDAV_CONFIG__DB__IS_STARTUP_VACUUM_ENABLED"] = "true",
        });

        var enabled = await Program.IsDatabaseStartupVacuumEnabledAsync(
            environmentOverlay,
            CancellationToken.None);

        Assert.True(enabled);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        DavDatabaseContext.ResetOptionsForTests();
        try { Directory.Delete(_configPath, recursive: true); } catch (IOException) { }
    }
}