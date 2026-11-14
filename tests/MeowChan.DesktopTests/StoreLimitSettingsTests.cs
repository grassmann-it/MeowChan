using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// The limits of the private lists, the video preview cache, the export confirmation and the import are settings on the
/// "Storage and limits" page: each has a default (the former fixed value), a minimum and a maximum, is validated like the
/// existing limits, has a labelled row with a hint in every built-in language, and reaches the profile at start and after
/// a settings change.
/// </summary>
[Collection("Localization")]
public sealed class StoreLimitSettingsTests
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };

    // Config property, default, minimum, maximum, editor name (the same as the property), text key.
    private static readonly (string Property, int Default, int Minimum, int Maximum, string Key)[] Limits =
    {
        (nameof(Config.FavoriteEntryLimit), 50_000, 10_000, 500_000, "favoriteEntries"),
        (nameof(Config.FavoriteFileLimitMiB), 8, 1, 128, "favoriteFile"),
        (nameof(Config.PostBookmarkLimit), 10_000, 1_000, 100_000, "postBookmarks"),
        (nameof(Config.SavedSearchLimit), 500, 50, 5_000, "savedSearches"),
        (nameof(Config.ReaderGroupLimit), 500, 50, 5_000, "readerGroups"),
        (nameof(Config.ProfileRegistryLimit), 32, 1, 256, "profileRegistry"),
        (nameof(Config.SourcesWatchPreviewRows), 8, 3, 200, "sourcesWatchPreviewRows"),
        (nameof(Config.ExportConfirmFiles), 500, 10, 100_000, "exportConfirmFiles"),
        (nameof(Config.ExportConfirmGiB), 2, 1, 1_024, "exportConfirmGiB"),
        (nameof(Config.PosterCacheLimitMiB), 512, 16, 8_192, "posterCacheMiB"),
        (nameof(Config.PosterCacheFileLimit), 2_048, 32, 50_000, "posterCacheFiles"),
        (nameof(Config.AudioProbeCacheLimitMiB), 64, 16, 128, "audioCacheMiB"),
        (nameof(Config.AudioProbeCacheEntries), 400_000, 50_000, 1_000_000, "audioCacheEntries"),
        (nameof(Config.ThumbnailCacheTiles), 384, 32, 8_192, "thumbnailCacheTiles"),
        (nameof(Config.ClipboardUrlLimit), 32, 1, 500, "clipboardUrls"),
        (nameof(Config.ClipboardTextLimitKiB), 64, 4, 4_096, "clipboardText"),
        (nameof(Config.GalleryImageCacheMiB), 256, 0, 4_096, "galleryImageCacheMiB"),
        (nameof(Config.GalleryPreloadAhead), 3, 0, 20, "galleryPreloadAhead"),
        (nameof(Config.GalleryPreloadBehind), 2, 0, 20, "galleryPreloadBehind"),
        (nameof(Config.ImportMaxFilesPerBatch), 2_000, 1, 20_000, "importFiles"),
        (nameof(Config.ImportMaxBatchGiB), 50, 1, 16_384, "importBatchGiB"),
        (nameof(Config.ImportMaxFileMiB), 4_096, 1, 1_048_576, "importFileMiB"),
        (nameof(Config.ImportMaxTotalFiles), 50_000, 1, 500_000, "importTotal"),
        (nameof(Config.ImportCatalogLimitMiB), 32, 1, 128, "importCatalogMiB"),
        (nameof(Config.ImportMaxTagsPerBatch), 32, 1, 256, "importTags"),
        (nameof(Config.ImportMaxBatches), 100_000, 1_000, 1_000_000, "importBatches"),
        (nameof(Config.MediaIndexMaxEntries), 1_000_000, 100_000, 5_000_000, "mediaIndexEntries"),
        (nameof(Config.MediaIndexMaxThreads), 200_000, 20_000, 1_000_000, "mediaIndexThreads"),
        (nameof(Config.FolderSourceMaxFiles), 200_000, 1_000, 2_000_000, "folderSourceFiles"),
        (nameof(Config.FolderSourceMaxRoots), 8, 1, 64, "folderSourceRoots"),
        (nameof(Config.FolderSourceHashParallelism), 2, 1, 8, "folderSourceParallelism"),
        (nameof(Config.FolderSourceHashMaxFileGiB), 8, 1, 1_024, "folderSourceHashMaxFile"),
        (nameof(Config.FolderSourceRescanMinutes), 0, 0, 1_440, "folderSourceRescan"),
        (nameof(Config.FolderSourceCatalogLimitMiB), 256, 16, 4_096, "folderSourceCatalog"),
        (nameof(Config.FolderSourceGalleryHashWindow), 16, 0, 200, "folderSourceGalleryWindow"),
    };

    private static int Get(Config config, string property) => (int)typeof(Config).GetProperty(property)!.GetValue(config)!;

    private static void Set(Config config, string property, int value) => typeof(Config).GetProperty(property)!.SetValue(config, value);

    [Fact]
    public void EveryLimitHasItsFormerDefaultAndIsBoundedLikeTheExistingLimits()
    {
        Config defaults = new();
        foreach ((string property, int fallback, int minimum, int maximum, _) in Limits)
        {
            // The import defaults come from the import contract; the others are the former fixed values.
            Assert.Equal(fallback, Get(defaults, property));
            Assert.Equal(minimum, (int)typeof(Config).GetField(property + "Minimum", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!);
            Assert.Equal(maximum, (int)typeof(Config).GetField(property + "Maximum", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!);
            defaults.Validate();
            foreach (int valid in new[] { minimum, maximum })
            {
                Config candidate = defaults.Clone();
                Set(candidate, property, valid);
                candidate.Validate();
            }
            foreach (int invalid in new[] { minimum - 1, maximum + 1, int.MinValue, int.MaxValue })
            {
                Config candidate = defaults.Clone();
                Set(candidate, property, invalid);
                Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
                Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
            }
        }
    }

    [Fact]
    public void EveryLimitSurvivesCloneCopiesAndSaveReload()
    {
        using TempDirectory directory = new();
        Config original = new();
        foreach ((string property, _, _, int maximum, _) in Limits) Set(original, property, maximum);
        Config clone = original.Clone();
        Config live = new();
        live.CopySettingsFieldsFrom(clone);
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach ((string property, _, _, int maximum, _) in Limits)
        {
            Assert.Equal(maximum, Get(clone, property));
            Assert.Equal(maximum, Get(live, property));
            Assert.Equal(maximum, Get(loaded, property));
        }
    }

    [Fact]
    public async Task TheHostAppliesTheLimitsToTheProfileAtStartAndAfterASettingsChange()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FavoriteEntryLimit = 20_000, FavoriteFileLimitMiB = 16,
            PostBookmarkLimit = 2_000, SavedSearchLimit = 100, ReaderGroupLimit = 80,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        {
            await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((20_000, 16L * 1024 * 1024, 2_000, 100, 80),
                (host.StoreLimits.FavoriteEntries, host.StoreLimits.FavoriteFileBytes, host.StoreLimits.Bookmarks, host.StoreLimits.SavedSearches, host.StoreLimits.ReaderGroups));
            Assert.Equal(80, host.ReaderGroupLimit);

            Config draft = host.Config.Clone();
            draft.FavoriteEntryLimit = 100_000;
            draft.FavoriteFileLimitMiB = 64;
            draft.PostBookmarkLimit = 5_000;
            draft.SavedSearchLimit = 1_000;
            draft.ReaderGroupLimit = 1_500;
            await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((100_000, 64L * 1024 * 1024, 5_000, 1_000, 1_500),
                (host.StoreLimits.FavoriteEntries, host.StoreLimits.FavoriteFileBytes, host.StoreLimits.Bookmarks, host.StoreLimits.SavedSearches, host.StoreLimits.ReaderGroups));
        }
    }

    [Fact]
    public void AFileLimitWithAFractionIsNamedLikeTheUsageNextToItNotCutDown()
    {
        string previous = Texts.Language;
        try
        {
            InvalidDataException refusal = PrivateStoreLimit.Refusal(PrivateStoreLimitKind.FavoriteFile, 12L * 1024 * 1024 + 512 * 1024, "English store message");
            Texts.SetLanguage("en");
            Assert.Contains("12.5 MiB", StoreLimitText.Describe(refusal));
            Texts.SetLanguage("de");
            Assert.Contains("12,5 MiB", StoreLimitText.Describe(refusal));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void ALimitReachedIsTurnedIntoASentenceThatNamesTheSettingInEveryLanguage()
    {
        string previous = Texts.Language;
        try
        {
            (PrivateStoreLimitKind Kind, long Limit, string Setting, string Digits)[] cases =
            {
                (PrivateStoreLimitKind.FavoriteEntries, 50_000, "budgets.favoriteEntries", "50"),
                (PrivateStoreLimitKind.FavoriteFile, 8L * 1024 * 1024, "budgets.favoriteFile", "8 MiB"),
                (PrivateStoreLimitKind.Bookmarks, 10_000, "budgets.postBookmarks", "10"),
                (PrivateStoreLimitKind.SavedSearches, 500, "budgets.savedSearches", "500"),
                (PrivateStoreLimitKind.ReaderGroups, 500, "budgets.readerGroups", "500"),
                (PrivateStoreLimitKind.PostFilterRules, 200, "budgets.postFilterRules", "200"),
                (PrivateStoreLimitKind.HiddenPosts, 10_000, "budgets.hiddenPosts", "10"),
            };
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                foreach ((PrivateStoreLimitKind kind, long limit, string setting, string digits) in cases)
                {
                    InvalidDataException refusal = PrivateStoreLimit.Refusal(kind, limit, "English store message");
                    string text = StoreLimitText.Describe(refusal)!;
                    Assert.NotNull(text);
                    Assert.Contains(Texts.T(setting), text);
                    Assert.Contains(Texts.T("settings.storageLimits"), text);
                    Assert.Contains(digits, text);
                    Assert.DoesNotContain("limit.", text);
                    Assert.DoesNotContain("English store message", text);
                    InvalidDataException localized = Assert.IsType<InvalidDataException>(StoreLimitText.Localize(refusal));
                    Assert.Equal(text, localized.Message);
                    Assert.Same(refusal, localized.InnerException);
                }
            }
            Assert.Null(StoreLimitText.Describe(new InvalidDataException("Other")));
            InvalidDataException plain = new("Other");
            Assert.Same(plain, StoreLimitText.Localize(plain));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void EveryLimitHasALabelAndAHintWithItsRangeInEveryBuiltInLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                foreach ((_, _, int minimum, int maximum, string key) in Limits)
                {
                    string label = Texts.T("budgets." + key);
                    string hint = Texts.T("budgets." + key + "Hint");
                    Assert.NotEqual("budgets." + key, label);
                    Assert.NotEqual("budgets." + key + "Hint", hint);
                    Assert.Contains(minimum.ToString(), hint);
                    Assert.Contains(maximum.ToString(), hint);
                }
                foreach (string group in new[] { "lists", "previews", "import", "folderSources" })
                    Assert.NotEqual("budgets.group." + group, Texts.T("budgets.group." + group));
            }
            // Wording rules of the project: "du", "Post", and the ellipsis directly after the word.
            Texts.SetLanguage("de");
            foreach ((_, _, _, _, string key) in Limits)
            {
                string text = Texts.T("budgets." + key) + " " + Texts.T("budgets." + key + "Hint");
                Assert.DoesNotContain("Beitrag", text);
                Assert.DoesNotContain("Beiträge", text);
                Assert.DoesNotContain("Watcher", text);
                Assert.DoesNotContain(" …", text);
                Assert.DoesNotContain("Sie ", text);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheThumbnailMemoryHintNamesTheLargestPreviewAndWhatTheMaximumCostsInEveryLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string hint = Texts.T("budgets.thumbnailCacheTilesHint");
                string point = language == "en" ? "." : ",";
                // A video poster is 340 x 250 x 4 bytes = about 0.33 MiB: 384 of them are about 125 MiB, 8192 are about 2.6 GiB.
                Assert.Contains("0" + point + "33", hint);
                Assert.Contains("125", hint);
                Assert.Contains("2" + point + "6", hint);
                Assert.DoesNotContain("60 MiB", hint);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheMediaIndexCountRefusalNamesBothSettingsInEveryLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string text = Texts.T("media.cache.tooMany", "1,000,000", "200,000", Texts.T("budgets.mediaIndexEntries"), Texts.T("budgets.mediaIndexThreads"), Texts.T("settings.storageLimits"));
                Assert.Contains(Texts.T("budgets.mediaIndexEntries"), text);
                Assert.Contains(Texts.T("budgets.mediaIndexThreads"), text);
                Assert.Contains(Texts.T("settings.storageLimits"), text);
                Assert.DoesNotContain("fixed", text);
                Assert.DoesNotContain("{", text);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void ImportTagsAboveTheConfiguredLimitAreRefusedByTheDialogSessionAndNameTheSetting()
    {
        ManualImportSession session = new((_, _, _) => throw new InvalidOperationException(), (_, _, _, _) => throw new InvalidOperationException(),
            DateTimeOffset.UnixEpoch) { MaximumTags = 2 };
        Assert.True(session.AddTag("one"));
        Assert.True(session.AddTag("two"));
        Assert.False(session.AddTag("three"));
        Assert.Equal(("import.tags.tooMany", 2), (session.MessageKey, (int)session.MessageArguments.Single()));
        Assert.Contains(Texts.T("budgets.importTags"), ManualImportSession.MessageText(session.MessageKey!, session.MessageArguments));
    }

    [Fact]
    public void MarkupHasALabelledRowForEveryLimitAboveTheAdvancedExpander()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        XElement expander = Named("SettingsWorkerBudgetsHeading");
        foreach ((string property, _, int minimum, int maximum, _) in Limits)
        {
            XElement field = Named(property + "Input");
            Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
            Assert.DoesNotContain(field.Ancestors(), e => ReferenceEquals(e, expander));
            Assert.Equal("{Binding " + property + ", Mode=TwoWay}", (string?)field.Attribute("Value"));
            Assert.Equal(minimum.ToString(), (string?)field.Attribute("Minimum"));
            Assert.Equal(maximum.ToString(), (string?)field.Attribute("Maximum"));
            Assert.Equal("{Binding ElementName=" + property + "FieldLabel}", (string?)field.Attribute("AutomationProperties.LabeledBy"));
            Assert.Equal("{Binding " + property + "Help}", (string?)field.Attribute("AutomationProperties.HelpText"));
            Assert.Equal("{Binding " + property + "Label}", (string?)Named(property + "FieldLabel").Attribute("Text"));
            Named(property + "InputError");
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + property + "Hint}");
        }
        // The new rows sit after the existing user limits, before the expander.
        List<XElement> order = markup.Descendants().ToList();
        int expanderIndex = order.IndexOf(expander);
        foreach ((string property, _, _, _, _) in Limits) Assert.True(order.IndexOf(Named(property + "Input")) < expanderIndex);
        Assert.True(order.IndexOf(Named("MediaIndexCacheMiBInput")) < order.IndexOf(Named(Limits[0].Property + "Input")));
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
