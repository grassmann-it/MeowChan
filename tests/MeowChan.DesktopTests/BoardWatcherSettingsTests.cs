using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BoardWatcherSettingsTests
{
    private const string Board = "https://boards.4chan.org/p/";
    private const string Pattern = "/rpt/";

    [Fact]
    public void SchemaOneWithoutWatcherFieldsStillLoadsRulesAndExceptions()
    {
        using var profile = new TempDirectory();
        File.WriteAllText(Path.Combine(profile.Path, BoardAdmissionStore.FileName), """
            {
              "SchemaVersion": 1,
              "Boards": {
                "https://boards.4chan.org/p/": {
                  "Rules": [{ "Field": 0, "Action": 0, "Contains": "/rpt/" }],
                  "Exceptions": { "https://boards.4chan.org/p/thread/1": 1 }
                }
              }
            }
            """);
        using var lease = ProfileLease.Acquire(profile.Path);
        var store = new BoardAdmissionStore(profile.Path, lease);
        BoardAdmissionPolicy policy = store.Get(Board);

        Assert.Null(store.LoadWarning);
        Assert.Null(policy.WatcherName);
        Assert.Null(policy.WatcherIntervalMinutes);
        Assert.Equal(new ThreadTextRule(ThreadTextField.Title, ThreadTextAction.Include, Pattern), Assert.Single(policy.Rules));
        Assert.Equal(ThreadAdmissionException.Exclude, policy.Exceptions[Board + "thread/1"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void OutOfRangeIntervalsCannotBeStored(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoardAdmissionPolicy([], null, null, minutes));
        var config = new Config { BoardWatcherIntervalMinutes = minutes };
        Assert.Throws<ArgumentOutOfRangeException>(config.Validate);
    }

    [Fact]
    public void WizardSuggestsNamesAndRejectsInvalidIntervalsBeforeApplying() => Run(() =>
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        BoardTitleSuggestion[] suggestions = new[] { Pattern, "/fgt/", "gear thread" }
            .Select(pattern => new BoardTitleSuggestion(pattern, 3, now.AddDays(-2), now, TimeSpan.FromDays(1), pattern)).ToArray();
        using var dialog = new BoardAdmissionWindow([Board], _ => BoardAdmissionPolicy.All,
            (_, _) => Task.FromResult(new BoardAdmissionPreview(Board, "Photography", BoardAdmissionPolicy.All, [])), true,
            suggest: (_, _) => Task.FromResult(new BoardTitleSuggestions(9, now, suggestions)), defaultWatcherInterval: 17);
        var owner = new Window();
        owner.Show();
        try
        {
            Task<BoardAdmissionSelection?> result = dialog.ShowDialog<BoardAdmissionSelection?>(owner);
            Layout(dialog);
            var name = Find<TextBox>(dialog, "BoardAdmissionWatcherName");
            var interval = Find<TextBox>(dialog, "BoardAdmissionWatcherInterval");
            var apply = Find<Button>(dialog, "BoardAdmissionApply");
            Assert.True(apply.IsEnabled);
            Assert.Equal("17", interval.Text);
            Assert.Equal("Photography", name.PlaceholderText);

            Find<CheckBox>(dialog, "BoardAdmissionSuggestion." + Pattern).IsChecked = true;
            Layout(dialog);
            Assert.Equal("/p/ · /rpt/", name.Text);
            Find<CheckBox>(dialog, "BoardAdmissionSuggestion./fgt/").IsChecked = true;
            Find<CheckBox>(dialog, "BoardAdmissionSuggestion.gear thread").IsChecked = true;
            Layout(dialog);
            Assert.Equal("/p/ · 3 patterns", name.Text);
            Texts.SetLanguage("de");
            Layout(dialog);
            Assert.Equal("/p/ · 3 Muster", name.Text);
            Assert.Equal(Texts.T("wizard.watcher.name"), AutomationProperties.GetName(name));

            name.Text = string.Empty;
            Find<CheckBox>(dialog, "BoardAdmissionSuggestion./fgt/").IsChecked = false;
            Layout(dialog);
            Assert.Empty(name.Text ?? string.Empty);
            foreach (string invalid in new[] { "0", "1441", "1.5" })
            {
                interval.Text = invalid;
                Layout(dialog);
                Assert.False(apply.IsEnabled);
                Click(apply);
                Assert.False(result.IsCompleted);
            }
            foreach (string valid in new[] { "1", "1440", string.Empty })
            {
                interval.Text = valid;
                Layout(dialog);
                Assert.True(apply.IsEnabled);
            }
            Click(apply);
            BoardAdmissionPolicy saved = Assert.IsType<BoardAdmissionSelection>(BooruComponentDispatcher.Wait(result)).Policies[Board];
            Assert.Null(saved.WatcherName);
            Assert.Null(saved.WatcherIntervalMinutes);
            Assert.Equal(2, saved.Rules.Count);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            owner.Close();
        }
    });

    [Fact]
    public void DefaultIntervalResetsAndTransfersAsASetting() => Run(() =>
    {
        using var profile = new TempDirectory();
        var config = new Config { DownloadPath = profile.Path, BoardWatcherIntervalMinutes = 29 };
        var editor = new SettingsEditor(config);
        try
        {
            var input = Assert.Single(editor.GetLogicalDescendants().OfType<NumericUpDown>(),
                control => AutomationProperties.GetAutomationId(control) == "BoardWatcherIntervalMinutesInput");
            Assert.Equal((29m, 1m, 1440m), (input.Value, input.Minimum, input.Maximum));
            editor.ResetDraft(SettingsEditor.SettingsResetScope.Behavior);
            Assert.Equal(5m, input.Value);
            Assert.Equal(29, config.Clone().BoardWatcherIntervalMinutes);
            Config restored = JsonSerializer.Deserialize(SettingsTransfer.Export(config, "1.1.2").ToJsonString(),
                ConfigJsonContext.Default.Config)!;
            Assert.Equal(29, restored.BoardWatcherIntervalMinutes);
        }
        finally { editor.EndLifetime(); }
    });

    [Fact]
    public void DirectAdmissionUsesTheDefaultForNewBoardsAndPreservesSavedOverrides()
    {
        var saved = new BoardAdmissionPolicy([], null, "Local name", 37);
        BoardAdmissionPolicy existing = MainWindow.DirectBoardAdmissions([Board], _ => saved, 29).Policies[Board];
        Assert.Equal("Local name", existing.WatcherName);
        Assert.Equal(37, existing.WatcherIntervalMinutes);
        Assert.Equal(29, MainWindow.DirectBoardAdmissions([Board], _ => BoardAdmissionPolicy.All, 29).Policies[Board].WatcherIntervalMinutes);
        Assert.Null(MainWindow.DirectBoardAdmissions([Board], _ => new BoardAdmissionPolicy([]), 29).Policies[Board].WatcherIntervalMinutes);
    }

    private static T Find<T>(Window window, string id) where T : Control => Assert.Single(
        window.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using var platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        Texts.SetLanguage("en");
        try { test(); }
        finally { Texts.SetLanguage("en"); }
    });
}
