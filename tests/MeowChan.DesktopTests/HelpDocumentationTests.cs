using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Import;
using MeowChan.PluginHost.Http;
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
    public void DocumentedDefaultAndAllowedRangeMatchConfig(string setting, int defaultValue, int minimum, int maximum)
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
        Assert.Contains("settings page for changing the reader", text);
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
        AssertNumber(cards, @"Pages contain (?<value>\d+) posts", ArchivePage.ReaderPageSizeDefault);
        AssertNumber(cards, @"widths default to (?<value>\d+) pixels", ReaderDisplayPreferences.ReaderThumbnailWidthDefault);
        AssertNumber(cards, @"and (?<value>\d+) pixels in the large reader",
            ReaderDisplayPreferences.EffectiveThumbnailWidth(ReaderDisplayPreferences.ReaderThumbnailWidthDefault, true));
        AssertNumber(cards, @"longer than (?<value>\d+) UTF-16 code units", ReaderExternalLink.MaxLength);
        Assert.Contains("not additional Settings choices in this checkout", cards);
        Assert.Contains("only for this app session, not after a restart", cards);
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
        AssertNumber(text, @"(?<value>\d+)-hour cap", BooruHostHttp.MaximumCooldown.TotalHours);
        AssertNumber(text, @"no earlier than (?<value>\w+) seconds", BooruHostHttp.RetryDelay.TotalSeconds);
        AssertNumber(text, @"operation has at most (?<value>\d+) sends", BooruHostHttp.MaximumSendsPerJob);
        AssertNumber(text, @"(?<value>\d+)-second lifetime", BooruHostHttp.MaximumJobLifetime.TotalSeconds);
        AssertNumber(text, @"(?<value>\d+)-second deadline per response", BooruHostHttp.ResponseTimeout.TotalSeconds);
        AssertNumber(text, @"(?<value>\d+) MiB on the wire", BooruHostHttp.MaximumWireBytes / (1024 * 1024));
        AssertNumber(text, @"(?<value>\d+) MiB decoded", BooruHostHttp.MaximumDecodedBytes / (1024 * 1024));
    }

    [Fact]
    public void GermanCatalogDefaultsAndCooldownBoundsMatchTheSameCode()
    {
        string text = Plain(Read(DanbooruGuide));
        AssertNumber(text, @"Schreibgrenze von (?<value>\d+) MiB", new Config().BooruCatalogLimitMiB);
        AssertNumber(text, @"auf (?<value>\d+) bis \d+ MiB", Config.BooruCatalogMinimumLimitMiB);
        AssertNumber(text, @"auf \d+ bis (?<value>\d+) MiB", Config.BooruCatalogMaximumLimitMiB);
        AssertNumber(text, @"mindestens (?<value>\d+) Sekunden", BooruHostHttp.MinimumCooldown.TotalSeconds);
        AssertNumber(text, @"höchstens (?<value>\d+) Stunden", BooruHostHttp.MaximumCooldown.TotalHours);
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
