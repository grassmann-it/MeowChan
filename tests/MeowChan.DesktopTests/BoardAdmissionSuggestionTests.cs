using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit.Abstractions;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BoardAdmissionSuggestionTests(ITestOutputHelper output)
{
    private const string Board = BoardArchiveHostTests.BoardUrl;
    private const string Rpt = "/rpt/";
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThreadTextRule Manual = new(ThreadTextField.Title, ThreadTextAction.Exclude, "spam");
    private static readonly ThreadTextRule Suggested = new(ThreadTextField.Title, ThreadTextAction.Include, Rpt);
    private static readonly string[] ExpectedLiteralRules = ["lens advice crew", "recent photo thread"];
    private static readonly string[] ExpectedCatalogRequests = ["g", "p"];

    [Fact]
    public void ToggleAndEditUpdateTheExistingPreviewAndApplyReturnsOnlyTheDraft() => Run(() =>
    {
        using var profile = new TempDirectory();
        using var lease = ProfileLease.Acquire(profile.Path);
        var store = new BoardAdmissionStore(profile.Path, lease);
        var initial = new BoardAdmissionPolicy([Manual]);
        store.Save(Board, initial);
        string path = Path.Combine(profile.Path, BoardAdmissionStore.FileName);
        byte[] saved = File.ReadAllBytes(path);
        using BoardAdmissionWindow dialog = NewDialog(store.Get, (_, _) => Task.FromResult(Preview(initial)));
        var owner = new Window();
        owner.Show();
        try
        {
            Task<BoardAdmissionSelection?> result = dialog.ShowDialog<BoardAdmissionSelection?>(owner);
            dialog.PlatformImpl!.Activated?.Invoke();
            Layout(dialog);
            var check = Find<CheckBox>(dialog, "BoardAdmissionSuggestion." + Rpt);
            var summary = Find<TextBlock>(dialog, "BoardAdmissionSummary");
            var details = Find<TextBlock>(dialog, "BoardAdmissionSuggestionDetails." + Rpt);
            var warning = Find<TextBlock>(dialog, "BoardAdmissionNarrowingWarning");
            Assert.False(check.IsChecked);
            Assert.False(warning.IsEffectivelyVisible);
            Assert.Contains("3 to watch", summary.Text);
            BooruComponentDispatcher.WaitUntil(() => details.Text?.Contains("2 threads in the catalog now") == true, "catalog match count");
            Assert.Contains("2 threads in the catalog now", details.Text);

            Assert.True(check.Focus());
            dialog.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            dialog.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Layout(dialog);
            Assert.True(check.IsChecked);
            TextBox rule = Assert.Single(Editors(dialog), editor => editor.Text == Rpt);
            Assert.Contains("2 to watch", summary.Text);
            Assert.True(warning.IsEffectivelyVisible);
            Assert.Equal(Texts.T("wizard.suggest.narrow"), warning.Text);
            Assert.Contains(Controls<ComboBox>(dialog, "BoardAdmissionRuleField"), field => field.SelectedItem?.ToString() == Texts.T("wizard.titleField"));
            Assert.Contains(Controls<ComboBox>(dialog, "BoardAdmissionRuleAction"), action => action.SelectedItem?.ToString() == Texts.T("wizard.include"));

            rule.Text = "#2";
            Layout(dialog);
            Assert.Contains("1 to watch", summary.Text);
            Assert.Contains("adjusted", details.Text);
            check.IsChecked = false;
            Layout(dialog);
            Assert.Equal(Manual.Contains, Assert.Single(Editors(dialog)).Text);
            Assert.False(warning.IsEffectivelyVisible);
            Assert.Contains("3 to watch", summary.Text);
            Assert.Equal(saved, File.ReadAllBytes(path));

            check.IsChecked = true;
            Click(Find<Button>(dialog, "BoardAdmissionApply"));
            var selection = Assert.IsType<BoardAdmissionSelection>(BooruComponentDispatcher.Wait(result));
            Assert.Equal(new[] { Manual, Suggested }, selection.Policies[Board].Rules);
            Assert.Equal(saved, File.ReadAllBytes(path));
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public void CatalogFailureKeepsTheDraftAndLanguageRefreshUpdatesTheOpenBarebonesDialog() => Run(() =>
    {
        var initial = new BoardAdmissionPolicy([Manual]);
        int requests = 0;
        using BoardAdmissionWindow dialog = NewDialog(_ => initial, (_, _) => ++requests == 1
            ? Task.FromException<BoardAdmissionPreview>(new IOException("fixture catalog unavailable"))
            : Task.FromResult(Preview(initial)), WorkView.Barebones, existing: true);
        dialog.Show();
        try
        {
            Layout(dialog);
            var check = Find<CheckBox>(dialog, "BoardAdmissionSuggestion." + Rpt);
            var details = Find<TextBlock>(dialog, "BoardAdmissionSuggestionDetails." + Rpt);
            var title = Find<TextBlock>(dialog, "BoardAdmissionSuggestionsTitle");
            var scanned = Find<TextBlock>(dialog, "BoardAdmissionSuggestionsScanned");
            var warning = Find<TextBlock>(dialog, "BoardAdmissionNarrowingWarning");
            check.IsChecked = true;
            Assert.True(check.IsChecked);
            Assert.True(warning.IsEffectivelyVisible);
            Assert.False(Find<Button>(dialog, "BoardAdmissionApply").IsEnabled);
            Assert.Contains("fixture catalog unavailable", Find<TextBlock>(dialog, "BoardAdmissionStatus").Text);
            Assert.DoesNotContain("in the catalog now", details.Text);

            Click(Find<Button>(dialog, "BoardAdmissionRetry"));
            Layout(dialog);
            Assert.True(check.IsChecked);
            BooruComponentDispatcher.WaitUntil(() => details.Text?.Contains("2 threads in the catalog now") == true, "catalog match count after retry");
            Assert.Contains("2 threads in the catalog now", details.Text);
            Assert.Equal(2, requests);

            Texts.SetLanguage("de");
            Layout(dialog);
            Assert.Equal("Vorschläge aus deinem Archiv", title.Text);
            Assert.Equal("5 gesicherte Threads ausgewertet", scanned.Text);
            Assert.Contains("jetzt im Katalog", details.Text);
            Assert.Equal(Texts.T("wizard.suggest.narrowWatching"), warning.Text);
            Assert.True(check.IsChecked);
            Click(Find<Button>(dialog, "BoardAdmissionCancel"));
            Assert.False(dialog.IsVisible);
            Assert.Equal(Manual, Assert.Single(initial.Rules));
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    });

    [Fact]
    public void ExistingMatchingRuleIsCheckedAndCancelDiscardsChanges() => Run(() =>
    {
        using var profile = new TempDirectory();
        using var lease = ProfileLease.Acquire(profile.Path);
        var store = new BoardAdmissionStore(profile.Path, lease);
        var initial = new BoardAdmissionPolicy([Manual, Suggested]);
        store.Save(Board, initial);
        string path = Path.Combine(profile.Path, BoardAdmissionStore.FileName);
        byte[] saved = File.ReadAllBytes(path);
        using BoardAdmissionWindow dialog = NewDialog(store.Get, (_, _) => Task.FromResult(Preview(initial)), existing: true);
        var owner = new Window();
        owner.Show();
        try
        {
            var result = dialog.ShowDialog<BoardAdmissionSelection?>(owner);
            Layout(dialog);
            var check = Find<CheckBox>(dialog, "BoardAdmissionSuggestion." + Rpt);
            Assert.True(check.IsChecked);
            Assert.Single(Editors(dialog), editor => editor.Text == Rpt);
            Assert.False(Find<TextBlock>(dialog, "BoardAdmissionNarrowingWarning").IsEffectivelyVisible);
            check.IsChecked = false;
            Assert.Equal(Manual.Contains, Assert.Single(Editors(dialog)).Text);
            Assert.Equal(saved, File.ReadAllBytes(path));
            Click(Find<Button>(dialog, "BoardAdmissionCancel"));
            Assert.Null(BooruComponentDispatcher.Wait(result));
            Assert.Equal(saved, File.ReadAllBytes(path));
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            owner.Close();
        }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NoPatternLeavesTheSectionHiddenAndTheExistingDialogUsable(int titles) => Run(() =>
    {
        using var dialog = new BoardAdmissionWindow([Board], _ => BoardAdmissionPolicy.All,
            (_, _) => Task.FromResult(Preview(BoardAdmissionPolicy.All)), true,
            suggest: (_, token) => Task.FromResult(TitlePatternMiner.Mine(
                Enumerable.Range(1, titles).Select(i => Row(i, "/rpt/ #" + i, i)).ToArray(), "g", Now, 3, 12, token)));
        dialog.Show();
        try
        {
            Layout(dialog);
            Assert.False(Find<StackPanel>(dialog, "BoardAdmissionSuggestions").IsEffectivelyVisible);
            Assert.True(Find<Button>(dialog, "BoardAdmissionApply").IsEnabled);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void LiteralCanaryCoversCounterPrefixMixedCaseAndOutsideTitleHits()
    {
        BoardTitleHistory[] rows = [Row(1, "Recent Photo Thread 12", 6), Row(2, "recent photo thread #13", 5),
            Row(3, "RECENT PHOTO THREAD – 14", 4), Row(4, "Lens Advice Crew – autumn", 3),
            Row(5, "Lens Advice Crew winter", 2), Row(6, "Lens Advice Crew spring", 1),
            Row(7, "Thread #1", 3), Row(8, "Thread #2", 2), Row(9, "Thread #3", 1),
            Row(10, "A fourth THREAD title", 1), Row(11, "A fifth Thread title", 1),
            Row(12, "Ｆｕｌｌｗｉｄｔｈ #1", 1), Row(13, "Ｆｕｌｌｗｉｄｔｈ #2", 1), Row(14, "Ｆｕｌｌｗｉｄｔｈ #3", 1)];
        var expected = TitlePatternMiner.Mine(rows, "g", Now, 3, 12);
        Assert.Equal(ExpectedLiteralRules, expected.Items.Select(item => item.RuleText));
        Assert.Equal(expected.Items.ToArray(), TitlePatternMiner.Mine(rows.Reverse().ToArray(), "g", Now, 3, 12).Items.ToArray());
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(9, true)]
    public void SingleWordMustMatchAtMostAQuarterOfAllTitles(int unrelatedCount, bool offered)
    {
        var rows = Enumerable.Range(1, 3).Select(i => Row(i, "Thread #" + i, i))
            .Concat(UnrelatedTitles(unrelatedCount)).ToArray();

        var result = TitlePatternMiner.Mine(rows, "g", Now, 3, 12);

        Assert.Equal(3 + unrelatedCount, result.Scanned);
        if (offered) Assert.Equal("thread", Assert.Single(result.Items).RuleText);
        else Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("news", "/news/", "Camera news")]
    [InlineData("test_1", "/test_1/", "Camera test")]
    [InlineData("analogphotographers", "/analogphotographers/", "Camera analogphotographers")]
    [InlineData("test_1", "/test_1/test_1/", "Camera test")]
    public void OwnBoardCodeDoesNotBecomeAWordRule(string board, string titlePrefix, string unrelatedTitle)
    {
        var rows = Enumerable.Range(1, 3).Select(i => Row(i, $"{titlePrefix} #{i}", i))
            .Concat(UnrelatedTitles(16)).Append(Row(99, unrelatedTitle, 1)).ToArray();

        var result = TitlePatternMiner.Mine(rows, board, Now, 3, 12);

        Assert.Equal(20, result.Scanned);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("/film/test_1/")]
    [InlineData("/test_1/film/")]
    [InlineData("/test_1/test_1/film/test_1/")]
    public void ForeignBoardCodeKeepsItsSlashesWhenItSharesABoundaryWithTheOwnCode(string titlePrefix)
    {
        var rows = Enumerable.Range(1, 3).Select(i => Row(i, $"{titlePrefix} #{i}", i))
            .Concat(UnrelatedTitles(16)).Append(Row(99, "Camera film", 1)).ToArray();

        var result = TitlePatternMiner.Mine(rows, "test_1", Now, 3, 12);

        Assert.Equal(20, result.Scanned);
        var suggestion = Assert.Single(result.Items);
        Assert.Equal("/film/", suggestion.RuleText);
        Assert.Equal(3, suggestion.Count);
    }

    [Fact]
    public void PrefixFallbackGroupsLeadingVolumeNumbersWithDistinctSeasonNames()
    {
        BoardTitleHistory[] rows = [Row(1, "Vol. 1 Lens Advice Crew – Autumn", 3),
            Row(2, "Vol. 2 Lens Advice Crew – Winter", 2), Row(3, "Vol. 3 Lens Advice Crew – Spring", 1)];

        var result = TitlePatternMiner.Mine(rows, "g", Now, 3, 12);

        var suggestion = Assert.Single(result.Items);
        Assert.Equal("lens advice crew", suggestion.RuleText);
        Assert.Equal(3, suggestion.Count);
        Assert.Equal(rows[2].Title, suggestion.ExampleTitle);
    }

    [Fact]
    public async Task AllCatalogPreviewsGetTheEighthSlotWhileArchiveReadingCanBeCanceled()
    {
        using var profile = new TempDirectory();
        BoardArchiveHostTests.Configure(profile.Path);
        var requests = new ConcurrentQueue<string>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CatalogFixtureProvider(async (board, token) =>
        {
            requests.Enqueue(board);
            if (board == "g") await releaseFirst.Task.WaitAsync(token);
        });
        await using var host = OwnedUiDispatcher.CreateHost(() => new DesktopSessionHost(profile.Path, [provider]));
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);

        // Park seven real commands in the same host lane used by the required catalog previews.
        var commandGate = (SemaphoreSlim)typeof(DesktopSessionHost)
            .GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        await commandGate.WaitAsync();
        var parked = Enumerable.Range(1, 7).Select(id => host.RemoveThreadAsync(id)).ToArray();
        Task<BoardTitleSuggestions>? search = null;
        try
        {
            Assert.Equal(7, host.AcceptedCommandCount);
            using var connection = ArchiveSql.OpenConnection(Path.Combine(profile.Path, "archive.db"), SqliteOpenMode.ReadWrite);
            using var writeLock = connection.CreateCommand();
            writeLock.CommandText = "BEGIN EXCLUSIVE";
            writeLock.ExecuteNonQuery();
            try
            {
                Run(() =>
                {
                    const string secondBoard = "https://boards.4chan.org/p/";
                    var starts = new List<(string Kind, string Url)>();
                    CancellationToken searchToken = default;
                    using var dialog = new BoardAdmissionWindow([Board, secondBoard], _ => BoardAdmissionPolicy.All,
                        (url, token) =>
                        {
                            starts.Add(("preview", url));
                            return host.PreviewBoardAdmissionAsync(url, token);
                        }, true, suggest: (url, token) =>
                        {
                            starts.Add(("archive", url));
                            searchToken = token;
                            return search = host.ReadBoardTitleSuggestionsAsync(url, token);
                        });
                    dialog.Show();
                    try
                    {
                        Layout(dialog);
                        Assert.Equal(("preview", Board), starts[0]);
                        Button apply = Find<Button>(dialog, "BoardAdmissionApply");
                        Assert.Equal(8, host.AcceptedCommandCount);
                        Assert.False(search!.IsCompleted);
                        Assert.False(apply.IsEnabled);
                        releaseFirst.SetResult();
                        BooruComponentDispatcher.WaitUntil(() => apply.IsEnabled, "both required previews to load");
                        Assert.Equal(new[] { Board, secondBoard }, starts.Where(entry => entry.Kind == "preview").Select(entry => entry.Url));
                        Assert.Equal(Board, Assert.Single(starts, entry => entry.Kind == "archive").Url);
                        Assert.Equal(7, host.AcceptedCommandCount);
                        Assert.False(search!.IsCompleted);
                        Click(Find<Button>(dialog, "BoardAdmissionCancel"));
                        Assert.True(searchToken.IsCancellationRequested);
                    }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                });
            }
            finally
            {
                writeLock.CommandText = "ROLLBACK";
                writeLock.ExecuteNonQuery();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search!);
            Assert.Equal(ExpectedCatalogRequests, requests.ToArray());
        }
        finally
        {
            commandGate.Release();
            await Task.WhenAll(parked).WaitAsync(TimeSpan.FromSeconds(45));
        }
    }

    private sealed class CatalogFixtureProvider(Func<string, CancellationToken, Task> read) : FourChanImageBoard
    {
        public override IBoard GetBoard(Uri boardUri) => new CatalogFixtureBoard(this, boardUri, read);
    }

    private sealed class CatalogFixtureBoard(IImageBoard provider, Uri uri, Func<string, CancellationToken, Task> read)
        : FourChanBoard(provider, uri)
    {
        public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
        {
            await read(BoardCode, cancellationToken);
            return [];
        }
    }

    [Fact]
    public void FiftySuggestionsPublishCountsAfterThePreviewCanBeUsed() => Run(() =>
    {
        const int suggestionCount = 50;
        const int catalogCount = 5_000;
        var patterns = Enumerable.Range(0, suggestionCount)
            .Select(i => new BoardTitleSuggestion($"/tag{i:00}/", 3, Now, Now, TimeSpan.Zero, $"Example /tag{i:00}/"))
            .ToArray();
        var catalog = Enumerable.Range(0, catalogCount)
            .Select(i => new BoardCatalogThread(i.ToString(CultureInfo.InvariantCulture), Board + "thread/" + i,
                patterns[i % suggestionCount].RuleText.ToUpperInvariant(), "")).ToArray();
        var history = new TaskCompletionSource<BoardTitleSuggestions>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dialog = new BoardAdmissionWindow([Board], _ => BoardAdmissionPolicy.All,
            (_, _) => Task.FromResult(new BoardAdmissionPreview(Board, "/g/", BoardAdmissionPolicy.All, catalog)), true,
            suggest: (_, _) => history.Task);
        dialog.Show();
        try
        {
            Layout(dialog);
            var apply = Find<Button>(dialog, "BoardAdmissionApply");
            Assert.True(apply.IsEnabled);
            var section = Find<StackPanel>(dialog, "BoardAdmissionSuggestions");
            bool visibleWithoutCount = false;
            section.PropertyChanged += (_, e) =>
            {
                if (e.Property == StackPanel.IsVisibleProperty && section.IsVisible)
                {
                    var details = Assert.Single(section.GetLogicalDescendants().OfType<TextBlock>(),
                        text => AutomationProperties.GetAutomationId(text) == "BoardAdmissionSuggestionDetails./tag00/");
                    visibleWithoutCount = apply.IsEnabled && details.Text?.Contains("in the catalog now") == false;
                }
            };
            history.SetResult(new(150, Now, patterns));
            BooruComponentDispatcher.WaitUntil(() =>
            {
                Layout(dialog);
                return Controls<TextBlock>(dialog, "BoardAdmissionSuggestionDetails./tag49/").SingleOrDefault()?.Text
                    ?.Contains("100 threads in the catalog now") == true;
            }, "all 50 catalog match counts");
            Assert.True(visibleWithoutCount);
            Assert.All(patterns, pattern => Assert.Contains("100 threads in the catalog now",
                Find<TextBlock>(dialog, "BoardAdmissionSuggestionDetails." + pattern.RuleText).Text));
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public async Task HostReadsFiveThousandRecentTitlesLocallyAndUsesFirstSightForRhythm()
    {
        using var profile = new TempDirectory();
        BoardArchiveHostTests.Configure(profile.Path);
        await SeedArchiveAsync(profile.Path);
        using var transport = new BoardArchiveHostTests.ArchiveTransport();
        await using DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => BoardArchiveHostTests.CreateHost(profile.Path, transport));
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        Assert.Empty((await host.ReadBoardTitleSuggestionsAsync("https://boards.4chan.org/p/")).Items);

        Run(() =>
        {
            Stopwatch? elapsed = null;
            using var dialog = NewDialog(_ => BoardAdmissionPolicy.All, (_, _) => Task.FromResult(Preview(BoardAdmissionPolicy.All)),
                suggest: (url, token) => { elapsed = Stopwatch.StartNew(); return host.ReadBoardTitleSuggestionsAsync(url, token); });
            dialog.Show();
            try
            {
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    Layout(dialog);
                    return Controls<CheckBox>(dialog, "BoardAdmissionSuggestion." + Rpt).Any();
                }, "proposal from 5000 local archive titles");
                TimeSpan timeToShowSuggestion = elapsed!.Elapsed;
                output.WriteLine($"Suggestion from 5,000 local archive titles displayed after {timeToShowSuggestion.TotalMilliseconds:F1} ms.");
                Assert.True(timeToShowSuggestion < TimeSpan.FromMilliseconds(200),
                    $"Suggestion displayed after {timeToShowSuggestion.TotalMilliseconds:F1} ms");
                Assert.Equal("5,000 saved threads analysed", Find<TextBlock>(dialog, "BoardAdmissionSuggestionsScanned").Text);
            }
            finally { dialog.Close(); }
        });

        var read = await host.ReadBoardTitleSuggestionsAsync(Board);
        // Section 2.2c also requires the common prefix from the three long titles in this scan.
        Assert.Equal(new[] { "lens advice crew", Rpt }, read.Items.Select(item => item.RuleText));
        var suggestion = Assert.Single(read.Items, item => item.RuleText == Rpt);
        Assert.Equal(Rpt, suggestion.RuleText);
        Assert.Equal(5, suggestion.Count);
        Assert.Equal(Now.AddDays(-3), suggestion.LastSeenAt);
        Assert.Equal(TimeSpan.FromDays(2), suggestion.Rhythm);
        Assert.Equal("/RPT/ #5", suggestion.ExampleTitle);
        Assert.Empty(transport.Requests);
        Assert.Empty(host.GetView().Boards);
        Assert.False(File.Exists(Path.Combine(profile.Path, BoardAdmissionStore.FileName)));
    }

    [Fact]
    public async Task BoardTitleHistoryReadWorkStaysBoundedAfterReopeningAnOlderArchive()
    {
        using var profile = new TempDirectory();
        await SeedArchiveAsync(profile.Path, lastThread: 80_000);
        using var lease = ProfileLease.Acquire(profile.Path);
        using var connection = ArchiveSql.OpenConnection(Path.Combine(profile.Path, "archive.db"), SqliteOpenMode.ReadWrite);
        CheckRead();
        using (var drop = connection.CreateCommand())
        {
            // Reproduce a current-version archive saved before the optional index shipped.
            drop.CommandText = "DROP INDEX IF EXISTS archive_board_title_history";
            drop.ExecuteNonQuery();
        }
        await using var reopened = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        CheckRead();

        void CheckRead()
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            int batches = 0;
            raw.sqlite3_progress_handler(connection.Handle!, 1_000, _ => { batches++; return 0; }, null);
            try
            {
                var history = ArchiveSql.ReadBoardTitleHistory(connection, transaction, "4chan", "g", 5_000, CancellationToken.None);
                Assert.Equal(5_000, history.Count);
                Assert.Equal("5000", history[0].Thread);
                Assert.Equal("1", history[^1].Thread);
                Assert.DoesNotContain(history, thread => thread.Title.StartsWith("/old/", StringComparison.Ordinal));
                Assert.InRange(batches, 1, 100);
            }
            finally { raw.sqlite3_progress_handler(connection.Handle!, 0, null, null); }
        }
    }

    [Fact]
    public void SettingsLoadSaveResetAndRejectOutOfRangeValues() => Run(() =>
    {
        Config baseline = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal((3, 12, 5000), (baseline.BoardSuggestionMinThreads, baseline.BoardSuggestionLimit, baseline.BoardSuggestionScanThreads));
        using var profile = new TempDirectory();
        var config = new Config { DownloadPath = profile.Path, BoardSuggestionMinThreads = 4,
            BoardSuggestionLimit = 8, BoardSuggestionScanThreads = 2_000 };
        Config candidate = config.Clone();
        candidate.BoardSuggestionScanThreads = 99;
        Assert.Throws<ArgumentOutOfRangeException>(candidate.Validate);
        Config clamped = JsonSerializer.Deserialize("""{"BoardSuggestionScanThreads":99999}""", ConfigJsonContext.Default.Config)!;
        Assert.Equal(50_000, clamped.BoardSuggestionScanThreads);
        var editor = new SettingsEditor(config);
        try
        {
            var minimum = Setting(editor, "BoardSuggestionMinThreadsInput");
            var limit = Setting(editor, "BoardSuggestionLimitInput");
            var scan = Setting(editor, "BoardSuggestionScanThreadsInput");
            Assert.Equal((4m, 8m, 2_000m), (minimum.Value, limit.Value, scan.Value));
            editor.ResetDraft(SettingsEditor.SettingsResetScope.Behavior);
            Assert.Equal((3m, 12m, 5_000m), (minimum.Value, limit.Value, scan.Value));
            Config roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config),
                ConfigJsonContext.Default.Config)!;
            Assert.Equal((4, 8, 2_000), (roundTrip.BoardSuggestionMinThreads, roundTrip.BoardSuggestionLimit, roundTrip.BoardSuggestionScanThreads));
        }
        finally { editor.EndLifetime(); }
    });

    [Theory]
    [InlineData("BoardSuggestionMinThreadsInput", 4, 5)]
    [InlineData("BoardSuggestionLimitInput", 8, 9)]
    [InlineData("BoardSuggestionScanThreadsInput", 2_000, 2_001)]
    public void EditingABoardLimitAfterAnEmbeddedSaveRetiresTheSavedStatus(string id, int first, int second) => Run(() =>
    {
        using var profile = new TempDirectory();
        BoardArchiveHostTests.Configure(profile.Path);
        Config config = Config.LoadConfig(profile.Path);
        var editor = new SettingsEditor(config, applySettings: candidate =>
        {
            candidate.SaveConfig(profile.Path);
            return Task.CompletedTask;
        });
        try
        {
            NumericUpDown input = Setting(editor, id);
            input.Value = first;
            var save = Assert.Single(editor.GetLogicalDescendants().OfType<Button>(),
                button => ControlAutomationPeer.CreatePeerForElement(button)?.GetAutomationId() == "SaveSettingsButton");
            Click(save);
            BooruComponentDispatcher.WaitUntil(() => !editor.IsBusy, "the embedded settings save");
            Assert.False(editor.HasError, editor.ErrorDetails);
            Assert.Equal(Texts.T("settings.saved"), editor.StatusMessage);
            input.Value = second;
            Assert.Equal(Texts.T("settings.unsaved"), editor.StatusMessage);
            Config stored = Config.LoadConfig(profile.Path);
            int value = id switch
            {
                "BoardSuggestionMinThreadsInput" => stored.BoardSuggestionMinThreads,
                "BoardSuggestionLimitInput" => stored.BoardSuggestionLimit,
                _ => stored.BoardSuggestionScanThreads,
            };
            Assert.Equal(first, value);
        }
        finally { editor.EndLifetime(); }
    });

    [Fact]
    public void MacCommandShiftShortcutsKeepTheirExistingActions()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.G, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift },
            "reader.find-previous", "reader"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift },
            "board.filters", "downloads"));
    }

    [Theory]
    [InlineData("BoardSuggestionMinThreadsInput", Config.BoardSuggestionMinThreadsDefault)]
    [InlineData("BoardSuggestionLimitInput", Config.BoardSuggestionLimitDefault)]
    [InlineData("BoardSuggestionScanThreadsInput", Config.BoardSuggestionScanThreadsDefault)]
    public void RowResetRecoversAnInvalidBoardLimitDraftAtTheSavedDefault(string id, int defaultValue) => Run(() =>
    {
        using var profile = new TempDirectory();
        BoardArchiveHostTests.Configure(profile.Path);
        var editor = new SettingsEditor(Config.LoadConfig(profile.Path), applySettings: candidate =>
        {
            candidate.SaveConfig(profile.Path);
            return Task.CompletedTask;
        });
        try
        {
            NumericUpDown input = Setting(editor, id);
            var save = Assert.Single(editor.GetLogicalDescendants().OfType<Button>(),
                button => ControlAutomationPeer.CreatePeerForElement(button)?.GetAutomationId() == "SaveSettingsButton");
            string defaultText = Assert.IsType<string>(input.Text);
            Save();
            Assert.False(ResetEntry().Available);
            input.Text = "abc";
            Assert.Equal(defaultValue, input.Value);
            Assert.Equal(Texts.T("settings.unsaved"), editor.StatusMessage);

            ContextMenuEntry reset = ResetEntry();
            Assert.True(reset.Available, reset.Reason);
            reset.Invoke!();
            Assert.Equal(defaultText, input.Text);
            Assert.False(ResetEntry().Available);
            Save();

            Config stored = Config.LoadConfig(profile.Path);
            Assert.Equal(defaultValue, id switch
            {
                "BoardSuggestionMinThreadsInput" => stored.BoardSuggestionMinThreads,
                "BoardSuggestionLimitInput" => stored.BoardSuggestionLimit,
                _ => stored.BoardSuggestionScanThreads,
            });

            void Save()
            {
                Click(save);
                BooruComponentDispatcher.WaitUntil(() => !editor.IsBusy, "the embedded settings save");
                Assert.False(editor.HasError, editor.ErrorDetails);
                Assert.Equal(Texts.T("settings.saved"), editor.StatusMessage);
            }

            ContextMenuEntry ResetEntry() => Assert.Single(editor.ComposeRowMenu(input)!.ToModel().Entries,
                entry => entry.CommandId == SettingsRowMenu.Reset);
        }
        finally { editor.EndLifetime(); }
    });

    private static async Task SeedArchiveAsync(string path, int lastThread = 5_004)
    {
        using var lease = ProfileLease.Acquire(path);
        await using var catalog = await ArchiveCatalog.OpenAsync(path, lease);
        await catalog.WriteAsync((connection, transaction) =>
        {
            using var command = ArchiveSql.Command(connection, transaction, """
                WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n < $last)
                INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                SELECT '4chan','g',n,'https://boards.4chan.org/g/thread/' || n,
                    CASE WHEN n = 5 THEN '/RPT/ #5' WHEN n < 5 THEN '/rpt/ #' || n
                         WHEN n < 4998 THEN 'lens' || char(65+(n/676)%26,65+(n/26)%26,65+n%26) || ' field note'
                         WHEN n = 4998 THEN $autumn WHEN n = 4999 THEN $winter WHEN n = 5000 THEN $spring
                         WHEN n = 5001 THEN '' ELSE '/old/ #' || n END,
                    $media,0,0,CASE WHEN n < 6 THEN strftime('%Y-%m-%dT12:00:00.0000000+00:00', $base, printf('-%d days', 13-2*n))
                                   WHEN n = 5000 THEN $newest
                                   WHEN n < 5002 THEN $base ELSE $old END,0 FROM seq
                """);
            command.Parameters.AddWithValue("$last", lastThread);
            command.Parameters.AddWithValue("$media", path);
            command.Parameters.AddWithValue("$base", ArchiveSql.Date(Now));
            command.Parameters.AddWithValue("$newest", ArchiveSql.Date(Now.AddDays(1)));
            command.Parameters.AddWithValue("$old", ArchiveSql.Date(Now.AddDays(-100)));
            command.Parameters.AddWithValue("$autumn", LongTitle("Autumn", "apricot"));
            command.Parameters.AddWithValue("$winter", LongTitle("Winter", "evergreen"));
            command.Parameters.AddWithValue("$spring", LongTitle("Spring", "walnut"));
            return command.ExecuteNonQuery();
        });
    }

    private static string LongTitle(string season, string word) => "Lens Advice Crew – " + season + " "
        + string.Join(' ', Enumerable.Repeat(word, 768));

    private static IEnumerable<BoardTitleHistory> UnrelatedTitles(int count) => Enumerable.Range(0, count)
        .Select(i => Row(100 + i, "Single" + (char)('a' + i) + " note", 1));

    private static BoardAdmissionWindow NewDialog(Func<string, BoardAdmissionPolicy> policy,
        Func<string, CancellationToken, Task<BoardAdmissionPreview>> preview, WorkView view = WorkView.Simple,
        bool existing = false, Func<string, CancellationToken, Task<BoardTitleSuggestions>>? suggest = null) =>
        new([Board], policy, preview, true, view, suggest ?? ((_, token) => Task.FromResult(TitlePatternMiner.Mine(
            Enumerable.Range(1, 5).Select(i => Row(i, "/rpt/ #" + i, 13 - 2 * i)).ToArray(), "g", Now, 3, 12, token))), existing);

    private static BoardTitleHistory Row(int id, string title, int age) => new(id.ToString(CultureInfo.InvariantCulture), title, Now.AddDays(-age), Now);
    private static BoardAdmissionPreview Preview(BoardAdmissionPolicy policy) => new(Board, "/g/", policy,
        [new("1", Board + "thread/1", "/rpt/ #1", ""), new("2", Board + "thread/2", "/RPT/ #2", ""),
         new("3", Board + "thread/3", "Single title", ""), new("4", Board + "thread/4", "spam", "")]);

    private static IEnumerable<T> Controls<T>(Control root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Where(control => AutomationProperties.GetAutomationId(control) == id);
    private static T Find<T>(Control root, string id) where T : Control => Assert.Single(Controls<T>(root, id));
    private static IEnumerable<TextBox> Editors(Window root) => Controls<TextBox>(root, "BoardAdmissionRuleText");
    private static NumericUpDown Setting(SettingsEditor root, string id) => Assert.Single(root.GetLogicalDescendants().OfType<NumericUpDown>(),
        control => AutomationProperties.GetAutomationId(control) == id);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Run(Action action) => BooruComponentDispatcher.Run(() =>
    {
        using var platform = BooruReviewTestPlatform.Enter();
        action();
    });
}
