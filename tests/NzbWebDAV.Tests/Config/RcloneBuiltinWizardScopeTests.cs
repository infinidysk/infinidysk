using System.Reflection;
using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Config;

/// <summary>
/// The guided setup offers the built-in mount, because running rclone ourselves
/// is the path that needs no second container. It offers only the two settings
/// that decision needs: whether to use it, and what to mount. Tuning stays in
/// Settings, where the whole mount list is editable.
/// </summary>
public class RcloneBuiltinWizardScopeTests
{
    private static HashSet<string> AllowedWizardKeys()
    {
        var field = typeof(SetupWizardService)
            .GetField("AllowedConfigKeys", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (HashSet<string>)field.GetValue(null)!;
    }

    [Theory]
    [InlineData(ConfigKeys.RcloneBuiltinEnabled)]
    [InlineData(ConfigKeys.RcloneBuiltinMounts)]
    public void TheChoiceTheWizardOffers_IsWritableByIt(string configKey)
    {
        Assert.Contains(configKey, AllowedWizardKeys());
    }

    [Theory]
    [InlineData(ConfigKeys.RcloneBuiltinRcPort)]
    [InlineData(ConfigKeys.RcloneBuiltinCacheDir)]
    [InlineData(ConfigKeys.RcloneBuiltinCacheSizeLimit)]
    public void TuningKeys_StayOutOfTheSetupWizard(string configKey)
    {
        Assert.DoesNotContain(configKey, AllowedWizardKeys());
    }

    [Fact]
    public void WizardVersion_IsNotBumpedByAnAddedChoice()
    {
        // Bumping re-prompts every existing installation. Offering a new option
        // inside an existing step does not warrant that: anyone already set up
        // can switch to the built-in mount from Settings.
        Assert.Equal(1, SetupWizardService.CurrentWizardVersion);
    }
}
