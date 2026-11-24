using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Automation;
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
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Persistence;
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
            suggest: (_, _) => Task.FromResult(TitlePatternMiner.Mine(
                Enumerable.Range(1, titles).Select(i => Row(i, "/rpt/ #" + i, i)).ToArray(), "g", Now, 3, 12)));
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
        Assert.Equal(new[] { "lens advice crew", "recent photo thread" }, expected.Items.Select(item => item.RuleText));
        Assert.Equal(expected.Items.ToArray(), TitlePatternMiner.Mine(rows.Reverse().ToArray(), "g", Now, 3, 12).Items.ToArray());
    }

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
        var suggestion = Assert.Single(read.Items);
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

    [Fact]
    public void MacCommandShiftGAlreadyResolvesToFindPrevious()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.G, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift },
            "reader.find-previous", "reader"));
    }

    private static async Task SeedArchiveAsync(string path)
    {
        using var lease = ProfileLease.Acquire(path);
        await using var catalog = await ArchiveCatalog.OpenAsync(path, lease);
        await catalog.WriteAsync((connection, transaction) =>
        {
            using var command = ArchiveSql.Command(connection, transaction, """
                WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n < 5004)
                INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
                SELECT '4chan','g',n,'https://boards.4chan.org/g/thread/' || n,
                    CASE WHEN n = 5 THEN '/RPT/ #5' WHEN n < 5 THEN '/rpt/ #' || n
                         WHEN n < 5001 THEN 'lens' || char(65+(n/676)%26,65+(n/26)%26,65+n%26) || ' field note'
                         WHEN n = 5001 THEN '' ELSE '/old/ #' || n END,
                    $media,0,0,CASE WHEN n < 6 THEN strftime('%Y-%m-%dT12:00:00.0000000+00:00', $base, printf('-%d days', 13-2*n))
                                   WHEN n < 5002 THEN $base ELSE $old END,0 FROM seq
                """);
            command.Parameters.AddWithValue("$media", path);
            command.Parameters.AddWithValue("$base", ArchiveSql.Date(Now));
            command.Parameters.AddWithValue("$old", ArchiveSql.Date(Now.AddDays(-100)));
            return command.ExecuteNonQuery();
        });
    }

    private static BoardAdmissionWindow NewDialog(Func<string, BoardAdmissionPolicy> policy,
        Func<string, CancellationToken, Task<BoardAdmissionPreview>> preview, WorkView view = WorkView.Simple,
        bool existing = false, Func<string, CancellationToken, Task<BoardTitleSuggestions>>? suggest = null) =>
        new([Board], policy, preview, true, view, suggest ?? ((_, _) => Task.FromResult(TitlePatternMiner.Mine(
            Enumerable.Range(1, 5).Select(i => Row(i, "/rpt/ #" + i, 13 - 2 * i)).ToArray(), "g", Now, 3, 12))), existing);

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
