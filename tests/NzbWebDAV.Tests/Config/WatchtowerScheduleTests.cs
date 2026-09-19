using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class WatchtowerScheduleTests
{
    private static ConfigItem Item(string name, string value) =>
        new() { ConfigName = name, ConfigValue = value };

    // Monday 2026-08-24 at the given local wall-clock time, as UTC.
    private static DateTimeOffset LocalMonday(int hour, int minute = 0)
    {
        var wall = new DateTime(2026, 8, 24, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, TimeZoneInfo.Local));
    }

    // 02:00-09:00 every day.
    private const string NightWindow =
        """{"Enabled":true,"Windows":[{"Days":[0,1,2,3,4,5,6],"StartMinute":120,"EndMinute":540}]}""";

    [Fact]
    public void ValidateConfigItems_RejectsInvalidWatchtowerSchedule()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems(
                new List<ConfigItem> { Item(ConfigKeys.WatchtowerSchedule, "{not-json") }));
        Assert.Contains("JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateConfigItems_AcceptsWatchtowerSchedule()
    {
        ConfigManager.ValidateConfigItems(new List<ConfigItem> { Item(ConfigKeys.WatchtowerSchedule, NightWindow) });
    }

    [Fact]
    public void IsWatchtowerWindowOpen_UnsetSchedule_IsAlwaysOpen()
    {
        var config = new ConfigManager();
        Assert.True(config.IsWatchtowerWindowOpen(LocalMonday(14)));
        Assert.False(config.GetWatchtowerSchedule().Enabled);
    }

    [Fact]
    public void IsWatchtowerWindowOpen_FollowsTheWindow()
    {
        var config = new ConfigManager();
        config.UpdateValues(new List<ConfigItem> { Item(ConfigKeys.WatchtowerSchedule, NightWindow) });

        Assert.True(config.IsWatchtowerWindowOpen(LocalMonday(2)));
        Assert.True(config.IsWatchtowerWindowOpen(LocalMonday(8, 59)));
        Assert.False(config.IsWatchtowerWindowOpen(LocalMonday(9)));
        Assert.False(config.IsWatchtowerWindowOpen(LocalMonday(14)));
        Assert.False(config.IsWatchtowerWindowOpen(LocalMonday(1, 59)));
    }

    [Fact]
    public void IsWatchtowerWindowOpen_DisabledSchedule_IsOpen()
    {
        var config = new ConfigManager();
        config.UpdateValues(new List<ConfigItem>
        {
            Item(ConfigKeys.WatchtowerSchedule, NightWindow.Replace("\"Enabled\":true", "\"Enabled\":false")),
        });
        Assert.True(config.IsWatchtowerWindowOpen(LocalMonday(14)));
    }
}
