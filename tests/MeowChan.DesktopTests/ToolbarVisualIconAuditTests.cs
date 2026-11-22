using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ToolbarVisualIconAuditTests
{
    private static readonly Dictionary<string, string> TextExceptions = new()
    {
        ["TaggingGroup"] = "ComboBox for the queue scope.",
        ["TaggingFilter"] = "ComboBox for the queue state.",
        ["TaggingClearYes"] = "Explicit destructive confirmation.",
        ["TaggingClearNo"] = "Safe answer to the confirmation.",
        ["MediaSearchScope"] = "Search-field chip that displays the selected scope.",
        ["MediaSort"] = "ComboBox for the sort criterion.",
        ["PluginCategory"] = "ComboBox for the source or plugin category.",
        ["RuleWizardStepScope"] = "Numbered navigation through the wizard.",
        ["RuleWizardStepSettings"] = "Numbered navigation through the wizard.",
        ["RuleWizardStepReview"] = "Numbered navigation with a textual attention cue.",
        ["StatisticsSegmentTab"] = "Tab controlling the visible status page.",
        ["DiagnosticsSegmentTab"] = "Tab with a live problem-count badge.",
        ["ThreadsTab"] = "Tab controlling the visible download list.",
        ["BoardsTab"] = "Tab controlling the visible download list.",
        ["CancelResetButton"] = "Safe answer to the statistics reset prompt.",
        ["ConfirmResetButton"] = "Explicit destructive confirmation."
    };

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void TaggingActionsKeepTheirIconsAndAccessibleLabelsWhenCompacted(string language) => Run(() =>
    {
        Texts.SetLanguage(language);
        using TempDirectory profile = new();
        DesktopSessionHost session = new(profile.Path, []);
        TaggingPage page = new(session);
        using TestWindow window = new(page);
        try
        {
            TaggingLayoutRhythmTests.RenderSnapshot(page, new TagQueueSnapshot(1, 0, [], [TaggingLayoutRhythmTests.Item()], TagQueueLoadState.Ready));
            page.List.SelectedIndex = 0;
            Settle(window);
            Audit(page.Header, "TaggingStamp", "TaggingStart", "TaggingGroup", "TaggingFilter");
            Audit(page.SelectionBar, "TaggingSelectionTag", "TaggingSelectionDone", "TaggingSelectionMore", "TaggingSelectionClear");
            Assert.Equal("more-horizontal", IconButtons.KeyOf(page.SelectionMoreButton));
            Assert.Equal(CommandCatalog.Get(TaggingCommands.Tag).Icon, IconButtons.KeyOf(page.SelectionTagButton));
            Assert.Equal(new[] { "plus", "edit-2", "x-circle" }, page.GroupManageMenuModel()!.Entries.Select(item => item.Icon));

            TaggingControlRow row = Assert.IsType<TaggingControlRow>(page.SelectionBar.Child);
            row.Measure(new Size(176, double.PositiveInfinity));
            Assert.False(row.GetValue(TaggingControlRow.ShowActionLabelsProperty));
            foreach (Button button in new[] { page.SelectionTagButton, page.SelectionDoneButton, page.SelectionMoreButton })
            {
                Assert.True(HasRenderedIcon(button), button.Name);
                Assert.False(LabelIsVisible(button));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string));
            }
            row.Measure(new Size(1000, double.PositiveInfinity));
            Assert.True(LabelIsVisible(page.SelectionDoneButton));

            page.ShowSelectionMenu();
            Settle(window);
            MenuItem[] entries = page.OpenSelectionMenu!.Items.OfType<MenuItem>().ToArray();
            Assert.NotEmpty(entries);
            foreach (MenuItem item in entries)
                Assert.True(item.Icon is Control icon && HasRenderedIcon(icon), item.Header?.ToString());
            MenuItem remove = Assert.Single(entries, item => Equals(item.Tag, TaggingCommands.Remove));
            Assert.Contains("danger", remove.Classes);
            Assert.Equal("x", CommandCatalog.Get(TaggingCommands.Remove).Icon);
            page.OpenSelectionMenu.Close();

            Flyout stamp = Assert.IsType<Flyout>(page.StampButton.Flyout);
            stamp.ShowAt(page.StampButton);
            Settle(window);
            Audit(Assert.IsAssignableFrom<Control>(stamp.Content), "TaggingStampAdd", "TaggingStampClose");
            stamp.Hide();
            CommandBindings.InvokeAction(page.ClearButton);
            Settle(window);
            Audit(Bar(page, "TaggingClearConfirm"), "TaggingClearYes", "TaggingClearNo");
        }
        finally
        {
            window.Close();
            page.End();
            BooruComponentDispatcher.Wait(session.DisposeAsync().AsTask());
        }
    });

    [Fact]
    public void BooruSearchAndWatcherCardsRenderIcons() => Run(() =>
    {
        BooruUiHarness harness = new();
        harness.Watchers.Seed(BooruUiFixtures.Watcher("w1", "cat", clock: harness.Clock));
        using BooruPage page = new(harness.Services);
        using TestWindow window = new(page);
        BooruComponentDispatcher.Wait(page.ActivateAsync());
        Settle(window);
        Audit(Bar(page, "BooruSearchActions"), BooruWorkbenchView.AlternateSubmitName, BooruWorkbenchView.DownloadAllName);
        Assert.Equal("download", IconButtons.KeyOf(Bar<Button>(page, BooruWorkbenchView.DownloadAllName)));
        Button watchers = Bar<Button>(page.View.Header, BooruWorkbenchView.OpenWatchersName);
        Assert.True(HasRenderedIcon(watchers));
        Assert.Equal("eye", IconButtons.KeyOf(watchers));
        Assert.Equal(Texts.Count("booru.ui.tab.watchers", 1), AutomationProperties.GetName(watchers));
        Assert.Equal(Texts.Count("booru.tabs.watchersTip", 1), ToolTip.GetTip(watchers));
        page.View.ShowWatchers();
        Settle(window);
        Control watcher = Bar(page, "BooruWatcher:w1");
        string check = BooruWorkbenchView.CheckPrefix + "w1";
        string remove = BooruWorkbenchView.RemovePrefix + "w1";
        Audit(watcher, check, remove);
        Assert.Equal(CommandCatalog.Get("watch.check").Icon, IconButtons.KeyOf(Bar<Button>(watcher, check)));
        Assert.Equal(CommandCatalog.Get("watch.remove").Icon, IconButtons.KeyOf(Bar<Button>(watcher, remove)));
    });

    [Fact]
    public void DownloadsActionsRenderIconsBesideTheTextTabs() => Run(() =>
    {
        using TempDirectory profile = new();
        new Config { CloseToTray = false, MonitorClipboardUrls = false, FirstRunSetupVersion = 1, WorkspaceMode = WorkView.Advanced.ToSetting() }.SaveConfig(profile.Path);
        MainWindow window = new(new DesktopSessionHost(profile.Path, [])) { Width = 1600, Height = 900 };
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            Settle(window);
            Audit(Bar(window, "DownloadsTabs"), "ThreadsTab", "BoardsTab");
            window.FindControl<SearchBox>("DownloadSearchInput")!.ShowSearchIcon = false;
            window.FocusDownloadSearch();
            Settle(window);
            Audit(Bar(window, "WorkspaceListActions"), "DownloadSearchButton", "DetailsToggleButton");
            Audit(Bar(window, "AddUrlButton"), "AddUrlButton");
        }
        finally
        {
            window.Close();
            BooruComponentDispatcher.Wait(closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    });

    [Fact]
    public void ArchiveAndReaderToolbarsRenderIcons() => Run(() =>
    {
        ArchivePage page = ReaderMediaSidebarLayoutTests.Page();
        page.FindControl<Grid>("ArchiveSearchRow")!.IsVisible = false;
        using TestWindow window = new(page);
        try
        {
            page.ArchiveColumnModePreference = WorkView.Advanced.ToSetting();
            page.ArchiveSplitPreference = 840;
            var tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            tabs.IsVisible = true;
            tabs.SelectedItem = page.FindControl<TabItem>("ArchiveMediaTab");
            Settle(window);
            Audit(Bar(page, "ArchiveHeaderActions"), "RefreshArchiveButton");
            Audit(Bar(page, "ArchiveResultActions"), "ArchiveLayoutList", "ArchiveLayoutTable");
            Audit(Bar(page, "ReaderToolbar"), "ArchiveOpenGallery", "ReaderFindOpen", "ReaderTagsOpen");
            Audit(Bar(page, "ReaderMediaTools"), "ArchiveMediaLayoutTiles", "ArchiveMediaLayoutTable");
            Audit(Bar(page, "ReaderMediaActions"), "PreviewArchiveMediaButton");
        }
        finally { window.Close(); page.EndLifetime(); }
    });

    [Fact]
    public void MediaPluginsRulesStatusAndSettingsRenderIconsInTheirActionRows() => Run(() =>
    {
        MediaPage media = new(_ => throw new InvalidOperationException("The toolbar does not need a media library."));
        PluginsPage plugins = new();
        RulesPage rules = new() { IsAvailable = true };
        SettingsEditor settings = new() { IsAvailable = true };
        StatusPage status = new();
        StatisticsPage statistics = new();
        DiagnosticsPage diagnostics = new();
        diagnostics.FindControl<SearchBox>("DiagnosticTextFilter")!.ShowSearchIcon = false;
        foreach (SearchBox input in settings.GetLogicalDescendants().OfType<SearchBox>()) input.ShowSearchIcon = false;
        status.AttachStatistics(statistics);
        status.AttachDiagnostics(diagnostics);
        using TestWindow window = new(media);
        try
        {
            Audit(media.ToolbarRow, "MediaRefresh");
            window.ShowPage(plugins);
            Audit(Bar(plugins, "PluginsHeaderActions"), "PluginsAdd", "PluginsRefresh", "PluginCategory");
            window.ShowPage(rules);
            Audit(Bar(rules, "RulesHeaderActions"), "RulesHelpButton");
            Audit(Bar(rules, "RulesOverviewEmptyActions"), "CreateFirstRuleButton");
            rules.OpenNewRuleWizard();
            Settle(window);
            Audit(Bar(rules, "RulesFooterActions"), "OpenNewRuleScopeButton");
            Audit(rules.FindControl<Button>("RuleWizardStepScope")!, "RuleWizardStepScope");
            Audit(rules.FindControl<Button>("RuleWizardStepSettings")!, "RuleWizardStepSettings");
            Audit(rules.FindControl<Button>("RuleWizardStepReview")!, "RuleWizardStepReview");
            window.ShowPage(status);
            Audit(status.Segments, "StatisticsSegmentTab", "DiagnosticsSegmentTab");
            statistics.ApplyWorkView(WorkView.Advanced);
            Settle(window);
            Audit(Bar(statistics, "StatisticsActions"), "ResetStatisticsButton");
            Button reset = Bar<Button>(statistics, "ResetStatisticsButton");
            Flyout resetPrompt = Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(reset));
            resetPrompt.ShowAt(reset);
            Settle(window);
            Audit(Assert.IsAssignableFrom<Control>(resetPrompt.Content), "CancelResetButton", "ConfirmResetButton");
            resetPrompt.Hide();
            status.ShowSegment(StatusSegment.Diagnostics);
            Settle(window);
            Audit(Bar(diagnostics, "DiagnosticsActionToolbar"), "CopyDiagnosticButton");
            window.ShowPage(settings);
            Audit(Bar(settings, "SettingsFooter"), "SaveSettingsButton", "CancelSettingsButton");
            Audit(Bar(settings, "ThemeColorsButton"), "ThemeColorsButton");
        }
        finally
        {
            window.Close();
            media.EndLifetime();
            plugins.EndLifetime();
            rules.EndLifetime();
            status.EndLifetime();
            statistics.EndLifetime();
            diagnostics.EndLifetime();
            settings.EndLifetime();
        }
    });

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        Texts.SetLanguage("en");
        try { test(); }
        finally { Texts.SetLanguage("en"); }
    });

    private static Control Bar(Control page, string name) => Bar<Control>(page, name);

    private static T Bar<T>(Control page, string name) where T : Control =>
        Assert.Single(page.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static void Audit(Control bar, params string[] expected)
    {
        Control[] actions = bar.GetSelfAndVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && IsAction(control)).ToArray();
        string[] names = actions.Select(action => action.Name ?? action.GetType().Name).ToArray();
        Assert.True(expected.All(names.Contains), $"{bar.Name}: expected {string.Join(", ", expected)}, found {string.Join(", ", names)}");
        string[] missing = actions.Where(action =>
            !(action.Name is string name && TextExceptions.TryGetValue(name, out string? reason) && !string.IsNullOrWhiteSpace(reason))
            && !HasRenderedIcon(action)).Select(action => action.Name ?? action.GetType().Name).ToArray();
        Assert.True(missing.Length == 0, $"{bar.Name}: missing icons on {string.Join(", ", missing)}");
    }

    private static bool IsAction(Control control) => control switch
    {
        ComboBox or TabItem => true,
        Button { TemplatedParent: ComboBox or ScrollBar or TextBox or ToggleSwitch } => false,
        Button => true,
        _ => false
    };

    private static bool HasRenderedIcon(Control action) => action.GetSelfAndVisualDescendants().Any(visual =>
        visual.IsEffectivelyVisible && visual is Border { OpacityMask: ImageBrush } or Avalonia.Controls.Shapes.Path { Data: not null });

    private static bool LabelIsVisible(Button button) => button.GetVisualDescendants().OfType<TextBlock>()
        .Any(label => label.IsEffectivelyVisible && label.Text == button.Content as string);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class TestWindow : Window, IDisposable
    {
        internal TestWindow(Control page)
        {
            Width = 1600;
            Height = 900;
            Content = page;
            Show();
            Settle(this);
        }

        internal void ShowPage(Control page)
        {
            Content = page;
            Settle(this);
        }

        public void Dispose()
        {
            if (IsVisible) Close();
        }
    }
}
