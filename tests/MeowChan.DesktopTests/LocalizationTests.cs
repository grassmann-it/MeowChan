using System.Globalization;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection { }

[Collection("Localization")]
public sealed class LocalizationTests : IDisposable
{
    public LocalizationTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public void BuiltInsHaveMatchingKeysAndFormattingContracts()
    {
        Assert.Empty(Texts.ValidateBuiltInCatalogs());
        Assert.Equal(new[] { "en", "de" }, Texts.Languages.Select(language => language.Code));
        Assert.Equal("Simple", Texts.T("mode.simple"));
        Assert.Equal("Advanced", Texts.T("mode.advanced"));
        Assert.Equal("Barebones", Texts.T("mode.barebones"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void EveryLinuxStartupFailureHasLocalizedGuidance(string language)
    {
        Texts.SetLanguage(language);
        foreach (var kind in Enum.GetValues<MeowChan.Desktop.LinuxVideoPreflightErrorKind>())
        {
            if (kind == MeowChan.Desktop.LinuxVideoPreflightErrorKind.None) continue;
            var result = new MeowChan.Desktop.LinuxVideoPreflightResult(kind);
            Assert.NotEqual(result.MessageKey, Texts.T(result.MessageKey));
            Assert.Contains("MeowChan", Texts.T(result.MessageKey));
        }
    }

    [Fact]
    public void SwitchingLanguageFormatsNumbersWithoutChangingProcessCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        Texts.SetLanguage("DE");

        Assert.Equal("de", Texts.Language);
        Assert.Equal("Einstellungen", Texts.T("settings.title"));
        Assert.Equal("1 / 8 (12,5 %)", Texts.T("progress.media", 1, 8, 12.5));
        Assert.Equal(original, CultureInfo.CurrentCulture);
    }

    [Theory]
    [InlineData("en", "settings.light", "Light")]
    [InlineData("en", "settings.dark", "Dark")]
    [InlineData("de", "settings.light", "Hell")]
    [InlineData("de", "settings.dark", "Dunkel")]
    public void BothThemeToggleTargetsHaveTranslatedLabels(string language, string key, string expected)
    {
        Texts.SetLanguage(language);

        Assert.Equal(expected, Texts.T(key));
    }

    [Fact]
    public void MissingDirectoryDoesNotWriteOrWarn()
    {
        using TempDirectory directory = new TempDirectory();
        string missing = Path.Combine(directory.Path, "LanguagePacks");

        Texts.Initialize(missing);

        Assert.False(Directory.Exists(missing));
        Assert.Empty(Texts.Warnings);
        Assert.Equal(2, Texts.Languages.Count);
    }

    [Fact]
    public void PartialPackUsesEnglishForMissingKeysAndSupportsPlainText()
    {
        using TempDirectory directory = new TempDirectory();
        WritePack(directory, "fr", new Dictionary<string, string>
        {
            ["settings.title"] = "Préférences",
            ["downloads.empty"] = "<Button>Texte simple</Button>",
            ["future.key"] = "Ignored by this version"
        });

        Texts.Initialize(directory.Path);
        Texts.SetLanguage("fr");

        Assert.Empty(Texts.Warnings);
        Assert.Equal("Préférences", Texts.T("settings.title"));
        Assert.Equal("Open folder", Texts.T("action.openFolder"));
        Assert.Equal("<Button>Texte simple</Button>", Texts.T("downloads.empty"));
        Assert.Equal("future.key", Texts.T("future.key"));
    }

    [Fact]
    public void TranslationCanReorderPlaceholdersAndUseEscapedBraces()
    {
        using TempDirectory directory = new TempDirectory();
        WritePack(directory, "fr", new Dictionary<string, string>
        {
            ["admission.summary"] = "{{Résultat}} {2} refusées, {0} ajoutées, {1} présentes"
        });
        Texts.Initialize(directory.Path);
        Texts.SetLanguage("fr");

        Assert.Empty(Texts.Warnings);
        Assert.Equal("{Résultat} 3 refusées, 1 ajoutées, 2 présentes", Texts.T("admission.summary", 1, 2, 3));
    }

    [Theory]
    [InlineData("{0} / {2}")]
    [InlineData("{0} / {1} ({2:0.00}%)")]
    [InlineData("{0,999999999} / {1} ({2:0.##}%)")]
    [InlineData("{0} / {1} ({2:0.##%)")]
    public void InvalidOrChangedPlaceholderContractsRejectTheWholePack(string translatedProgress)
    {
        using TempDirectory directory = new TempDirectory();
        WritePack(directory, "fr", new Dictionary<string, string>
        {
            ["settings.title"] = "Préférences",
            ["progress.media"] = translatedProgress
        });

        Texts.Initialize(directory.Path);

        Assert.DoesNotContain(Texts.Languages, language => language.Code == "fr");
        Assert.Single(Texts.Warnings);
        Assert.Equal("Settings", Texts.T("settings.title"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":2,\"code\":\"fr\",\"nativeName\":\"Français\",\"strings\":{\"settings.title\":\"Préférences\"}}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1,\"code\":\"fr\",\"nativeName\":\"Français\",\"strings\":{\"settings.title\":\"Préférences\"}}")]
    [InlineData("{\"schemaVersion\":1,\"code\":\"fr\",\"nativeName\":\"Français\",\"strings\":{\"settings.title\":\"A\",\"settings.title\":\"B\"}}")]
    [InlineData("{\"schemaVersion\":1,\"code\":\"fr\",\"nativeName\":\"Français\",\"strings\":{\"settings.title\":false}}")]
    [InlineData("{\"schemaVersion\":1,\"code\":\"../fr\",\"nativeName\":\"Français\",\"strings\":{\"settings.title\":\"Préférences\"}}")]
    [InlineData("{\"schemaVersion\":1,\"code\":\"fr\",\"nativeName\":\"Français\",\"strings\":{\"unknown.key\":\"Only unknown keys\"}}")]
    public void InvalidPacksProduceWarningsWithoutBreakingOtherLanguages(string json)
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "invalid.json"), json);
        WritePack(directory, "es", new Dictionary<string, string> { ["settings.title"] = "Ajustes" });

        Texts.Initialize(directory.Path);
        Texts.SetLanguage("es");

        Assert.Single(Texts.Warnings);
        Assert.Equal("Ajustes", Texts.T("settings.title"));
        Assert.Contains(Texts.Languages, language => language.Code == "en");
        Assert.Contains(Texts.Languages, language => language.Code == "de");
    }

    [Fact]
    public void DuplicateLanguageCannotOverrideTheBuiltInCatalog()
    {
        using TempDirectory directory = new TempDirectory();
        WritePack(directory, "EN", new Dictionary<string, string> { ["settings.title"] = "Overridden" });

        Texts.Initialize(directory.Path);

        Assert.Single(Texts.Warnings);
        Assert.Equal("Settings", Texts.T("settings.title"));
        Assert.Equal(2, Texts.Languages.Count);
    }

    [Fact]
    public void UnavailableSavedLanguageFallsBackAndWarningIsNotRepeated()
    {
        Texts.SetLanguage("de");
        Texts.SetLanguage("fr");
        Texts.SetLanguage("fr");

        Assert.Equal("en", Texts.Language);
        Assert.Equal("Settings", Texts.T("settings.title"));
        Assert.Single(Texts.Warnings);
    }

    [Fact]
    public void TooFewFormatArgumentsDoNotCrashTheDesktop()
    {
        Texts.SetLanguage("de");
        Assert.Equal("{0} / {1} ({2:0.##}%)", Texts.T("progress.media", 1));
        Assert.Equal("unknown.key", Texts.T("unknown.key", 1));
    }

    [Fact]
    public void NotificationsOnlyFireOnActualLanguageChangesAndCanBeUnsubscribed()
    {
        int notifications = 0;
        EventHandler handler = (_, _) => notifications++;
        Texts.Changed += handler;
        try
        {
            Texts.SetLanguage("en");
            Texts.SetLanguage("de");
            Texts.SetLanguage("DE");
            Assert.Equal(1, notifications);
        }
        finally
        {
            Texts.Changed -= handler;
        }
        Texts.SetLanguage("en");
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ReadersSeeCompleteCatalogSnapshotsDuringLanguageChanges()
    {
        Task writer = Task.Run(() =>
        {
            for (int index = 0; index < 200; index++)
                Texts.SetLanguage(index % 2 == 0 ? "de" : "en");
        });
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int index = 0; index < 200; index++)
            {
                string result = Texts.T("progress.media", 1, 8, 12.5);
                Assert.True(result is "1 / 8 (12.5%)" or "1 / 8 (12,5 %)", result);
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));
    }

    private static void WritePack(TempDirectory directory, string code, Dictionary<string, string> strings)
    {
        string json = JsonSerializer.Serialize(new { schemaVersion = 1, code, nativeName = "Test language", strings });
        File.WriteAllText(Path.Combine(directory.Path, code + ".json"), json);
    }
}
