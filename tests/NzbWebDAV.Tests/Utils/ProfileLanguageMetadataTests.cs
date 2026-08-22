using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ProfileLanguageMetadataTests
{
    [Fact]
    public void NormalizeAudioLanguages_MapsAliasesAndKeepsUnknownTokens()
    {
        Assert.Equal(["de", "en"], ProfileLanguageMetadata.NormalizeAudioLanguages("English, German"));
        Assert.Equal(["klingon"], ProfileLanguageMetadata.NormalizeAudioLanguages("Klingon"));
    }

    [Fact]
    public void NormalizeSubtitleLanguages_StaysSeparateFromAudioFlags()
    {
        var subs = ProfileLanguageMetadata.NormalizeSubtitleLanguages("English, Spanish");
        Assert.Equal(["en", "es"], subs);
        Assert.Empty(ProfileLanguageMetadata.AudioFlagMarkers(subs).Except(["🇬🇧", "🇪🇸"]));
    }

    [Fact]
    public void AudioFlagMarkers_OnlyMapKnownAudioCodes()
    {
        Assert.Equal(["🇬🇧"], ProfileLanguageMetadata.AudioFlagMarkers(["en"]));
        Assert.Empty(ProfileLanguageMetadata.AudioFlagMarkers(["klingon"]));
    }
}
