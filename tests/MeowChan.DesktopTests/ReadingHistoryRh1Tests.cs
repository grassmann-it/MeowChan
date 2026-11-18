using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// RH1: the reading history remembers the last read post per thread and every place opens it. Covers the tolerant file reading and its
/// corruption cases, the jump to a missing post, the menu of "Continue reading", the one icon at every link and the language packs.
/// </summary>
[Collection("Localization")]
public sealed class ReadingHistoryRh1Tests : IDisposable
{
    public ReadingHistoryRh1Tests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static LocalReadingPosition Place(int thread, string post = "10", string? title = null, string board = "g") =>
        new("4chan", board, thread.ToString(), post, -12.5, post, title ?? "Thread " + thread);
    private static LocalReadingHistoryEntry Entry(int thread, int minutes = 0, string post = "10", string? title = null) =>
        new(Place(thread, post, title), T0.AddMinutes(minutes));
    private static DesktopSessionHost Host(string path) => new(path, Array.Empty<IImageBoard>());
    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));

    // ---- model: round trip and the last read post of a thread ----

    [Fact]
    public void TheHistoryRoundTripsWithTheLastReadPostAndTheTimePerThread()
    {
        LocalReadingHistory history = LocalReadingHistory.Add(null, Place(1, "11", "Cats"), T0, 10);
        history = LocalReadingHistory.Add(history, Place(2, "22", "Dogs"), T0.AddMinutes(5), 10);
        history = LocalReadingHistory.Add(history, Place(1, "19", "Cats"), T0.AddMinutes(9), 10);

        LocalReadingHistory strict = LocalReadingHistory.Parse(history.Serialize());
        LocalReadingHistory tolerant = LocalReadingHistory.ParseTolerant(history.Serialize(), out int dropped);
        Assert.Equal(0, dropped);
        foreach (LocalReadingHistory read in new[] { strict, tolerant })
        {
            Assert.Equal(new[] { "1", "2" }, read.Entries.Select(entry => entry.Position.Thread));
            Assert.Equal(new[] { "19", "22" }, read.Entries.Select(entry => entry.LastReadPostId));
            Assert.Equal(new[] { T0.AddMinutes(9), T0.AddMinutes(5) }, read.Entries.Select(entry => entry.At));
            Assert.Equal("Cats", read.Entries[0].Position.Title);
            Assert.Equal(-12.5, read.Entries[0].Position.Top);
        }
    }

    [Fact]
    public void FindReturnsTheNewestPlaceOfOneThreadOnly()
    {
        LocalReadingHistory history = new(new[] { Entry(1, 3, "30"), Entry(2, 2, "20"), Entry(1, 1, "10") });
        Assert.Equal("30", history.Find(new ResourceId("4chan", "g", "1"))!.LastReadPostId);
        Assert.Equal("20", history.Find(new ResourceId("4chan", "g", "2"))!.LastReadPostId);
        Assert.Null(history.Find(new ResourceId("4chan", "a", "1")));
        Assert.Null(history.Find(new ResourceId("8kun", "g", "1")));
    }

    [Fact]
    public void AFileOfAnotherVersionWithUnknownFieldsStillReads()
    {
        string json = """
            {"Version":7,"Entries":[{"Position":{"Source":"4chan","Board":"g","Thread":"1","PostId":"5","Top":0,"SelectedPostId":null,"Title":"T","Extra":1},
            "At":"2026-11-14T12:00:00+00:00","Pinned":true}],"Future":{"a":1}}
            """;
        LocalReadingHistory history = LocalReadingHistory.ParseTolerant(System.Text.Encoding.UTF8.GetBytes(json), out int dropped);
        Assert.Equal(0, dropped);
        Assert.Equal("5", Assert.Single(history.Entries).LastReadPostId);
    }

    // ---- corruption ----

    [Fact]
    public void OneDamagedEntryNeverCostsTheOthersAndTheDuplicatesAreDropped()
    {
        string good1 = JsonSerializer.Serialize(Entry(1, 1)), good2 = JsonSerializer.Serialize(Entry(2, 2)), stale = JsonSerializer.Serialize(Entry(1, 0, "3"));
        string json = "{\"Entries\":[" + string.Join(",", good1, "null", "{\"At\":\"2026-11-14T12:00:00+00:00\"}",
            "{\"Position\":{\"Source\":\"4chan\",\"Board\":\"g\",\"Thread\":\"9\",\"PostId\":\"\",\"Top\":0},\"At\":\"2026-11-14T12:00:00+00:00\"}",
            "\"text\"", good2, stale) + "]}";
        LocalReadingHistory history = LocalReadingHistory.ParseTolerant(System.Text.Encoding.UTF8.GetBytes(json), out int dropped);
        Assert.Equal(new[] { "2", "1" }, history.Entries.Select(entry => entry.Position.Thread));
        Assert.Equal("10", history.Entries[1].LastReadPostId); // the newer entry of thread 1 won
        Assert.Equal(5, dropped);                              // null, no position, empty post, a string, the older duplicate
        // The strict reader still refuses such a file as a whole (the earlier contract of the file).
        Assert.ThrowsAny<Exception>(() => LocalReadingHistory.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void AFileThatIsNoListAtAllStillThrowsSoItCanBeSetAside()
    {
        Assert.ThrowsAny<JsonException>(() => LocalReadingHistory.ParseTolerant("not json"u8.ToArray(), out _));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.ParseTolerant("[]"u8.ToArray(), out _));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.ParseTolerant("{}"u8.ToArray(), out _));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.ParseTolerant("{\"Entries\":3}"u8.ToArray(), out _));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.ParseTolerant(new byte[LocalReadingHistory.MaximumFileBytes + 1], out _));
    }

    [Fact]
    public async Task AnUnreadableListIsKeptAsCorruptCopyAndTheNextPlaceStartsANewList()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "reading-history.json");
        File.WriteAllText(path, "{ this is not json");
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        await host.RememberReadingPositionAsync(Place(7, "70"), host.ReadingPositionGeneration);

        Assert.Equal("{ this is not json", File.ReadAllText(path + ".corrupt"));
        IReadOnlyList<LocalReadingHistoryEntry> history = await host.ReadReadingHistoryAsync();
        Assert.Equal("7", Assert.Single(history).Position.Thread);
    }

    [Fact]
    public async Task ADamagedEntryInTheFileLeavesTheOtherPlacesInTheHistory()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        string json = "{\"Entries\":[" + JsonSerializer.Serialize(Entry(1, 2, "12")) + ",{\"broken\":true}," + JsonSerializer.Serialize(Entry(2, 1, "21")) + "]}";
        File.WriteAllText(Path.Combine(directory.Path, "reading-history.json"), json);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        Assert.Equal(new[] { "12", "21" }, (await host.ReadReadingHistoryAsync()).Select(entry => entry.LastReadPostId));
        await host.RememberReadingPositionAsync(Place(3, "30"), host.ReadingPositionGeneration);
        Assert.Equal(new[] { "3", "1", "2" }, (await host.ReadReadingHistoryAsync()).Select(entry => entry.Position.Thread));
    }

    [Fact]
    public async Task APositionSavedBeforeTheListExistedIsTakenOverWhenTheFirstNewPlaceIsWritten()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        File.WriteAllBytes(Path.Combine(directory.Path, "reading-position.json"),
            JsonSerializer.SerializeToUtf8Bytes(Place(5, "55", "Old thread"), ReadingPositionJsonContext.Default.LocalReadingPosition));
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        Assert.Equal("55", Assert.Single(await host.ReadReadingHistoryAsync()).LastReadPostId); // shown from the position before any write

        await host.RememberReadingPositionAsync(Place(6, "66"), host.ReadingPositionGeneration);

        IReadOnlyList<LocalReadingHistoryEntry> history = await host.ReadReadingHistoryAsync();
        Assert.Equal(new[] { "6", "5" }, history.Select(entry => entry.Position.Thread));
        Assert.Equal("Old thread", history[1].Position.Title);
        Assert.Equal("55", history[1].LastReadPostId);
    }

    // ---- size: 500 places are small and quick ----

    [Fact]
    public async Task FiveHundredPlacesStayFarBelowTheFileCeilingAndLoadQuickly()
    {
        Assert.Equal((50, 10, 500), (Config.ReadingHistoryLimitDefault, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum));
        Assert.Equal(10, Config.ReadingHistoryMenuEntriesDefault);
        LocalReadingHistory history = LocalReadingHistory.Empty;
        for (int thread = 1; thread <= 500; thread++)
            history = LocalReadingHistory.Add(history, Place(thread, (thread * 1000).ToString(), "A typical thread title with some words " + thread), T0.AddSeconds(thread), 500);
        byte[] bytes = history.Serialize();
        Assert.Equal(500, history.Entries.Length);
        Assert.InRange(bytes.Length, 1, 256 * 1024);                  // a usual entry is about 0.3 KiB: 500 are about 150 KiB
        Assert.True(bytes.Length < LocalReadingHistory.MaximumFileBytes / 4);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        for (int round = 0; round < 20; round++) Assert.Equal(500, LocalReadingHistory.ParseTolerant(bytes, out _).Entries.Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "20 loads of 500 places took " + watch.Elapsed);

        // Through the host: one more write on top of 500 places, then a read.
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false, ReadingHistoryLimit = 500 }.SaveConfig(directory.Path);
        File.WriteAllBytes(Path.Combine(directory.Path, "reading-history.json"), bytes);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        watch.Restart();
        await host.RememberReadingPositionAsync(Place(501, "1"), host.ReadingPositionGeneration);
        IReadOnlyList<LocalReadingHistoryEntry> read = await host.ReadReadingHistoryAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "write and read of 500 places took " + watch.Elapsed);
        Assert.Equal(500, read.Count);
        Assert.Equal("501", read[0].Position.Thread);
        Assert.Equal("2", read[^1].Position.Thread); // thread 1 fell off
    }

    // ---- jump: the saved post, the next one when it is missing ----

    private static readonly string[] Posts = { "100", "105", "110", "120" };

    [Theory]
    [InlineData("110", 2, true)]
    [InlineData("100", 0, true)]
    [InlineData("101", 1, false)]   // missing: the next post that exists
    [InlineData("106", 2, false)]
    [InlineData("119", 3, false)]
    [InlineData("50", 0, false)]    // before the first post: the first
    [InlineData("500", 3, false)]   // after the last post: the last
    [InlineData("missing", 0, false)] // not a number: the first post, as before
    public void AMissingPostResolvesToTheNextOneThatExists(string saved, int index, bool exact)
    {
        ReadingJump.Target target = ReadingJump.Resolve(Posts, saved);
        Assert.Equal(index, target.Index);
        Assert.Equal(exact, target.Exact);
    }

    [Fact]
    public void TheJumpComparesLongNumbersAndIgnoresPostsWithoutANumber()
    {
        string[] ids = { "9999999999999999999999", "x1", "10000000000000000000000" }; // 22 digits, not a number, 23 digits
        Assert.Equal(new ReadingJump.Target(0, true), ReadingJump.Resolve(ids, "9999999999999999999999"));
        Assert.Equal(new ReadingJump.Target(0, false), ReadingJump.Resolve(ids, "9999999999999999999998"));
        Assert.Equal(new ReadingJump.Target(2, false), ReadingJump.Resolve(ids, "9999999999999999999999" + "0")); // a longer number is larger
        Assert.Equal(new ReadingJump.Target(2, false), ReadingJump.Resolve(ids, "10000000000000000000001")); // after the last: the last
        Assert.Equal(new ReadingJump.Target(1, false), ReadingJump.Resolve(new[] { "a", "5" }, "3"));
        Assert.Equal(new ReadingJump.Target(-1, false), ReadingJump.Resolve(Array.Empty<string>(), "5"));
    }

    [Fact]
    public void TheMissingPostHintSaysTheNextPostIsShown()
    {
        Assert.Contains("next available post", Texts.T("resume.fallback"));
        Assert.DoesNotContain("first locally captured", Texts.T("resume.fallback"));
        string reader = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Resume.cs"));
        Assert.Contains("ReadingJump.Resolve(", reader);
        Assert.Contains("FlashPostAsync(postId)", reader);                // the landing post is marked briefly, as after a quote jump
        Assert.Contains("fallback ? Texts.T(\"resume.fallback\")", reader); // and the hint says so
    }

    [Fact]
    public void AReplacementLandingPostDoesNotReuseAnOldUnreadBoundary()
    {
        ResourceId id = new("4chan", "g", "42");
        ArchivedThreadView view = new(new ArchiveThreadSummary(id, "https://example.invalid/42", "fixture", "",
            ThreadSourceState.Archived, true, T0, T0, 2, 1),
            new[]
            {
                new CapturedPost("100", 0, PostFieldPresence.All),
                new CapturedPost("110", 1, PostFieldPresence.All)
            });
        LocalReadingPosition exact = Place(42, "100", "fixture");

        Assert.Null(ArchivePage.ResumeUnreadMark(view, exact, fallback: true));
        Assert.Equal((id, "100"), ArchivePage.ResumeUnreadMark(view, exact, fallback: false));
    }

    // ---- the menu of "Continue reading" ----

    private static IReadOnlyList<LocalReadingHistoryEntry> Entries(int count) =>
        Enumerable.Range(1, count).Select(index => Entry(index, -index, (index * 10).ToString())).ToArray();

    [Fact]
    public void TheListHasTheNewestThreadsAsRowsWithTitleAndDetailLines()
    {
        IReadOnlyList<ReadingHistoryRow> rows = ReadingHistoryMenu.Rows(Entries(25), 10, 50, T0);

        Assert.Equal(10, rows.Count);
        Assert.Equal(rows.Count, rows.Select(row => row.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10).Select(index => "reading.place:4chan/g/" + index), rows.Select(row => row.Id));
        Assert.Equal("Thread 1", rows[0].Title); Assert.Contains("No. 10", rows[0].Detail); Assert.Contains("4chan /g/", rows[0].Detail);
        Assert.Equal("Thread 10", rows[9].Title); Assert.Contains("No. 100", rows[9].Detail);
        Assert.Equal("1", rows[0].Place.Thread);
        Assert.Equal("100", rows[9].Place.PostId);
        Assert.Contains("Thread 1", rows[0].Text); Assert.Contains("No. 10", rows[0].Text);
        Assert.Equal("Show full history…", CommandCatalog.Label(ReadingHistoryMenu.HistoryCommandId));
    }

    [Theory]
    [InlineData(10, 25, 10)]
    [InlineData(3, 25, 3)]
    [InlineData(25, 25, 25)]
    [InlineData(25, 12, 12)]  // never more than the history keeps
    [InlineData(25, 1, 10)]   // a history below its minimum counts as the minimum
    [InlineData(1, 25, 3)]    // below the minimum
    [InlineData(500, 5000, 25)] // above both maxima
    public void TheNumberOfListedThreadsIsTheSettingClampedAndCappedByTheHistorySize(int menuEntries, int historyLimit, int expected)
    {
        Assert.Equal(expected, ReadingHistoryMenu.Shown(menuEntries, historyLimit));
        Assert.Equal(expected, ReadingHistoryMenu.Rows(Entries(60), menuEntries, historyLimit, T0).Count);
    }

    [Fact]
    public void TheShortcutOpensTheNewestPlaceSoItStaysTheCommandsOneShortcut()
    {
        Assert.Equal("Primary+Shift+R", CommandCatalog.Get(ReadingHistoryMenu.ContinueCommandId).Shortcut);
        Assert.Null(CommandCatalog.Get(ReadingHistoryMenu.HistoryCommandId).Shortcut);
    }

    [Fact]
    public void AnEmptyHistoryHasNoRows()
    {
        Assert.Empty(ReadingHistoryMenu.Rows(Array.Empty<LocalReadingHistoryEntry>(), 10, 25, T0));
    }

    [Fact]
    public void TitlesKeepTheirUnderscoresAndBothTextsNameThreadAndPost()
    {
        LocalReadingHistoryEntry entry = Entry(3, 0, "77", "my_cat_thread");
        ReadingHistoryRow shown = ReadingHistoryMenu.Rows(new[] { entry }, 10, 25, T0).Single();
        Assert.Equal("my_cat_thread", shown.Title); // a button, not a menu header: no access-key markers to double
        Assert.Equal("Continue reading “my_cat_thread” at post No. 77", ReadingHistoryMenu.EntryTip(entry.Position));
        Assert.Equal("Continue reading “/g/ thread 3” at post No. 9", ReadingHistoryMenu.EntryTip(Place(3, "9") with { Title = null }));
        Assert.Equal(ReadingHistoryMenu.EntryTip(entry.Position), shown.Tip);
        (string title, string detail) = ReadingHistoryMenu.Row(entry, T0.AddHours(2));
        Assert.Equal("my_cat_thread", title);
        Assert.Contains("4chan /g/", detail); Assert.Contains("No. 77", detail);
        Assert.Equal(ReadingHistoryMenu.EntryText(entry, T0.AddHours(2)), MainWindow.ReadingHistoryEntryText(entry, T0.AddHours(2)));
    }

    // ---- one command, one icon, everywhere ----

    [Fact]
    public void ContinueReadingIsOneCatalogCommandWithOneFixedIcon()
    {
        CommandDefinition command = CommandCatalog.Get(ReadingHistoryMenu.ContinueCommandId);
        Assert.Equal(ReadingHistoryMenu.ContinueIcon, command.Icon);
        Assert.Equal("book-open", command.Icon);
        Assert.Equal(command.Icon, CommandCatalog.IconForState(command.Id, null));
        Assert.Equal(command.Icon, CommandCatalog.IconForState(command.Id, "anything")); // no state variants
        Assert.Single(CommandCatalog.All, entry => entry.Id == ReadingHistoryMenu.ContinueCommandId);
        Assert.Equal("Continue reading", CommandCatalog.Label(command.Id));
        foreach (string surface in new[] { "header", "app-menu", "header-reading-menu", "reading-history-window", "archive-thread-menu" })
            Assert.Contains(surface, command.Contexts);
        Assert.True(Assert.IsAssignableFrom<CommandDefinition>(CommandCatalog.Get(ReadingHistoryMenu.HistoryCommandId)).IsImplemented);
        Assert.Null(CommandCatalog.Get(ReadingHistoryMenu.HistoryCommandId).Shortcut);
        // An icon of the catalog has its mask file.
        Assert.True(File.Exists(Root("Assets/icons/feather/masks/" + command.Icon + ".png")));
        Assert.True(File.Exists(Root("Assets/icons/feather/masks/" + CommandCatalog.Get(ReadingHistoryMenu.HistoryCommandId).Icon + ".png")));
    }

    [Fact]
    public void EveryLinkOfContinueReadingGetsItsIconFromTheCommandAndNoSourceNamesAnotherOne()
    {
        string resume = File.ReadAllText(Root("Desktop/Shell/MainWindow.Resume.cs"));
        // The header button and the app menu entry: the command's presentation (icon, label, tooltip, shortcut).
        Assert.Matches(@"CommandBindings\.Apply\(button,\s*""navigation\.continue-reading""", resume);
        Assert.Matches(@"CommandBindings\.Apply\(_resumeReadingMenu,\s*""navigation\.continue-reading""", resume);
        // The history submenu's header, the menu's entries and the window's rows: the same command's icon.
        Assert.Contains("CommandBindings.Icon(ReadingHistoryMenu.ContinueCommandId)", resume);
        Assert.Contains("AddTrigger(ContinueReadingButton)", resume); // right-click and the menu key open the list on the book button
        Assert.Contains("AddTrigger(ContinueReadingHistoryButton)", resume);
        string flyout = File.ReadAllText(Root("Desktop/Shell/ReadingHistoryFlyout.cs"));
        // The list's rows carry the continue command's icon and its last row the history command's.
        Assert.Contains("CommandBindings.Icon(ReadingHistoryMenu.ContinueCommandId)", flyout);
        Assert.Contains("CommandBindings.Icon(ReadingHistoryMenu.HistoryCommandId)", flyout);
        string window = File.ReadAllText(Root("Desktop/Shell/ReadingHistoryWindow.cs"));
        Assert.Contains("CommandBindings.Icon(ReadingHistoryMenu.ContinueCommandId)", window);
        // The archive list's thread menu entry and the macOS menu bar.
        Assert.Matches(@"builder\.Command\(""navigation\.continue-reading""", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Commands.cs")));
        Assert.Contains("\"navigation.continue-reading\"", File.ReadAllText(Root("Desktop/Commands/MacMenuModel.cs")));
        Assert.Contains("shell.header.continue-reading", File.ReadAllText(Root("Desktop/WorkViews/WorkViewPolicy.Shell.cs")));

        // No source file picks an icon of its own for these links: the only "book-open" spellings are the catalog's and the constant.
        string[] spellings = Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(file => File.ReadAllText(file).Contains("book-open", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file)).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "CommandCatalog.cs", "ReadingHistoryMenu.cs" }, spellings);
        // And none of those files names an icon file for a control that links the command.
        foreach (string file in new[] { "Desktop/Shell/MainWindow.Resume.cs", "Desktop/Shell/ReadingHistoryWindow.cs", "Desktop/Views/Archive/ArchivePage.Resume.cs" })
            Assert.DoesNotMatch(@"NamedIcon\(|IconFromKey\(|IconUri\(", File.ReadAllText(Root(file)));
    }

    // ---- settings: the menu length is a setting with default, minimum and maximum ----

    [Fact]
    public void TheMenuLengthIsASettingWithDefaultMinimumAndMaximumAndSurvivesTheTransfers()
    {
        Assert.Equal((10, 3, 25), (Config.ReadingHistoryMenuEntriesDefault, Config.ReadingHistoryMenuEntriesMinimum, Config.ReadingHistoryMenuEntriesMaximum));
        Assert.Equal(10, new Config().ReadingHistoryMenuEntries);
        Config loaded = JsonSerializer.Deserialize<Config>("{\"ReadingHistoryMenuEntries\":999}")!;
        Assert.Equal(25, loaded.ReadingHistoryMenuEntries);
        Assert.Equal(3, JsonSerializer.Deserialize<Config>("{\"ReadingHistoryMenuEntries\":-4}")!.ReadingHistoryMenuEntries);
        Assert.Equal(10, JsonSerializer.Deserialize<Config>("{}")!.ReadingHistoryMenuEntries); // an old settings file keeps the default
        Config set = new() { ReadingHistoryMenuEntries = 7 };
        Assert.Equal(7, set.Clone().ReadingHistoryMenuEntries);
        Config copy = new(); copy.CopyFrom(set);
        Assert.Equal(7, copy.ReadingHistoryMenuEntries);
        Config fields = new(); fields.CopySettingsFieldsFrom(set);
        Assert.Equal(7, fields.ReadingHistoryMenuEntries);
        // RL1: the history limits are on the settings page only; the popover keeps the display options.
        Assert.DoesNotContain(ReaderQuickSettings.Fields, field => field.Id == SettingsFieldId.ReadingHistoryMenuEntries);
    }

    // ---- language packs: the new texts are real translations in all twelve languages ----

    private static readonly string[] NewKeys =
    {
        "resume.history.showAll", "resume.history.dialogHint", "resume.history.entryTip", "resume.thread.action",
        "settings.reader.historyMenuEntries", "settings.reader.historyMenuEntries.hint"
    };

    [Fact]
    public void EveryNewTextExistsInAllTwelveLanguagesTranslatedAndWithTheSamePlaceholders()
    {
        string[] codes = Texts.Languages.Select(info => info.Code).ToArray();
        Assert.Equal(12, codes.Length);
        foreach (string key in NewKeys.Append("resume.history.hint").Append("resume.fallback"))
        {
            Texts.SetLanguage("en");
            string english = Texts.T(key);
            Assert.NotEqual(key, english);
            string[] placeholders = Placeholders(english);
            foreach (string code in codes.Where(code => code != "en"))
            {
                Texts.SetLanguage(code);
                string text = Texts.T(key);
                Assert.NotEqual(key, text);
                Assert.False(string.IsNullOrWhiteSpace(text), key + " in " + code);
                Assert.Equal(placeholders, Placeholders(text));
                Assert.NotEqual(english, text);
            }
        }
        // The entry texts format in every language (a broken brace would throw or show a placeholder).
        foreach (string code in codes)
        {
            Texts.SetLanguage(code);
            Assert.DoesNotContain("{", Texts.T("resume.history.entryTip", "Cats", "77"));
            Assert.DoesNotContain("{", Texts.T("resume.thread.action", "77"));
            Assert.Contains("77", Texts.T("resume.thread.action", "77"));
            Assert.Contains("Cats", Texts.T("resume.history.entryTip", "Cats", "77"));
        }
    }

    [Fact]
    public void TheCommandLabelOfTheFullHistoryEntryIsTranslatedLikeTheCatalogText()
    {
        Texts.SetLanguage("de");
        Assert.Equal("Ganzen Verlauf zeigen…", CommandCatalog.Label(ReadingHistoryMenu.HistoryCommandId));
        Texts.SetLanguage("pl");
        Assert.Equal(Texts.T("resume.history.showAll"), CommandCatalog.Label(ReadingHistoryMenu.HistoryCommandId));
        Assert.NotEqual("Show full history…", CommandCatalog.Label(ReadingHistoryMenu.HistoryCommandId));
    }

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, "\\{\\d+\\}").Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}
