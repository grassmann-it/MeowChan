using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Import;
using MeowChan.PluginHost.Http;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// Documentation contracts, not evidence of live provider behavior or native UI acceptance.
/// Reads repository Markdown and embedded resources only: no profile, language-state mutation,
/// store construction, HTTP client or application startup is needed.
/// </summary>
public sealed class HelpDocumentationTests
{
    private const string Help = "docs/Help.md";
    private const string DanbooruGuide = "docs/user-guide/danbooru.md";

    public static TheoryData<string, int, int, int> Limits()
    {
        // An unbound Config supplies real defaults without loading or saving a profile. Explicit
        // references (including internal constants via InternalsVisibleTo) catch renamed settings.
        Config c = new();
        return new()
        {
            { "Free-space reserve (GiB)", c.MinimumFreeSpaceGiB, Config.MinimumFreeSpaceGiBMinimum, Config.MinimumFreeSpaceGiBMaximum },
            { "Danbooru catalog limit (MiB)", c.BooruCatalogLimitMiB, Config.BooruCatalogMinimumLimitMiB, Config.BooruCatalogMaximumLimitMiB },
            { "Own tag limit", c.PrivateTagLimit, Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit },
            { "Own tag file limit (MiB)", c.PrivateTagFileLimitMiB, Config.PrivateTagFileMinimumLimitMiB, Config.PrivateTagFileMaximumLimitMiB },
            { "Favorites and ratings: entries per list", c.FavoriteEntryLimit, Config.FavoriteEntryLimitMinimum, Config.FavoriteEntryLimitMaximum },
            { "Favorites and ratings: file size (MiB)", c.FavoriteFileLimitMiB, Config.FavoriteFileLimitMiBMinimum, Config.FavoriteFileLimitMiBMaximum },
            { "Post bookmarks", c.PostBookmarkLimit, Config.PostBookmarkLimitMinimum, Config.PostBookmarkLimitMaximum },
            { "Saved searches", c.SavedSearchLimit, Config.SavedSearchLimitMinimum, Config.SavedSearchLimitMaximum },
            { "Reader groups", c.ReaderGroupLimit, Config.ReaderGroupLimitMinimum, Config.ReaderGroupLimitMaximum },
            { "Post filter rules", ProfileStoreLimits.DefaultPostFilterRules, ProfileStoreLimits.MinimumPostFilterRules, ProfileStoreLimits.PostFilterRuleCeiling },
            { "Individually hidden posts", ProfileStoreLimits.DefaultHiddenPosts, ProfileStoreLimits.MinimumHiddenPosts, ProfileStoreLimits.HiddenPostCeiling },
            { "Sources page: watch rows per source", c.SourcesWatchPreviewRows, Config.SourcesWatchPreviewRowsMinimum, Config.SourcesWatchPreviewRowsMaximum },
            { "Ask before “Export all matches” above (files)", c.ExportConfirmFiles, Config.ExportConfirmFilesMinimum, Config.ExportConfirmFilesMaximum },
            { "Ask before “Export all matches” above (GiB)", c.ExportConfirmGiB, Config.ExportConfirmGiBMinimum, Config.ExportConfirmGiBMaximum },
            { "Video preview cache size (MiB)", c.PosterCacheLimitMiB, Config.PosterCacheLimitMiBMinimum, Config.PosterCacheLimitMiBMaximum },
            { "Video preview cache: files", c.PosterCacheFileLimit, Config.PosterCacheFileLimitMinimum, Config.PosterCacheFileLimitMaximum },
            { "Gallery image cache (MiB)", c.GalleryImageCacheMiB, Config.GalleryImageCacheMiBMinimum, Config.GalleryImageCacheMiBMaximum },
            { "Gallery images decoded ahead", c.GalleryPreloadAhead, Config.GalleryPreloadAheadMinimum, Config.GalleryPreloadAheadMaximum },
            { "Gallery images decoded behind", c.GalleryPreloadBehind, Config.GalleryPreloadBehindMinimum, Config.GalleryPreloadBehindMaximum },
            { "Sound check cache: size (MiB)", c.AudioProbeCacheLimitMiB, Config.AudioProbeCacheLimitMiBMinimum, Config.AudioProbeCacheLimitMiBMaximum },
            { "Sound check cache: videos", c.AudioProbeCacheEntries, Config.AudioProbeCacheEntriesMinimum, Config.AudioProbeCacheEntriesMaximum },
            { "Media previews kept in memory", c.ThumbnailCacheTiles, Config.ThumbnailCacheTilesMinimum, Config.ThumbnailCacheTilesMaximum },
            { "Media index cache limit (MiB)", c.MediaIndexCacheLimitMiB, Config.MediaIndexCacheMinimumLimitMiB, Config.MediaIndexCacheMaximumLimitMiB },
            { "Media index cache: entries", c.MediaIndexMaxEntries, Config.MediaIndexMaxEntriesMinimum, Config.MediaIndexMaxEntriesMaximum },
            { "Media index cache: threads", c.MediaIndexMaxThreads, Config.MediaIndexMaxThreadsMinimum, Config.MediaIndexMaxThreadsMaximum },
            { "Clipboard: URLs checked per read", c.ClipboardUrlLimit, Config.ClipboardUrlLimitMinimum, Config.ClipboardUrlLimitMaximum },
            { "Clipboard: text searched (KiB)", c.ClipboardTextLimitKiB, Config.ClipboardTextLimitKiBMinimum, Config.ClipboardTextLimitKiBMaximum },
            { "Import: files per batch", c.ImportMaxFilesPerBatch, Config.ImportMaxFilesPerBatchMinimum, Config.ImportMaxFilesPerBatchMaximum },
            { "Import: size per batch (GiB)", c.ImportMaxBatchGiB, Config.ImportMaxBatchGiBMinimum, Config.ImportMaxBatchGiBMaximum },
            { "Import: largest single file (MiB)", c.ImportMaxFileMiB, Config.ImportMaxFileMiBMinimum, Config.ImportMaxFileMiBMaximum },
            { "Import: files in total", c.ImportMaxTotalFiles, Config.ImportMaxTotalFilesMinimum, Config.ImportMaxTotalFilesMaximum },
            { "Import: number of batches", c.ImportMaxBatches, Config.ImportMaxBatchesMinimum, Config.ImportMaxBatchesMaximum },
            { "Import: tags per batch", c.ImportMaxTagsPerBatch, Config.ImportMaxTagsPerBatchMinimum, Config.ImportMaxTagsPerBatchMaximum },
            { "Import: catalog file size (MiB)", c.ImportCatalogLimitMiB, Config.ImportCatalogLimitMiBMinimum, Config.ImportCatalogLimitMiBMaximum }
        };
    }

    [Fact]
    public void StorageLimitsTableHasExactlyTheMappedSettings()
    {
        string[][] rows = LimitRows();
        Assert.Equal(Limits().Count, rows.Length);
        Assert.Equal(Limits().Select(row => (string)row[0]).Order(StringComparer.Ordinal),
            rows.Select(row => row[0]).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Limits))]
    public void DocumentedDefaultAndAllowedRangeMatchSettingsContracts(string setting, int defaultValue, int minimum, int maximum)
    {
        string[] row = Assert.Single(LimitRows(), row => row[0] == setting);
        Assert.Equal(defaultValue, Number(row[1]));
        Match range = Capture(row[2], @"^(?<minimum>\d+)\s+to\s+(?<maximum>\d+)$");
        Assert.Equal(minimum, Number(range.Groups["minimum"].Value));
        Assert.Equal(maximum, Number(range.Groups["maximum"].Value));
        Assert.InRange(defaultValue, minimum, maximum);
    }

    public static TheoryData<string, int, int, int> StatisticsLimits()
    {
        Config c = new();
        return new()
        {
            { "Statistics update interval (milliseconds)", c.StatsSampleIntervalMs, Config.StatsSampleIntervalMsMinimum, Config.StatsSampleIntervalMsMaximum },
            { "Graph length (readings)", c.StatsHistorySamples, Config.StatsHistorySamplesMinimum, Config.StatsHistorySamplesMaximum },
            { "Update interval in the tray (milliseconds)", c.StatsBackgroundIntervalMs, Config.StatsBackgroundIntervalMsMinimum, Config.StatsBackgroundIntervalMsMaximum },
            { "Days of statistics kept", c.StatsRetentionDays, Config.StatsRetentionDaysMinimum, Config.StatsRetentionDaysMaximum },
            { "Statistics saving interval (minutes)", c.StatsPersistIntervalMinutes, Config.StatsPersistIntervalMinutesMinimum, Config.StatsPersistIntervalMinutesMaximum },
            { "Most sources in the statistics", c.StatsMaxSources, Config.StatsMaxSourcesMinimum, Config.StatsMaxSourcesMaximum }
        };
    }

    [Theory]
    [MemberData(nameof(StatisticsLimits))]
    public void StatisticsDefaultsAndRangesMatchConfig(string setting, int defaultValue, int minimum, int maximum)
    {
        string[][] rows = TableRows("Statistics and disk traffic");
        Assert.Equal(StatisticsLimits().Count, rows.Length);
        string[] row = Assert.Single(rows, row => row[0] == setting);
        Assert.Equal(defaultValue, Number(row[1]));
        Match range = Capture(row[2], @"^(?<minimum>\d+)\s+to\s+(?<maximum>\d+)$");
        Assert.Equal(minimum, Number(range.Groups["minimum"].Value));
        Assert.Equal(maximum, Number(range.Groups["maximum"].Value));
    }

    [Fact]
    public void HelpDescribesIntegratedReaderExportAndPluginBoundaries()
    {
        string text = Read(Help);
        Assert.Contains("**Show as conversation**", text);
        Assert.Contains("**View → Post filters…**", text);
        Assert.Contains("### Reader display settings", text);
        Assert.DoesNotContain("not wired in yet", text);
        Assert.DoesNotContain("not additional Settings choices", text);
        Assert.Contains("translated for English, German", text);
        Assert.Contains("Simplified Chinese", text);
        Assert.Contains("**Plugins → Review bundled repairs**", text);
        Assert.DoesNotContain("Review bundled updates", text);
        Assert.Contains("not a claim that 1.1.0 packages have passed release acceptance", text);
    }

    [Fact]
    public void ReaderDisplayDefaultsAndLinkLengthMatchTheImplementedSeams()
    {
        string cards = Plain(Section(Read(Help), "Reading posts as cards"));
        string display = Plain(Section(Read(Help), "Reader display settings"));
        AssertNumber(display, @"widths default to (?<value>\d+) pixels", ReaderDisplayPreferences.ReaderThumbnailWidthDefault);
        AssertNumber(display, @"and (?<value>\d+) pixels in the large reader",
            ReaderDisplayPreferences.EffectiveThumbnailWidth(ReaderDisplayPreferences.ReaderThumbnailWidthDefault, true));
        AssertNumber(cards, @"longer than (?<value>\d+) UTF-16 code units", ReaderExternalLink.MaxLength);
        Assert.Contains("only for this app session, not after a restart", cards);
    }

    public static TheoryData<string, string, int, int, int> ReaderAndGalleryLimits() => new()
    {
        // P1 persists these component contracts. Referencing the components also allows this
        // docs-only branch to compile before the separate P1 implementation is integrated.
        { "Reader display settings", "Preview image width (px)", ReaderDisplayPreferences.ReaderThumbnailWidthDefault, ReaderDisplayPreferences.ReaderThumbnailWidthMinimum, ReaderDisplayPreferences.ReaderThumbnailWidthMaximum },
        { "Reader display settings", "Fold long posts above (lines)", ReaderDisplayLimits.FoldLinesDefault, ReaderDisplayLimits.FoldLinesMinimum, ReaderDisplayLimits.FoldLinesMaximum },
        { "Reader display settings", "Lines in quote previews", ReaderDisplayLimits.PreviewLinesDefault, ReaderDisplayLimits.PreviewLinesMinimum, ReaderDisplayLimits.PreviewLinesMaximum },
        { "Reader display settings", "Quote preview delay (ms)", ReaderDisplayLimits.PreviewDelayDefault, ReaderDisplayLimits.PreviewDelayMinimum, ReaderDisplayLimits.PreviewDelayMaximum },
        { "Reader display settings", "Reply cards per step", ReaderDisplayLimits.ReplyStepDefault, ReaderDisplayLimits.ReplyStepMinimum, ReaderDisplayLimits.ReplyStepMaximum },
        { "Reader display settings", "Lines per reply card", ReaderDisplayLimits.ReplyLinesDefault, ReaderDisplayLimits.ReplyLinesMinimum, ReaderDisplayLimits.ReplyLinesMaximum },
        { "Reader display settings", "Posts per reader page", ArchivePage.ReaderPageSizeDefault, ArchivePage.ReaderPageSizeMinimum, ArchivePage.ReaderPageSizeMaximum },
        { "Reader display settings", "Maximum conversation indentation depth", ReaderConversationTree.DepthDefault, ReaderConversationTree.DepthMinimum, ReaderConversationTree.DepthMaximum },
        { "Gallery Fit enlargement", "Maximum enlargement (%)", GalleryFitPreferences.MaxUpscalePercentDefault, GalleryFitPreferences.MaxUpscalePercentMinimum, GalleryFitPreferences.MaxUpscalePercentMaximum }
    };

    [Theory]
    [MemberData(nameof(ReaderAndGalleryLimits))]
    public void ReaderAndGalleryDefaultsAndRangesMatchTheirComponentContracts(string heading, string setting, int defaultValue, int minimum, int maximum)
    {
        string[] row = Assert.Single(TableRows(heading), row => row[0] == setting);
        Assert.Equal(defaultValue, Number(row[1]));
        Match range = Capture(row[2], @"^(?<minimum>\d+) to (?<maximum>\d+)$");
        Assert.Equal(minimum, Number(range.Groups["minimum"].Value));
        Assert.Equal(maximum, Number(range.Groups["maximum"].Value));
        Assert.InRange(defaultValue, minimum, maximum);
    }

    [Fact]
    public void ReaderP1NumericTablesCoverAllElevenFieldsWithoutDuplicates()
    {
        // Eight reader numbers, the gallery cap, and the two post-filter store limits.
        foreach (string heading in new[] { "Reader display settings", "Gallery Fit enlargement" })
            Assert.Equal(ReaderAndGalleryLimits().Where(row => (string)row[0] == heading).Select(row => (string)row[1]).Order(StringComparer.Ordinal),
                TableRows(heading).Select(row => row[0]).Order(StringComparer.Ordinal));
        Assert.Equal(11, ReaderAndGalleryLimits().Count + Limits().Count(row => (string)row[0] is "Post filter rules" or "Individually hidden posts"));
    }

    [Fact]
    public void ReaderChoicesAndDisplayOnlyBoundariesAreDocumented()
    {
        string display = Plain(Section(Read(Help), "Reader display settings"));
        Assert.Equal(new[] { "System", "Long", "SinceOp", "RelativeToNow" }, Enum.GetNames<ReaderDateStyle>());
        Assert.Equal(new[] { "Hide", "Hover", "Always" }, Enum.GetNames<ReaderSpoilerMode>());
        Assert.Equal(ReaderDateStyle.System, new ReaderDisplayPreferences().ReaderDateStyle);
        Assert.Equal(ReaderSpoilerMode.Hide, ReaderDisplayLimits.ClampSpoilerMode((ReaderSpoilerMode)(-1)));
        Assert.Contains("System format (default), Weekday and seconds, Since opening post and Relative to now", display);
        Assert.Contains("Keep hidden (default), Show on hover and Always show", display);
        Assert.Contains("current reading position in view", display);
        Assert.Contains("not the archive thread-list page size", display);
        Assert.Contains("not refreshed by a timer", display);
        Assert.Contains("Changes stay in the Settings draft until Save succeeds", display);
        Assert.Contains("Discard draft restores the saved values", display);
        Assert.Contains("a failed save does not apply the draft", display);

        string reading = Plain(Section(Read(Help), "Images, search and navigation"));
        Assert.Contains("The conversation switch is session-only", reading);
        Assert.Contains("deeper replies stay visible at the last indentation level", reading);
        Assert.Contains("not a provider conversation function and sends no requests", reading);
        Assert.DoesNotContain("depth limit is not yet available", reading);
    }

    [Fact]
    public void UnlimitedGalleryGuidanceKeepsTheCapWithoutApplyingIt()
    {
        string gallery = Plain(Section(Read(Help), "Gallery Fit enlargement"));
        GalleryFitPreferences defaults = new();
        Assert.False(defaults.Limit);
        Assert.Equal(GalleryFitPreferences.MaxUpscalePercentDefault, defaults.MaxUpscalePercent);
        Assert.Null(defaults.MaxUpscale(1));
        Assert.Contains("No limit (default) or Up to a limit", gallery);
        Assert.Contains("manual zoom is not capped", gallery);
        Assert.Contains("does not erase a valid percentage", gallery);
        Assert.Contains("does not use it to constrain Fit", gallery);
        Assert.Contains("the last saved valid value is kept instead", gallery);
        Assert.Contains("Discard draft restores the saved choice and percentage", gallery);
    }

    [Fact]
    public void ExportFrameLanguagesMatchTheImplementedLocales()
    {
        string export = Plain(Section(Read(Help), "Portable HTML export"));
        Match languages = Capture(export, @"translated for (?<languages>.*?)\. Unsupported languages use English");
        string[] names = Regex.Split(languages.Groups["languages"].Value, @",\s*|\s+and\s+");
        (string Name, string Code)[] expected =
        [
            ("English", "en"), ("German", "de"), ("Spanish", "es"), ("French", "fr"),
            ("Russian", "ru"), ("Czech", "cs"), ("Italian", "it"), ("Japanese", "ja"),
            ("Korean", "ko"), ("Polish", "pl"), ("Brazilian Portuguese", "pt-BR"), ("Simplified Chinese", "zh-Hans")
        ];
        Assert.Equal(expected.Select(locale => locale.Name), names);
        Assert.Equal(Texts.BundledPackCodes().Concat(["en", "de"]).Order(StringComparer.Ordinal),
            expected.Select(locale => locale.Code).Order(StringComparer.Ordinal));
        foreach ((string _, string code) in expected)
        {
            HtmlExportLocalization locale = new(code);
            Assert.Equal(code, locale.Language);
            Assert.False(string.IsNullOrWhiteSpace(locale.Get(HtmlExportText.Theme)));
            Assert.False(string.IsNullOrWhiteSpace(locale.Get(HtmlExportText.About)));
        }
        HtmlExportLocalization fallback = new("unsupported");
        Assert.Equal("en", fallback.Language);
        Assert.Equal("Theme", fallback.Get(HtmlExportText.Theme));
    }

    [Fact]
    public void PausedWatcherGuidancePromisesOneCheckNotOneHttpRequest()
    {
        // A pure domain example: an established, paused watcher can need a second page in
        // its manual check. No provider, transport, profile or scheduler is opened here.
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        Assert.True(DanbooruWatcher.TryCreate("help-watcher", "cat", now, out var watcher, out _));
        watcher = DanbooruWatcher.BeginCheck(watcher!, now).Watcher;
        watcher = DanbooruWatcher.ApplyPage(watcher,
            new(DanbooruWatcher.ProviderId, "cat", 1, ["100"], true), now).Watcher;
        watcher = DanbooruWatcher.Pause(watcher).Watcher;
        watcher = DanbooruWatcher.BeginCheck(watcher, now, manual: true).Watcher;
        watcher = DanbooruWatcher.ApplyPage(watcher,
            new(DanbooruWatcher.ProviderId, "cat", 1, ["102"], false), now).Watcher;
        Assert.Equal(DanbooruWatcherState.Checking, watcher.State);
        Assert.Equal(2, watcher.Progress.NextPage);
        watcher = DanbooruWatcher.ApplyPage(watcher,
            new(DanbooruWatcher.ProviderId, "cat", 2, ["101", "100"], true), now).Watcher;
        Assert.Equal(DanbooruWatcherState.Paused, watcher.State);

        string help = Plain(Section(Read(Help), "Saved watchers"));
        string guide = Plain(Read(DanbooruGuide));
        Assert.Contains("including on a paused watcher", help);
        Assert.DoesNotContain("exactly one request", help);
        Assert.Contains("auch bei einer pausierten Beobachtung", guide);
        Assert.DoesNotContain("genau eine Anfrage", guide);
        Assert.Contains("Jetzt prüfen ist kein Fortsetzen", guide);
    }

    [Fact]
    public void GlobalPauseAndPostFilterTransferGuidanceNamesTheCurrentBoundaries()
    {
        // F2/F3 runtime integration is parent-owned; this pins the Help wording that must
        // accompany it without starting a watcher, host or transfer operation.
        string watcher = Plain(Section(Read(Help), "Saved watchers"));
        Assert.Contains("automatic watcher checks do not start", watcher, StringComparison.Ordinal);
        Assert.Contains("An automatic check already running only finishes the page it is reading", watcher, StringComparison.Ordinal);
        Assert.Contains("Check now still runs one manual check, without unpausing the session or bypassing provider cooldowns", watcher, StringComparison.Ordinal);
        Assert.Contains("automatic checks continue below the saved cursor; individual pauses and cooldowns are kept", watcher, StringComparison.Ordinal);

        string cards = Plain(Section(Read(Help), "Reading posts as cards"));
        Assert.Contains("Post-filter profile transfer and backup are host APIs only", cards, StringComparison.Ordinal);
        Assert.Contains("not wired to a product-facing command or the transfer-package workflow yet", cards, StringComparison.Ordinal);
    }

    [Fact]
    public void StatisticsHelpNamesTheDirectionalHeaderContractAndMeasurementLimits()
    {
        // The directional graph sources are part of the integrated I/O contract. This is a
        // documentation check only; it does not start a sampler or inspect OS traffic.
        Assert.True(Enum.IsDefined(typeof(IoGraphSource), IoGraphSource.NetworkIo));
        Assert.True(Enum.IsDefined(typeof(IoGraphSource), IoGraphSource.DiskIo));

        string text = Plain(Section(Read(Help), "Statistics and disk traffic"));
        Assert.Contains("Network I/O (NetworkIo) keeps Down and Up as separate directions", text, StringComparison.Ordinal);
        Assert.Contains("Disk I/O (DiskIo) keeps Write and Read separate as well", text, StringComparison.Ordinal);
        Assert.Contains("Auto keeps both available in every work view where the layout fits", text, StringComparison.Ordinal);
        Assert.Contains("application-payload and media-I/O counters, not operating-system totals", text, StringComparison.Ordinal);
        Assert.Contains("explicit layout can select Network, Disk or text without summing the categories", text, StringComparison.Ordinal);
        Assert.DoesNotContain("upload is not measured", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writes only", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disk history graph is drawn only in Advanced", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Network only hides the disk value", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StatisticsCatalogAndHeaderValuesNameTheMeasurementLimits()
    {
        // The catalogs are read as data (no language state); the display seams are the production header rules.
        string[] names = ["stats.header.networkTitle", "stats.header.diskTitle", "stats.header.netValues", "stats.header.diskValues", "stats.footnote"];
        Dictionary<string, string> english = IoStatsPageTexts.Entries.Where(entry => names.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.English, StringComparer.Ordinal);
        Assert.Equal(names.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Network", english["stats.header.networkTitle"]);
        Assert.Equal("Disk", english["stats.header.diskTitle"]);
        Assert.Contains("request payload", english["stats.footnote"], StringComparison.Ordinal);
        Assert.Contains("without headers or encryption", english["stats.footnote"], StringComparison.Ordinal);
        Assert.DoesNotContain("Upload is not measured", english["stats.footnote"], StringComparison.Ordinal);

        // The header shows download and upload, write and read as values; Automatic draws both graphs in every work view.
        IoRates rates = new(NetDown: 10, NetUp: 5, DiskWrite: 20, DiskRead: 3000, Requests: 4);
        Assert.Equal(20, IoStatsDisplay.HeaderDiskRate(rates));
        Assert.False(IoStatsDisplay.HeaderShowsDiskValue(IoStatsDisplay.HeaderGraphNetwork));
        Assert.True(IoStatsDisplay.HeaderShowsDiskValue(IoStatsDisplay.HeaderGraphAuto));
        Assert.True(IoStatsDisplay.HeaderShowsDisk(IoStatsDisplay.HeaderGraphAuto));
        Assert.True(IoStatsDisplay.HeaderShowsDisk(IoStatsDisplay.HeaderGraphNetworkAndDisk));
    }

    [Fact]
    public void ReaderApplyAndQuotePreviewDateGuidanceMatchesTheProductSeams()
    {
        // A preview whose target thread is not the open one has no opening post (see ReaderSettingsExcerptTests for the
        // style rendering and the live-apply anchor checks); this pins the guidance to that seam.
        ResourceId other = new("4chan", "g", "999");
        Assert.Null(ArchivePage.ExcerptOpeningPostAt(null, other));
        Assert.Null(ArchivePage.ExcerptOpeningPostAt(null, null));

        string display = Plain(Section(Read(Help), "Reader display settings"));
        Assert.Contains("keeps your place in the open reader", display, StringComparison.Ordinal);
        Assert.Contains("does not jump to another post, take keyboard focus or switch the thread's tab", display, StringComparison.Ordinal);
        Assert.Contains("Quote previews use the same format; their tooltip shows the full local date", display, StringComparison.Ordinal);
        Assert.Contains("a post from another thread has no known opening post here, so Since opening post shows System format", display, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginChangeConsentGuidanceMatchesTheDialogAndItsSeparateConfirmations()
    {
        // The invalidation behavior itself is exercised by PluginChangeReviewTests (BundledPluginChangeConsent).
        Dictionary<string, string> english = PluginChangeReviewCatalog.Entries
            .Where(entry => entry.Key is "plugins.changeReview.ackChanges" or "plugins.changeReview.ackRights"
                or "plugins.changeReview.save")
            .ToDictionary(entry => entry.Key, entry => entry.English, StringComparer.Ordinal);
        Assert.Equal(3, english.Count);
        Assert.NotEqual(english["plugins.changeReview.ackChanges"], english["plugins.changeReview.ackRights"]);
        Assert.Contains("capability/settings changes", english["plugins.changeReview.ackChanges"], StringComparison.Ordinal);
        Assert.Contains("separately", english["plugins.changeReview.ackRights"], StringComparison.Ordinal);

        string text = Plain(Section(Read(Help), "Changed bundled-plugin capabilities or settings"));
        Assert.Contains("Editing a setting in the dialog, or using the acknowledgement for removed values, clears the change confirmation, even if you put the old value back", text, StringComparison.Ordinal);
        Assert.Contains("tick it again after your last edit", text, StringComparison.Ordinal);
        Assert.Contains("The separate permission confirmation is not cleared by edits", text, StringComparison.Ordinal);
        Assert.Contains("Save reviewed plugin for next start", text, StringComparison.Ordinal);
        Assert.Equal("Save reviewed plugin for next start", english["plugins.changeReview.save"]);
    }

    [Fact]
    public void DanbooruDisabledPluginAndGapRestartGuidanceMatchesTheShippedTexts()
    {
        (string Key, string English, string German)[] ui = BooruUiCatalog.Entries;
        (string Key, string English, string German)[] codes = MeowChan.Desktop.Localization.BooruCodeCatalog.Entries;
        (string Key, string English, string German) unavailable = Assert.Single(codes, entry => entry.Key == "booru.watcher.problem.source_unavailable");
        (string Key, string English, string German) closing = Assert.Single(codes, entry => entry.Key == "booru.watcher.problem.scheduler_stopped");
        (string Key, string English, string German) blocked = Assert.Single(codes, entry => entry.Key == "booru.watcher.problem.continuation_blocked");
        (string Key, string English, string German) restart = Assert.Single(ui, entry => entry.Key == "booru.ui.watcher.restart");
        (string Key, string English, string German) restarted = Assert.Single(ui, entry => entry.Key == "booru.ui.watcher.msg.restarted");
        (string Key, string English, string German) title = Assert.Single(ui, entry => entry.Key == "booru.ui.notify.queued.title");
        Assert.Contains("disabled or not running", unavailable.English, StringComparison.Ordinal);
        Assert.Contains("Nothing was requested", unavailable.English, StringComparison.Ordinal);
        Assert.Contains("watchers are no longer checked", closing.English, StringComparison.Ordinal);
        Assert.Contains($"\"{restart.English}\"", blocked.English, StringComparison.Ordinal);
        Assert.Contains("Posts in the gap are not downloaded; files already downloaded stay", restarted.English, StringComparison.Ordinal);

        string help = Plain(Section(Read(Help), "Saved watchers"));
        string guide = Plain(Read(DanbooruGuide));
        Assert.Contains($"offers {restart.English}. That accepts the gap", help, StringComparison.Ordinal);
        Assert.Contains("ends the watcher without deleting local data and creates a new one for the same search with a fresh baseline", help, StringComparison.Ordinal);
        Assert.Contains("Posts in the gap are not downloaded; files already downloaded stay", help, StringComparison.Ordinal);
        Assert.Contains($"bietet {restart.German} an", guide, StringComparison.Ordinal);
        Assert.Contains("Posts in der Lücke werden nicht geladen; bereits geladene Dateien bleiben", guide, StringComparison.Ordinal);
        Assert.Contains($"Titel {title.German}", guide, StringComparison.Ordinal);
        Assert.Contains($"titled {title.English}", help, StringComparison.Ordinal);

        Assert.Contains("While the Danbooru plugin is disabled or not running, new searches, lookups, Load original and watcher commands are refused with a notice that the plugin is not available, and no watcher check runs", help, StringComparison.Ordinal);
        Assert.Contains("A check still in flight when the plugin is disabled is discarded and establishes no baseline", help, StringComparison.Ordinal);
        Assert.Contains("Originals already accepted into the download queue stay there", help, StringComparison.Ordinal);
        Assert.Contains("Enabling the plugin again restarts watcher scheduling from the saved catalog", help, StringComparison.Ordinal);
        Assert.Contains("Once shutdown begins, watchers stop first and new Danbooru requests are refused", help, StringComparison.Ordinal);
        Assert.Contains("Ist das Danbooru-Plugin deaktiviert oder läuft es nicht, werden neue Suchen, Postabrufe, Original laden und Befehle für Suchbeobachtungen mit dem Hinweis abgelehnt, dass das Plugin nicht verfügbar ist", guide, StringComparison.Ordinal);
        Assert.Contains("Eine Prüfung, die beim Deaktivieren noch läuft, wird verworfen und legt keine Baseline fest", guide, StringComparison.Ordinal);
        Assert.Contains("Beim erneuten Aktivieren startet die Planung der Beobachtungen aus dem gespeicherten Katalog neu", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void NumberedFooterNavigationMatchesTheCommandOrderLabelsAndDefaultShortcuts()
    {
        string section = Plain(Section(Read(Help), "Selection and shortcuts"));
        Match navigation = Capture(section,
            @"Ctrl\+(?<first>\d+) through Ctrl\+(?<last>\d+) open (?<labels>.*?) in exactly that order");
        string[] labels = Regex.Split(navigation.Groups["labels"].Value, @",\s*|\s+and\s+");
        string[] ids = MainPageOrder.CommandIds.ToArray();
        Assert.Equal(ids.Select(id => CommandCatalog.Get(id).LabelEn), labels);
        int first = Number(navigation.Groups["first"].Value);
        int last = Number(navigation.Groups["last"].Value);
        Assert.Equal(ids.Length, last - first + 1);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.Equal((int?)(first + i), MainPageOrder.Digit(ids[i]));
            Assert.Equal("Primary+" + (first + i).ToString(CultureInfo.InvariantCulture), CommandCatalog.Get(ids[i]).Shortcut);
        }
    }

    [Fact]
    public void DocumentedPreviewLanguagesMatchTheEmbeddedPacksExactly()
    {
        string section = Plain(Section(Read(Help), "Language packs"));
        Match list = Capture(section, @"bundles (?<languages>.*?); every one is marked (?<status>\w+)");
        string[] documented = Regex.Split(list.Groups["languages"].Value, @",\s*|\s+and\s+");
        List<string> embedded = new();
        foreach (string code in Texts.BundledPackCodes())
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            Assert.NotNull(stream);
            using JsonDocument pack = JsonDocument.Parse(stream);
            Assert.Equal(code, pack.RootElement.GetProperty("code").GetString());
            string name = pack.RootElement.GetProperty("nativeName").GetString()!;
            string suffix = " (" + list.Groups["status"].Value + ")";
            Assert.EndsWith(suffix, name);
            embedded.Add(name[..^suffix.Length]);
        }
        Assert.NotEmpty(embedded);
        Assert.Equal(embedded.Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(Help, "up to (?<value>\\w+) tags", @"(?<pages>\w+) pages of (?<size>\d+) posts")]
    [InlineData(DanbooruGuide, "höchstens (?<value>\\w+) Tag-Terme", @"(?<pages>\w+) Seiten mit je (?<size>\d+) (?:Treffern|Posts)")]
    public void DanbooruTagAndPagingBudgetsMatchTheParserWatcherAndHttpContracts(string file, string tagsPattern, string pagesPattern)
    {
        string text = Plain(file == Help ? Section(Read(file), "Danbooru search and watchers") : Read(file));
        int tags = Number(Capture(text, tagsPattern).Groups["value"].Value);
        Assert.Equal(DanbooruTagQuery.MaximumTagTerms, tags);
        AssertNumber(text, file == Help
            ? @"tags and (?<value>\w+) optional rating:"
            : @"und optional (?<value>\w+) rating:-Term",
            DanbooruTagQuery.MaximumTerms - DanbooruTagQuery.MaximumTagTerms);
        Match[] pages = Regex.Matches(text, pagesPattern).Cast<Match>().ToArray();
        Assert.Equal(2, pages.Length); // Search and watcher each document their budget.
        foreach (Match page in pages)
        {
            int count = Number(page.Groups["pages"].Value);
            int size = Number(page.Groups["size"].Value);
            Assert.Equal(DanbooruTagQuery.MaximumSearchPages, count);
            Assert.Equal(DanbooruWatcher.DefaultPageBudget, count);
            Assert.Equal(BooruHostHttp.MaximumSearchPage, count);
            Assert.Equal(DanbooruTagQuery.MaximumSearchPageSize, size);
            Assert.Equal(DanbooruWatcher.DefaultPageSize, size);
            Assert.Equal(BooruHostHttp.MaximumSearchLimit, size);
        }

        // Exercise the documented boundary with both polarities and the optional rating.
        // This is a pure parser check; it does not imply provider acceptance or make requests.
        string query = string.Join(' ', Enumerable.Range(0, tags).Select(i => (i % 2 == 0 ? "" : "-") + "tag" + i));
        Assert.True(DanbooruTagQuery.TryParse(query, out var plainQuery, out var error), error.ToString());
        Assert.Equal(query, plainQuery!.ProviderQuery); // No implicit rating or ordering term.
        Assert.True(DanbooruTagQuery.TryParse(query + " rating:g", out var parsed, out error), error.ToString());
        Assert.Equal(tags, parsed!.TagTermCount);
        Assert.Equal(query + " rating:g", parsed.ProviderQuery);
        Assert.False(DanbooruTagQuery.TryParse(query + " extra_tag", out _, out error));
        Assert.Equal(DanbooruTagQueryError.TooManyTagTerms, error);
        Assert.False(DanbooruTagQuery.TryParse("tag rating:g rating:s", out _, out error));
        Assert.Equal(DanbooruTagQueryError.DuplicateRating, error);
    }

    [Fact]
    public void DocumentedMetadataHttpBudgetsMatchTransportConstants()
    {
        string text = Plain(Section(Read(Help), "Waiting, local data and offline use"));
        AssertNumber(text, @"starts? at least (?<value>\w+) second", BooruHostHttp.MinimumStartSpacing.TotalSeconds);
        AssertNumber(text, @"pause of at least (?<value>\d+) seconds", BooruHostHttp.MinimumCooldown.TotalSeconds);
        Assert.Contains("a longer valid Retry-After is not shortened", text, StringComparison.Ordinal);
        Assert.Contains("Only the technical maximum representable date bounds that deadline", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b\d+-hour cap\b", text);
        AssertNumber(text, @"no earlier than (?<value>\w+) seconds", BooruHostHttp.RetryDelay.TotalSeconds);
        AssertNumber(text, @"operation has at most (?<value>\d+) sends", BooruHostHttp.MaximumSendsPerJob);
        AssertNumber(text, @"(?<value>\d+)-second lifetime", BooruHostHttp.MaximumJobLifetime.TotalSeconds);
        AssertNumber(text, @"(?<value>\d+)-second deadline per response", BooruHostHttp.ResponseTimeout.TotalSeconds);
        AssertNumber(text, @"(?<value>\d+) MiB on the wire", BooruHostHttp.MaximumWireBytes / (1024 * 1024));
        AssertNumber(text, @"(?<value>\d+) MiB decoded", BooruHostHttp.MaximumDecodedBytes / (1024 * 1024));
    }

    [Fact]
    public void GermanCatalogDefaultsAndProviderCooldownContractMatchTheSameCode()
    {
        string text = Plain(Read(DanbooruGuide));
        AssertNumber(text, @"Schreibgrenze von (?<value>\d+) MiB", new Config().BooruCatalogLimitMiB);
        AssertNumber(text, @"auf (?<value>\d+) bis \d+ MiB", Config.BooruCatalogMinimumLimitMiB);
        AssertNumber(text, @"auf \d+ bis (?<value>\d+) MiB", Config.BooruCatalogMaximumLimitMiB);
        AssertNumber(text, @"Lesedeckel von (?<value>\d+) MiB", BooruCatalogStore.MaximumDocumentBytes / (1024 * 1024));
        AssertNumber(text, @"mindestens (?<value>\d+) Sekunden", BooruHostHttp.MinimumCooldown.TotalSeconds);
        Assert.Contains("Ein längeres gültiges Retry-After wird nicht auf 24 Stunden verkürzt", text, StringComparison.Ordinal);
        Assert.Contains("nur das technisch maximal darstellbare Datum begrenzt diesen Zeitpunkt", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"höchstens \d+ Stunden", text);
    }

    [Theory]
    [InlineData("Own-tag v1 backup and 1.1.0 local data", PrivateTagStore.V1BackupFileName)]
    [InlineData("Own tags and the shared media menu", PrivateTagStore.FileName)]
    [InlineData("Importing your own files", ManualImportCatalogStore.FileName)]
    public void BackupGuidanceNamesTheActualStoreFiles(string heading, string fileName)
    {
        string[] codeSpans = Regex.Matches(Section(Read(Help), heading), @"`(?<code>[^`\r\n]+)`")
            .Select(match => match.Groups["code"].Value).ToArray();
        Assert.Contains(fileName, codeSpans);
    }

    [Theory]
    [InlineData(Help)]
    [InlineData(DanbooruGuide)]
    public void LocalMarkdownLinksResolveInsideTheCheckout(string document)
    {
        // Inline links and reference definitions used by these guides; deliberately not a web
        // crawler or a general Markdown renderer. Code examples are not navigation links.
        string markdown = WithoutFencedCode(Read(document));
        markdown = Regex.Replace(markdown, @"`[^`\r\n]*`", "");
        IEnumerable<string> targets = Regex.Matches(markdown, @"\[[^\]\r\n]*\]\(<?(?<target>[^\s)>]+)>?(?:\s+""[^""]*"")?\)")
            .Select(match => match.Groups["target"].Value)
            .Concat(Regex.Matches(markdown, @"(?m)^\s{0,3}\[[^\]]+\]:\s*<?(?<target>[^\s>]+)>?")
                .Select(match => match.Groups["target"].Value));
        int localLinks = 0;
        foreach (string target in targets)
        {
            // Includes https/mailto/data/file schemes and protocol-relative remote URLs. Never
            // consult the network, the user's profile, or an absolute external filesystem path.
            if (target.StartsWith("//", StringComparison.Ordinal) || Regex.IsMatch(target, @"^[A-Za-z][A-Za-z0-9+.-]*:")) continue;
            string[] parts = target.Split('#', 2);
            string relative = Uri.UnescapeDataString(parts[0].Split('?', 2)[0]);
            string path = relative.Length == 0 ? Root(document)
                : relative.StartsWith('/') ? Root(relative.TrimStart('/'))
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Root(document))!, relative));
            Assert.True(path.StartsWith(Path.TrimEndingDirectorySeparator(Root("")) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                $"{document}: local link escapes the checkout: {target}");
            Assert.True(File.Exists(path), $"{document}: missing local link target: {target}");
            localLinks++;
            if (parts.Length == 2 && parts[1].Length > 0 && Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                Assert.Contains(Uri.UnescapeDataString(parts[1]), HeadingAnchors(File.ReadAllText(path)));
        }
        if (document == Help) Assert.True(localLinks > 0, "Help's local navigation was not parsed.");
    }

    private static string[][] LimitRows() => TableRows("Storage and limits");

    private static string[][] TableRows(string heading)
    {
        string[][] table = Section(Read(Help), heading).Split('\n')
            .Where(line => line.TrimStart().StartsWith('|'))
            .Select(line => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray()).ToArray();
        Assert.True(table.Length >= 2, $"{heading} table is missing.");
        Assert.Equal(new[] { "Setting", "Default", "Allowed" }, table[0]);
        Assert.All(table, row => Assert.Equal(3, row.Length));
        Assert.All(table[1], cell => Assert.Matches(@"^:?-{3,}:?$", cell));
        return table.Skip(2).ToArray();
    }

    private static void AssertNumber(string text, string pattern, double expected) =>
        Assert.Equal(expected, (double)Number(Capture(text, pattern).Groups["value"].Value));

    private static int Number(string text) => text switch
    {
        "one" or "ein" or "eine" or "einen" => 1,
        "two" or "zwei" => 2,
        "five" or "fünf" => 5,
        _ => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture)
    };

    private static Match Capture(string text, string pattern)
    {
        Match[] matches = Regex.Matches(text, pattern, RegexOptions.CultureInvariant).Cast<Match>().ToArray();
        Assert.True(matches.Length == 1, $"Expected one documentation field matching /{pattern}/, found {matches.Length}.");
        return matches[0];
    }

    private static string Plain(string text) => Regex.Replace(text.Replace("**", "").Replace("`", ""), @"\s+", " ").Trim();

    private static string Section(string markdown, string heading)
    {
        Match[] headings = Regex.Matches(markdown, @"(?m)^(?<level>#{1,6})\s+(?<title>[^\r\n]+)\r?$").Cast<Match>().ToArray();
        Match start = Assert.Single(headings, match => match.Groups["title"].Value.Trim() == heading);
        int depth = start.Groups["level"].Length;
        int end = headings.FirstOrDefault(match => match.Index > start.Index && match.Groups["level"].Length <= depth)?.Index ?? markdown.Length;
        return markdown[(start.Index + start.Length)..end];
    }

    private static IEnumerable<string> HeadingAnchors(string markdown)
    {
        // GitHub-style slugs for the ATX headings used in the linked local guides, including
        // duplicate-heading suffixes. Explicit HTML anchors are supported as well.
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        markdown = WithoutFencedCode(markdown);
        foreach (Match match in Regex.Matches(markdown, @"(?m)^#{1,6}\s+(?<title>[^\r\n]+)"))
        {
            string title = Regex.Replace(match.Groups["title"].Value, @"\s+#+\s*$", "");
            title = Regex.Replace(title, @"\[([^\]]+)\]\([^)]*\)", "$1");
            string slug = Regex.Replace(title.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\p{M}_\-\s]", "").Replace(' ', '-');
            int duplicate = occurrences.GetValueOrDefault(slug);
            occurrences[slug] = duplicate + 1;
            yield return duplicate == 0 ? slug : slug + "-" + duplicate.ToString(CultureInfo.InvariantCulture);
        }
        foreach (Match match in Regex.Matches(markdown, "<(?:a|h[1-6])\\b[^>]*\\b(?:id|name)=[\"'](?<id>[^\"']+)[\"']", RegexOptions.IgnoreCase))
            yield return match.Groups["id"].Value;
    }

    private static string WithoutFencedCode(string markdown) =>
        Regex.Replace(markdown, @"(?ms)^\s{0,3}(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n.*?^\s{0,3}\k<fence>\s*$", "");

    private static string Read(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
