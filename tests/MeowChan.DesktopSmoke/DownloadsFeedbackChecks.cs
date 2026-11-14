using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Rendered undo toast (action button beside the close X), the pause entry of the tray when this run has a
/// tray, the "show in list" link of an already watched address and the plain-language retry reason.
/// Fake action only: nothing is removed, admitted or downloaded.
/// </summary>
internal static class DownloadsFeedbackChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(MainWindow window)
    {
        Config config = window.SessionHost.Config;
        bool information = config.ShowInformationNotifications;
        string language = Texts.Language;
        try
        {
            config.ShowInformationNotifications = true;
            foreach (string current in new[] { "en", "de" })
            {
                Texts.SetLanguage(current);
                await CheckActionToastAsync(window, current);
            }
            await CheckUpdateToastAsync(window);
            CheckTrayPauseEntry(window);
            await CheckDuplicateLinkAsync(window);
            await CheckArchiveBridgeHintAsync(window);
            await CheckAdmissionProblemToolsAsync(window);
        }
        finally
        {
            config.ShowInformationNotifications = information;
            Texts.SetLanguage(language);
        }
        Console.WriteLine("SMOKE downloads feedback: undo toast with action, tray pause entry, show-in-list link, one-time Archive hint, empty toolbar and the problems-only filter with put-back passed.");
    }

    private static NotificationCard[] Cards(MainWindow window) => window.GetVisualDescendants().OfType<NotificationCard>().ToArray();

    private static async Task CheckActionToastAsync(MainWindow window, string language)
    {
        int runs = 0;
        MethodInfo show = typeof(MainWindow).GetMethod("ShowActionNotification", Instance)!;
        string label = Texts.T("watch.undo");
        show.Invoke(window, new object?[] { Texts.T("notification.session"), Texts.T("watch.removed", 2), NotificationType.Information,
            new[] { new FeedbackToastAction(label, () => { runs++; return Task.CompletedTask; }) }, null, "smoke.watch-removed-" + language });
        FeedbackToastContent? toast = null;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            toast = Cards(window).Select(card => card.Content).OfType<FeedbackToastContent>().FirstOrDefault(content => content.ActionButton != null);
            return toast?.ActionButton is { IsEffectivelyVisible: true, Bounds.Width: > 0 } && toast.DismissButton.IsEffectivelyVisible;
        });
        Button action = toast!.ActionButton!;
        Require(action.Bounds.Height >= 28 && (string)action.Content! == label && AutomationProperties.GetName(action) == label,
            "The undo toast action has no visible, named button in " + language + ".");
        Require(AutomationProperties.GetName(toast) == FeedbackToastContent.AccessibleName(toast.Title, toast.Message),
            "The toast with an action lost its accessible name.");
        Require(toast.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text =>
            AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite), "The toast message lost its live region.");
        Program.CaptureWindow(window, "smoke-downloads-undo-toast-" + language + ".png");
        SmokeActionClick.Execute(action);
        await Program.WaitUntilAsync(() => !Cards(window).Any(card => ReferenceEquals(card.Content, toast)));
        Require(runs == 1, "The toast action ran " + runs + " times.");
    }

    // Fake release only. "Release notes" is not clicked: it would start the real browser.
    private static async Task CheckUpdateToastAsync(MainWindow window)
    {
        MeowChan.Desktop.Updates.UpdateInfo update = new(MeowChan.Desktop.Updates.SemanticVersion.Parse("99.0.0"),
            new Uri("https://fixture.invalid/releases/v99.0.0"), new Uri("https://fixture.invalid/releases/v99.0.0"));
        foreach (string current in new[] { "en", "de" })
        {
            Texts.SetLanguage(current);
            typeof(MainWindow).GetMethod("ShowUpdateAvailableToast", Instance)!.Invoke(window, new object?[] { update });
            FeedbackToastContent? toast = null;
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                toast = Cards(window).Select(card => card.Content).OfType<FeedbackToastContent>().FirstOrDefault(content => content.ActionButtons.Count == 3);
                return toast != null && toast.ActionButtons.All(button => button.IsEffectivelyVisible && button.Bounds.Width > 0);
            });
            Require(toast!.Title == Texts.T("updates.toastTitle", update.Version)
                && toast.ActionButtons.Select(button => (string)button.Content!).SequenceEqual(
                    new[] { Texts.T("updates.toastNotes"), Texts.T("updates.toastLater"), Texts.T("updates.skip") }),
                "The update toast lost its title or actions in " + current + ".");
            Program.CaptureWindow(window, "smoke-update-toast-" + current + ".png");
            SmokeActionClick.Execute(toast.ActionButtons[1]);
            await Program.WaitUntilAsync(() => !Cards(window).Any(card => ReferenceEquals(card.Content, toast)));
        }
        Avalonia.Controls.NativeMenuItem? item = (Avalonia.Controls.NativeMenuItem?)typeof(MainWindow).GetField("_trayUpdateItem", Instance)!.GetValue(window);
        if (typeof(MainWindow).GetField("_trayIcon", Instance)!.GetValue(window) != null)
            Require(item is { IsVisible: false } || window.SessionHost.Updates.View.Update != null, "The tray update entry shows without an update.");
    }

    private static void CheckTrayPauseEntry(MainWindow window)
    {
        // A run without a native tray (no host, no-tray mode) has no entry to inspect.
        if (typeof(MainWindow).GetField("_trayIcon", Instance)!.GetValue(window) == null) return;
        Avalonia.Controls.NativeMenuItem? pause = (Avalonia.Controls.NativeMenuItem?)typeof(MainWindow).GetField("_trayPauseItem", Instance)!.GetValue(window);
        Require(pause != null, "The tray menu has no pause entry.");
        bool paused = window.SessionHost.Projection.IsPaused;
        Require(pause!.Header is string header && header.StartsWith(Texts.T(paused ? "action.resume" : "action.pause"), StringComparison.Ordinal),
            "The tray pause entry does not name the action it will perform.");
    }

    private static async Task CheckDuplicateLinkAsync(MainWindow window)
    {
        Type feedbackType = typeof(MainWindow).GetNestedType("AdmissionFeedback", BindingFlags.NonPublic)!;
        System.Collections.IList results = (System.Collections.IList)typeof(MainWindow).GetField("_admissionResults", Instance)!.GetValue(window)!;
        MethodInfo refresh = typeof(MainWindow).GetMethod("RefreshAdmissionResults", Instance)!;
        DesktopThreadEntry entry = SnapshotFixtures.Thread(98951, "98951", "Duplicate fixture");
        await using SyntheticShellRows rows = await SyntheticShellRows.CreateAsync(window, new[] { entry });
        results.Clear();
        results.Add(Activator.CreateInstance(feedbackType, entry.Snapshot.Url, MeowChan.AdmissionStatus.AlreadyPresent, null, false, (long?)entry.WatchId));
        try
        {
            refresh.Invoke(window, null);
            Button link = window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<Button>()
                .Single(button => button.Name == "AdmissionShowInListButton");
            Require(AutomationProperties.GetName(link) == Texts.T("admission.showInList") + ": " + entry.Snapshot.Url,
                "The show-in-list link has no accessible name with its address.");
            SmokeActionClick.Execute(link);
            DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
            // Either the row was selected, or the link said why not; it never does nothing.
            await Program.WaitUntilAsync(() => ReferenceEquals(grid.SelectedItem, rows.Threads[0])
                || Cards(window).Any(card => card.Content is FeedbackToastContent toast
                    && (toast.Message == Texts.T("diagnostics.removed") || toast.Message == Texts.T("admission.showInListHidden"))));
        }
        finally
        {
            results.Clear();
            refresh.Invoke(window, null);
        }
    }

    /// <summary>
    /// UX flows AP16: a large Add with rejected URLs offers the counters, a "Problems only" filter for the report and an
    /// action that puts the rejected URLs back into the Add field. Fake admission results only: nothing is admitted.
    /// </summary>
    private static async Task CheckAdmissionProblemToolsAsync(MainWindow window)
    {
        Type feedbackType = typeof(MainWindow).GetNestedType("AdmissionFeedback", BindingFlags.NonPublic)!;
        System.Collections.IList results = (System.Collections.IList)typeof(MainWindow).GetField("_admissionResults", Instance)!.GetValue(window)!;
        MethodInfo refresh = typeof(MainWindow).GetMethod("RefreshAdmissionResults", Instance)!;
        StackPanel tools = window.AdmissionProblemToolsForTests ?? throw new InvalidOperationException("The problem tools were not built.");
        TextBox field = window.FindControl<TextBox>("UrlInput")!;
        string fieldText = field.Text ?? string.Empty;
        string language = Texts.Language;
        void Add(string url, MeowChan.AdmissionStatus status) =>
            results.Add(Activator.CreateInstance(feedbackType, url, status, null, false, (long?)null));
        TextBox Report() => window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<TextBox>().Single(box => box.Name == "AdmissionReport");
        try
        {
            foreach (string current in new[] { "en", "de" })
            {
                Texts.SetLanguage(current);
                results.Clear();
                field.Text = string.Empty;
                for (int index = 1; index <= 6; index++) Add("https://boards.4chan.org/g/thread/" + index, MeowChan.AdmissionStatus.Added);
                Add("not a url", MeowChan.AdmissionStatus.Invalid);
                Add("https://example.invalid/x", MeowChan.AdmissionStatus.Unsupported);
                refresh.Invoke(window, null);
                CheckBox problemsOnly = tools.Children.OfType<CheckBox>().Single();
                Button putBack = tools.Children.OfType<Button>().Single();
                Require(tools.IsVisible && problemsOnly.IsVisible, "The problem tools are hidden although URLs were rejected.");
                Require(Equals(problemsOnly.Content, Texts.T("admission.problemsOnly", 2)) && Equals(putBack.Content, Texts.T("admission.putBack")),
                    "The problem tools do not show their " + current + " labels.");
                Require(window.FindControl<TextBlock>("AdmissionResultsTitle")!.Text == Texts.T("admission.summary", 6, 0, 2), "The result counters changed.");
                Require(Report().Text!.Split('\n').Length == 8, "The unfiltered report does not list all eight results.");
                problemsOnly.IsChecked = true;
                Require(Report().Text!.Split('\n').Length == 2 && window.FindControl<Expander>("AdmissionResultsExpander")!.IsExpanded,
                    "The filter does not reduce the visible report to the two rejected URLs.");
                SmokeActionClick.Execute(putBack);
                Require(field.Text == "not a url" + Environment.NewLine + "https://example.invalid/x", "The rejected URLs were not put back into the Add field.");
                // Nothing rejected: the tools disappear again.
                results.Clear();
                for (int index = 1; index <= 3; index++) Add("https://boards.4chan.org/g/thread/" + index, MeowChan.AdmissionStatus.Added);
                refresh.Invoke(window, null);
                Require(!tools.IsVisible, "The problem tools stay visible without a rejected URL.");
            }
            await Task.CompletedTask;
        }
        finally
        {
            results.Clear();
            field.Text = fieldText;
            window.FindControl<Expander>("AdmissionResultsExpander")!.IsExpanded = false;
            refresh.Invoke(window, null);
            Texts.SetLanguage(language);
        }
    }

    /// <summary>
    /// UX flows AP1: an empty Downloads page offers no list tools; the first successful Add shows one line that names the
    /// Archive (with its shortcut) and offers "Open in Archive"; answering it (or using the action) stores that for good.
    /// Fake admission results only: nothing is admitted or downloaded.
    /// </summary>
    private static async Task CheckArchiveBridgeHintAsync(MainWindow window)
    {
        Type feedbackType = typeof(MainWindow).GetNestedType("AdmissionFeedback", BindingFlags.NonPublic)!;
        System.Collections.IList results = (System.Collections.IList)typeof(MainWindow).GetField("_admissionResults", Instance)!.GetValue(window)!;
        MethodInfo refresh = typeof(MainWindow).GetMethod("RefreshAdmissionResults", Instance)!;
        MethodInfo refreshSearch = typeof(MainWindow).GetMethod("RefreshDownloadSearchPresentation", Instance)!;
        MethodInfo noteStart = typeof(MainWindow).GetMethod("NoteAdmissionStart", Instance)!;
        MethodInfo navigate = typeof(MainWindow).GetMethod("NavigateWorkspace", Instance)!;
        FieldInfo page = typeof(MainWindow).GetField("_selectedPage", Instance)!;
        Config config = window.SessionHost.Config;
        bool seen = config.ArchiveBridgeHintSeen;
        string language = Texts.Language;
        Border hint = window.FindControl<Border>("ArchiveBridgeHint") ?? throw new InvalidOperationException("The Archive hint was not built.");
        try
        {
            Texts.SetLanguage("en");
            config.ArchiveBridgeHintSeen = false;
            // Without a thread or board the list tools have nothing to act on; they are hidden, not just disabled.
            refreshSearch.Invoke(window, null);
            if (window.FindControl<DataGrid>("ThreadsDataGrid")!.ItemsSource is System.Collections.ICollection { Count: 0 })
            {
                Require(!window.FindControl<Button>("CopyAllUrlsButton")!.IsVisible && !window.FindControl<Button>("DownloadsColumnsButton")!.IsVisible,
                    "An empty Downloads page still shows its list tools.");
                Require(window.FindControl<TextBlock>("ThreadsEmptyState")!.Text == Texts.T("downloads.empty"), "The empty guide changed text.");
            }
            noteStart.Invoke(window, null);
            DesktopThreadEntry entry = SnapshotFixtures.Thread(98961, "98961", "Hint fixture");
            results.Clear();
            results.Add(Activator.CreateInstance(feedbackType, entry.Snapshot.Url, MeowChan.AdmissionStatus.Added, null, false, (long?)null));
            refresh.Invoke(window, null);
            bool startedEmpty = (bool)typeof(MainWindow).GetField("_admissionStartedEmpty", Instance)!.GetValue(window)!;
            if (startedEmpty)
            {
                Require(hint.IsVisible, "The first successful Add did not show the Archive hint.");
                string text = window.FindControl<TextBlock>("ArchiveBridgeHintText")!.Text ?? string.Empty;
                Require(text.Contains(CommandCatalog.Label("navigation.archive"), StringComparison.Ordinal), "The hint does not name the Archive: " + text);
                Require(window.FindControl<Button>("ArchiveBridgeHintOpen")!.Content as string == Texts.T("downloads.archiveHint.open"), "The hint action is not 'Open in Archive'.");
                Texts.SetLanguage("de");
                await Task.Yield();
                Require((window.FindControl<TextBlock>("ArchiveBridgeHintText")!.Text ?? string.Empty).Contains("Archiv", StringComparison.Ordinal),
                    "The hint did not follow the language switch.");
                Texts.SetLanguage("en");
                SmokeActionClick.Execute(window.FindControl<Button>("ArchiveBridgeHintOpen")!);
                await Program.WaitUntilAsync(() => page.GetValue(window)?.ToString() == "Archive");
                Require(!hint.IsVisible && config.ArchiveBridgeHintSeen, "The hint stays after its action was used.");
                navigate.Invoke(window, new object?[] { Enum.Parse(page.FieldType, "Downloads"), true });
                // Once answered it never returns, whatever the next Add reports.
                noteStart.Invoke(window, null);
                refresh.Invoke(window, null);
                Require(!hint.IsVisible, "The hint came back after it was answered.");
            }
        }
        finally
        {
            results.Clear();
            refresh.Invoke(window, null);
            Texts.SetLanguage(language);
            config.ArchiveBridgeHintSeen = seen;
            refreshSearch.Invoke(window, null);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
