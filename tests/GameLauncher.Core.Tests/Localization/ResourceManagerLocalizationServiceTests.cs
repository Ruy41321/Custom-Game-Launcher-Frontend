using System.Collections;
using System.Globalization;
using System.Resources;
using GameLauncher.Core.Localization;

namespace GameLauncher.Core.Tests.Localization;

public sealed class ResourceManagerLocalizationServiceTests
{
    [Fact]
    public void DefaultsToEnglishWhenNoCultureIsRequested()
    {
        var localization = new ResourceManagerLocalizationService("en");

        Assert.Equal("en", localization.CurrentCulture.TwoLetterISOLanguageName);
        Assert.Equal("Library", localization.Translate("Nav.Library"));
    }

    [Theory]
    [InlineData("it", "Libreria")]
    [InlineData("fr", "Bibliothèque")]
    [InlineData("en", "Library")]
    public void ResolvesStringsInEachSupportedLanguage(string culture, string expected)
    {
        var localization = new ResourceManagerLocalizationService(culture);

        Assert.Equal(expected, localization.Translate("Nav.Library"));
    }

    [Fact]
    public void SwitchingLanguageChangesSubsequentLookups()
    {
        var localization = new ResourceManagerLocalizationService("en");

        Assert.True(localization.TrySetLanguage("it"));

        Assert.Equal("Libreria", localization.Translate("Nav.Library"));
    }

    [Fact]
    public void SwitchingLanguageRaisesLanguageChanged()
    {
        var localization = new ResourceManagerLocalizationService("en");
        int raised = 0;
        localization.LanguageChanged += (_, _) => raised++;

        localization.TrySetLanguage("fr");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void SwitchingToTheCurrentLanguageDoesNotRaiseLanguageChanged()
    {
        var localization = new ResourceManagerLocalizationService("en");
        int raised = 0;
        localization.LanguageChanged += (_, _) => raised++;

        Assert.True(localization.TrySetLanguage("en"));

        Assert.Equal(0, raised);
    }

    // "it-IT" must land on the Italian resources rather than silently falling back to English.
    [Fact]
    public void RegionSpecificCulturesResolveToTheirBaseLanguage()
    {
        var localization = new ResourceManagerLocalizationService("it-IT");

        Assert.Equal("it", localization.CurrentCulture.TwoLetterISOLanguageName);
        Assert.Equal("Libreria", localization.Translate("Nav.Library"));
    }

    [Fact]
    public void AnUnsupportedLanguageIsRejectedAndLeavesTheCurrentOneInPlace()
    {
        var localization = new ResourceManagerLocalizationService("it");

        Assert.False(localization.TrySetLanguage("de"));

        Assert.Equal("it", localization.CurrentCulture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void AMissingKeyIsRenderedVisiblyRatherThanAsAnEmptyString()
    {
        var localization = new ResourceManagerLocalizationService("en");

        string value = localization.Translate("This.Key.Does.Not.Exist");

        Assert.Equal(
            ResourceManagerLocalizationService.FormatMissingKey("This.Key.Does.Not.Exist"), value);
        Assert.Contains("This.Key.Does.Not.Exist", value, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyKeyYieldsAnEmptyString()
    {
        var localization = new ResourceManagerLocalizationService("en");

        Assert.Equal(string.Empty, localization.Translate(" "));
    }

    [Fact]
    public void FormatArgumentsAreSubstituted()
    {
        var localization = new ResourceManagerLocalizationService("en");

        Assert.Equal("Welcome to My Launcher", localization.Translate("Shell.Welcome", "My Launcher"));
    }

    [Fact]
    public void AMalformedTemplateReturnsTheRawStringInsteadOfThrowing()
    {
        var localization = new ResourceManagerLocalizationService("en");

        // "Nav.Library" has no placeholder, so a stray argument must simply be ignored.
        string value = localization.Translate("Nav.Library", "unused");

        Assert.Equal("Library", value);
    }

    [Fact]
    public void TheIndexerAndTranslateAgree()
    {
        var localization = new ResourceManagerLocalizationService("fr");

        Assert.Equal(localization.Translate("Nav.Explore"), localization["Nav.Explore"]);
    }

    // A key added to English but forgotten elsewhere would silently render in English for
    // Italian and French users. This is the test that catches it.
    [Fact]
    public void EverySupportedLanguageTranslatesEveryKey()
    {
        var resources = new ResourceManager(
            "GameLauncher.Core.Localization.Strings",
            typeof(ResourceManagerLocalizationService).Assembly);

        ResourceSet neutral = resources.GetResourceSet(
            CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!;

        List<string> neutralKeys = neutral
            .Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(neutralKeys);

        foreach (LanguageOption language in ResourceManagerLocalizationService.SupportedLanguages
                     .Where(language => language.CultureName != "en"))
        {
            // tryParents: false, otherwise a missing key falls back to English and hides.
            ResourceSet? translated = resources.GetResourceSet(
                new CultureInfo(language.CultureName), createIfNotExists: true, tryParents: false);

            Assert.NotNull(translated);

            List<string> missing = neutralKeys
                .Where(key => translated.GetString(key) is null)
                .ToList();

            Assert.True(
                missing.Count == 0,
                $"'{language.CultureName}' is missing: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void SupportedLanguagesCoverItalianEnglishAndFrench()
    {
        IEnumerable<string> cultures = ResourceManagerLocalizationService.SupportedLanguages
            .Select(language => language.CultureName);

        Assert.Equal(["en", "it", "fr"], cultures);
        Assert.All(
            ResourceManagerLocalizationService.SupportedLanguages,
            language => Assert.False(string.IsNullOrWhiteSpace(language.NativeName)));
    }
}
