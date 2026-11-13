using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Http;
using SourceMaturity = MeowChan.PluginSdk.SourceMaturity;

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
            MenuItem sourcesNavigation = window.FindControl<MenuItem>("SourcesMenuItem")!;
            await Program.WaitUntilAsync(() => sourcesNavigation.IsEnabled && !window.SessionRowsApplying);
            sourcesNavigation.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            // Navigation may defer the page refresh until the current row frame is complete.
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Sources
                && !window.SessionRowsApplying
                && !string.IsNullOrEmpty(livePage.FindControl<TextBlock>("SourceIdentity")!.Text));
            string? selectedIdentity = livePage.FindControl<TextBlock>("SourceIdentity")!.Text;
            ProviderDescriptor selectedSource = window.SessionHost.GetProviderDescriptors().SingleOrDefault(provider =>
                selectedIdentity == Texts.T("sources.identity", provider.SourceId, provider.BaseUri.AbsoluteUri))
                ?? throw new InvalidOperationException("The productive Sources selection did not identify a registered canonical source: " + selectedIdentity);
            Button selectedCard = livePage.FindControl<StackPanel>("SourceCards")!.Children.OfType<Button>()
                .Single(card => card.Classes.Contains("selected"));
            // Navigation refreshes the card's Content synchronously, before its
            // button template necessarily materializes a visual ContentPresenter.
            StackPanel selectedLabels = selectedCard.Content as StackPanel
                ?? throw new InvalidOperationException("The selected source card lost its label content.");
            string[] selectedText = selectedLabels.Children.OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();
            Console.WriteLine("SMOKE productive source card: source=" + selectedSource.SourceId
                + "; labels=" + string.Join(" | ", selectedText.Take(2))
                + "; visual-text-count=" + selectedCard.GetVisualDescendants().OfType<TextBlock>().Count() + ".");
            // Since b38a8b7 the card name carries the source-wording tier (e.g. "MeowChan · experimental"),
            // whose exact bundled wording is pinned by BundledSourceWordingEvidenceTests.
            SourceRow selectedRow = SourcesSnapshot.From(window.SessionHost.GetProviderDescriptors(), window.SessionHost.GetView(),
                window.SessionHost.GetSourceMediaIntegrityPolicies()).Single(row => row.Id == selectedSource.SourceId);
            string selectedName = selectedSource.DisplayName
                + (selectedRow.Classification is { } tier ? " · " + MeowChan.Desktop.Plugins.SourceWording.DescribeLabels(tier) : string.Empty);
            Require(selectedText.Length >= 2 && selectedText[0] == "● " + selectedName
                && selectedText[1] == selectedSource.BaseUri.AbsoluteUri,
                "The productive Sources details disagreed with the selected source card.");
            livePage.FindControl<Button>("SourceRulesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine("SMOKE productive source rules: selected=" + selectedSource.SourceId
                + "; actual=" + window.RulesPage.SelectedScopeChoice?.Scope + ".");
            Require(window.SelectedPage == MainWindow.WorkspacePage.Rules
                && window.RulesPage.SelectedScopeChoice?.Scope == MeowChan.Rules.MediaRuleScope.ForSource(selectedSource.SourceId),
                "The productive Sources page did not open the shared canonical source rule editor.");
            window.FindControl<MenuItem>("SourcesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            // The page no longer admits anything itself: its button goes to Downloads and focuses the URL field there,
            // and the shared admission/reporting path runs from that field exactly as for any pasted link.
            Button handoff = livePage.FindControl<Button>("SourceAddToDownloadsButton")!;
            Require(handoff.IsEffectivelyEnabled, "The productive Sources page did not offer its add-in-Downloads button.");
            handoff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            TextBox downloadsInput = window.FindControl<TextBox>("UrlInput")!;
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads && downloadsInput.IsFocused);
            Require(string.IsNullOrEmpty(downloadsInput.Text),
                "The Sources hand-off prefilled the Downloads field; a base URL would not be a valid thread link.");
            downloadsInput.Text = "https://unknown.invalid/g/thread/123";
            window.FindControl<Button>("AddUrlButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<TextBox>().Any());
            TextBox report = window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<TextBox>().Single();
            Require(report.Text?.Contains(Texts.T("admission.unsupported")) == true
                && downloadsInput.Text == "https://unknown.invalid/g/thread/123",
                "The Downloads field did not use the shared admission/reporting path for a link entered after the Sources hand-off.");
            downloadsInput.Text = string.Empty;
        }
        string? editedSource = null;
        int handoffs = 0, showAll = 0;
        SourcesPage page = new SourcesPage(_ => false, source => editedSource = source, () => { }, () => handoffs++, () => showAll++);
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
                await Program.WaitUntilAsync(() => page.Bounds.Width > 0 && page.Bounds.Width <= 760
                        && page.FindControl<Grid>("SourcesSplit")!.ColumnDefinitions[0].ActualWidth < 1
                        && page.FindControl<Grid>("SourcesSplit")!.ColumnSpacing == 0);
                foreach (string language in new[] { "en", "de" })
                {
                    Texts.SetLanguage(language);
                    page.Refresh(providers, view, true);
                    Button firstSource = (Button)page.FindControl<StackPanel>("SourceCards")!.Children[0];
                    Require(firstSource.Content is StackPanel && !firstSource.GetVisualDescendants().OfType<Button>().Any(),
                        "Source row still requires a separate Select button.");
                    if (page.FindControl<Button>("SourceBackButton")!.IsVisible)
                    {
                        if (page.FindControl<Border>("SourceDetailsPanel")!.IsVisible)
                            page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(page.FindControl<StackPanel>("SourcesListPane")!.IsVisible
                            && !page.FindControl<Border>("SourceDetailsPanel")!.IsVisible,
                            "Narrow Sources did not start with the source list.");
                        firstSource.Focus();
                        firstSource.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Program.WaitUntilAsync(() => page.FindControl<Button>("SourceBackButton")!.IsFocused);
                        Require(page.FindControl<Border>("SourceDetailsPanel")!.IsVisible
                            && !page.FindControl<StackPanel>("SourcesListPane")!.IsVisible,
                            "Narrow row activation did not open details.");
                        page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Require(firstSource.IsFocused && page.FindControl<StackPanel>("SourcesListPane")!.IsVisible,
                            "Narrow Back did not restore source focus.");
                        firstSource.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Require(window.SelectedPage == MainWindow.WorkspacePage.Sources, "Sources navigation did not retain its page.");
                    Require(page.FindControl<TextBlock>("SourceWatchSummary")!.Text!.Contains("1"), "Sources lost active watch counts.");
                    Require(page.FindControl<TextBlock>("SourceCaptureAvailability")!.Text == Texts.T("sources.captureAvailable"),
                        "Capture support and initialized session availability were not shown.");
                    RequireIconLabel(page.FindControl<Button>("SourceBackButton")!, Texts.T("nav.sources"));
                    RequireIconLabel(page.FindControl<Button>("SourceWebsiteButton")!,
                        Texts.T("sources.website", "https://boards.4chan.org/"));
                    RequireIconLabel(page.FindControl<Button>("SourceRulesButton")!, Texts.T("sources.rules"));
                    RequireIconLabel(page.FindControl<Button>("SourceDiagnosticsButton")!, Texts.T("sources.diagnostics"));
                    RequireIconLabel(page.FindControl<Button>("SourceAddToDownloadsButton")!, Texts.T("sources.addInDownloads"));
                    RequireIconLabel(page.FindControl<Button>("SourceOpenWatchButton")!, Texts.T("diagnostics.openWatch"));
                    page.FindControl<ScrollViewer>("SourcesScroll")!.ScrollToHome();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Program.CaptureWindow(window, $"smoke-sources-{mode.ToLowerInvariant()}-{language}.png");
                    if (mode == "Simple") await CaptureBothThemesAsync(window, $"smoke-sources-{mode.ToLowerInvariant()}-{language}");
                    page.FindControl<Button>("SourceRulesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(editedSource == "4chan", "Source rules targeted a display label instead of canonical source identity.");
                    int handoffsBefore = handoffs;
                    page.FindControl<Button>("SourceAddToDownloadsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(handoffs == handoffsBefore + 1, "The add-in-Downloads button did not hand over to Downloads exactly once.");
                    page.Refresh(providers, view, true);
                    ListBox watches = page.FindControl<ListBox>("SourceWatchesList")!;
                    watches.BringIntoView();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    SourceWatchRow watchRow = watches.ItemsSource!.Cast<SourceWatchRow>().Single();
                    watches.SelectedItem = watchRow;
                    // One watch is two lines: its URL, then the status with the current request pause. No grid, no second copy.
                    await Program.WaitUntilAsync(() => watches.GetVisualDescendants().OfType<TextBlock>().Count() == 2);
                    string?[] watchLines = watches.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                    Require(watchLines[0] == watchRow.Resource && watchLines[1] == watchRow.Status && watchLines[1]!.Contains("25")
                        && !page.GetVisualDescendants().OfType<DataGrid>().Any(),
                        "A watch was not shown as its URL line plus one status line.");
                    Require(!page.FindControl<Button>("SourceAllWatchesButton")!.IsVisible,
                        "A single watch offered a \"show all\" link although nothing is beyond the preview.");
                    page.FindControl<Button>("SourceOpenWatchButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(!string.IsNullOrWhiteSpace(page.FindControl<TextBlock>("SourcesActionError")!.Text), "Stale watch action was silently accepted.");
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Button openWatch = page.FindControl<Button>("SourceOpenWatchButton")!;
                    openWatch.BringIntoView();
                    await Program.WaitUntilAsync(() => Fits(window, openWatch));
                    Program.CaptureWindow(window, $"smoke-sources-details-{mode.ToLowerInvariant()}-{language}.png");
                    if (mode == "Simple") await CaptureBothThemesAsync(window, $"smoke-sources-details-{mode.ToLowerInvariant()}-{language}");
                    page.Refresh(providers, view with { Threads = [] }, true);
                    Require(!page.FindControl<Button>("SourceOpenWatchButton")!.IsEnabled, "Removed watch left navigation enabled.");
                }
            }
            Require(handoffs == 6, "Opening or refreshing Sources navigated to Downloads without a click on its button.");
            await CheckNavigationAsync(window, wide: false);
            // Native Windows desktops can be narrower than the requested 1280 DIP.
            // Exercise wide navigation only where the actual working area permits it.
            Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                ?? throw new InvalidOperationException("Sources navigation has no screen.");
            double frameWidth = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
            double targetWidth = Math.Min(1280, Math.Floor(screen.WorkingArea.Width / window.RenderScaling - frameWidth));
            window.Width = targetWidth;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - targetWidth) <= 1);
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
            await CaptureWideAsync(window, page, providers, view, targetWidth);
            if (page.Bounds.Width >= 886)
            {
                // Opening details retained _showDetails; shrinking must move focus out of the hidden list.
                selectedSource.Focus();
                window.Width = 760;
                await Program.WaitUntilAsync(() => page.FindControl<Button>("SourceBackButton")!.IsFocused);
                window.Width = targetWidth;
                await Program.WaitUntilAsync(() => page.FindControl<Button>("SourceWebsiteButton")!.IsFocused);
                window.Width = 760;
                await Program.WaitUntilAsync(() => page.FindControl<Button>("SourceBackButton")!.IsVisible);
                page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = targetWidth;
                await Program.WaitUntilAsync(() => page.FindControl<Border>("SourceDetailsPanel")!.IsEffectivelyVisible);
                Button handoffButton = page.FindControl<Button>("SourceAddToDownloadsButton")!;
                handoffButton.Focus();
                window.Width = 760;
                await Program.WaitUntilAsync(() => selectedSource.IsFocused && page.FindControl<StackPanel>("SourcesListPane")!.IsVisible);
                window.Width = targetWidth;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - targetWidth) <= 1);
            }
            await CheckNavigationAsync(window, wide: targetWidth >= 1120);
            await CheckWatchPreviewAsync(page, providers, view, () => showAll);
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
        Console.WriteLine("SMOKE sources: passive capabilities, current cooldowns, exact watch actions, the Downloads hand-off and compact EN/DE views passed.");
    }
    // Light and dark captures of the same state; the page itself has no theme-specific logic, so this is evidence only.
    private static async Task CaptureBothThemesAsync(MainWindow window, string name)
    {
        ThemeVariant? original = Application.Current!.RequestedThemeVariant;
        try
        {
            foreach ((ThemeVariant variant, string suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                Application.Current.RequestedThemeVariant = variant;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Program.WaitUntilAsync(() => window.ActualThemeVariant == variant);
                Program.CaptureWindow(window, $"{name}-{suffix}.png");
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = original;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    // The wide two-pane layout (1440 x 920 where the screen allows it) in both themes and both languages, then the former size again.
    private static async Task CaptureWideAsync(MainWindow window, SourcesPage page, ProviderDescriptor[] providers,
        DesktopSessionView view, double restoreWidth)
    {
        double restoreHeight = window.Height;
        string language = Texts.Language;
        try
        {
            window.Width = 1440; window.Height = 920;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 1440) <= 1 && Math.Abs(window.Bounds.Height - 920) <= 1);
            foreach (string wideLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(wideLanguage);
                page.Refresh(providers, view, true);
                ((Button)page.FindControl<StackPanel>("SourceCards")!.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                page.FindControl<ScrollViewer>("SourcesScroll")!.ScrollToHome();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await CaptureBothThemesAsync(window, $"smoke-sources-wide-{wideLanguage}");
            }
        }
        finally
        {
            Texts.SetLanguage(language);
            window.Width = restoreWidth; window.Height = restoreHeight;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - restoreWidth) <= 1);
            page.Refresh(providers, view, true);
            page.SelectSource("thread-only");
        }
    }
    // The preview length is the "Sources page: watch rows per source" setting: three rows minimum, the rest one link away under Downloads.
    private static async Task CheckWatchPreviewAsync(SourcesPage page, ProviderDescriptor[] providers, DesktopSessionView view, Func<int> showAllClicks)
    {
        DesktopThreadEntry[] threads = Enumerable.Range(0, 5)
            .Select(index => SnapshotFixtures.Thread(1100 + index, (200 + index).ToString(), "Preview fixture " + index)).ToArray();
        DesktopSessionView many = view with { Threads = threads };
        Func<int>? original = page.GetWatchPreviewRows;
        ListBox list = page.FindControl<ListBox>("SourceWatchesList")!;
        Button all = page.FindControl<Button>("SourceAllWatchesButton")!;
        try
        {
            page.SelectSource("4chan");
            foreach ((int setting, int shown, bool link) in new[] { (3, 3, true), (4, 4, true), (5, 5, false), (200, 5, false), (1, 3, true) })
            {
                page.GetWatchPreviewRows = () => setting;
                page.Refresh(providers, many, true);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(list.ItemsSource!.Cast<SourceWatchRow>().Count() == shown && all.IsVisible == link,
                    $"Watch preview setting {setting} showed {list.ItemsSource!.Cast<SourceWatchRow>().Count()} rows (expected {shown}, link {link}).");
                if (link) Require((all.Content as string) == Texts.T("sources.showAll", 5),
                    "The show-all link did not name the number of watches: " + all.Content);
            }
            page.GetWatchPreviewRows = () => 3;
            page.Refresh(providers, many, true);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            int before = showAllClicks();
            all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(showAllClicks() == before + 1, "The show-all link did not hand over to Downloads exactly once.");
        }
        finally
        {
            page.GetWatchPreviewRows = original;
            page.Refresh(providers, view, true);
        }
    }
    private static async Task CheckNavigationAsync(MainWindow window, bool wide)
    {
        Button sources = window.FindControl<Button>("SourcesNavigationButton")!;
        Button tools = window.FindControl<Button>("ToolsNavigationButton")!;
        Require(window.SessionHost.Config.WorkspaceMode == "Advanced" && window.Bounds.Width > 0
            && (window.Bounds.Width >= 1120) == wide,
            $"Sources navigation did not reach the requested Advanced geometry: wide={wide}; mode={window.SessionHost.Config.WorkspaceMode}; width={window.Bounds.Width}.");
        window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads, "Sources navigation fixture did not leave the Sources page.");
        // Label measurement decides which optional destinations fit. The same
        // destination must have exactly one footer route at either width.
        MenuFlyout flyout = tools.Flyout as MenuFlyout
            ?? throw new InvalidOperationException("Tools has no productive menu.");
        MenuItem item = flyout.Items.OfType<MenuItem>().Single(item => item.Tag as string == "nav.sources");
        Require(item.IsVisible == !sources.IsVisible, "Sources is duplicated or absent between the footer and Tools.");
        bool direct = sources.IsVisible;
        if (direct)
        {
            Require(Fits(window, sources) && sources.IsEffectivelyEnabled, "Wide Sources navigation is outside the usable window.");
            sources.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else
        {
            Require(Fits(window, tools) && tools.IsEffectivelyEnabled, "Tools navigation is outside the usable window or disabled.");
            flyout.ShowAt(tools);
            await Program.WaitUntilAsync(() => flyout.IsOpen && item.IsEffectivelyVisible && item.Bounds.Width > 0);
            Require(item.IsEffectivelyEnabled, "Compact Sources menu action is disabled.");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => !flyout.IsOpen);
        }
        Require(window.SelectedPage == MainWindow.WorkspacePage.Sources && sources.Classes.Contains("selected")
            && tools.Classes.Contains("selected") == !direct,
            "The available Advanced navigation did not open and select the productive Sources page.");
        Console.WriteLine($"SMOKE sources navigation: {(direct ? "direct button" : "Tools menu")}; actual-width={window.Bounds.Width}; working-area={window.Screens.ScreenFromWindow(window)?.WorkingArea}.");
    }
    private static void RequireIconLabel(Button button, string expected)
    {
        Require(button.Content is StackPanel panel && panel.Children.Count == 2
            && panel.Children[0] is Control
            && panel.Children[1] is TextBlock label && label.Text == expected,
            $"{button.Name} lost its icon or translated action label.");
    }

    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && point.HasValue && point.Value.X >= 0 && point.Value.Y >= 0
            && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1 && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
