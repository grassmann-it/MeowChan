using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Http;

namespace MeowChan.DesktopSmoke;

internal static class SourcesChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        // Dismiss earlier deliberate preference-failure fixtures before this page's visual checks.
        foreach (NotificationCard notification in window.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            notification.Close();
        await Program.WaitUntilAsync(() => !window.GetVisualDescendants().OfType<NotificationCard>()
            .Any(notification => notification.IsEffectivelyVisible));
        Config initial = Config.LoadConfig();
        double width = window.Width;
        double height = window.Height;
        ContentControl host = window.FindControl<ContentControl>("SourcesPageHost")!;
        object? previous = host.Content;
        if (previous is SourcesPage livePage)
        {
            Texts.SetLanguage("en");
            window.FindControl<MenuItem>("SourcesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            livePage.FindControl<Button>("SourceRulesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SelectedPage == MainWindow.WorkspacePage.Rules
                && window.RulesPage.SelectedScopeChoice?.Scope == MeowChan.Rules.MediaRuleScope.ForSource("4chan"),
                "The productive Sources page did not open the shared canonical source rule editor.");
            window.FindControl<MenuItem>("SourcesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            livePage.FindControl<TextBox>("SourceUrlInput")!.Text = "https://unknown.invalid/g/thread/123";
            livePage.FindControl<Button>("SourceAddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !livePage.IsBusy
                && !string.IsNullOrEmpty(livePage.FindControl<TextBlock>("SourceUrlResult")!.Text));
            TextBox report = window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<TextBox>().Single();
            Require(report.Text?.Contains(Texts.T("admission.unsupported")) == true
                && livePage.FindControl<TextBox>("SourceUrlInput")!.Text == "https://unknown.invalid/g/thread/123",
                "The productive source URL action did not use the shared admission/reporting path.");
            livePage.FindControl<TextBox>("SourceUrlInput")!.Text = string.Empty;
        }
        string? editedSource = null;
        string? submitted = null;
        int submissions = 0;
        SourcesPage page = new SourcesPage(_ => false, source => editedSource = source, () => { }, () => { }, input =>
        { submitted = input; submissions++; return Task.FromResult(false); });
        host.Content = page;
        DesktopThreadEntry thread = SnapshotFixtures.Thread(991, "123", "Source fixture") with
        {
            Snapshot = SnapshotFixtures.Thread(991, "123", "Source fixture").Snapshot with
            {
                LastCheckFailure = new FailureInfo(FailureKind.Http, "Fixture thread unavailable", System.Net.HttpStatusCode.NotFound),
                RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(25), 2)
            }
        };
        DesktopSessionView view = new DesktopSessionView([thread], [], [], DesktopSessionState.Ready, null);
        ProviderDescriptor[] providers =
        [
            new ProviderDescriptor("4chan", "4chan", new Uri("https://boards.4chan.org/"),
                ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog | ProviderCapabilities.PostCapture
                | ProviderCapabilities.OriginalMedia, Array.AsReadOnly(new[] { "boards.4chan.org", "boards.4channel.org" }), null, true),
            new ProviderDescriptor("thread-only", "Thread-only fixture", new Uri("https://fixture.invalid/"),
                ProviderCapabilities.ThreadWatch, Array.AsReadOnly(new[] { "fixture.invalid" }), "fixture-version", false)
        ];
        try
        {
            foreach (string mode in new[] { "Simple", "Barebones", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                window.Width = mode == "Barebones" ? 640 : 760;
                window.Height = mode == "Barebones" ? 480 : 560;
                window.FindControl<MenuItem>("SourcesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (mode == "Barebones")
                    await Program.WaitUntilAsync(() => page.Bounds.Width > 0 && page.Bounds.Width < 760
                        && page.FindControl<Grid>("SourcesSplit")!.ColumnDefinitions.Count == 1);
                foreach (string language in new[] { "en", "de" })
                {
                    Texts.SetLanguage(language);
                    page.Refresh(providers, view, true);
                    page.FindControl<TextBox>("SourceUrlInput")!.Text = "https://unknown.invalid/g/thread/123";
                    Button firstSource = (Button)page.FindControl<StackPanel>("SourceCards")!.Children[0];
                    Require(firstSource.Content is StackPanel && !firstSource.GetVisualDescendants().OfType<Button>().Any(),
                        "Source row still requires a separate Select button.");
                    if (page.Bounds.Width < 760)
                    {
                        if (page.FindControl<Border>("SourceDetailsPanel")!.IsVisible)
                            page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(page.FindControl<StackPanel>("SourcesListPane")!.IsVisible
                            && !page.FindControl<Border>("SourceDetailsPanel")!.IsVisible,
                            "Narrow Sources did not start with the source list.");
                        firstSource.Focus();
                        firstSource.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(page.FindControl<Border>("SourceDetailsPanel")!.IsVisible
                            && !page.FindControl<StackPanel>("SourcesListPane")!.IsVisible,
                            "Narrow row activation did not open details.");
                        page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Require(firstSource.IsFocused && page.FindControl<StackPanel>("SourcesListPane")!.IsVisible
                            && page.FindControl<TextBox>("SourceUrlInput")!.Text == "https://unknown.invalid/g/thread/123",
                            "Narrow Back did not restore source focus and URL draft.");
                        firstSource.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Require(window.SelectedPage == MainWindow.WorkspacePage.Sources, "Sources navigation did not retain its page.");
                    Require(page.FindControl<TextBlock>("SourceWatchSummary")!.Text!.Contains("1"), "Sources lost active watch counts.");
                    Require(page.FindControl<TextBlock>("SourceCaptureAvailability")!.Text == Texts.T("sources.captureAvailable"),
                        "Capture support and initialized session availability were not shown.");
                    Require(page.FindControl<Button>("SourceWebsiteButton")!.Content?.ToString()
                        == Texts.T("sources.website", "https://boards.4chan.org/"),
                        "Selected source website is not directly reachable.");
                    page.FindControl<ScrollViewer>("SourcesScroll")!.ScrollToHome();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Program.CaptureWindow(window, $"smoke-sources-{mode.ToLowerInvariant()}-{language}.png");
                    page.FindControl<Button>("SourceRulesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(editedSource == "4chan", "Source rules targeted a display label instead of canonical source identity.");
                    page.FindControl<Button>("SourceAddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(submitted == "https://unknown.invalid/g/thread/123"
                        && page.FindControl<TextBox>("SourceUrlInput")!.Text == submitted, "Admission did not preserve rejected URL text.");
                    page.Refresh(providers, view, true);
                    DataGrid watches = page.FindControl<DataGrid>("SourceWatchesGrid")!;
                    watches.BringIntoView();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    watches.SelectedItem = watches.ItemsSource!.Cast<SourceWatchRow>().Single();
                    await Program.WaitUntilAsync(() => page.FindControl<TextBox>("SourceWatchDetails")!.Text?.Contains("25") == true);
                    page.FindControl<Button>("SourceOpenWatchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!string.IsNullOrWhiteSpace(page.FindControl<TextBlock>("SourcesActionError")!.Text), "Stale watch action was silently accepted.");
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Button openWatch = page.FindControl<Button>("SourceOpenWatchButton")!;
                    openWatch.BringIntoView();
                    await Program.WaitUntilAsync(() => Fits(window, openWatch));
                    Program.CaptureWindow(window, $"smoke-sources-details-{mode.ToLowerInvariant()}-{language}.png");
                    page.Refresh(providers, view with { Threads = [] }, true);
                    Require(!page.FindControl<Button>("SourceOpenWatchButton")!.IsEnabled, "Removed watch left navigation enabled.");
                    Require(page.FindControl<TextBox>("SourceUrlInput")!.Text == submitted, "Refresh lost the retained URL draft.");
                }
            }
            Require(submissions == 6, "Opening or refreshing Sources caused unsolicited admissions.");
            window.Width = 1280;
            await Program.WaitUntilAsync(() => page.Bounds.Width >= 760
                && page.FindControl<Grid>("SourcesSplit")!.ColumnDefinitions.Count == 2);
            Button selectedSource = (Button)page.FindControl<StackPanel>("SourceCards")!.Children[1];
            selectedSource.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            selectedSource.Focus();
            page.Refresh(providers, view, true);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(selectedSource.IsFocused, "A passive Sources refresh replaced the focused source control.");
            Require(page.FindControl<TextBlock>("SourceHeading")!.Text == "Thread-only fixture"
                && page.FindControl<TextBlock>("SourceCaptureAvailability")!.Text == Texts.T("sources.captureUnavailable")
                && page.FindControl<TextBlock>("SourceVersion")!.Text!.Contains("fixture-version"),
                "Thread-only provider selection or optional descriptor details were lost after refresh.");
            await Program.WaitUntilAsync(() => window.FindControl<Button>("SourcesNavigationButton")!.IsVisible);
            window.FindControl<Button>("SourcesNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SelectedPage == MainWindow.WorkspacePage.Sources
                && window.FindControl<Button>("SourcesNavigationButton")!.Classes.Contains("selected"),
                "Wide Advanced navigation did not expose the productive Sources page.");
            page.Refresh([], view, true);
            Require(page.FindControl<TextBlock>("SourcesEmpty")!.IsVisible && !page.FindControl<Border>("SourceDetailsPanel")!.IsVisible,
                "Empty provider registry did not show its genuine empty state.");
        }
        finally
        {
            page.EndLifetime(); host.Content = previous;
            Texts.SetLanguage(initial.Language);
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            window.Width = width; window.Height = height;
            window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Console.WriteLine("SMOKE sources: passive capabilities, current cooldowns, exact watch actions, shared admission drafts and compact EN/DE views passed.");
    }
    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && point.HasValue && point.Value.X >= 0 && point.Value.Y >= 0
            && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1 && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
