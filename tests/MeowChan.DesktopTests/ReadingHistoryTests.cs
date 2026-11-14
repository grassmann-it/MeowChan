using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReadingHistoryTests
{
    private static LocalReadingPosition Place(int thread, string post = "10", string? title = null, string board = "g") =>
        new("4chan", board, thread.ToString(), post, -12.5, post, title ?? "Thread " + thread);
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static DesktopSessionHost Host(string path) => new(path, Array.Empty<IImageBoard>());

    [Fact]
    public void ANewPlaceGoesFirstAndReplacesTheEarlierPlaceOfTheSameThread()
    {
        LocalReadingHistory history = LocalReadingHistory.Add(null, Place(1, "11"), T0, 10);
        history = LocalReadingHistory.Add(history, Place(2, "22"), T0.AddMinutes(1), 10);
        history = LocalReadingHistory.Add(history, Place(3, "33"), T0.AddMinutes(2), 10);
        history = LocalReadingHistory.Add(history, Place(1, "19"), T0.AddMinutes(3), 10);

        Assert.Equal(new[] { "1", "3", "2" }, history.Entries.Select(entry => entry.Position.Thread));
        Assert.Equal("19", history.Entries[0].Position.PostId);
        Assert.Equal(T0.AddMinutes(3), history.Entries[0].At);
    }

    [Fact]
    public void ThreadsOfAnotherBoardOrSourceAreNotTheSameThread()
    {
        LocalReadingHistory history = LocalReadingHistory.Add(null, Place(1, board: "g"), T0, 10);
        history = LocalReadingHistory.Add(history, Place(1, board: "a"), T0, 10);
        history = LocalReadingHistory.Add(history, Place(1, board: "g") with { Source = "8kun" }, T0, 10);
        Assert.Equal(3, history.Entries.Length);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(10, 10)]
    [InlineData(250, 250)]
    [InlineData(500, 500)]
    [InlineData(1, 10)]        // below the minimum: the minimum
    [InlineData(100000, 500)]  // above the maximum: the maximum
    public void TheLimitKeepsTheNewestAndTheOldestFallOff(int limit, int expected)
    {
        LocalReadingHistory history = LocalReadingHistory.Empty;
        for (int thread = 1; thread <= 600; thread++) history = LocalReadingHistory.Add(history, Place(thread), T0.AddMinutes(thread), limit);

        Assert.Equal(expected, history.Entries.Length);
        Assert.Equal("600", history.Entries[0].Position.Thread);
        Assert.Equal((601 - expected).ToString(), history.Entries[^1].Position.Thread);
    }

    [Fact]
    public void ALoweredLimitCutsWhatTheMenuShowsAtOnce()
    {
        LocalReadingHistory history = LocalReadingHistory.Empty;
        for (int thread = 1; thread <= 40; thread++) history = LocalReadingHistory.Add(history, Place(thread), T0.AddMinutes(thread), 100);
        Assert.Equal(40, history.Newest(100).Count);
        Assert.Equal(10, history.Newest(10).Count);
        Assert.Equal("40", history.Newest(10)[0].Position.Thread);
    }

    [Fact]
    public void TheFileStaysBelowItsCeilingEvenWithMaximumSizedEntries()
    {
        string wide = new('x', 500);
        LocalReadingHistory history = LocalReadingHistory.Empty;
        for (int thread = 1; thread <= 400; thread++)
            history = LocalReadingHistory.Add(history,
                new LocalReadingPosition(wide, wide, thread + wide[..400], wide, 1, wide, new string('t', LocalReadingPosition.TitleMaximumLength)), T0.AddMinutes(thread), 500);

        // About 3 KiB per entry: 400 of them pass the 1 MiB ceiling, so the oldest fall off to what fits.
        Assert.True(history.Serialize().Length <= LocalReadingHistory.MaximumFileBytes);
        Assert.InRange(history.Entries.Length, 250, 399);
        Assert.StartsWith("400", history.Entries[0].Position.Thread);
        Assert.NotNull(LocalReadingHistory.Parse(history.Serialize()));
    }

    [Fact]
    public void ParsingRejectsOversizedBrokenAndInvalidFiles()
    {
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse(new byte[LocalReadingHistory.MaximumFileBytes + 1]));
        Assert.ThrowsAny<JsonException>(() => LocalReadingHistory.Parse("not json"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse("null"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse("{\"Entries\":null}"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse("{\"Entries\":[null]}"u8.ToArray()));
        LocalReadingHistory bad = new(new[] { new LocalReadingHistoryEntry(Place(1) with { PostId = "" }, T0) });
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse(bad.Serialize()));
        LocalReadingHistory badTitle = new(new[] { new LocalReadingHistoryEntry(Place(1) with { Title = "a\nb" }, T0) });
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse(badTitle.Serialize()));
        LocalReadingHistory ancient = new(new[] { new LocalReadingHistoryEntry(Place(1), new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)) });
        Assert.Throws<InvalidDataException>(() => LocalReadingHistory.Parse(ancient.Serialize()));
    }

    [Fact]
    public void TitleSnapshotsAreCleanedAndBounded()
    {
        Assert.Null(LocalReadingPosition.CleanTitle(null));
        Assert.Null(LocalReadingPosition.CleanTitle("  \t\n "));
        Assert.Equal("a b c", LocalReadingPosition.CleanTitle("a\nb\tc"));
        string clean = LocalReadingPosition.CleanTitle(new string('x', 1000))!;
        Assert.Equal(LocalReadingPosition.TitleMaximumLength, clean.Length);
        Place(1, title: clean).Validate();
    }

    [Fact]
    public async Task HostKeepsOnePlacePerThreadNewestFirstUpToTheLimitAndNeverStoresPostText()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false, ReadingHistoryLimit = 10 }.SaveConfig(directory.Path);
        await using (DesktopSessionHost host = Host(directory.Path))
        {
            await host.StartAsync();
            for (int thread = 1; thread <= 13; thread++)
                await host.RememberReadingPositionAsync(Place(thread, (thread * 10).ToString()), host.ReadingPositionGeneration);
            await host.RememberReadingPositionAsync(Place(4, "49"), host.ReadingPositionGeneration);
            IReadOnlyList<LocalReadingHistoryEntry> history = await host.ReadReadingHistoryAsync();
            Assert.Equal(new[] { "4", "13", "12", "11", "10", "9", "8", "7", "6", "5" }, history.Select(entry => entry.Position.Thread));
            Assert.Equal("49", history[0].Position.PostId);
            Assert.Equal("Thread 4", history[0].Position.Title);
        }
        // The list survives an owner restart, beside the single position.
        await using DesktopSessionHost restarted = Host(directory.Path);
        await restarted.StartAsync();
        Assert.Equal(10, (await restarted.ReadReadingHistoryAsync()).Count);
        Assert.Equal("49", (await restarted.ReadReadingPositionAsync())!.PostId);
        Assert.True(new FileInfo(Path.Combine(directory.Path, "reading-history.json")).Length <= LocalReadingHistory.MaximumFileBytes);
        Assert.Single(Directory.GetFiles(directory.Path, "reading-history*"));
    }

    [Fact]
    public async Task ALowerLimitInTheSettingsShowsFewerEntriesWithoutRewritingTheFile()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false, ReadingHistoryLimit = 100 }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        for (int thread = 1; thread <= 25; thread++) await host.RememberReadingPositionAsync(Place(thread), host.ReadingPositionGeneration);
        Assert.Equal(25, (await host.ReadReadingHistoryAsync()).Count);
        Config lowered = host.Config.Clone(); lowered.ReadingHistoryLimit = 10;
        await host.ApplySettingsAsync(lowered);
        Assert.Equal(10, (await host.ReadReadingHistoryAsync()).Count);
    }

    [Fact]
    public async Task WithoutTheOptInNothingIsWrittenAndTheListIsEmpty()
    {
        using TempDirectory directory = new();
        new Config { SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        await host.RememberReadingPositionAsync(Place(1), host.ReadingPositionGeneration);
        Assert.Empty(await host.ReadReadingHistoryAsync());
        Assert.Empty(Directory.GetFiles(directory.Path, "reading-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgetAndTheOptOutDeleteTheListAndTheLateWritesStayOut(bool disable)
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        long old = host.ReadingPositionGeneration;
        await host.RememberReadingPositionAsync(Place(1), old);
        await host.RememberReadingPositionAsync(Place(2), old);
        Assert.Equal(2, (await host.ReadReadingHistoryAsync()).Count);
        Assert.True(File.Exists(Path.Combine(directory.Path, "reading-history.json")));
        if (disable) { Config draft = host.Config.Clone(); draft.RememberReadingPosition = false; await host.ApplySettingsAsync(draft); }
        else await host.ForgetReadingPositionAsync();

        Assert.False(File.Exists(Path.Combine(directory.Path, "reading-history.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
        await host.RememberReadingPositionAsync(Place(3), old); // a write of the forgotten generation
        Assert.Empty(Directory.GetFiles(directory.Path, "reading-*"));
        Assert.Empty(await host.ReadReadingHistoryAsync());
    }

    [Fact]
    public async Task ADamagedListNeverFailsThePositionWriteAndIsReplacedByAFreshOne()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "reading-history.json");
        File.WriteAllText(file, "broken JSON");
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => host.ReadReadingHistoryAsync()); // reported, not hidden
        await host.RememberReadingPositionAsync(Place(1), host.ReadingPositionGeneration);
        Assert.Equal("1", (await host.ReadReadingPositionAsync())!.Thread);
        IReadOnlyList<LocalReadingHistoryEntry> history = await host.ReadReadingHistoryAsync();
        Assert.Equal("1", Assert.Single(history).Position.Thread);
        Assert.Equal(DesktopSessionState.Ready, host.State);
    }

    [Fact]
    public async Task AnUnwritableListLeavesThePositionSavedAndTheSessionReady()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        Directory.CreateDirectory(Path.Combine(directory.Path, "reading-history.json")); // a directory in the file's place
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        await host.RememberReadingPositionAsync(Place(1), host.ReadingPositionGeneration);
        Assert.Equal("1", (await host.ReadReadingPositionAsync())!.Thread);
        Assert.Equal(DesktopSessionState.Ready, host.State);
    }

    [Fact]
    public async Task AProfileThatOnlyHasTheSinglePositionShowsItAsTheFirstEntry()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        File.WriteAllBytes(Path.Combine(directory.Path, "reading-position.json"),
            JsonSerializer.SerializeToUtf8Bytes(new LocalReadingPosition("4chan", "g", "1000", "1020", -3, null), ReadingPositionJsonContext.Default.LocalReadingPosition));
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        LocalReadingHistoryEntry entry = Assert.Single(await host.ReadReadingHistoryAsync());
        Assert.Equal("1000", entry.Position.Thread);
        Assert.Null(entry.Position.Title);
        Assert.False(File.Exists(Path.Combine(directory.Path, "reading-history.json"))); // reading never writes
    }

    [Fact]
    public void TheMenuTextNamesTheThreadThePostAndHowLongAgo()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
        DateTimeOffset now = T0.AddHours(2);
        string titled = MainWindow.ReadingHistoryEntryText(new LocalReadingHistoryEntry(Place(1, "77", "Cats of the net"), T0), now);
        Assert.StartsWith("Cats of the net · ", titled);
        Assert.Contains("77", titled);
        string untitled = MainWindow.ReadingHistoryEntryText(new LocalReadingHistoryEntry(Place(5, "9") with { Title = null }, T0), now);
        Assert.StartsWith("/g/ thread 5 · ", untitled);
        string longTitle = MainWindow.ReadingHistoryEntryText(new LocalReadingHistoryEntry(Place(1, "1", new string('w', 150)), T0), now);
        Assert.Contains("…", longTitle);
        Assert.True(longTitle.Length < 150);
    }
}
