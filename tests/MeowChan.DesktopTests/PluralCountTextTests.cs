using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Texts with a number in front of a countable noun ("{0} threads") must pick their form through <c>Texts.Count</c>
/// (CLDR categories) or be count-neutral ("Threads: {0}"). A new such text without a singular form fails here until it is
/// converted or listed with its reason. The bundled packs and the built-in en/de catalogs are checked for the same shape.
/// </summary>
[Collection("Localization")]
public sealed class PluralCountTextTests : IDisposable
{
    public PluralCountTextTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    /// <summary>The keys that were moved to Texts.Count with a "&lt;key&gt;.one" form (the converted call sites).</summary>
    internal static readonly string[] ConvertedKeys =
    {
        "suggestions.count", "savedSearch.conflictList", "viewer.favorites.exportShownHint", "archive.media.missingVersions",
        "booru.media.statusCount", "booru.media.gallery.scope", "booru.media.gallery.tags", "booru.media.tags.selection.count",
        "booru.media.tags.selection.tooMany", "booru.media.tags.copied", "export.resultsAction", "export.resultsFrozen",
        "export.legacyFound", "setup.parallelEffective", "settings.parallelEffective", "archive.cleanup.row.versions",
        "archive.delete.filesRemoved", "import.tags.tooMany", "import.limit.files", "media.indexing", "media.tile.sameThread",
        "media.selection.note.galleryPartial", "media.exportAll.preparing", "media.exportAll.confirm", "plugins.catalogResult",
        "archive.command.selectionCount", "archive.command.batchCount", "viewer.favorites.unavailable", "viewer.export.scope",
        "readerFind.count", "readerGroups.added", "readerGroups.missingMembers", "readerGroups.matches",
        "readerGroups.matchesNarrow", "reader.newPostsCount", "rules.changedScopes", "selection.failed", "sources.count",
        "tags.manage.orphansUnchecked", "tags.dialog.usesName", "tags.dialog.row.all", "tags.dialog.removedMany",
        "tags.suggest.count", "work.missingAndErrors", "work.interventionCount", "booru.ui.status.results",
        "booru.ui.post.favorites", "queue.ask", "tagging.page.progress.results",
        "downloads.searchShowBoards", "downloads.searchShowThreads", "media.count.media", "media.count.threads",
        "archive.delete.threads", "archive.delete.files",
        "archive.delete.bytes", "import.done.tags", "export.collectionSelection", "media.filter.count", "media.untagged.hint"
    };

    // Catalog texts of the shape "{n} noun" with no singular form. Every one is either count-neutral for the number in the
    // packs, reached only for counts of two or more, a bounded setting or product constant, a range text, or a noun that
    // is the same in every shipped language. A new entry needs the same kind of reason.
    private static readonly Dictionary<string, string> NoSingularFormAllowed = new(StringComparer.Ordinal)
    {
        ["boardAdmission.startupSizeLimit"] = "the placeholders name two files and a settings key",
        ["booru.crossLinks.navigationFailed"] = "the number is a post id (\"post #{0}\"), not a count",
        ["booru.media.tags.search.tooMany"] = "the Danbooru tag-search limit is a fixed provider constant above 1 and the marked count exceeds it",
        ["booru.media.tile.inMixed"] = "label form (\"in threads: {0} and Danbooru posts: {1}\"); the pair is shown only for equal bytes in both places",
        ["booru.media.tile.inPosts"] = "shown only for two or more provider posts (guarded by ProviderPostCount > 1)",
        ["booru.ui.result.showInMediaFor"] = "the number is a post id (\"#{0}\") and \"Media\" is the name of a page",
        ["booru.ui.tags.searchFull"] = "the Danbooru tag-search limit is a fixed provider constant above 1",
        ["feedback.repeated"] = "shown only when the same notice repeated (Count > 1)",
        ["limit.favoriteEntries"] = "reached through Texts.Count (StoreLimitText); a store limit has a minimum far above 1",
        ["limit.hiddenPosts"] = "reached through Texts.Count (StoreLimitText); a store limit has a minimum far above 1",
        ["limit.postBookmarks"] = "reached through Texts.Count (StoreLimitText); a store limit has a minimum far above 1",
        ["limit.postFilterRules"] = "reached through Texts.Count (StoreLimitText); a store limit has a minimum far above 1",
        ["limit.readerGroups"] = "reached through Texts.Count (StoreLimitText); a store limit has a minimum far above 1",
        ["limit.warn.favoriteEntries"] = "reached through Texts.Count with the store limit as the count (minimum far above 1)",
        ["media.tile.duplicatesHint"] = "shown only for a file found in two or more places",
        ["media.tile.inThreads"] = "shown only for two or more threads (guarded by ThreadCount > 1)",
        ["media.untagged.count"] = "\"media tags\" is not the counted noun of the number",
        ["progress.mediaOf"] = "\"media\" is the same in the singular and the plural",
        ["settings.exportFilenameTemplate.tooLong"] = "the number is the fixed template length cap (ExportFilenameTemplate.MaxTemplateLength = 120), never 1",
        ["settings.exportFilenameTemplateHint"] = "{1} is the fixed template length cap (ExportFilenameTemplate.MaxTemplateLength = 120), never 1",
        ["settings.galleryPlayback.videoRepeatsHint"] ="range text of a bounded setting (\"Default {0}, from {1} to {2} passes\")",
        ["reader.sidebar.noMedia"] = "the number is a post number (\"No. {0}\") and \"media\" is the same in the singular and the plural",
        ["sources.showAll"] = "\"Downloads\" is the name of a page, not the counted noun",
        ["stats.settings.maxSourcesHint"] = "range text of a bounded setting",
        ["stats.settings.persistIntervalHint"] = "range text of a bounded setting (minutes)",
        ["stats.settings.retentionDaysHint"] = "range text of a bounded setting (days)",
        ["tagging.help.note"] = "\"{2}\" is the name of a key, not a count",
        ["tags.dialog.row.some"] = "\"{1} of {2} ticked\" form; the total is at least 2 whenever only some carry the tag",
        ["tags.quick.chip.all"] = "the chip row exists only for a multi-selection, so the total is always at least 2",
        ["tags.quick.chip.some"] = "only shown for a multi-selection (total at least 2)",
    };

    private static readonly string Nouns =
        "threads|posts|files|media|items|entries|tags|images|results|matches|copies|originals|members|occurrences|times|rules|" +
        "sources|hits|ratings|rows|errors|failures|versions|targets|folders|groups|pages|candidates|suggestions|videos|boards|" +
        "attachments|characters|duplicates|downloads|transfers|bookmarks|filters|users|updates|changes|lines|words|keys|settings|" +
        "operations|days|passes|seconds|minutes|hours|requests|links|urls|workers|steps|slots";

    // "{0} threads", "{1:N0} posts", "{0} Danbooru posts": a number placeholder, then at most two words, then a countable noun.
    private static readonly Regex NumberThenNoun = new(
        @"\{\d+(?::[A-Za-z0-9,#.]+)?\}[ ]+(?:[A-Za-z\-]+[ ]+){0,2}?(?:" + Nouns + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    [Fact]
    public void EnglishTextsWithANumberBeforeANounHaveASingularFormOrAreAllowedWithAReason()
    {
        string[] offenders = BuiltInCatalog.English
            .Where(entry => PluralRules.BaseKey(entry.Key) == null && NumberThenNoun.IsMatch(entry.Value))
            .Select(entry => entry.Key)
            .Where(key => !BuiltInCatalog.English.ContainsKey(key + ".one") && !NoSingularFormAllowed.ContainsKey(key))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "Texts with a count before a noun need Texts.Count with a \".one\" form (CountCatalog + every pack) or a count-neutral wording:\n"
            + string.Join("\n", offenders));
        // The allow-list may only shrink with the code: a key that is gone or no longer has the shape would hide a future one.
        foreach (string key in NoSingularFormAllowed.Keys)
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? text), "Allowed key no longer exists: " + key);
            Assert.True(NumberThenNoun.IsMatch(text!), "Allowed key no longer has a number before a noun, remove it: " + key);
            Assert.False(BuiltInCatalog.English.ContainsKey(key + ".one"), "Allowed key has a singular form now, remove it: " + key);
        }
    }

    [Fact]
    public void CountKeysAreOnlyRenderedThroughTheCountPath()
    {
        string[] countKeys = BuiltInCatalog.English.Keys
            .Where(key => PluralRules.BaseKey(key) is { } baseKey && key.EndsWith(".one", StringComparison.Ordinal) && BuiltInCatalog.English.ContainsKey(baseKey))
            .Select(key => key[..^".one".Length]).ToArray();
        Assert.Contains("viewer.favorites.unavailable", countKeys);
        // A line that names a count key must render it with Texts.Count (or a counted carrier), or the file carries the key to such a call.
        Regex countPath = new(@"Texts\.Count\(|\.Counted\(|CountHelpKey");
        Dictionary<string, string[]> carriers = new(StringComparer.Ordinal)
        {
            // The key travels as data (a message key, a status key, a required-key list) and is rendered by a counted call elsewhere in the file.
            ["Desktop/Views/Media/ManualImportSession.cs"] = ["import.limit.files", "import.tags.tooMany"],
            ["Desktop/Views/Archive/ArchivePage.ThreadFind.cs"] = ["readerFind.count"],
            ["Desktop/WorkStatusFormatter.cs"] = ["work.interventionCount"],
            ["Desktop/Booru/BooruText.cs"] = ["booru.ui.status.results", "booru.ui.post.favorites", "booru.ui.tab.watchers"],
            ["Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs"] = ["readerGroups.matches", "readerGroups.matchesNarrow"],
        };
        List<string> offenders = new();
        foreach (string path in Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Relative(path);
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.StartsWith("Desktop/Localization/", StringComparison.Ordinal)
                || relative == "Desktop/Booru/BooruUiCatalog.cs") continue;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (string key in countKeys)
                {
                    if (!line.Contains('"' + key + '"')) continue;
                    // A multi-line call (the key on one line, the count on the next) is read as one statement.
                    string statement = line + (i + 1 < lines.Length ? lines[i + 1] : string.Empty);
                    if (countPath.IsMatch(statement)) continue;
                    if (carriers.TryGetValue(relative, out string[]? allowed) && allowed.Contains(key)) continue;
                    offenders.Add(relative + ":" + (i + 1) + ": " + key);
                }
            }
        }
        Assert.True(offenders.Count == 0, "Render these keys with Texts.Count (they have a singular form):\n" + string.Join("\n", offenders));
    }

    // The ru/pl/cs packs: a number directly before a noun is wrong for most numbers, so such a value belongs only to a key that
    // goes through Texts.Count (it has a ".one" form), or is listed. Everything else uses "Nouns: {n}".
    private static readonly Dictionary<string, string[]> SlavicNounStems = new(StringComparer.Ordinal)
    {
        ["ru"] = ["тред", "пост", "файл", "вложени", "публикац", "медиа", "запис", "тег", "ярлык", "результат", "совпаден", "копи", "оригинал", "участник", "вхожден", "правил", "источник", "закладк", "групп", "страниц", "кандидат", "предложен", "элемент", "раз", "секунд", "минут", "дн", "час", "поток", "задач", "попыт", "ошиб", "сбо", "строк", "объект", "вариант", "версии", "версий", "доск", "борд", "канал", "слот", "ссылк", "адрес", "шаг", "пункт"],
        ["pl"] = ["wątk", "post", "plik", "załącznik", "wpis", "tag", "wynik", "trafień", "kopi", "oryginał", "członk", "wystąpien", "regu", "źródł", "zakładk", "grup", "stron", "kandydat", "sugesti", "element", "raz", "sekund", "minut", "dni", "godzin", "zadań", "próbą", "błęd", "wiersz", "obiekt", "wersj", "tablic", "link"],
        ["cs"] = ["vlákn", "příspěv", "soubor", "příloh", "záznam", "štítk", "výsledk", "shod", "kopi", "originál", "člen", "výskyt", "pravid", "zdroj", "záložk", "skupin", "stran", "kandidát", "návrh", "položk", "krát", "sekund", "minut", "dn", "hodin", "úkol", "pokus", "chyb", "řádk", "objekt", "verz", "desk", "odkaz"],
    };

    // Values whose number is a range or constant, a measured unit abbreviation, a "{0} of {1}" form or not followed by a noun at all.
    private static readonly HashSet<string> SlavicNumberBeforeNounAllowed = new(StringComparer.Ordinal)
    {
        "booru.media.tags.search.tooMany", "feedback.repeated", "limit.warn.favoriteEntries",
        "media.tile.duplicatesHint", "progress.mediaOf", "reader.date.unit.d", "stats.settings.maxSourcesHint",
        "stats.settings.persistIntervalHint", "stats.settings.retentionDaysHint", "store.backupBlocked.newer", "tags.filter.error.tooMany",
        "transfer.import.resultSet"
    };

    [Theory]
    [InlineData("ru")]
    [InlineData("pl")]
    [InlineData("cs")]
    public void SlavicPackTextsOutsideTheCountPathAreCountNeutral(string code)
    {
        Regex numberThenNoun = new(@"\{\d+(?::\w+)?\}[ ]+(?:" + string.Join("|", SlavicNounStems[code]) + ")", RegexOptions.IgnoreCase);
        Dictionary<string, string> pack = Pack(code);
        string[] offenders = pack.Where(entry => PluralRules.BaseKey(entry.Key) == null && numberThenNoun.IsMatch(entry.Value)
                && !pack.ContainsKey(entry.Key + ".one") && !SlavicNumberBeforeNounAllowed.Contains(entry.Key))
            .Select(entry => entry.Key + " = " + entry.Value).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, code + ": a number before a noun outside Texts.Count; use \"Nouns: {n}\" or add the plural forms:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("en", 1, 1, "Page 1 of at most 1 · Results per page: 1", "Search limits (pages: 1, results per page: 1).")]
    [InlineData("en", 1, 2, "Page 1 of at most 1 · Results per page: 2", "Search limits (pages: 1, results per page: 2).")]
    [InlineData("en", 2, 1, "Page 1 of at most 2 · Results per page: 1", "Search limits (pages: 2, results per page: 1).")]
    [InlineData("de", 1, 1, "Seite 1 von höchstens 1 · Treffer pro Seite: 1", "Suchlimits (Seiten: 1, Treffer pro Seite: 1).")]
    [InlineData("de", 1, 2, "Seite 1 von höchstens 1 · Treffer pro Seite: 2", "Suchlimits (Seiten: 1, Treffer pro Seite: 2).")]
    [InlineData("de", 2, 1, "Seite 1 von höchstens 2 · Treffer pro Seite: 1", "Suchlimits (Seiten: 2, Treffer pro Seite: 1).")]
    public void BooruPagerAndLimitMessageHandleSinglePagesAndResults(string code, int pages, int results,
        string pager, string limit)
    {
        Texts.SetLanguage(code);
        Assert.Equal(pager, Texts.T("booru.ui.page.info", 1, pages, results));
        Assert.StartsWith(limit, Texts.T("booru.ui.page.limit", pages, results), StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishAndGermanSingularFormsKeepTheBasePlaceholders()
    {
        string[] oneKeys = BuiltInCatalog.English.Keys.Where(key => key.EndsWith(".one", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(oneKeys);
        foreach (string key in oneKeys)
        {
            string baseKey = key[..^".one".Length];
            Assert.True(BuiltInCatalog.English.ContainsKey(baseKey), "Singular form without a base text: " + key);
            Assert.Equal(Placeholders(BuiltInCatalog.English[baseKey]), Placeholders(BuiltInCatalog.English[key]));
            Assert.True(BuiltInCatalog.German.ContainsKey(key), "German singular form missing: " + key);
            Assert.Equal(Placeholders(BuiltInCatalog.German[baseKey]), Placeholders(BuiltInCatalog.German[key]));
            Assert.Equal(Placeholders(BuiltInCatalog.English[baseKey]), Placeholders(BuiltInCatalog.German[key]));
        }
        foreach (string key in ConvertedKeys)
            Assert.True(BuiltInCatalog.English.ContainsKey(key + ".one") && BuiltInCatalog.German.ContainsKey(key + ".one"), "Converted key without a singular text: " + key);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("pt-BR")]
    [InlineData("cs")]
    [InlineData("it")]
    [InlineData("pl")]
    public void EveryConvertedKeyResolvesToAFullSentenceForEveryNumberCategory(string code)
    {
        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        foreach (string key in ConvertedKeys)
        {
            int placeholders = Placeholders(BuiltInCatalog.English[key]).Length;
            foreach (int count in new[] { 0, 1, 2, 3, 4, 5, 11, 12, 14, 21, 22, 25, 100, 101, 102 })
            {
                object?[] arguments = Enumerable.Repeat((object?)count, placeholders).ToArray();
                string text = Texts.Count(key, count, arguments);
                Assert.False(string.IsNullOrWhiteSpace(text) || text == key || text.Contains('{') || text.Contains('}'), $"{code} {key} {count}: '{text}'");
                Assert.Contains(count.ToString(CultureInfo.InvariantCulture), text);
            }
        }
    }

    [Theory]
    [InlineData("en", 0, 0, "0 of 0 results")]
    [InlineData("en", 0, 1, "0 of 1 result")]
    [InlineData("en", 1, 201, "1 of 201 results")]
    [InlineData("en", 1234, 5678, "1,234 of 5,678 results")]
    [InlineData("de", 0, 1, "0 von 1 Treffer")]
    [InlineData("de", 0, 201, "0 von 201 Treffern")]
    [InlineData("ru", 2, 21, "2 из 21 результата")]
    [InlineData("ru", 1, 22, "1 из 22 результатов")]
    [InlineData("ru", 1, 11, "1 из 11 результатов")]
    [InlineData("ja", 2, 21, "結果 21 件中 2 件")]
    public void TaggingProgressUsesTheTotalForPluralSelection(string code, int decided, int total, string expected)
    {
        Texts.SetLanguage(code);
        Assert.Equal(expected, Texts.Count("tagging.page.progress.results", total, decided, total));
    }

    [Theory]
    [InlineData("en", "viewer.favorites.unavailable", 1, "1 original is currently unavailable.")]
    [InlineData("en", "viewer.favorites.unavailable", 3, "3 originals are currently unavailable.")]
    [InlineData("de", "viewer.favorites.unavailable", 1, "1 Original ist derzeit nicht verfügbar.")]
    [InlineData("de", "viewer.favorites.unavailable", 3, "3 Originale sind derzeit nicht verfügbar.")]
    [InlineData("es", "viewer.favorites.unavailable", 1, "1 original no está disponible actualmente.")]
    [InlineData("es", "viewer.favorites.unavailable", 3, "3 originales no están disponibles actualmente.")]
    [InlineData("fr", "viewer.favorites.unavailable", 1, "1 original est actuellement indisponible.")]
    [InlineData("fr", "viewer.favorites.unavailable", 3, "3 originaux sont actuellement indisponibles.")]
    [InlineData("it", "viewer.favorites.unavailable", 1, "1 originale non è al momento disponibile.")]
    [InlineData("it", "viewer.favorites.unavailable", 3, "3 originali non sono al momento disponibili.")]
    [InlineData("pt-BR", "viewer.favorites.unavailable", 1, "1 original está indisponível no momento.")]
    [InlineData("pt-BR", "viewer.favorites.unavailable", 3, "3 originais estão indisponíveis no momento.")]
    [InlineData("ru", "sources.count", 1, "Зарегистрировано источников: 1")]
    [InlineData("ru", "sources.count", 2, "Зарегистрировано источников: 2")]
    [InlineData("ru", "sources.count", 5, "Зарегистрировано источников: 5")]
    [InlineData("pl", "tags.dialog.usesName", 1, "użyto 1 raz")]
    [InlineData("pl", "tags.dialog.usesName", 2, "użyto 2 razy")]
    [InlineData("pl", "tags.dialog.usesName", 5, "użyto 5 razy")]
    [InlineData("zh-Hans", "sources.count", 1, "1 个已注册的来源")]
    public void SingularAndPluralFormsAgreeWithTheNumber(string code, string key, int count, string expected)
    {
        Texts.SetLanguage(code);
        Assert.Equal(expected, Texts.Count(key, count));
    }

    [Theory]
    [InlineData(1, "3 / 1 chyba")]
    [InlineData(2, "3 / 2 chyby")]
    [InlineData(4, "3 / 4 chyby")]
    [InlineData(5, "3 / 5 chyb")]
    public void CzechErrorsFollowTheCountOfErrorsNotOfMissingFiles(int errors, string expected)
    {
        Texts.SetLanguage("cs");
        Assert.Equal(expected, Texts.Count("work.missingAndErrors", errors, 3, errors));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public void LanguagesWithoutNumberFormsShowTheSameTextForOneAndMany(string code)
    {
        Texts.SetLanguage(code);
        Dictionary<string, string> pack = Pack(code);
        foreach (string key in ConvertedKeys)
        {
            Assert.DoesNotContain(key + ".one", pack.Keys);
            int placeholders = Placeholders(BuiltInCatalog.English[key]).Length;
            foreach (int count in new[] { 1, 7 })
            {
                object?[] arguments = Enumerable.Repeat((object?)count, placeholders).ToArray();
                Assert.Equal(string.Format(Texts.Culture, pack[key], arguments), Texts.Count(key, count, arguments));
            }
        }
    }

    private static string[] Placeholders(string format) =>
        Placeholder.Matches(format).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> Pack(string code)
    {
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }

    private static string Relative(string path) => Path.GetRelativePath(Root(""), path).Replace('\\', '/');

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
