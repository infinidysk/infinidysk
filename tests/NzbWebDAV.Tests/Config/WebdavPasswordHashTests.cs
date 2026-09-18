using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Config;

[Collection(nameof(ConfigPathCollection))]
public sealed class WebdavPasswordHashTests
{
    [Fact]
    public void EnvironmentPassword_IsHashedOnceAndTracksChanges()
    {
        var previous = Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
        try
        {
            var password = $"env-{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable("WEBDAV_PASSWORD", password);
            var config = new ConfigManager();

            var first = config.GetWebdavPasswordHash();
            var second = config.GetWebdavPasswordHash();

            Assert.NotNull(first);
            // A fresh PBKDF2 hash per call would differ (random salt) and defeat the
            // credential caches that key on the hash.
            Assert.Equal(first, second);
            Assert.True(PasswordUtil.Verify(first!, password));
            Assert.False(PasswordUtil.Verify(first, "wrong-password"));

            var changed = $"env-{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable("WEBDAV_PASSWORD", changed);
            var third = config.GetWebdavPasswordHash();

            Assert.NotEqual(first, third);
            Assert.True(PasswordUtil.Verify(third!, changed));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBDAV_PASSWORD", previous);
        }
    }

    [Fact]
    public void PersistedPasswordHash_OverridesEnvironmentPassword()
    {
        var previous = Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("WEBDAV_PASSWORD", "legacy-password");
            var config = new ConfigManager();
            var persistedHash = PasswordUtil.Hash("persisted-password");
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.WebdavPass, ConfigValue = persistedHash },
            ]);

            Assert.Equal(persistedHash, config.GetWebdavPasswordHash());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBDAV_PASSWORD", previous);
        }
    }
}
