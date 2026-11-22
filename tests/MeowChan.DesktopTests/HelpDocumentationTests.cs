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
using MeowChan.Desktop.Stats;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Interop.Links;
using MeowChan.PluginHost.Http;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// Documentation contracts, not evidence of live provider behavior or native UI acceptance.
/// Reads repository Markdown/source and embedded resources only: no profile, language-state mutation,
/// store construction, HTTP client or application startup is needed.
/// </summary>
public sealed class HelpDocumentationTests
{
    private const string Help = "docs/Help.md";
    private const string DanbooruGuide = "docs/user-guide/danbooru.md";
    private const string SettingsLimitsGuide = "docs/user-guide/settings-export-and-limits.md";

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
            { SettingsLimitLabel("archiveTagFilterTagsPerList"), c.ArchiveTagFilterTagsPerList, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum },
            { SettingsLimitLabel("urlTextExportLimitMiB"), c.UrlTextExportLimitMiB, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum },
            { SettingsLimitLabel("slashTagSuggestionCount"), c.SlashTagSuggestionCount, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum },
            { SettingsLimitLabel("videoScrubCacheMiB"), c.VideoScrubCacheLimitMiB, Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum },
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
            { "Import: catalog file size (MiB)", c.ImportCatalogLimitMiB, Config.ImportCatalogLimitMiBMinimum, Config.ImportCatalogLimitMiBMaximum },
            // The seven TP5 quick tagging limits; the row names are the English labels of the settings page itself.
            { QuickTagLabel("quickTagSuggestions"), c.QuickTagSuggestionCount, Config.QuickTagSuggestionCountMinimum, Config.QuickTagSuggestionCountMaximum },
            { QuickTagLabel("quickTagRecent"), c.QuickTagRecentCount, Config.QuickTagRecentCountMinimum, Config.QuickTagRecentCountMaximum },
            { QuickTagLabel("quickTagUndo"), c.QuickTagUndoDepth, Config.QuickTagUndoDepthMinimum, Config.QuickTagUndoDepthMaximum },
            { QuickTagLabel("quickTagNeighbours"), c.QuickTagNeighbourScan, Config.QuickTagNeighbourScanMinimum, Config.QuickTagNeighbourScanMaximum },
            { QuickTagLabel("quickTagFilename"), c.QuickTagFilenameTokens, Config.QuickTagFilenameTokensMinimum, Config.QuickTagFilenameTokensMaximum },
            { QuickTagLabel("quickTagSourceTags"), c.QuickTagSourceTagsPerMedium, Config.QuickTagSourceTagsPerMediumMinimum, Config.QuickTagSourceTagsPerMediumMaximum },
            { QuickTagLabel("quickTagMarked"), c.QuickTagMarkedMediaLimit, Config.QuickTagMarkedMediaLimitMinimum, Config.QuickTagMarkedMediaLimitMaximum }
        };
    }

    private static string SettingsLimitLabel(string stem) =>
        SettingsLimitsCatalog.Entries.Single(entry => entry.Key == "budgets." + stem).English;

    private static string QuickTagLabel(string stem)
    {
        Assert.Contains(QuickTagSettingsCatalog.Limits, limit => limit.Stem == stem);
        return QuickTagSettingsCatalog.Entries.Single(entry => entry.Key == "budgets." + stem).English;
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
        Assert.Contains("not a claim of release acceptance", text);
    }

    [Fact]
    public void FooterAndIntegratedWorkViewHelpMatchesTheCurrentNavigationPolicies()
    {
        string defaults = Plain(Section(Read(Help), "Desktop view defaults"));
        // The footer gained Tagging behind Media (footer button of the tagging page). Help.md and the Danbooru guide belong to Codex and may
        // still spell the work group without it; both spellings are accepted until they list it (Ctrl+1…7 and everything else is pinned as before).
        static string Spelling(IReadOnlyList<string> group, Func<CommandDefinition, string> label, bool withTagging) =>
            string.Join(", ", group.Where(id => withTagging || id != "navigation.tagging").Select(id => label(CommandCatalog.Get(id))));
        static string Order(bool withTagging, Func<CommandDefinition, string> label) =>
            string.Join(" | ", MainPageOrder.Groups.Select(group => Spelling(group, label, withTagging)));
        foreach (IReadOnlyList<string> group in MainPageOrder.Groups)
            Assert.True(new[] { true, false }.Any(withTagging => defaults.Contains(Spelling(group, c => c.LabelEn, withTagging), StringComparison.Ordinal)),
                "Help.md does not list the footer group: " + Spelling(group, c => c.LabelEn, true));
        string danbooru = Plain(Read(DanbooruGuide));
        Assert.True(new[] { true, false }.Any(withTagging => defaults.Contains(Order(withTagging, c => c.LabelDe), StringComparison.Ordinal)),
            "Help.md does not list the German footer order: " + Order(true, c => c.LabelDe));
        Assert.True(new[] { true, false }.Any(withTagging => danbooru.Contains(Order(withTagging, c => c.LabelDe), StringComparison.Ordinal)),
            "The Danbooru guide does not list the German footer order: " + Order(true, c => c.LabelDe));
        Assert.Contains("followed by More (German: Mehr) and Settings", defaults, StringComparison.Ordinal);
        Assert.Contains("More contains only destinations moved out of the footer", defaults, StringComparison.Ordinal);
        Assert.DoesNotContain("More contains Downloads", defaults, StringComparison.Ordinal);

        string workViews = Plain(Section(Read(Help), "Integrated work-view behavior"));
        (WorkViewPage Page, string Marker)[] integrated =
        [
            (WorkViewPage.Downloads, "Downloads"),
            (WorkViewPage.Archive, "Archive"),
            (WorkViewPage.Reader, "Reader"),
            (WorkViewPage.Gallery, "Gallery"),
            (WorkViewPage.Media, "Media"),
            (WorkViewPage.Booru, "Booru"),
            (WorkViewPage.Rules, "Rules"),
            (WorkViewPage.Sources, "The shared Plugins page"),
            (WorkViewPage.Status, "Status (Diagnostics and Statistics)"),
            (WorkViewPage.Diagnostics, "Diagnostics and Statistics"),
            (WorkViewPage.Statistics, "Diagnostics and Statistics"),
            (WorkViewPage.Settings, "Settings"),
            (WorkViewPage.Dialogs, "Dialogs and menus")
        ];
        foreach ((WorkViewPage page, string marker) in integrated)
        {
            Assert.NotEmpty(WorkViewPolicy.Entries.Where(entry => entry.Page == page));
            Assert.Contains(marker, workViews, StringComparison.Ordinal);
        }

        // The panel, popover and explicit multi-target flow are integrated; do not restore the old unavailable claims.
        Assert.DoesNotMatch(@"\b(?:AP|TP|PT|WV)\d+\b", workViews);
        Assert.Contains("In the gallery, Tags and Tag… open the quick tag panel in the slot of the post panel instead of a dialog", workViews, StringComparison.Ordinal);
        Assert.Contains("[Gallery tag panel](#gallery-tag-panel)", Section(Read(Help), "Integrated work-view behavior"), StringComparison.Ordinal);
        Assert.Contains("Media, reader attachments and Archive media tiles use a small tag popover", workViews, StringComparison.Ordinal);
        Assert.Contains("the gallery panel can change tags on explicitly marked media together", workViews, StringComparison.Ordinal);
        Assert.DoesNotContain("not connected to the gallery", workViews, StringComparison.Ordinal);
        Assert.DoesNotContain("still opens the existing tag dialog", workViews, StringComparison.Ordinal);
        Assert.Contains("gallery-tag-panel", HeadingAnchors(Read(Help)));

        var pluginDetails = Assert.Single(SourcesPluginsCatalog.Entries, entry => entry.Key == "sources.pluginDetails");
        Assert.Contains(pluginDetails.English, workViews, StringComparison.Ordinal);
        Assert.Contains(pluginDetails.German, workViews, StringComparison.Ordinal);
        var showPlugin = Assert.Single(PluginCatalog.Entries, entry => entry.Key == "plugins.manageSource");
        var showSources = Assert.Single(PluginCatalog.Entries, entry => entry.Key == "plugins.watches");
        Assert.Contains(showPlugin.English, workViews, StringComparison.Ordinal);
        Assert.Contains(showSources.English, workViews, StringComparison.Ordinal);
        Assert.Contains("German: " + showPlugin.German, workViews, StringComparison.Ordinal);
        Assert.Contains("German: " + showSources.German, workViews, StringComparison.Ordinal);
    }

    [Fact]
    public void GalleryTagPanelHelpDescribesOnlyTheWiredFlowAndItsOwnLimits()
    {
        string panel = Plain(Section(Read(Help), "Gallery tag panel"));

        // Command/data contracts only; real host routing is pinned below without creating a gallery or tag store.
        string[] wired = [QuickTagCommands.Next, QuickTagCommands.Previous, QuickTagCommands.CommitNext,
            QuickTagCommands.MarkNext, QuickTagCommands.MarkPrevious, QuickTagCommands.ThreadSection,
            QuickTagCommands.Undo, QuickTagCommands.StampApply];
        Assert.Equal(wired.Concat(Enumerable.Range(1, QuickTagCommands.SuggestionCount).Select(QuickTagCommands.Suggestion)).Order(StringComparer.Ordinal),
            GalleryTagPanelCommands.Handled.Order(StringComparer.Ordinal));
        Assert.Empty(QuickTagCommands.AwaitingHandler);
        Assert.Equal(GalleryTagPanelCommands.Handled.Order(StringComparer.Ordinal), QuickTagCommands.WiredIds.Order(StringComparer.Ordinal));
        Assert.Equal(new QuickTagKeyRoute(QuickTagKeyAction.MarkNext), GalleryTagPanelCommands.RouteOf(QuickTagCommands.MarkNext));
        Assert.Equal(new QuickTagKeyRoute(QuickTagKeyAction.UnmarkPrevious), GalleryTagPanelCommands.RouteOf(QuickTagCommands.MarkPrevious));
        Assert.Equal(new QuickTagKeyRoute(QuickTagKeyAction.ApplyStamp), GalleryTagPanelCommands.RouteOf(QuickTagCommands.StampApply));
        Assert.Contains("Shift+Page Down marks the shown medium and advances", panel, StringComparison.Ordinal);
        Assert.Contains("Shift+Page Up goes back and removes the mark from the medium it reaches", panel, StringComparison.Ordinal);
        Assert.Equal("Shift+PageDown", CommandCatalog.Get(QuickTagCommands.MarkNext).Shortcut);
        Assert.Equal("Shift+PageUp", CommandCatalog.Get(QuickTagCommands.MarkPrevious).Shortcut);
        Assert.Contains("With marks present, tag changes apply to all marked media, not an unmarked shown medium", panel, StringComparison.Ordinal);
        Assert.Contains("A tag carried by only some is under On some; one click gives it to all, the next removes it from all", panel, StringComparison.Ordinal);
        Assert.Contains("Clear marks clears the marking selection", panel, StringComparison.Ordinal);
        Assert.Contains("Outside the gallery, Tag… opens the small popover", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("not connected", panel, StringComparison.Ordinal);

        // Shortcuts named in the text are the catalog defaults, not a second copy.
        Assert.Equal("PageDown", CommandCatalog.Get(QuickTagCommands.Next).Shortcut);
        Assert.Equal("PageUp", CommandCatalog.Get(QuickTagCommands.Previous).Shortcut);
        Assert.Equal("Primary+Enter", CommandCatalog.Get(QuickTagCommands.CommitNext).Shortcut);
        Assert.Equal("Primary+T", CommandCatalog.Get(QuickTagCommands.ThreadSection).Shortcut);
        Assert.Equal("Primary+Z", CommandCatalog.Get(QuickTagCommands.Undo).Shortcut);
        Assert.Equal("Primary+Shift+T", CommandCatalog.Get("media.tags.edit").Shortcut);
        for (int number = 1; number <= QuickTagCommands.SuggestionCount; number++)
            Assert.Equal("Alt+" + number, CommandCatalog.Get(QuickTagCommands.Suggestion(number)).Shortcut);
        foreach (string phrase in new[]
        {
            "Ctrl+Shift+T (Command+Shift+T on macOS)", "Alt+1 to Alt+9 (Command+1 to Command+9 on macOS)", "Page Down and Page Up",
            "Ctrl+Enter (Command+Enter)", "Ctrl+Z (Command+Z)", "Ctrl+T (Command+T)", "Shift+P switches to the post panel"
        })
            Assert.Contains(phrase, panel, StringComparison.Ordinal);

        // Surface texts quoted by the help retain their documented English and German labels.
        IReadOnlyDictionary<string, string> english = BuiltInCatalog.English;
        Assert.Contains("Open tag editor", panel, StringComparison.Ordinal);
        Assert.Contains("Tag-Editor öffnen", panel, StringComparison.Ordinal);
        Assert.Contains(english["tags.quick.assigned"].Replace(" ({0})", ""), panel, StringComparison.Ordinal);
        Assert.Contains(english["tags.quick.thread.heading"].Replace(" ({0})", ""), panel, StringComparison.Ordinal);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, english["tags.quick.gallery.position"], 4, 27), panel, StringComparison.Ordinal);
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, english["tags.quick.gallery.positionMore"], 4, 27), panel, StringComparison.Ordinal);
        Assert.Contains(english["tags.quick.undo"], panel, StringComparison.Ordinal);
        foreach (string reason in new[] { "frequent", "recent", "thread", "neighbour", "fileName", "title", "sourceTag" })
            Assert.Contains(english["tags.quick.reason." + reason], panel, StringComparison.Ordinal);
        Assert.Contains(english["tags.quick.reason.sameFile"], panel, StringComparison.Ordinal);

        // Medium, thread and source tags stay three things; the three named settings and the undo depth are the real labels.
        Assert.Contains("Medium tags are your own tags on exact media revisions", panel, StringComparison.Ordinal);
        Assert.Contains("Thread tags mark the thread, not its media files", panel, StringComparison.Ordinal);
        Assert.Contains("Source tags come from a source such as Danbooru", panel, StringComparison.Ordinal);
        Assert.Contains("Danbooru is not edited", panel, StringComparison.Ordinal);
        Assert.Contains("Nothing is assigned automatically", panel, StringComparison.Ordinal);
        Assert.Contains("The favorite/rating row above the field acts on the shown medium or all marked media", panel, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Z undoes the newest tag, favorite or rating change made through the panel", panel, StringComparison.Ordinal);
        Assert.Contains("Tags, favorites and ratings remain distinct: adding a tag never changes a rating or favorite by itself", panel, StringComparison.Ordinal);
        Assert.Contains("it reverses exactly the media changed by that step", panel, StringComparison.Ordinal);
        Assert.Contains("the Tag undo steps setting", panel, StringComparison.Ordinal);
        Assert.Contains("Source tags per medium", panel, StringComparison.Ordinal);
        Assert.Equal("Tag undo steps", QuickTagLabel("quickTagUndo"));
        Assert.Equal("Source tags per medium", QuickTagLabel("quickTagSourceTags"));

        // The limits table has the real group/defaults; marking has a live selection limit, not a changed tag-store write ceiling.
        string limits = Plain(Section(Read(Help), "Storage and limits"));
        Assert.Contains("Quick tagging group (German: Schnell-Taggen)", limits, StringComparison.Ordinal);
        Assert.Equal("Quick tagging", english[QuickTagSettingsCatalog.GroupKey]);
        Assert.Equal("Schnell-Taggen", BuiltInCatalog.German[QuickTagSettingsCatalog.GroupKey]);
        Assert.Contains("applies a new value to panels you open afterwards", limits, StringComparison.Ordinal);
        Assert.Contains("Media marked for tagging limits the [gallery marking selection](#gallery-tag-panel)", limits, StringComparison.Ordinal);
        Assert.Contains("(default 500, 10–5000); its tag-store write limit stays fixed and separate", limits, StringComparison.Ordinal);
        Assert.Contains("an already open panel keeps the limits it opened with", limits, StringComparison.Ordinal);
        Assert.Contains("including 0, trims the remembered list at once, keeping the newest entries", limits, StringComparison.Ordinal);
        // Seven documented rows plus the tag panel's own suggestion limit; Help.md still has to get that row (it is Codex's file), then
        // the table mapping above takes it too.
        Assert.Equal(8, QuickTagSettingsCatalog.Limits.Length);
    }

    [Fact]
    public void GalleryMarkStampAndPopoverHelpHasCurrentSourceConnections()
    {
        // Source contracts only: these pin actual adapters, not runtime writes, focus or native UI behavior.
        string viewer = Read("Desktop/GalleryViewer.TagPanel.cs");
        Assert.Contains("surface.NavigationRequested += OnTagNavigationRequested;", viewer, StringComparison.Ordinal);
        Assert.Contains("ApplyMarkResult(controller.Mark(controller.ShownEntry()));", viewer, StringComparison.Ordinal);
        Assert.Contains("ApplyMarkResult(controller.Unmark(previous));", viewer, StringComparison.Ordinal);
        Assert.Contains("surface.MarksClearRequested += OnTagMarksClearRequested;", viewer, StringComparison.Ordinal);
        Assert.Contains("surface.ExtraUndo = controller.ExtraUndo;", viewer, StringComparison.Ordinal);
        Assert.Contains("run(decisions, controller.DecisionTargets())", viewer, StringComparison.Ordinal);
        string model = Read("Desktop/GalleryTagPanelModel.cs");
        Assert.Contains("if (Marks.Count > 0) return Selection(out reason);", model, StringComparison.Ordinal);
        Assert.Contains("_owner.OpenSelectionTarget(Marks.Snapshot(), out reason)", model, StringComparison.Ordinal);

        string panel = Plain(Section(Read(Help), "Gallery tag panel"));
        Assert.Contains("gallery panel, Media/reader/Archive popovers and Tagging page share the same stamp for this profile, saved with its settings", panel, StringComparison.Ordinal);
        Assert.Contains("each tag is its own undo step", panel, StringComparison.Ordinal);
        Assert.Contains("Lowering that size removes names from the end of the stamp, not tags from media", panel, StringComparison.Ordinal);
        Assert.Equal("Primary+Shift+Enter", CommandCatalog.Get(QuickTagCommands.StampApply).Shortcut);
        string owner = Read("Desktop/Shell/MainWindow.GalleryTags.cs");
        Assert.Contains("TaggingSettings.StampOptions(_config).CreateStamp(_config.TagStamp)", owner, StringComparison.Ordinal);
        Assert.Contains("_quickTagStamp.Capacity = _config.TagStampSize", owner, StringComparison.Ordinal);
        Assert.Contains("config => config.TagStamp = (string[])names.Clone()", owner, StringComparison.Ordinal);
        Assert.Contains("await RunAsync(target => target.AddExistingAsync(tag.Id, token));", Read("Desktop/QuickTagSession.cs"), StringComparison.Ordinal);
        Assert.Contains("case QuickTagKeyAction.ApplyStamp: OpenActions.Start(() => ApplyStampAsync(), \"tags.quick.stamp.key\"); return true;", Plain(Read("Desktop/Controls/QuickTagSurface.cs")), StringComparison.Ordinal);
        Assert.Contains("return surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.ApplyStamp))", Read("Desktop/Controls/QuickTagPopover.cs"), StringComparison.Ordinal);
        Assert.Contains("TagPopoverAsync = OpenMediaLibraryTagPopoverAsync", Read("Desktop/Shell/MainWindow.Media.cs"), StringComparison.Ordinal);
        Assert.Contains("_archivePage.TagPopoverAsync = OpenArchiveMediaTagPopoverAsync", Read("Desktop/Shell/MainWindow.Navigation.cs"), StringComparison.Ordinal);
        string popover = Read("Desktop/Shell/MainWindow.QuickTags.cs");
        Assert.Contains("if (!Current() || _mediaTagDialogOpen) return;", popover, StringComparison.Ordinal);
        Assert.Contains("if (_isClosing || _galleryTagDialogOpen || !isCurrent()) return;", popover, StringComparison.Ordinal);
        Assert.Contains("QuickTagPopover.OpenBlocked(anchor, label", popover, StringComparison.Ordinal);
    }

    [Fact]
    public void TaggingHelpMatchesThePageWalkAndCardSourceConnections()
    {
        string text = Plain(Section(Read(Help), "Tagging queue and settings"));
        foreach (string phrase in new[] { "it assigns no tags, favorites or ratings", "Open Tagging (German: Taggen) with its footer button right after Media",
            "Missing files stay in the queue", "Inside the tag panel, typing and chip keys keep their normal roles",
            "Ctrl+Shift+Enter applies the stamp and advances", "Shift+K toggles card mode during a walk",
            "Saving a smaller walk size does not remove queue entries" })
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        Assert.Equal("Q", CommandCatalog.Get("media.queue.add").Shortcut);
        // The page is a numbered footer page behind Media; its digit follows the applied footer layout.
        Assert.Contains("navigation.tagging", MainPageOrder.CommandIds);
        Assert.NotNull(MainPageOrder.Digit("navigation.tagging")); // numbered like every footer page since F1
        // The ⋯ menu holds no page entries since F1 (Help.md still says "⋯ menu"); the footer's More menu carries the tagging entry.
        Assert.Contains("<MenuItem Tag=\"nav.tagging\" Classes=\"localized\" Header=\"Tagging\" Click=\"OnTaggingNavigationClick\" />", Read("Desktop/Shell/MainWindow.axaml"), StringComparison.Ordinal);
        // Pin the host-to-page-to-gallery chain, not just configuration fields or a plan comment.
        string host = Read("Desktop/Shell/MainWindow.TaggingPage.cs");
        Assert.Contains("return NavigateWorkspace(WorkspacePage.Tagging);", host, StringComparison.Ordinal);
        Assert.Contains("StartTagging = StartTaggingFromPageAsync", host, StringComparison.Ordinal);
        Assert.Contains("Stamp = QuickTagStampSet", host, StringComparison.Ordinal);
        Assert.Contains("private Task StartTaggingFromPageAsync() => OpenTagQueueGalleryAsync(_taggingPage?.SelectedGroupId, _taggingPage?.UngroupedGroupSelected == true);", host, StringComparison.Ordinal);
        Assert.Contains("_gallery.StartTaggingMode(walk,", Read("Desktop/Shell/MainWindow.TagQueue.cs"), StringComparison.Ordinal);
        string mode = Read("Desktop/GalleryViewer.TaggingMode.cs");
        Assert.Contains("inField && !listOpen && string.IsNullOrEmpty(surface.Input.Text)", mode, StringComparison.Ordinal);
        Assert.Contains("surface.StampApplied += OnTaggingStampApplied;", mode, StringComparison.Ordinal);
        Assert.Contains("return RouteCardKey(e);", mode, StringComparison.Ordinal);
        Assert.Contains("if (!_cardsOn) return false;", Read("Desktop/GalleryViewer.Cards.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderHelpMatchesOptInVideoAndQuickSettingsSourceConnections()
    {
        string text = Plain(Section(Read(Help), "Reader media sidebar and inline video"));
        foreach (string phrase in new[] { "Changes are saved and apply immediately", "full Settings page still uses Save/Discard",
            // The sidebar is on by default and "Play videos in the reader" became the choice "Clicking a medium" (15.11.2026): Help.md
            // (owned by Codex) states the new defaults; its two old phrases are no longer pinned here.
            "Only one inline video plays at a time", "On Windows, a video not wholly inside the post viewport is released",
            "Autoplay videos muted is another opt-in, also off by default", "Neither option fetches video from a source" })
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        Assert.True(new Config().ReaderMediaSidebar);
        Assert.Equal(ReaderMediaClickMode.Sidebar, new Config().EffectiveReaderMediaClick);
        Assert.False(new Config().ReaderInlineVideoAutoplayMuted);
        // Only read the actual hosts; no player, decoder, settings window or session is constructed.
        Assert.Contains("_archivePage.QuickSettingsHost = CreateQuickSettingsHost();", Read("Desktop/Shell/MainWindow.Navigation.cs"), StringComparison.Ordinal);
        string media = Read("Desktop/Views/Archive/ArchivePage.ReaderMedia.cs");
        Assert.Contains("InitializeInlineVideo();", media, StringComparison.Ordinal);
        Assert.Contains("InitializeMediaSidebar();", media, StringComparison.Ordinal);
        Assert.Contains("new QuickSettingsPopover(QuickSettingsSurfaces.Get(QuickSettingsSurfaces.Reader), host", Read("Desktop/Views/Archive/ArchivePage.QuickSettings.cs"), StringComparison.Ordinal);
        string video = Read("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        Assert.Contains("ArchivePosts.SetValue(ArchiveAttachmentView.InlineVideoProperty, _inlineVideo);", video, StringComparison.Ordinal);
        Assert.Contains("return Task.WhenAll(_inlineVideo.ReleaseAllAsync(), preview);", video, StringComparison.Ordinal);
        Assert.Contains("return _inlineVideo.PauseAllAsync();", video, StringComparison.Ordinal);
        string controller = Read("Desktop/ReaderInlineVideo.cs");
        Assert.Contains("await PauseOthersAsync(owner);", controller, StringComparison.Ordinal);
        Assert.Contains("Trim(Options.MaxPlayers - 1, keep: owner);", controller, StringComparison.Ordinal);
        Assert.Contains("nativeSurface ? (intersects && fullyVisible ? ViewportAction.Keep : ViewportAction.Release)", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileHelpMatchesSeparateWindowAndListOnlyRemovalSourceContracts()
    {
        string text = Plain(Section(Read(Help), "Profiles in separate windows"));
        foreach (string phrase in new[] { "this window stays open", "An already open profile is not started twice",
            "checks the path and explains unsuitable choices without creating the folder", "added as a read-only folder source",
            "the new window still starts and explains the refusal", "removing a list entry deletes neither the profile nor its media" })
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        // Source only: never instantiate a service/launcher/registry or inspect a real profile.
        string host = Read("Desktop/Shell/MainWindow.Profiles.cs");
        Assert.Contains("NewProfileDialog dialog = new(CreateProfileService(), _config.Language);", host, StringComparison.Ordinal);
        Assert.Contains("StartupMediaFolderRegistration.RegisterAsync(session, argument, others)", host, StringComparison.Ordinal);
        string service = Read("Desktop/ProfileSwitchService.cs");
        Assert.Contains("case ProfileOpenCheck.InUse: return new(ProfileActionStatus.InUse", service, StringComparison.Ordinal);
        Assert.Contains("if (Validate(request.Name, request.Folder, request.DownloadFolder, request.MediaFolder) is { } problem) return new(ProfileActionStatus.Refused, problem);", service, StringComparison.Ordinal);
        Assert.Contains("return _registry.Forget(folder) ? ProfileForgetOutcome.Removed : ProfileForgetOutcome.NotListed;", service, StringComparison.Ordinal);
        Assert.Contains("_launcher.Launch(target, mediaFolder);", service, StringComparison.Ordinal);
        Assert.Contains("Process.Start(info)", Read("Desktop/ProfileLauncher.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gallery.toolbar.tags", Presence.Overflow, Presence.Direct, Presence.Direct, "rating, media tags and slideshow controls are under More in Barebones and on the toolbar from Simple on")]
    [InlineData("gallery.toolbar.slideshow", Presence.Overflow, Presence.Direct, Presence.Direct, "A chosen or running slideshow keeps its toggle on the toolbar even in Barebones")]
    // E2 (QW4): the playback mode left the bar in every view; the phrases are the parts of docs/Help.md that name each row.
    [InlineData("gallery.toolbar.playback-mode", Presence.Overflow, Presence.Overflow, Presence.Overflow, "playback mode")]
    [InlineData("gallery.toolbar.help", Presence.Overflow, Presence.Overflow, Presence.Direct, "shortcut help")]
    [InlineData("diagnostics.filters", Presence.Collapsed, Presence.Direct, Presence.Direct, "Diagnostics starts with its filters collapsed in Barebones")]
    [InlineData("diagnostics.toolbar.copy", Presence.Overflow, Presence.Direct, Presence.Direct, "Copy moves under More in Barebones")]
    [InlineData("diagnostics.toolbar.retry-filtered", Presence.Overflow, Presence.Overflow, Presence.Direct, "retrying all filtered entries stays under More in Barebones and Simple")]
    [InlineData("statistics.reset", Presence.Overflow, Presence.Overflow, Presence.Direct, "Reset statistics is under More in Barebones and Simple, directly available in Advanced")]
    [InlineData("settings.group.reading-media", Presence.Collapsed, Presence.Direct, Presence.Direct, "Simple opens interface extras, download extras, reading/media and behavior")]
    [InlineData("settings.group.limits", Presence.Collapsed, Presence.Collapsed, Presence.Direct, "storage limits and the keyboard-shortcuts group remain collapsed")]
    [InlineData("settings.group.shortcuts", Presence.Collapsed, Presence.Collapsed, Presence.Direct, "Advanced opens all six groups")]
    [InlineData("dialogs.board-admission.text-rules", Presence.Collapsed, Presence.Direct, Presence.Direct, "board-admission text rules start collapsed only in Barebones")]
    [InlineData("dialogs.export.options", Presence.Collapsed, Presence.Collapsed, Presence.Direct, "export update/backup options start collapsed in Barebones and Simple")]
    public void WorkViewRemainderHelpMatchesSpecificPolicyRows(string id, Presence barebones, Presence simple,
        Presence advanced, string guidance)
    {
        WorkViewEntry entry = WorkViewPolicy.Get(id);
        Assert.Equal(barebones, entry.Barebones);
        Assert.Equal(simple, entry.Simple);
        Assert.Equal(advanced, entry.Advanced);
        Assert.Contains(guidance, Plain(Section(Read(Help), "Integrated work-view behavior")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Add results and rejected URLs", "admission.problemsOnly")]
    [InlineData("Add results and rejected URLs", "admission.putBack")]
    [InlineData("Archive filters and saved searches", "tags.filter.add")]
    [InlineData("Archive filters and saved searches", "tags.filter.add.mode")]
    [InlineData("Archive filters and saved searches", "tags.filter.include")]
    [InlineData("Archive filters and saved searches", "tags.filter.exclude")]
    [InlineData("Saving a video frame", "command.video.snapshot")]
    [InlineData("Saving a video frame", "gallery.frame.saved")]
    [InlineData("Rule templates", "rules.save")]
    [InlineData("Rule templates", "rules.overview.create")]
    [InlineData("Rule templates", "rules.discard")]
    [InlineData("Statistics and disk traffic", "stats.headerIcons.label")]
    [InlineData("Back and Close", "archive.back")]
    [InlineData("Back and Close", "gallery.return")]
    [InlineData("Back and Close", "command.media.return")]
    [InlineData("Back and Close", "reader.close")]
    [InlineData("Back and Close", "command.preview.close")]
    [InlineData("Back and Close", "action.hideDetails")]
    public void HelpActionNamesMatchEnglishAndGermanCatalogs(string heading, string key)
    {
        string text = Plain(Section(Read(Help), heading));
        Assert.Contains(BuiltInCatalog.English[key].Replace("{0}", "n"), text, StringComparison.Ordinal);
        Assert.Contains(BuiltInCatalog.German[key].Replace("{0}", "n"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedUrlHelpDescribesTheNonDestructivePutBackRules()
    {
        string text = Plain(Section(Read(Help), "Add results and rejected URLs"));
        Assert.Contains("If every row is a problem, there is nothing to filter and this checkbox is not shown", text);
        Assert.Contains("Already present does not", text);
        Assert.Contains("that text is kept and only missing rejected URLs are appended", text);
        Assert.Contains("This action itself adds nothing", text);
        string[] rejected = AdmissionProblemPlan.RejectedUrls(new[]
        {
            ("https://example.invalid/rejected", true),
            ("https://example.invalid/accepted", false),
            ("https://example.invalid/rejected", true)
        }).ToArray();
        Assert.Equal(new[] { "https://example.invalid/rejected" }, rejected);
        Assert.Equal(rejected[0], AdmissionProblemPlan.FieldText("submitted", "submitted", rejected));
        Assert.Equal("new draft" + Environment.NewLine + rejected[0],
            AdmissionProblemPlan.FieldText("new draft", "submitted", rejected));
        foreach (string id in new[] { "thread.details", "plugins.details" })
        {
            Assert.Equal("Show details", CommandCatalog.Get(id).LabelEn);
            Assert.Equal("Details anzeigen", CommandCatalog.Get(id).LabelDe);
            Assert.Contains(CommandCatalog.Get(id).LabelEn, text);
            Assert.Contains(CommandCatalog.Get(id).LabelDe, text);
        }
    }

    [Fact]
    public void ArchiveHelpSeparatesTheCurrentFilterEditorFromThreadAndMediaTagging()
    {
        string filters = Plain(Section(Read(Help), "Archive filters and saved searches"));
        Assert.Contains("open Add tag…", filters);
        Assert.Contains("Enter includes it; Shift+Enter or a leading - excludes it", filters);
        Assert.Contains("small toggle on a draft chip switches between include and exclude", filters);
        Assert.Contains("Text typed but not added is flagged when you search", filters);
        Assert.Contains("Adding or toggling a draft tag waits for Search", filters);
        Assert.Equal(("tag", false), TagFilterPicker.ParseQuery("tag", false));
        Assert.Equal(("tag", true), TagFilterPicker.ParseQuery("tag", true));
        Assert.Equal(("tag", true), TagFilterPicker.ParseQuery("-tag", false));
        string wiring = Read("Desktop/Views/Archive/ArchivePage.TagFilter.cs");
        Assert.Contains("ArchiveTagChoice.Picked = (id, exclude) => AddDraftTag(id, include: !exclude)", wiring);
        Assert.Contains("ArchiveTagChoice.KeepQueryOnDismiss = true", wiring);
        Assert.Contains("() => ToggleDraftChip(drafted[position].Id)", wiring);
        Assert.Contains("<controls:TagFilterPicker x:Name=\"ArchiveTagChoice\"", Read("Desktop/Views/Archive/ArchivePage.axaml"));
        string tags = Plain(Section(Read(Help), "Own tags and the shared media menu"));
        Assert.Contains("own-tag editor beside the posts", tags);
        Assert.Contains("inline editor follows the open thread until you close it", tags);
        Assert.Equal("Primary+T", CommandCatalog.Get("thread.tags").Shortcut);
        Assert.Equal("Primary+Shift+T", CommandCatalog.Get("media.tags.edit").Shortcut);
        Assert.Contains("Ctrl+Shift+T / Command+Shift+T", tags);
        Assert.Contains("Open tag editor", tags, StringComparison.Ordinal);
        Assert.Contains("Tag-Editor öffnen", tags, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave5HelpKeepsNewGalleryAndMediaLabelsBilingual()
    {
        string gallery = Plain(Section(Read(Help), "Images, search and navigation"));
        foreach (string phrase in new[]
        {
            "Shift plus a letter shows or hides a panel or block",
            "Shift+K toggles Card mode on / off",
            "Kartenmodus ein / aus",
            "Shift+D starts or pauses Start / pause slideshow",
            "Diashow starten / pausieren",
            "M adds the favorite (German: Favorit hinzufügen), U removes it and F toggles it",
            "Favorit hinzufügen",
            "Shift+P performs Show post context",
            "Post-Kontext anzeigen",
            "Shift+L"
        })
            Assert.Contains(phrase, gallery, StringComparison.Ordinal);

        string media = Plain(Section(Read(Help), "Images, search and navigation"));
        Assert.Contains("Tag list", media, StringComparison.Ordinal);
        Assert.Contains("Tag-Liste", media, StringComparison.Ordinal);
        string focus = Plain(Section(Read(Help), "Media filters and focus mode"));
        Assert.Contains("Exit focus", focus, StringComparison.Ordinal);
        Assert.Contains("Fokus beenden", focus, StringComparison.Ordinal);
    }

    [Fact]
    public void GalleryHelpMatchesLeftPlacementAndUnreducedSafetyState()
    {
        string text = Plain(Section(Read(Help), "Images, search and navigation"));
        Assert.Contains("far left of the header, before the title, with the favorite star directly to its right", text);
        Assert.Contains("Zoom steps, original export, saved ratings, frame saving, video rules, overlays and fullscreen stay under More in every view", text);
        // The left-hand Post | Tags segment is not the gallery's Back action.
        Assert.Contains("Back to {target}", text);
        Assert.Contains("The side panel has Post | Tags at the top left, the live medium counter immediately beside it, and × alone at the right", text);
        Assert.Contains("The counter updates as you navigate, with localized position text and polite accessibility announcements", text);
        string sidePanel = Read("Desktop/Controls/GallerySidePanel.cs");
        Assert.Contains("Children.Add(_segment);", sidePanel);
        Assert.Contains("Children.Add(close); SetColumn(close, 2);", sidePanel);
        // Tom's rule of 14.11.: the code places the way back first at the LEFT with the star directly right of it. The two Help.md assertions
        // above (right edge wording) change together with the Help section, which the coordinator edits (GL3 report has the sentence).
        Assert.Contains("_close.HorizontalAlignment = HorizontalAlignment.Left", Read("Desktop/GalleryViewer.cs"));
        Assert.Contains("_close.Bounds.Width : 44) + StarGap", Read("Desktop/GalleryViewer.Favorites.cs"));
        foreach (WorkView view in Enum.GetValues<WorkView>())
        {
            foreach (string id in new[] { "gallery.back", "gallery.favorite.indicator", "gallery.video.transport",
                "gallery.status.media-unavailable", "gallery.info.provenance", "gallery.favorite.confirm" })
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("gallery.menu.save-frame", view));
            Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("gallery.toolbar.post-panel", view));
        }
        Assert.Contains("gallery.toolbar.slideshow", WorkViewPolicy.GalleryBarToolIds(WorkView.Barebones, slideshowActive: true));
        Assert.DoesNotContain("gallery.toolbar.slideshow", WorkViewPolicy.GalleryBarToolIds(WorkView.Barebones));
    }

    [Fact]
    public void MediaHelpMatchesSavedSortAndNarrowToolbarContracts()
    {
        string text = Plain(Section(Read(Help), "Images, search and navigation"));
        Assert.Contains("The sort criterion and direction are saved for the next start; Random is not remembered", text);
        Assert.Contains("the next start returns to your last non-random criterion and direction", text);
        Config saved = new(); // Pure preferences: no profile is opened or saved.
        Assert.True(MediaSortPreference.Remember(saved, MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed));
        Assert.False(MediaSortPreference.Remember(saved, MediaLibrarySort.Random, MediaLibrarySortDirection.Default));
        Assert.Equal((MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed),
            MediaSortPreference.Restore(saved.MediaSort, saved.MediaSortDirection));
        Assert.Equal((MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed),
            MediaSortPreference.Restore(MediaSortPreference.Format(MediaLibrarySort.Size),
                MediaSortPreference.Format(MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed)));
        Assert.Equal((MediaLibrarySort.ByThread, MediaLibrarySortDirection.Default),
            MediaSortPreference.Restore(MediaSortPreference.Format(MediaLibrarySort.Random),
                MediaSortPreference.Format(MediaLibrarySort.Random, MediaLibrarySortDirection.Reversed)));
        string mediaHost = Read("Desktop/Shell/MainWindow.Media.cs");
        Assert.Contains("_mediaPage.SaveSortAsync = (sort, direction) => sort == MediaLibrarySort.Random", mediaHost);
        Assert.Contains("MediaSortPreference.Remember(config, sort, direction)", mediaHost);
        Assert.Contains("_mediaPage.RestoreSort(savedSort, savedDirection)", Read("Desktop/Shell/MainWindow.Media.cs"));
        Assert.Contains("In narrow windows the focus button, Refresh and Shuffle again move under More; Order stays visible", text);
        Assert.Equal(new[] { "media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle" },
            MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Reduced));
        Assert.False(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Compact, "media.sort"));
        foreach (WorkView view in Enum.GetValues<WorkView>())
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("media.sort", view));
        foreach (string key in new[] { "media.exportAll", "command.media.rating.manage", "media.showMissing", "media.filter.missingChip" })
        {
            Assert.Contains(BuiltInCatalog.English[key], text);
            Assert.Contains(BuiltInCatalog.German[key], text);
        }
    }

    [Fact]
    public void FrameFolderAndReadFirstHeaderGuidanceKeepTheirCurrentBoundaries()
    {
        string frames = Plain(Section(Read(Help), "Saving a video frame"));
        Assert.Equal("Shift+S", CommandCatalog.Get("video.snapshot").Shortcut); // UX-LEDGER-17: S skips tagging items
        Assert.Contains("On first use you choose a folder", frames);
        Assert.Contains("Cancel saves nothing", frames);
        Assert.Contains("Existing files are never overwritten", frames);
        Assert.Contains("it is not remembered as the frames folder", frames);
        Assert.Contains("There is no separate Settings control", frames);
        string export = Path.Combine(Path.GetTempPath(), "help-export");
        Assert.False(GalleryFrameFile.IsDedicatedFolder(export, export));
        Assert.True(GalleryFrameFile.IsDedicatedFolder(Path.Combine(Path.GetTempPath(), "help-frames"), export));

        string header = Plain(Section(Read(Help), "From a package to an offline picture"));
        foreach (string key in new[] { "stats.header.down", "stats.header.up", "stats.header.read", "stats.header.write" })
        {
            Assert.Contains(BuiltInCatalog.English[key], header);
            Assert.Contains(BuiltInCatalog.German[key], header);
        }
        Assert.Contains("Down and Read are solid with a filled area; Up and Write are dashed", header);
        Assert.DoesNotContain("Down ↓", header);
        Assert.DoesNotContain("Read R", header);
        Assert.Equal(37, IoStatsDisplay.HeaderDiskRate(new IoRates(NetDown: 1, NetUp: 2, DiskWrite: 11, DiskRead: 37, Requests: 0)));
    }

    [Fact]
    public void SnapshotHelpFollowsTheCommandCatalogAndTheGermanNameUsersActuallySee()
    {
        string frames = Plain(Section(Read(Help), "Saving a video frame"));
        CommandDefinition snapshot = CommandCatalog.Get("video.snapshot");
        Assert.Equal("command.video.snapshot", snapshot.LabelKey);
        Assert.Equal("Shift+S", snapshot.Shortcut); // UX-LEDGER-17: S is tagging skip
        Assert.Contains("**Save frame as image**", Section(Read(Help), "Saving a video frame"), StringComparison.Ordinal);
        Assert.Contains(snapshot.LabelEn, frames, StringComparison.Ordinal);

        // CommandCatalog.Label looks the key up in the localization catalog first and uses the definition's LabelDe only as a
        // fallback, so the German name shown in the gallery menu is the catalog text. The Help must name that one.
        string shownGerman = BuiltInCatalog.German[snapshot.LabelKey];
        Assert.Contains("(German: " + shownGerman + ", " + snapshot.Shortcut + ")", frames, StringComparison.Ordinal);
        Assert.DoesNotContain("Einzelbild als Bild speichern", Read(Help), StringComparison.Ordinal);

        // The flow the new sentences describe: a free numbered name from the video name and position, only for a ready video, Under More.
        string unused = Path.Combine(Path.GetTempPath(), "help-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unused);
        try
        {
            string temporary = GalleryFrameFile.TemporaryPath(unused);
            File.WriteAllBytes(temporary, [1]);
            Assert.Equal("clip-1-12-345.png", Path.GetFileName(GalleryFrameFile.Commit(temporary, unused, "clip.webm", 72_345)));
        }
        finally { Directory.Delete(unused, recursive: true); }
        Assert.Contains("named after the video and the playback position", frames, StringComparison.Ordinal);
        Assert.Contains("only while a video is shown and ready", frames, StringComparison.Ordinal);
        Assert.Contains("if (CanUseVideoTransport && _videoReadyRequest == _request && _entry != null) SaveFrameRequested", Read("Desktop/GalleryViewer.cs"), StringComparison.Ordinal);
        foreach (WorkView view in Enum.GetValues<WorkView>())
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("gallery.menu.save-frame", view));
        Assert.Contains("sits under More in every work view", frames, StringComparison.Ordinal);
        Assert.Contains("Der Dateiname nennt das Video und die Abspielposition", frames, StringComparison.Ordinal);
        Assert.Contains("in jeder Arbeitsansicht unter Mehr", frames, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesSaveHelpDescribesTheOverviewReturnTheStatusTextAndTheFocusTarget()
    {
        string rules = Plain(Section(Read(Help), "Rule templates"));
        string saved = string.Format(CultureInfo.InvariantCulture, BuiltInCatalog.English["rules.overview.saved"], "…");
        Assert.Equal("Rule for … saved. Applies to new decisions.", saved);
        Assert.Contains("“" + saved + "”", rules, StringComparison.Ordinal);
        Assert.Contains("opens on an overview of saved rules by scope, unsaved drafts and the read-only built-in defaults", rules, StringComparison.Ordinal);
        Assert.Contains("(German: Programmstandards)", rules, StringComparison.Ordinal);
        Assert.Equal("Programmstandards", BuiltInCatalog.German["rules.overview.defaults"]);
        Assert.Contains("guides you through Scope, Settings and Review", rules, StringComparison.Ordinal);
        Assert.Equal("Scope|Settings|Review", string.Join('|', new[] { "rules.wizard.stepScope", "rules.wizard.stepSettings", "rules.wizard.stepReview" }
            .Select(key => BuiltInCatalog.English[key])));
        Assert.Contains("puts keyboard focus on the saved rule's row, or on the new row for a new rule", rules, StringComparison.Ordinal);
        Assert.Contains("If saving is refused or fails", rules, StringComparison.Ordinal);
        Assert.Contains("you stay in the editor and see the reason", rules, StringComparison.Ordinal);
        Assert.Contains("Drafts stay on the overview when you switch pages", rules, StringComparison.Ordinal);

        // Static source seams (no page is constructed): the Save button runs the return path and a refusal keeps the wizard open.
        string page = Read("Desktop/Views/Rules/RulesPage.axaml.cs");
        Assert.Contains("OnSaveRule(object? sender, RoutedEventArgs e) => await SaveAndReturnToOverviewAsync()", page, StringComparison.Ordinal);
        Assert.Contains("public bool CanSave => IsEditable && _draft.Dirty && !_draft.Stale;", page, StringComparison.Ordinal);
        string wizard = Read("Desktop/Views/Rules/RulesPage.Wizard.cs");
        Assert.Contains("if (!saved || _ended || !_wizard.IsOpen || _draft.Scope != scope) return saved;", wizard, StringComparison.Ordinal);
        Assert.Contains("_overviewStatus = (\"rules.overview.saved\", FormatScope(scope));", wizard, StringComparison.Ordinal);
        Assert.Contains("_wizardReturnScope = scope;", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("Save rule stays available while a field is invalid", Read(Help), StringComparison.Ordinal);
    }

    [Fact]
    public void BackAndCloseHelpMatchesNamedHeaderRowsAndDialogEscapeGuards()
    {
        string text = Plain(Section(Read(Help), "Back and Close"));
        Assert.Contains("stand at the far left of their header row", text, StringComparison.Ordinal);
        Assert.Contains("come first in the Tab order", text, StringComparison.Ordinal);
        Assert.Contains("Inline rows keep their × close action at the far right", text, StringComparison.Ordinal);
        Assert.Contains("The gallery keeps Back to {target} at the far left", text, StringComparison.Ordinal);
        Assert.Contains("Esc closes as before", text, StringComparison.Ordinal);
        Assert.Contains("Dialogs keep their title-bar × at the right", text, StringComparison.Ordinal);
        Assert.Contains("Esc first ends a local edit, recording or question; it does not close while saving or deleting", text, StringComparison.Ordinal);
        Assert.Contains("Settings asks about unsaved changes", text, StringComparison.Ordinal);
        // Source contracts, not a claim that every window or native title bar has been accepted.
        Assert.Contains("WindowChrome.Frame(window, content, KindFor(modal), captionName)", Read("Desktop/Controls/DialogWindow.cs"), StringComparison.Ordinal);
        string escape = Read("Desktop/Controls/DialogEscape.cs");
        Assert.Contains("if (tryCancelLocal?.Invoke() == true) return DialogEscapeOutcome.CancelledLocal;", escape, StringComparison.Ordinal);
        Assert.Contains("return isBusy?.Invoke() == true ? DialogEscapeOutcome.Busy : DialogEscapeOutcome.Close;", escape, StringComparison.Ordinal);
        Assert.Contains("if (outcome != DialogEscapeOutcome.Close) return;", escape, StringComparison.Ordinal);
        string settings = Read("Desktop/Views/Settings/SettingsWindow.axaml.cs");
        Assert.Contains("tryCancelLocal: AskBeforeDiscardingOnEscape, isBusy: () => Editor.IsBusy", settings, StringComparison.Ordinal);
        Assert.Contains("if (discard && !Editor.IsBusy) Close();", settings, StringComparison.Ordinal);
        Assert.True(HeaderBar.BackGap > 0 && HeaderBar.BackSpacing > 0);

        // Every button the Help names is declared in a HeaderBar: the ways back of pages in the back slot, the inline tool rows' closes in
        // the end slot (static XAML check; layout and tab order are covered by HeaderBarContractTests).
        string main = Read("Desktop/Shell/MainWindow.axaml");
        string archive = Read("Desktop/Views/Archive/ArchivePage.axaml");
        (string Xaml, string Name)[] backs =
        [
            (archive, "ArchiveBackButton"), (archive, "ArchiveGalleryReturn"), (archive, "ArchiveMediaReturn"), (archive, "ArchiveCloseReader")
        ];
        foreach ((string xaml, string name) in backs)
            Assert.Matches("<Button\\s+x:Name=\"" + name + "\"[^>]*controls:HeaderBar\\.IsBack=\"True\"", xaml);
        (string Xaml, string Name)[] ends =
        [
            (main, "DownloadSearchClose"), (main, "InspectorCloseButton"),
            (archive, "ClosePreviewButton")
        ];
        foreach ((string xaml, string name) in ends)
            Assert.Matches("<Button\\s+x:Name=\"" + name + "\"[^>]*controls:HeaderBar\\.IsEnd=\"True\"", xaml);

        // The reader header declares its ways back before its own title, so they are first in reading and Tab order.
        int start = archive.IndexOf("<controls:HeaderBar x:Name=\"ReaderHeaderBar\">", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string header = archive[start..archive.IndexOf("</controls:HeaderBar>", start, StringComparison.Ordinal)];
        int title = header.IndexOf("ArchiveReaderTitle", StringComparison.Ordinal);
        Assert.True(title >= 0);
        foreach (string name in new[] { "ArchiveGalleryReturn", "ArchiveMediaReturn", "ArchiveBackButton", "ArchiveCloseReader" })
        {
            int at = header.IndexOf(name, StringComparison.Ordinal);
            Assert.True(at >= 0 && at < title, name);
        }
    }

    [Fact]
    public void ShortcutHelpMatchesTheModelessSharedDraftSourceContract()
    {
        string text = Plain(Section(Read(Help), "Customizing shortcuts"));
        foreach (string phrase in new[] { "open the same window, not another copy", "It stays open while you use the main window, reader or gallery",
            "In the gallery it starts with Gallery, followed by Tag panel", "Esc first cancels a recording or local question",
            "unsaved changes prompt before closing", "The window shares its draft with Settings",
            "Conflicting bindings are marked and prevent saving until resolved" })
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        // Source contracts only. Do not construct a window/editor, mutate language state or save a profile.
        string window = Read("Desktop/Views/Shortcuts/ShortcutsWindow.cs");
        foreach (string connection in new[] { "ConditionalWeakTable<Window, ShortcutsWindow>",
            "DialogWindow.Configure(this, DialogSize.L, modal: false)", "new ShortcutBindingsEditor(host.ShortcutSession)",
            "SaveHandler = host.CommitShortcutsAsync", "window.Show(owner);", "if (For(owner) is { } open)",
            "IsPassive(Window window) => window is ShortcutsWindow",
            "DialogEscape.Attach(this, Editor.TryCancelLocal, () => Editor.IsSaving)", "Editor.AskBeforeClose(close =>" })
            Assert.Contains(connection, window, StringComparison.Ordinal);
        Assert.DoesNotContain("window.ShowDialog", window, StringComparison.Ordinal);
        string shell = Read("Desktop/Shell/MainWindow.Fullscreen.cs");
        Assert.Contains("ShortcutsWindow.Open(this, SettingsEditor, context", shell, StringComparison.Ordinal);
        Assert.Contains("!MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.IsPassive(window)", shell, StringComparison.Ordinal);
        Assert.Contains("ExpandedAreas: new[] { ShortcutGroups.TagPanel }", Read("Desktop/GalleryViewer.cs"), StringComparison.Ordinal);
        Assert.Contains("if (!ValidateDraft()) { RefreshFooter(); return; }", Read("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void CompactRateExamplesAndTheHeaderIconOptionMatchTheFormatterSettingAndCatalog()
    {
        string statistics = Plain(Section(Read(Help), "Statistics and disk traffic"));
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Assert.Equal("127 KiB/s", ByteUnits.Rate(127 * 1024, invariant));
        Assert.Equal("1.27 MiB/s", ByteUnits.Rate(1.27 * 1024 * 1024, invariant));
        Assert.Equal("12.7 MiB/s", ByteUnits.Rate(12.7 * 1024 * 1024, invariant));
        Assert.Equal("0.98 KiB/s", ByteUnits.Rate(1000, invariant));
        Assert.Equal("999 B/s", ByteUnits.Rate(999, invariant));
        foreach (string example in new[] { "127 KiB/s", "1.27 MiB/s", "12.7 MiB/s" })
            Assert.Contains(example, statistics, StringComparison.Ordinal);
        Assert.Contains("Rates use at most three digits with adaptive precision", statistics, StringComparison.Ordinal);
        Assert.Contains("1000 B/s becomes 0.98 KiB/s", statistics, StringComparison.Ordinal);
        Assert.Contains("Below 1 KiB/s whole bytes are shown", statistics, StringComparison.Ordinal);
        Assert.Contains("it does not change file-size totals", statistics, StringComparison.Ordinal);

        // The optional icon mode: off by default, labelled in the settings category the user sees, never the only name.
        Assert.False(new Config().StatsHeaderIcons);
        Assert.Contains("is optional and off by default", statistics, StringComparison.Ordinal);
        Assert.Contains("Settings → Session and window → Statistics (German: Sitzung und Fenster → Statistik)", statistics, StringComparison.Ordinal);
        Assert.Equal("Session and window", BuiltInCatalog.English["settings.behavior"]);
        Assert.Equal("Sitzung und Fenster", BuiltInCatalog.German["settings.behavior"]);
        Assert.Equal("Statistics", BuiltInCatalog.English["stats.settings.title"]);
        Assert.Equal("Statistik", BuiltInCatalog.German["stats.settings.title"]);
        Assert.Contains("Read keeps the solid line and Write the dashed one", statistics, StringComparison.Ordinal);
        Assert.Contains("the button keeps its full screen-reader description", statistics, StringComparison.Ordinal);
        Assert.Contains("Turning it off restores the words", statistics, StringComparison.Ordinal);
        foreach (string tip in new[] { "stats.headerIcons.readTip", "stats.headerIcons.downTip" })
            Assert.Contains("Solid line", BuiltInCatalog.English[tip], StringComparison.Ordinal);
        foreach (string tip in new[] { "stats.headerIcons.writeTip", "stats.headerIcons.upTip" })
            Assert.Contains("Dashed line", BuiltInCatalog.English[tip], StringComparison.Ordinal);
        string overview = Plain(Section(Read(Help), "From a package to an offline picture"));
        Assert.Contains("replace the words with symbols and arrows", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings → Behavior", Plain(Read(Help)), StringComparison.Ordinal);
    }

    [Fact]
    public void QuickTagLimitHelpNamesTheRecentTagsException()
    {
        string limits = Plain(Section(Read(Help), "Storage and limits"));
        Assert.Contains("an already open panel keeps the limits it opened with", limits, StringComparison.Ordinal);
        Assert.Contains("The one exception is Recently used tags remembered: a lower saved value, including 0, trims the remembered list at once, keeping the newest entries", limits, StringComparison.Ordinal);
        Assert.Equal("Recently used tags remembered", QuickTagLabel("quickTagRecent"));
        Assert.Contains("QuickTagSettings.ApplyRecentLimit(recent, _config);", Read("Desktop/Shell/MainWindow.GalleryPreferences.cs"), StringComparison.Ordinal);
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
        // Map exact field names to their component contracts: the Fit cap and manual zoom
        // have separate limits, even though they share one documentation section.
        { "Reader display settings", "Preview image width (px)", ReaderDisplayPreferences.ReaderThumbnailWidthDefault, ReaderDisplayPreferences.ReaderThumbnailWidthMinimum, ReaderDisplayPreferences.ReaderThumbnailWidthMaximum },
        { "Reader display settings", "Fold long posts above (lines)", ReaderDisplayLimits.FoldLinesDefault, ReaderDisplayLimits.FoldLinesMinimum, ReaderDisplayLimits.FoldLinesMaximum },
        { "Reader display settings", "Lines in quote previews", ReaderDisplayLimits.PreviewLinesDefault, ReaderDisplayLimits.PreviewLinesMinimum, ReaderDisplayLimits.PreviewLinesMaximum },
        { "Reader display settings", "Quote preview delay (ms)", ReaderDisplayLimits.PreviewDelayDefault, ReaderDisplayLimits.PreviewDelayMinimum, ReaderDisplayLimits.PreviewDelayMaximum },
        { "Reader display settings", "Reply cards per step", ReaderDisplayLimits.ReplyStepDefault, ReaderDisplayLimits.ReplyStepMinimum, ReaderDisplayLimits.ReplyStepMaximum },
        { "Reader display settings", "Lines per reply card", ReaderDisplayLimits.ReplyLinesDefault, ReaderDisplayLimits.ReplyLinesMinimum, ReaderDisplayLimits.ReplyLinesMaximum },
        { "Reader display settings", "Posts per reader page", ArchivePage.ReaderPageSizeDefault, ArchivePage.ReaderPageSizeMinimum, ArchivePage.ReaderPageSizeMaximum },
        { "Reader display settings", "Maximum conversation indentation depth", ReaderConversationTree.DepthDefault, ReaderConversationTree.DepthMinimum, ReaderConversationTree.DepthMaximum },
        { "Gallery Fit enlargement", "Maximum enlargement (%)", GalleryFitPreferences.MaxUpscalePercentDefault, GalleryFitPreferences.MaxUpscalePercentMinimum, GalleryFitPreferences.MaxUpscalePercentMaximum },
        { "Gallery Fit enlargement", "Maximum zoom (%)", GalleryZoomLimits.MaxPercentDefault, GalleryZoomLimits.MaxPercentMinimum, GalleryZoomLimits.MaxPercentMaximum }
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
    public void ReaderAndGalleryNumericTablesCoverAllTwelveFieldsWithoutDuplicates()
    {
        // Eight reader numbers, separate Fit and manual zoom caps, and two post-filter store limits.
        foreach (string heading in new[] { "Reader display settings", "Gallery Fit enlargement" })
            Assert.Equal(ReaderAndGalleryLimits().Where(row => (string)row[0] == heading).Select(row => (string)row[1]).Order(StringComparer.Ordinal),
                TableRows(heading).Select(row => row[0]).Order(StringComparer.Ordinal));
        Assert.Equal(12, ReaderAndGalleryLimits().Count + Limits().Count(row => (string)row[0] is "Post filter rules" or "Individually hidden posts"));
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
    public void ContinueReadingHelpMatchesRoutingHistoryFallbackAndReaderStatusSemantics()
    {
        string help = Plain(Read(Help));
        Assert.Contains("it remains visible while the reader or gallery is open", help, StringComparison.Ordinal);
        Assert.Contains("Inside the reader or gallery, left-click opens the history list", help, StringComparison.Ordinal);
        Assert.Contains($"default {Config.ReadingHistoryLimitDefault}, {Config.ReadingHistoryLimitMinimum}–{Config.ReadingHistoryLimitMaximum}", help, StringComparison.Ordinal);
        Assert.Contains($"default {Config.ReadingHistoryMenuEntriesDefault}, {Config.ReadingHistoryMenuEntriesMinimum}–{Config.ReadingHistoryMenuEntriesMaximum}", help, StringComparison.Ordinal);
        Assert.Contains("the next captured post is used when one exists, otherwise the last captured post", help, StringComparison.Ordinal);
        Assert.Contains("A missing saved post does not create a new unread boundary", help, StringComparison.Ordinal);
        Assert.Contains("The reader status line shows when automatic display is active", help, StringComparison.Ordinal);

        string resume = Read("Desktop/Views/Archive/ArchivePage.Resume.cs");
        Assert.Contains("_unreadMark = ResumeUnreadMark(view, position, fallback);", resume, StringComparison.Ordinal);
        Assert.Contains("fallback ? Texts.T(\"resume.fallback\")", resume, StringComparison.Ordinal);
        string live = Read("Desktop/Views/Archive/ArchivePage.Live.cs");
        Assert.Contains("liveState.Text = Texts.T(\"reader.live.stateOn\")", live, StringComparison.Ordinal);
        Assert.Contains("WorkViewGate.Apply(liveState, ArchiveWorkView, _followReader)", live, StringComparison.Ordinal);
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
    public void MediaSortBarHelpMatchesTheImplementedCriteriaAndDirectionRules()
    {
        string text = Plain(Section(Read(Help), "Images, search and navigation"));
        Match list = Capture(text, @"On Media, the Order selector \(German: Sortierung\) offers (?<sorts>.*?)\. Every order");
        string[] documented = Regex.Split(list.Groups["sorts"].Value, @",\s*|\s+and\s+");
        Dictionary<string, string> labels = MediaLibraryCatalog.Entries
            .Where(entry => entry.Key.StartsWith("media.sort.", StringComparison.Ordinal)
                && entry.Key is not "media.sort.direction" and not "media.sort.direction.ascending"
                and not "media.sort.direction.descending" and not "media.sort.direction.unavailable")
            .ToDictionary(entry => entry.Key, entry => entry.English, StringComparer.Ordinal);
        string Key(MediaLibrarySort sort) => sort switch
        {
            MediaLibrarySort.ByThread => "media.sort.thread",
            MediaLibrarySort.Newest => "media.sort.newest",
            MediaLibrarySort.Random => "media.sort.random",
            MediaLibrarySort.Captured => "media.sort.captured",
            MediaLibrarySort.Name => "media.sort.name",
            MediaLibrarySort.Size => "media.sort.size",
            MediaLibrarySort.References => "media.sort.references",
            MediaLibrarySort.TagCount => "media.sort.tagCount",
            MediaLibrarySort.Rating => "media.sort.rating",
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, null)
        };
        string[] expected = Enum.GetValues<MediaLibrarySort>().Select(sort => labels[Key(sort)]).ToArray();
        Assert.Equal(expected, documented);
        Assert.All(Enum.GetValues<MediaLibrarySort>(), sort => Assert.Equal(sort != MediaLibrarySort.Random, MediaLibraryView.HasDirection(sort)));
        Assert.Contains("Every order except Random has a direction toggle between its default and reversed order", text, StringComparison.Ordinal);
        Assert.Contains("Random has no direction toggle", text, StringComparison.Ordinal);
        Assert.Contains("Shuffle again (German: Neu mischen) changes its stable shuffle seed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StatisticsHelpNamesTheDirectionalHeaderContractAndMeasurementLimits()
    {
        // The directional graph sources are part of the integrated I/O contract. This is a
        // documentation check only; it does not start a sampler or inspect OS traffic.
        Assert.True(Enum.IsDefined(typeof(IoGraphSource), IoGraphSource.NetworkIo));
        Assert.True(Enum.IsDefined(typeof(IoGraphSource), IoGraphSource.DiskIo));

        string text = Plain(Section(Read(Help), "Statistics and disk traffic"));
        Assert.Contains("Network I/O keeps Down and Up as separate directions", text, StringComparison.Ordinal);
        Assert.Contains("Disk I/O keeps Read and Write separate as well", text, StringComparison.Ordinal);
        Assert.Contains("Auto keeps both available in every work view where the layout fits", text, StringComparison.Ordinal);
        Assert.Contains("application-payload and media-I/O counters, not operating-system totals", text, StringComparison.Ordinal);
        Assert.Contains("explicit layout can select Network, Disk or text without summing the categories", text, StringComparison.Ordinal);
        Assert.Contains("The header uses a word/rate matrix: Down then Up for network traffic, Read then Write for disk traffic", text, StringComparison.Ordinal);
        Assert.Contains("The visible header keeps Down / Up and Read / Write in solid-series / dashed-series order, with no Network or Disk headings", text, StringComparison.Ordinal);
        Assert.Contains("Off hides the display and stops its own sampling claim, not downloads or other statistics sampling", text, StringComparison.Ordinal);
        // Header-off is not a global measurement switch; the page still has its independent visible claim.
        Assert.Equal(IoStatsVisibility.None, IoStatsDisplay.Claim(windowShown: true, graphVisible: false, pageVisible: false));
        Assert.Equal(IoStatsVisibility.Window, IoStatsDisplay.Claim(windowShown: true, graphVisible: false, pageVisible: true));
        Assert.Contains("IoStatsDisplay.Claim(SpeedSamplingWanted, TransferSpeed.IsVisible, StatisticsSegmentShown)",
            Read("Desktop/Shell/MainWindow.Transfer.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("upload is not measured", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writes only", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disk history graph is drawn only in Advanced", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Network only hides the disk value", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentedHeaderChoicesCoverTheCurrentSettingsListInBothLanguages()
    {
        // Documentation/source contract only: no SettingsEditor or sampler is constructed here.
        string choices = Read("Desktop/Views/Settings/SettingsEditor.StatsHeaderGraph.cs");
        string[] keys = Regex.Matches(choices,
            """new\(IoStatsDisplay\.\w+,\s*Texts\.T\("(?<key>stats\.headerGraph\.[^"]+)"\)\)""")
            .Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(keys);
        foreach (string suffix in new[] { "auto", "network", "disk", "networkAndDisk", "sideBySide", "text", "off" })
            Assert.Contains("stats.headerGraph." + suffix, keys);
        string documented = Plain(Section(Read(Help), "Statistics and disk traffic"));
        foreach (string key in keys)
        {
            var label = Assert.Single(IoStatsPageTexts.Entries, entry => entry.Key == key);
            Assert.Contains(Plain(label.English), documented, StringComparison.Ordinal);
            Assert.Contains(Plain(label.German), documented, StringComparison.Ordinal);
        }
        Assert.Contains("does not turn off statistics collection", documented, StringComparison.Ordinal);
        Assert.Contains("schaltet die Statistikmessung nicht aus", documented, StringComparison.Ordinal);
    }

    [Fact]
    public void GermanBooruGuideKeepsManualChecksAvailableDuringGlobalPause()
    {
        string guide = Plain(Read("docs/user-guide/danbooru.md"));
        Assert.Contains("Sitzungspause verhindert neue automatische Prüfungen", guide, StringComparison.Ordinal);
        Assert.Contains("Jetzt prüfen führt auch während der globalen Pause eine manuelle Prüfung aus", guide, StringComparison.Ordinal);
        Assert.Contains("ohne die Sitzung fortzusetzen oder einen Provider-Cooldown zu umgehen", guide, StringComparison.Ordinal);
        Assert.Contains("individuelle Pausen und Cooldowns bleiben erhalten", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Sitzungspause verhindert ebenfalls neue Prüfungen", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void StatisticsCatalogAndHeaderValuesNameTheMeasurementLimits()
    {
        // The catalogs are read as data (no language state), and the visible Help wording is pinned alongside the display seams.
        string[] names = ["stats.header.networkTitle", "stats.header.diskTitle", "stats.header.netValues", "stats.header.diskValues",
            "stats.header.down", "stats.header.up", "stats.header.write", "stats.header.read", "stats.footnote"];
        Dictionary<string, string> english = IoStatsPageTexts.Entries.Where(entry => names.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.English, StringComparer.Ordinal);
        Assert.Equal(names.Order(StringComparer.Ordinal), english.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Network", english["stats.header.networkTitle"]);
        Assert.Equal("Disk", english["stats.header.diskTitle"]);
        Assert.Equal("↓ {0} ↑ {1}", english["stats.header.netValues"]);
        Assert.Equal("R {0} W {1}", english["stats.header.diskValues"]);
        Assert.Equal("Down", english["stats.header.down"]);
        Assert.Equal("Up", english["stats.header.up"]);
        Assert.Equal("Write", english["stats.header.write"]);
        Assert.Equal("Read", english["stats.header.read"]);
        string documented = Plain(Section(Read(Help), "Statistics and disk traffic"));
        Assert.Contains("The header uses a word/rate matrix: Down then Up for network traffic, Read then Write for disk traffic", documented, StringComparison.Ordinal);
        Assert.Contains("request payload", english["stats.footnote"], StringComparison.Ordinal);
        Assert.Contains("without headers or encryption", english["stats.footnote"], StringComparison.Ordinal);
        Assert.DoesNotContain("Upload is not measured", english["stats.footnote"], StringComparison.Ordinal);

        // The header shows download and upload, read and write as values; Automatic draws both graphs in every work view.
        IoRates rates = new(NetDown: 10, NetUp: 5, DiskWrite: 20, DiskRead: 3000, Requests: 4);
        Assert.Equal(3000, IoStatsDisplay.HeaderDiskRate(rates));
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
        string rawSection = Section(Read(Help), "Page shortcuts and footer numbering");
        string section = Plain(rawSection);
        // One canonical table: page, fixed letter, digit in Simple, digit in Barebones and in Advanced. Validate every row; do not silently filter malformed ones.
        Match pageTable = Capture(rawSection,
            @"(?m)^\| Page \| Letter \| Digit in Simple[^\r\n]*\|\r?\n\|[^\r\n]*\|\r?\n(?<rows>(?:\|[^\r\n]*\|\r?\n)+)");
        string[] rows = pageTable.Groups["rows"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToArray();
        // The table follows the digits a new profile (Simple, default footer) sees; Booru is hidden by default and comes last.
        string[] ids = FooterLayout.Default.NumberingFor(WorkView.Simple).ToArray();
        Assert.Equal(MainPageOrder.DefaultNumbering.Count, rows.Length);
        Assert.Equal(ids.Length, rows.Length);
        IReadOnlyList<string> barebones = FooterLayout.Default.NumberingFor(WorkView.Barebones);
        IReadOnlyList<string> advanced = FooterLayout.Default.NumberingFor(WorkView.Advanced);
        Assert.Equal(barebones, advanced);
        for (int i = 0; i < ids.Length; i++)
        {
            Match row = Capture(rows[i],
                @"^\| (?<label>[^|]+) \| Ctrl\+Shift\+(?<letter>[A-Z]) \| Ctrl\+(?<simple>\d) \| Ctrl\+(?<other>\d) \|$");
            string id = ids[i];
            Assert.Equal(CommandCatalog.Get(id).LabelEn, row.Groups["label"].Value.Trim());
            Assert.Equal(PageKeyScheme.Letter(id), row.Groups["letter"].Value);
            Assert.Equal(i + 1, Number(row.Groups["simple"].Value));
            Assert.Equal(barebones.ToList().IndexOf(id) + 1, Number(row.Groups["other"].Value));
        }
        // The default footer hides Booru, so its digit is the last one in every work view.
        Assert.Equal("navigation.booru", ids[^1]);
        Assert.Equal("navigation.booru", barebones[^1]);
        Assert.Equal(MainPageOrder.CommandIds.Count, new HashSet<string>(ids).Count);
        // Shown in the footer, Booru takes the fifth digit in every work view (a button under More in Barebones counts as well).
        foreach (WorkView view in WorkViewSetting.All)
            Assert.Equal(5, FooterLayout.Default.WithVisible("navigation.booru", true).NumberingFor(view).ToList().IndexOf("navigation.booru") + 1);
        Assert.Contains("The table lists the pages in the order of the digits in Simple", section, StringComparison.Ordinal);
        Assert.Contains("which the default footer hides, comes last", section, StringComparison.Ordinal);
        Assert.Contains("it takes digit 5 in every work view", section, StringComparison.Ordinal);
        Assert.Contains("A binding you saved for another command always wins over a default", section, StringComparison.Ordinal);
        foreach (string compatibility in MainPageOrder.StatusSegmentCommandIds.Append("navigation.plugins"))
        {
            Assert.False(MainPageOrder.Contains(compatibility));
            Assert.Null(MainPageOrder.Digit(compatibility));
            Assert.Null(CommandCatalog.Get(compatibility).Shortcut);
        }
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void DocumentedSavedPluginsOverrideWinsWithoutChangingStatusOrOtherPageSlots(ShortcutPlatform platform)
    {
        string help = Plain(Section(Read(Help), "Selection and shortcuts"));
        Assert.Contains("A saved navigation.plugins binding, for example on Primary+6, still opens All plugins on the Plugins page", help, StringComparison.Ordinal);
        Assert.Contains("a saved Plugins binding on Ctrl+6 or Ctrl+8, takes precedence over the Booru default and keeps its meaning", Plain(Read(Help)), StringComparison.Ordinal);
        foreach (string file in new[] { Help, DanbooruGuide })
            Assert.Contains("hat Vorrang vor dem Booru-Standard", Plain(Read(file)), StringComparison.Ordinal);

        // The Help sentence names the compatibility slot; the default layout assigns Booru from the footer order, so the mechanics are checked independently.
        ShortcutGesture six = ShortcutGesture.Parse("Primary+" + MainPageOrder.Digit("navigation.booru"));
        EffectiveShortcutBindings defaults = EffectiveShortcutBindings.Build(platform: platform);
        // Revision 15: the page's digit first, its fixed letter second (PageKeyScheme).
        Assert.True(EffectiveShortcutBindings.Equivalent(six, defaults.For("navigation.booru").First(), platform));
        Assert.Empty(defaults.For("navigation.plugins"));
        // Saved historical bindings remain byte-for-byte unchanged; the new default, not the user's override, yields.
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("navigation.plugins", [six]);
        foreach (string id in MainPageOrder.StatusSegmentCommandIds)
            section = section.Set(id, [ShortcutGesture.Parse(id == "navigation.statistics" ? "Primary+9" : "Primary+Shift+7")]);
        string before = section.Raw.GetRawText();
        EffectiveShortcutBindings saved = EffectiveShortcutBindings.Build(section, platform);
        Assert.Equal(before, section.Raw.GetRawText());
        Assert.True(EffectiveShortcutBindings.Equivalent(six, Assert.Single(saved.For("navigation.plugins")), platform));
        // The yielding digit is gone; Booru keeps its fixed letter, which the saved binding does not touch.
        Assert.Equal(new[] { PageKeyScheme.Letter("navigation.booru") }, saved.For("navigation.booru").Select(g => g.Key));
        Assert.DoesNotContain(saved.Issues, issue => issue.BlocksSave);
        Assert.Contains(saved.Issues, issue => issue.CommandId == "navigation.booru"
            && issue.OtherCommandId == "navigation.plugins" && issue.Reason?.Code == "defaultYields");
        foreach (string id in MainPageOrder.NumberedCommandIds.Where(id => id != "navigation.booru"))
            Assert.True(EffectiveShortcutBindings.Equivalent(defaults.For(id).First(), saved.For(id).First(), platform));
        foreach (string id in MainPageOrder.StatusSegmentCommandIds)
            Assert.True(EffectiveShortcutBindings.Equivalent(
                ShortcutGesture.Parse(id == "navigation.statistics" ? "Primary+9" : "Primary+Shift+7"), Assert.Single(saved.For(id)), platform));
        Assert.Equal(MainWindow.WorkspacePage.Sources, MainWindow.PageFor("navigation.plugins"));
        string shell = Read("Desktop/Shell/MainWindow.Fullscreen.cs");
        Assert.Contains("_sourcesPage?.ActivateAsync(SourcesPluginsFilter.AllPlugins)", shell, StringComparison.Ordinal);
        Assert.Contains("ShowStatus(segment)", shell, StringComparison.Ordinal);
        Assert.Contains("CanExecuteShellPowerCommand(e)", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void BooruDisplayHelpMatchesTheSavedPresentationSwitchAndItsHideScopes()
    {
        string text = Plain(Section(Read(Help), "Booru page and display switch"));
        // The switch is "Show Booru actions" in Settings (Interface > Footer), no longer the "Show Booru" check box of the footer or the app menu.
        var label = Assert.Single(FooterSettingsCatalog.Entries, entry => entry.Key == "settings.footer.booruActions");
        Assert.Contains(label.English, text, StringComparison.Ordinal);
        Assert.Contains(label.German, text, StringComparison.Ordinal);
        Assert.Contains("saved, profile-wide display switch for the Booru page and the Danbooru tools", text, StringComparison.Ordinal);
        Assert.Contains("The switch has no footer check box and no app-menu twin any more", text, StringComparison.Ordinal);
        Assert.Contains("not source authorization, plugin activation or a watcher pause", text, StringComparison.Ordinal);
        Assert.Contains("Sources, watchers and downloads keep running under their existing pause, cooldown and availability rules", text, StringComparison.Ordinal);
        Assert.Contains("does not automatically resume a paused watcher, submit a search or bypass those rules", text, StringComparison.Ordinal);
        Assert.Contains("F1 can still list a hidden or unavailable page binding", text, StringComparison.Ordinal);
        foreach (string file in new[] { Help, DanbooruGuide })
        {
            string german = Plain(Read(file));
            foreach (string phrase in new[] { label.German,
                "gespeicherter, profilweiter Anzeigeschalter",
                "Ohne eingebundene Booru-Dienste fehlen",
                "Booru-Einträge in Fußleiste, Mehr und Appmenü sowie Medien → Mehr → Booru öffnen aus",
                "das Seitenkürzel öffnet keine ausgeblendete Seite",
                "Von der gewählten Booru-Seite geht es zu Downloads zurück",
                "bewahrt den ungesendeten Entwurf, solange dieselbe Quelleneinbindung besteht",
                "nur Anzeige, keine Quellenautorisierung, Plugin-Aktivierung oder Suchbeobachtungspause",
                "Anzeigen setzt keine pausierte Suchbeobachtung automatisch fort" })
                Assert.Contains(phrase, german, StringComparison.Ordinal);
        }

        // Pure policy/config and source contracts, not a Window or a profile-backed runtime test.
        Config config = new();
        Assert.True(config.ShowBooruTools);
        bool paused = config.DownloadsPaused;
        var permissions = config.SourceMediaIntegrityPermissions;
        config.ShowBooruTools = false;
        Assert.Equal(paused, config.DownloadsPaused);
        Assert.Same(permissions, config.SourceMediaIntegrityPermissions);
        foreach (WorkView view in WorkViewSetting.All)
        {
            Assert.Equal(view == WorkView.Barebones ? Presence.Overflow : Presence.Direct,
                WorkViewPolicy.Resolve("shell.footer.booru", view));
            foreach (string id in new[] { "booru.search.input", "booru.search.submit", "booru.results", "booru.watchers.list", "booru.notice" })
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));
        }
        string host = Read("Desktop/Booru/MainWindow.BooruNavigation.cs");
        // F1: the footer check box and its ⋯ twin are gone; the setting is "Show Booru actions" in Settings (candidate.ShowBooruTools), and
        // turning it off while Booru is open leaves through the shell's unavailable-page fallback (NavigateWorkspace(WorkspacePage.Downloads)).
        foreach (string wiring in new[] { "NavigateWorkspace(WorkspacePage.Downloads)", "BooruMoreMenuItem.IsVisible = false", "_booruMenu.IsVisible = CanOpenBooru" })
            Assert.Contains(wiring, host, StringComparison.Ordinal);
        foreach (string forbidden in new[] { "ShowBooruToolsToggle", "_booruToolsMenu", "DownloadsPaused =", "SourceMediaIntegrityPermissions =",
            "PauseAsync(", "ResumeAsync(", "SearchAsync(", "CheckNowAsync(", "EnablePluginAsync(" })
            Assert.DoesNotContain(forbidden, host, StringComparison.Ordinal);
        Assert.Contains("candidate.ShowBooruTools = ShowBooruActions;", Read("Desktop/Views/Settings/SettingsEditor.Footer.cs"), StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(current.Services, services)", host, StringComparison.Ordinal);
        Assert.Contains("BooruPageHost.Content = page;", host, StringComparison.Ordinal);
        string booru = Read("Desktop/Booru/MainWindow.Booru.cs");
        Assert.Contains("_config.ShowBooruTools", booru, StringComparison.Ordinal);
        Assert.Contains("page.ShowQuery(query)", booru, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchAsync(", booru, StringComparison.Ordinal);
        string navigation = Read("Desktop/Shell/MainWindow.Navigation.cs");
        Assert.Contains("id != \"navigation.booru\" || CanOpenBooru", navigation, StringComparison.Ordinal);
        Assert.Contains("if (page == WorkspacePage.Booru && !CanOpenBooru) return false", navigation, StringComparison.Ordinal);
        string shell = Read("Desktop/Shell/MainWindow.Fullscreen.cs");
        // S3 routing: the page chord is resolved in ShellPageCommand, where Danbooru counts only while it can open.
        Assert.Contains("(id != \"navigation.booru\" || canOpenBooru) && CommandBindings.Matches(e, id)", Read("Desktop/Shell/MainWindow.Shortcuts.cs"), StringComparison.Ordinal);
        Assert.Contains("new List<string>(MainPageOrder.CommandIds)", shell, StringComparison.Ordinal);
        Assert.Contains("CommandCatalog.ShortcutDisplay(id) != null", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsResetAndExportHelpSeparatesDraftSavedValuesAndBackupsInBothLanguages()
    {
        string text = Plain(Section(Read(Help), "Resetting and exporting settings"));
        foreach (string key in new[] { "settings.reset", "settings.reset.all", "settings.reset.confirm", "settings.reset.cancel" })
        {
            var entry = Assert.Single(SettingsResetCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, text, StringComparison.Ordinal);
            Assert.Contains(entry.German, text, StringComparison.Ordinal);
        }
        foreach (string key in new[] { "settings.transfer.more", "settings.transfer.export" })
        {
            var entry = Assert.Single(SettingsTransferUiCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, text, StringComparison.Ordinal);
            Assert.Contains(entry.German, text, StringComparison.Ordinal);
        }
        foreach (string phrase in new[]
        {
            "Reset changes only the unsaved draft", "You still need Save", "Shortcut bindings have their own reset",
            "export uses the saved settings", "invalid or unsaved draft", "does not save, discard or change that draft",
            "Download paths and shortcut bindings are included", "no settings-import UI yet",
            "Zurücksetzen ändert nur den ungespeicherten Entwurf", "Erst Speichern",
            "Tastenkürzel setzt du separat", "Exportiert werden die gespeicherten Einstellungen",
            "Entwurf wird weder gespeichert noch verworfen oder verändert", "Downloadpfade und Tastenkürzel sind enthalten",
            "Eine Oberfläche zum Importieren von Einstellungen gibt es noch nicht"
        }) Assert.Contains(phrase, text, StringComparison.Ordinal);

        foreach (string key in new[] { "settings.interface", "r08d.settings.readingMedia", "settings.downloads", "settings.storageLimits",
            "updates.title", "feedback.settings", "settings.behavior" })
        {
            Assert.Contains(BuiltInCatalog.English[key], text, StringComparison.Ordinal);
            Assert.Contains(BuiltInCatalog.German[key], text, StringComparison.Ordinal);
        }

        // Pure serialization only; never construct an editor, open a picker or load a user's profile.
        Config saved = new() { WindowWidth = 1234, DownloadsPaused = true, LastExportDirectory = "not-exported" };
        var exported = SettingsTransfer.Export(saved, "1.1.0+documentation-contract");
        Assert.Equal(saved.SchemaVersion, exported[nameof(Config.SchemaVersion)]!.GetValue<int>());
        Assert.Equal("MeowChan 1.1.0", exported["ExportedBy"]!.GetValue<string>());
        foreach (string key in new[]
        {
            nameof(Config.WindowWidth), nameof(Config.WindowHeight), nameof(Config.WindowPositionX), nameof(Config.WindowPositionY),
            nameof(Config.WindowMaximized), nameof(Config.FirstRunSetupPending), nameof(Config.FirstRunSetupVersion),
            nameof(Config.WorkViewNoticePending), nameof(Config.SourceMediaIntegrityPermissions), nameof(Config.DownloadsPaused),
            nameof(Config.LastExportDirectory), nameof(Config.GalleryFrameDirectory), nameof(Config.GalleryContextOpen),
            nameof(Config.ArchiveBridgeHintSeen), nameof(Config.StatusPageSegment)
        }) Assert.False(exported.ContainsKey(key), key);
        Assert.True(exported.ContainsKey(nameof(Config.DownloadPath)));
        Assert.True(exported.ContainsKey(nameof(Config.ShortcutBindings)));
        Assert.Equal(1234, saved.WindowWidth);
        Assert.True(saved.DownloadsPaused);
        Assert.Equal("not-exported", saved.LastExportDirectory);
        Assert.Equal("meowchan-settings-2026-11-14.json", SettingsExportUi.SaveOptions(new DateTime(2026, 11, 14)).SuggestedFileName);
        Assert.Contains("meowchan-settings-yyyy-MM-dd.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AdditionalLimitGuidanceNamesBothCatalogLanguagesAndComponentBounds()
    {
        Config config = new();
        Assert.Equal(ArchiveTagFilter.DefaultTagsPerList, config.ArchiveTagFilterTagsPerList);
        Assert.Equal(ArchiveTagFilter.MinimumTagsPerList, Config.ArchiveTagFilterTagsPerListMinimum);
        Assert.Equal(ArchiveTagFilter.MaximumConfigurableTagsPerList, Config.ArchiveTagFilterTagsPerListMaximum);
        Assert.Equal(UrlTextExportFileWriter.DefaultLimitMiB, config.UrlTextExportLimitMiB);
        Assert.Equal(UrlTextExportFileWriter.MinimumLimitMiB, Config.UrlTextExportLimitMiBMinimum);
        Assert.Equal(UrlTextExportFileWriter.MaximumLimitMiB, Config.UrlTextExportLimitMiBMaximum);
        Assert.Equal(SlashTagSuggestions.DefaultSuggestionCount, config.SlashTagSuggestionCount);
        Assert.Equal(SlashTagSuggestions.MinimumSuggestionCount, Config.SlashTagSuggestionCountMinimum);
        Assert.Equal(SlashTagSuggestions.MaximumSuggestionCount, Config.SlashTagSuggestionCountMaximum);
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBDefault, config.VideoScrubCacheLimitMiB);
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMinimum);
        Assert.Equal(VideoScrubPreviewOptions.CacheLimitMiBMaximum, Config.VideoScrubCacheLimitMiBMaximum);
        string text = Plain(Section(Read(Help), "Storage and limits"));
        (string Stem, int Default, int Minimum, int Maximum)[] limits =
        [
            ("archiveTagFilterTagsPerList", config.ArchiveTagFilterTagsPerList, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum),
            ("urlTextExportLimitMiB", config.UrlTextExportLimitMiB, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum),
            ("slashTagSuggestionCount", config.SlashTagSuggestionCount, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum),
            ("videoScrubCacheMiB", config.VideoScrubCacheLimitMiB, Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum)
        ];
        foreach (var limit in limits)
        {
            var entry = Assert.Single(SettingsLimitsCatalog.Entries, candidate => candidate.Key == "budgets." + limit.Stem);
            Assert.Contains(entry.English, text, StringComparison.Ordinal);
            Assert.Contains(entry.German, text, StringComparison.Ordinal);
            Match german = Capture(text, Regex.Escape(entry.German) + @".*?\(Standard (?<default>\d+)(?: MiB)?, (?<minimum>\d+) bis (?<maximum>\d+)(?: MiB)?\)");
            Assert.Equal(limit.Default, Number(german.Groups["default"].Value));
            Assert.Equal(limit.Minimum, Number(german.Groups["minimum"].Value));
            Assert.Equal(limit.Maximum, Number(german.Groups["maximum"].Value));
        }
        Assert.Contains("lowering it does not truncate stored filters", text, StringComparison.Ordinal);
        Assert.Contains("zusätzliche Schutzgrenzen je Eintrag und Format bleiben bestehen", text, StringComparison.Ordinal);
        Assert.Contains("1 KiB = 1024 Byte, 1 MiB = 1024 KiB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkViewOverviewAndNoticeHelpDescribesTheConnectedDetailsHostAndVisibleDuration()
    {
        string text = Plain(Section(Read(Help), "Work-view overview and switch notice"));
        foreach (string key in new[] { "settings.section.workViews", "workview.overview.page", "workview.overview.filter",
            "workview.column.on", "workview.column.off", "workview.column.notOffered" })
        {
            var entry = Assert.Single(WorkViewOverviewCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, text, StringComparison.Ordinal);
            Assert.Contains(entry.German, text, StringComparison.Ordinal);
        }
        foreach (string key in new[] { "workview.switch.title", "workview.switch.undo", "workview.switch.details", "settings.workViewSwitchNoticeSeconds" })
        {
            var entry = Assert.Single(WorkViewSwitchCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, text, StringComparison.Ordinal);
            Assert.Contains(entry.German, text, StringComparison.Ordinal);
        }
        var rows = WorkViewOverview.Build();
        Assert.Equal(WorkViewPolicy.Entries.Count, rows.Count);
        Assert.All(WorkViewPolicy.Entries.Where(entry => entry.Safety ||
            WorkViewSetting.All.Any(view => entry.For(view) == Presence.Required)), entry =>
            Assert.True(Assert.Single(rows, row => row.Id == entry.Id).Protected));
        Assert.True(new Config().ShrinkWindowForBarebones);
        var shrink = Assert.Single(SettingsLimitsCatalog.Entries, entry => entry.Key == "settings.shrinkWindowForBarebones");
        Assert.Contains(shrink.English, text, StringComparison.Ordinal);
        Assert.Contains(shrink.German, text, StringComparison.Ordinal);
        string[] noticeRow = Assert.Single(TableRows("Work-view overview and switch notice"));
        Assert.Equal("Work-view switch notice (seconds)", noticeRow[0]);
        Assert.Equal(new Config().WorkViewSwitchNoticeSeconds, Number(noticeRow[1]));
        AssertNumber(text, @"Standard (?<value>\d+) Sekunden", new Config().WorkViewSwitchNoticeSeconds);
        Assert.Contains($"Bereich {Config.WorkViewSwitchNoticeSecondsMinimum} bis {Config.WorkViewSwitchNoticeSecondsMaximum}", text, StringComparison.Ordinal);
        Match range = Capture(noticeRow[2], @"^(?<minimum>\d+) to (?<maximum>\d+)$");
        Assert.Equal(Config.WorkViewSwitchNoticeSecondsMinimum, Number(range.Groups["minimum"].Value));
        Assert.Equal(Config.WorkViewSwitchNoticeSecondsMaximum, Number(range.Groups["maximum"].Value));
        foreach (string phrase in new[] { "read-only comparison", "policy defaults, not a live visibility inventory",
            "Undo restores the previous view, not unrelated settings or data", "0 disables the toast including Undo/Details",
            "not a custom-view editor", "Settings → Notifications", "duration stays in the draft until Save",
            "schreibgeschützten Vergleich", "Vorgaben, keine Liste der gerade sichtbaren Controls",
            "Einstellungen → Benachrichtigungen", "Dauer bleibt bis Speichern im Entwurf" })
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        foreach (string obsolete in new[] { "navigation host is not connected", "no settings input for that duration yet",
            "Navigationsanschluss an die Übersicht fehlt", "Ein Eingabefeld für die Dauer fehlt" })
            Assert.DoesNotContain(obsolete, text, StringComparison.Ordinal);
        Assert.Contains("WorkViewOverviewRequested += OpenWorkViewOverviewAsync;", Read("Desktop/Shell/MainWindow.WorkViewNotice.cs"), StringComparison.Ordinal);
        WorkViewSwitchNoticeState notices = new();
        notices.BeginSwitch();
        Assert.Null(notices.Applied(WorkView.Barebones, WorkView.Simple, 0, informationAllowed: true));
    }

    [Fact]
    public void DetailsNavigationHelpKeepsTheDraftCurrentViewChangedRowsAndNormalEntryResetDistinct()
    {
        string text = Plain(Section(Read(Help), "Work-view overview and switch notice"));
        foreach (string phrase in new[]
        {
            "normal protected Settings route", "without saving or discarding your existing settings draft",
            "session is restoring, closing or busy", "only the policy rows changed by that switch, across all pages",
            "active column follows the currently applied view", "Keyboard focus moves to Find an element",
            "Page/text filters narrow the changed rows further", "Reset filters removes the difference restriction too",
            "entering Work views normally also clears the previous difference, page and text filters",
            "normalen geschützten Einstellungsweg", "ohne deinen vorhandenen Einstellungsentwurf zu speichern oder zu verwerfen",
            "aktive Spalte folgt der aktuell angewandten Ansicht", "Tastaturfokus geht zu Element suchen",
            "Differenz-, Seiten- und Textfilter für den vollständigen Vergleich gelöscht"
        }) Assert.Contains(phrase, text, StringComparison.Ordinal);
        Assert.Contains(BuiltInCatalog.English["diagnostics.clearFilters"], text, StringComparison.Ordinal);
        Assert.Contains(BuiltInCatalog.German["diagnostics.clearFilters"], text, StringComparison.Ordinal);

        // Source contracts only: no editor, Window, dispatcher, profile or navigation is started here.
        string host = Read("Desktop/Shell/MainWindow.WorkViewDetails.cs");
        foreach (string seam in new[] { "if (_isClosing || !_restoreCompleted || WorkspaceCommandBusy)",
            "if (!NavigateWorkspace(WorkspacePage.Settings))", "SettingsEditor editor = SettingsEditor;",
            "editor.ApplyWorkView(_workViews.Current, viewChanged: false);", "editor.ShowWorkViewOverview(diff);",
            "editor.FocusWorkViewOverview();" }) Assert.Contains(seam, host, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsEditor", host, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelChanges", host, StringComparison.Ordinal);
        string overview = Read("Desktop/Views/Settings/SettingsEditor.WorkViewOverview.cs");
        foreach (string seam in new[] { "overview.EnsureLoaded();", "overview.SetActiveView(CurrentWorkView);",
            "overview.SetDifferenceFilter(diff);", "overview.SetDifferenceFilter(null);", "overview.SetPageFilter(page);",
            "overview.SetTextFilter(text);", "WorkViewOverviewTextFilter", "WorkViewOverviewTable", "target.Focus(NavigationMethod.Tab);" })
            Assert.Contains(seam, overview, StringComparison.Ordinal);
        Assert.Contains("_workViewOverview?.SetDifferenceFilter(null);", Read("Desktop/Views/Settings/SettingsEditor.Sections.cs"), StringComparison.Ordinal);
        string control = Read("Desktop/Views/Settings/WorkViewOverviewControl.cs");
        Assert.Contains("diff.Changes.Select(change => change.Entry.Id)", control, StringComparison.Ordinal);
        Assert.Contains("(_differenceIds == null || _differenceIds.Contains(row.Id)) && WorkViewOverview.Matches", control, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeDurationHelpNamesTheActualNotificationsInputAndItsSaveResetValidationSeams()
    {
        var markup = System.Xml.Linq.XDocument.Parse(Read("Desktop/Views/Settings/SettingsEditor.axaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var input = Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "WorkViewSwitchNoticeSecondsInput");
        Assert.Contains(input.Ancestors(), element => (string?)element.Attribute(x + "Name") == "SettingsNotificationsSection");
        Assert.Equal("{Binding WorkViewSwitchNoticeSeconds, Mode=TwoWay}", (string?)input.Attribute("Value"));
        Assert.Equal("{Binding WorkViewSwitchNoticeSecondsMinimum}", (string?)input.Attribute("Minimum"));
        Assert.Equal("{Binding WorkViewSwitchNoticeSecondsMaximum}", (string?)input.Attribute("Maximum"));
        string notice = Read("Desktop/Views/Settings/SettingsEditor.WorkViewNotice.cs");
        Assert.Contains("candidate.WorkViewSwitchNoticeSeconds = GetInteger", notice, StringComparison.Ordinal);
        Assert.Contains("ApplyWorkViewSwitchNoticeDraft(candidate);", Read("Desktop/Views/Settings/SettingsEditor.axaml.cs"), StringComparison.Ordinal);
        Assert.Contains("WorkViewSwitchNoticeSeconds = defaults.WorkViewSwitchNoticeSeconds;", Read("Desktop/Views/Settings/SettingsEditor.Reset.cs"), StringComparison.Ordinal);
        Assert.Contains("WorkViewNoticeNumericFields", Read("Desktop/Views/Settings/SettingsEditor.Validation.cs"), StringComparison.Ordinal);
        string text = Plain(Section(Read(Help), "Work-view overview and switch notice"));
        Assert.Contains("Discard draft restores the saved value", text, StringComparison.Ordinal);
        Assert.Contains("resetting Notifications loads the default into the draft", text, StringComparison.Ordinal);
        Assert.Contains("Zurücksetzen der Benachrichtigungen lädt den Standard in den Entwurf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UrlExportHelpLinksTheReferenceAndDoesNotTurnTheWriterBudgetIntoAFormatExpansion()
    {
        string section = Section(Read(Help), "Storage and limits");
        Assert.Equal(2, Regex.Matches(section, @"\]\(user-guide/settings-export-and-limits\.md\)").Count);
        string text = Plain(section);
        AssertNumber(text, @"The URL text format still has a fixed (?<value>\d+) MiB ceiling", PlainUrlText.MaxBytes / (1024 * 1024));
        AssertNumber(text, @"ceiling, (?<value>\d+) lines", PlainUrlText.MaxLines);
        AssertNumber(text, @"and (?<value>\d+) UTF-16 code units per line", PlainUrlText.MaxLineCharacters);
        AssertNumber(text, @"URL-Textformat bleibt auf (?<value>\d+) MiB", PlainUrlText.MaxBytes / (1024 * 1024));
        Assert.Equal(1, UrlTextExportFileWriter.MinimumLimitMiB);
        Assert.Equal(64, UrlTextExportFileWriter.MaximumLimitMiB);
        foreach (string phrase in new[] { "current archive plan exports exactly one thread page URL",
            "64 MiB does not enable a 64-MiB URL export", "heutige Archivplan exportiert genau eine Thread-Seiten-URL",
            "64 MiB Writerbudget ermöglichen keinen 64-MiB-URL-Export" }) Assert.Contains(phrase, text, StringComparison.Ordinal);
        string reference = Read(SettingsLimitsGuide);
        Assert.Contains("Technischer Formatvertrag: höchstens 1 MiB", reference, StringComparison.Ordinal);
        Assert.Contains("64 MiB einzustellen ermöglicht deshalb heute keinen 64-MiB-URL-Export", reference, StringComparison.Ordinal);
        Assert.Contains("parsed.Links.Count != 1", Read("Desktop/ArchiveUrlTextExportPlan.cs"), StringComparison.Ordinal);
        Assert.Contains("count >= PlainUrlText.MaxBytes", Read("Desktop/ArchiveUrlTextExportPlan.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void LocalBooruMediaLinkHelpDescribesTheIntegratedPageBridgeWithoutFetching()
    {
        string text = Plain(Section(Read(Help), "Local links to Media"));
        foreach (string key in new[] { "booru.ui.result.showInMedia", "media.toolbar.booru" })
        {
            var entry = Assert.Single(BooruCrossLinksCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, text, StringComparison.Ordinal);
            Assert.Contains(entry.German, text, StringComparison.Ordinal);
        }
        foreach (string phrase in new[] { "does not fetch the post or download its original", "missing/filtered-out local tile",
            "opens the Booru page without starting a search", "Show Booru actions is on",
            "weder der Post abgerufen noch sein Original heruntergeladen", "Booru-Aktionen anzeigen eingeschaltet ist",
            "dieselbe Hauptseite wie über Fußleiste und Appmenü, kein zweites Fenster" })
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        string media = Read("Desktop/Booru/MainWindow.BooruMedia.cs");
        Assert.Contains("ConfigureBooruMediaLinks(BooruPage page) => ConfigureBooruMediaLinks(page.View)", media, StringComparison.Ordinal);
        Assert.Contains("view.ShowInMedia = postId => ShowBooruPostInMediaAsync(view, postId);", media, StringComparison.Ordinal);
        Assert.Contains("page.AttachBooruOverflow(() => CanOpenBooru, ShowBooruWorkbench)", media, StringComparison.Ordinal);
        // Parent's origin-aware callback and cancellation chain: static contracts, not an async/runtime acceptance test.
        Assert.Contains("Action cancelNotice = origin.CaptureLinkActionCancellation();", media, StringComparison.Ordinal);
        Assert.Contains("bool Cancelled() { cancelNotice(); return false; }", media, StringComparison.Ordinal);
        Assert.Contains("|| !ReferenceEquals(sourcePage.View, origin)) return Cancelled();", media, StringComparison.Ordinal);
        Assert.Contains("Func<bool> navigationCurrent = CaptureMediaPageMenuGuard();", media, StringComparison.Ordinal);
        Assert.Contains("Func<bool> interactionCurrent = page.CaptureBooruHandoffInteraction();", media, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_booruPage, sourcePage) && ReferenceEquals(_session.BooruServices, services)", media, StringComparison.Ordinal);
        Assert.Contains("if (!Current() || !interactionCurrent()) return Cancelled();", media, StringComparison.Ordinal);
        Assert.Contains("page.ShowBooruPostAsync(postId, Current, cancelNotice)", media, StringComparison.Ordinal);
        Assert.Contains("error is not OutOfMemoryException && (!Current() || !interactionCurrent())", media, StringComparison.Ordinal);
        Assert.Contains("FocusExactEntryAsync(entry => IsSavedBooruEntry(entry, postId)", Read("Desktop/Views/Media/MediaPage.BooruCrossLinks.cs"), StringComparison.Ordinal);
        Assert.Contains("NavigateWorkspace(WorkspacePage.Booru)", Read("Desktop/Booru/MainWindow.Booru.cs"), StringComparison.Ordinal);
        Assert.Contains("ConfigureBooruMediaLinks(page);", Read("Desktop/Booru/MainWindow.BooruNavigation.cs"), StringComparison.Ordinal);
        Assert.Contains("navigation.booru", MainPageOrder.CommandIds);
    }

    [Fact]
    public void SharedSourcesAndStatusHelpMatchesTheIntegratedRoutesRatherThanTheOldPlans()
    {
        string sources = Plain(Section(Read(Help), "Sources and plugins"));
        foreach (string key in new[] { "nav.sources", "sources.show", "sources.pluginDetails", "sources.backToSource", "command.navigation.plugins" })
        {
            var entry = Assert.Single(SourcesPluginsCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, sources, StringComparison.Ordinal);
            Assert.Contains(entry.German, sources, StringComparison.Ordinal);
        }
        foreach (string key in new[] { "plugins.sources", "plugins.filters", "plugins.all" })
        {
            var entry = Assert.Single(PluginCatalog.Entries, candidate => candidate.Key == key);
            Assert.Contains(entry.English, sources, StringComparison.Ordinal);
            Assert.Contains(entry.German, sources, StringComparison.Ordinal);
        }
        Assert.Equal(new[] { "Sources", "ImageFilters", "AllPlugins" }, Enum.GetNames<SourcesPluginsFilter>());
        Assert.False(MainPageOrder.Contains("navigation.plugins"));
        WorkViewEntry[] pluginRows = WorkViewPolicy.Entries.Where(entry => entry.Id.StartsWith("plugins.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(pluginRows);
        Assert.All(pluginRows, entry => Assert.Equal(WorkViewPage.Sources, entry.Page));
        Assert.DoesNotContain("Plugins", Enum.GetNames<WorkViewPage>());
        Assert.Equal("Show all plugins", CommandCatalog.Get("navigation.plugins").LabelEn);
        Assert.Contains("There is no second plugins page", sources, StringComparison.Ordinal);
        Assert.Contains("opens the second level", sources, StringComparison.Ordinal);
        Assert.Contains("Kompatibilitätsbefehl", sources, StringComparison.Ordinal);
        Assert.Contains("not permission to activate it", sources, StringComparison.Ordinal);
        string status = Plain(Section(Read(Help), "Status page"));
        Assert.Equal(new[] { "Statistics", "Diagnostics" }, Enum.GetNames<StatusSegment>());
        // The Status digit follows the footer of the work view: 7 in Barebones and Advanced, 5 in Simple, the start view of a new profile.
        Assert.Equal(7, Array.IndexOf(MainPageOrder.DefaultNumbering.ToArray(), "navigation.status") + 1);
        Assert.Equal(5, FooterLayout.Default.NumberingFor(WorkView.Simple).ToList().IndexOf("navigation.status") + 1);
        Assert.Equal(7, FooterLayout.Default.NumberingFor(WorkView.Advanced).ToList().IndexOf("navigation.status") + 1);
        foreach (string phrase in new[] { "last-used segment", "header graph and tray open Statistics",
            "source selected", "Statistics becomes available after session recovery", "Booru is a separate main page",
            "Ctrl+7 in Barebones and Advanced, Ctrl+5 in Simple",
            "zuletzt benutzten Bereich", "dieser Quelle als Filter", "Booru ist eine eigene Hauptseite, kein Status-Bereich",
            "in Minimal und Erweitert Strg+7, in Einfach Strg+5" })
            Assert.Contains(phrase, status, StringComparison.Ordinal);
        Assert.Contains("navigation.booru", MainPageOrder.CommandIds);
        Assert.Equal(8, Array.IndexOf(MainPageOrder.DefaultNumbering.ToArray(), "navigation.booru") + 1);
        Assert.Equal(6, Array.IndexOf(MainPageOrder.DefaultNumbering.ToArray(), "navigation.sources") + 1);
        Assert.DoesNotContain("Open **Statistics** with Ctrl+8", Read(Help), StringComparison.Ordinal);
    }

    // Docs guard (UX review KB-01, GA-06, RN-01/02/05, MD-02): defaults that were removed or moved must not be described as current again.
    [Theory]
    [InlineData("docs/Help.md")]
    [InlineData("docs/user-guide/danbooru.md")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    [InlineData("docs/releases/v1.1.0.md")]
    public void UserFacingDocsDoNotDescribeRemovedOrMovedDefaultsAsCurrent(string file)
    {
        string text = Plain(Read(file));
        foreach (string stale in new[]
        {
            "B adds the current medium to favorites", "B performs Add favorite", "Add favourite moved from M to B",
            "previous/next arrows for the current selection", "Sidebar's Previous/Next buttons", "Ctrl+Shift+R opens Thread filters",
            "Ctrl/Cmd+Shift+R opens Thread filters", "Show Booru in the footer and app menu", "Strg+6 öffnet standardmäßig Booru",
            "Ctrl+6 Booru", "Ctrl+5 Booru", "S slideshow"
        })
            Assert.DoesNotContain(stale, text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<![+\w])M (mutes|mute,)", text); // Shift+M mutes; a bare M is the favorite key
        if (file != "docs/releases/v1.1.0.md" && file != "CHANGELOG.md")
            Assert.DoesNotContain("upper-right indicator", text, StringComparison.Ordinal);
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
        AssertNumber(text, @"maximum defaults to (?<value>\d+) hours", new Config().BooruRetryAfterCooldownHours);
        AssertNumber(text, @"configurable from (?<value>\d+) to \d+ hours", Config.BooruRetryAfterCooldownMinimumHours);
        AssertNumber(text, @"configurable from \d+ to (?<value>\d+) hours", Config.BooruRetryAfterCooldownMaximumHours);
        Assert.Contains("A valid longer provider wait is capped at that setting", text, StringComparison.Ordinal);
        Assert.Contains("missing, invalid, expired or shorter values keep the minimum", text, StringComparison.Ordinal);
        Assert.Contains("also saturates at the technical maximum representable date; this does not remove the configured cap", text, StringComparison.Ordinal);
        Assert.Contains("Lowering the setting can shorten an existing in-memory provider pause when the host next checks it", text, StringComparison.Ordinal);
        Assert.Contains("it does not promise an immediate watcher check", text, StringComparison.Ordinal);
        Assert.Contains("neither resumes a paused watcher nor automatically replays a failed search or post lookup", text, StringComparison.Ordinal);
        Assert.Contains("A 429 ends the current metadata job without retry", text, StringComparison.Ordinal);
        Assert.Contains("Later eligible work uses a new job and still obeys pacing and pause state", text, StringComparison.Ordinal);
        Assert.Contains("does not change the submitted query or add a rating filter", text, StringComparison.Ordinal);
        Assert.Contains("HTTP 502/503 permits at most one retry", text, StringComparison.Ordinal);
        Assert.Contains("bounded by the live configured cap, and the retry must fall strictly before the job deadline", text, StringComparison.Ordinal);
        Assert.Contains("the job fails with a budget notice rather than waiting beyond its lifetime", text, StringComparison.Ordinal);
        Assert.Contains("it does not authorize another host retry", text, StringComparison.Ordinal);
        Assert.DoesNotContain("is not shortened to 24 hours", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Only the technical maximum representable date bounds that deadline", text, StringComparison.Ordinal);
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
        AssertNumber(text, @"Deckel liegt standardmäßig bei (?<value>\d+) Stunden", new Config().BooruRetryAfterCooldownHours);
        AssertNumber(text, @"ist von (?<value>\d+) bis \d+ Stunden einstellbar", Config.BooruRetryAfterCooldownMinimumHours);
        AssertNumber(text, @"ist von \d+ bis (?<value>\d+) Stunden einstellbar", Config.BooruRetryAfterCooldownMaximumHours);
        Assert.Contains("Ein gültiger längerer Providerwunsch wird an diesem Deckel begrenzt", text, StringComparison.Ordinal);
        Assert.Contains("fehlende, ungültige, abgelaufene oder kürzere Angaben behalten die Mindestwartezeit", text, StringComparison.Ordinal);
        Assert.Contains("Zusätzlich sättigt die Kalenderrechnung am technisch maximal darstellbaren Datum; das hebt den eingestellten Deckel nicht auf", text, StringComparison.Ordinal);
        Assert.Contains("Ein Absenken der Einstellung kann eine bestehende Providerpause im Arbeitsspeicher beim nächsten Prüfen durch den Host verkürzen", text, StringComparison.Ordinal);
        Assert.Contains("es verspricht keine sofortige Beobachtungsprüfung", text, StringComparison.Ordinal);
        Assert.Contains("weder eine pausierte Suchbeobachtung fort noch wiederholt es automatisch eine fehlgeschlagene Suche oder einen Postabruf", text, StringComparison.Ordinal);
        Assert.Contains("Ein 429 beendet den aktuellen Metadatenjob ohne Wiederholung", text, StringComparison.Ordinal);
        Assert.Contains("Später zulässige Arbeit verwendet einen neuen Job und beachtet weiterhin Pacing und Pausenzustand", text, StringComparison.Ordinal);
        Assert.Contains("die gesendete Query wird dabei nicht geändert und kein Ratingfilter ergänzt", text, StringComparison.Ordinal);
        Assert.Contains("Vor dem einzigen 502/503-Wiederholungsversuch", text, StringComparison.Ordinal);
        Assert.Contains("durch den aktuellen Deckel begrenzt; der Versuch muss strikt vor der Jobfrist liegen", text, StringComparison.Ordinal);
        Assert.Contains("Andernfalls endet der Job mit einem Budgethinweis, statt über seine Laufzeit hinaus zu warten", text, StringComparison.Ordinal);
        Assert.Contains("das erlaubt keine weitere Host-Wiederholung", text, StringComparison.Ordinal);
        AssertNumber(text, @"Host mindestens (?<value>\w+) Sekunden", BooruHostHttp.RetryDelay.TotalSeconds);
        AssertNumber(text, @"starten mindestens (?<value>\w+) Sekunde", BooruHostHttp.MinimumStartSpacing.TotalSeconds);
        AssertNumber(text, @"Job erlaubt höchstens (?<value>\d+) Sends", BooruHostHttp.MaximumSendsPerJob);
        AssertNumber(text, @"dauert höchstens (?<value>\d+) Sekunden", BooruHostHttp.MaximumJobLifetime.TotalSeconds);
        AssertNumber(text, @"jeder Antwort höchstens (?<value>\d+) Sekunden", BooruHostHttp.ResponseTimeout.TotalSeconds);
        AssertNumber(text, @"auf (?<value>\d+) MiB übertragen", BooruHostHttp.MaximumWireBytes / (1024 * 1024));
        AssertNumber(text, @"und (?<value>\d+) MiB dekodiert", BooruHostHttp.MaximumDecodedBytes / (1024 * 1024));
        Assert.DoesNotContain("wird nicht auf 24 Stunden verkürzt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nur das technisch maximal darstellbare Datum begrenzt diesen Zeitpunkt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentedCooldownSettingBoundsAlsoMatchTheTransportBounds()
    {
        // The transport cap fields are internal to PluginHost, not a public SDK contract.
        // Read only these static constants; do not widen InternalsVisibleTo, create a host,
        // load a profile or make HTTP requests just to verify documentation/config agreement.
        static TimeSpan TransportBound(string name)
        {
            var field = typeof(BooruHostHttp).GetField(name,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<TimeSpan>(field!.GetValue(null));
        }

        Assert.Equal(Config.BooruRetryAfterCooldownDefaultHours, new Config().BooruRetryAfterCooldownHours);
        Assert.Equal(TimeSpan.FromHours(Config.BooruRetryAfterCooldownDefaultHours), TransportBound("DefaultMaximumCooldown"));
        Assert.Equal(TimeSpan.FromHours(Config.BooruRetryAfterCooldownMinimumHours), TransportBound("MinimumConfiguredCooldown"));
        Assert.Equal(TimeSpan.FromHours(Config.BooruRetryAfterCooldownMaximumHours), TransportBound("MaximumConfiguredCooldown"));
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
    [InlineData(SettingsLimitsGuide)]
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
