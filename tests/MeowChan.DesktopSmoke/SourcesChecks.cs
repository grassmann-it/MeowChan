using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Http;
using Ursa.Controls;
using SourceMaturity = MeowChan.PluginSdk.SourceMaturity;
using SourceWording = MeowChan.Desktop.Plugins.SourceWording;
using SourceWordingMaintenance = MeowChan.Desktop.Plugins.SourceWordingMaintenance;
using SourceWordingMaturity = MeowChan.Desktop.Plugins.SourceWordingMaturity;
using SourceWordingClassification = MeowChan.Desktop.Plugins.SourceWordingClassification;

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
            await CheckMergedCommandRoutesAsync(window, livePage);
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
            SelectionListItem selectedCard = livePage.FindControl<SelectionList>("SourceCards")!.Items.OfType<SelectionListItem>()
                .Single(card => card.Classes.Contains("selected"));
            // Navigation refreshes the card's Content synchronously, before its
            // row template necessarily materializes a visual ContentPresenter.
            StackPanel selectedLabels = selectedCard.Content as StackPanel
                ?? throw new InvalidOperationException("The selected source card lost its label content.");
            string[] selectedText = selectedLabels.Children.OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();
            Console.WriteLine("SMOKE productive source card: source=" + selectedSource.SourceId
                + "; labels=" + string.Join(" | ", selectedText.Take(2))
                + "; visual-text-count=" + selectedCard.GetVisualDescendants().OfType<TextBlock>().Count() + ".");
            // The card name is the plain source name; maintenance, maturity and status are chips, and only where the source deviates.
            SourceRow selectedRow = SourcesSnapshot.From(window.SessionHost.GetProviderDescriptors(), window.SessionHost.GetView(),
                window.SessionHost.GetSourceMediaIntegrityPolicies()).Single(row => row.Id == selectedSource.SourceId);
            string[] expectedChips = selectedRow.Badges.Where(badge => badge.Deviates).Select(badge => badge.ChipText).ToArray();
            string[] shownChips = selectedLabels.Children.OfType<WrapPanel>().Single().Children.OfType<Border>()
                .Select(chip => chip.GetLogicalDescendants().OfType<TextBlock>().Single().Text ?? string.Empty).ToArray();
            Require(selectedText.Length >= 2 && selectedText[0] == selectedSource.DisplayName
                && selectedText[1] == selectedSource.BaseUri.AbsoluteUri && shownChips.SequenceEqual(expectedChips),
                "The productive Sources details disagreed with the selected source card: " + string.Join(" | ", selectedText.Take(2))
                + " / chips " + string.Join(", ", shownChips) + " (expected " + string.Join(", ", expectedChips) + ").");
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
            // An earlier scenario may have left its report in the rows; wait for the one of this link.
            await Program.WaitUntilAsync(() => window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<TextBox>()
                .Any(row => row.Text?.Contains(Texts.T("admission.unsupported")) == true));
            TextBox report = window.FindControl<StackPanel>("AdmissionResultRows")!.Children.OfType<TextBox>()
                .Single(row => row.Text?.Contains(Texts.T("admission.unsupported")) == true);
            Require(report.Text?.Contains(Texts.T("admission.unsupported")) == true
                && downloadsInput.Text == "https://unknown.invalid/g/thread/123",
                "The Downloads field did not use the shared admission/reporting path for a link entered after the Sources hand-off: report=\"" + report.Text + "\" input=\"" + downloadsInput.Text + "\".");
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
                    await CheckSourceListKeyboardAsync(page);
                    SelectionListItem firstSource = (SelectionListItem)page.FindControl<SelectionList>("SourceCards")!.Items[0]!;
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
                        MergedSourcesSmoke.OpenSourceRow(page, firstSource);
                        await Program.WaitUntilAsync(() => page.FindControl<TextBlock>("SourceHeading")!.IsFocused);
                        Require(page.FindControl<Border>("SourceDetailsPanel")!.IsVisible
                            && !page.FindControl<StackPanel>("SourcesListPane")!.IsVisible,
                            "Narrow row activation did not open details.");
                        page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Require(firstSource.IsFocused && page.FindControl<StackPanel>("SourcesListPane")!.IsVisible,
                            "Narrow Back did not restore source focus.");
                        MergedSourcesSmoke.OpenSourceRow(page, firstSource);
                    }
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Require(window.SelectedPage == MainWindow.WorkspacePage.Sources, "Sources navigation did not retain its page.");
                    Require(page.FindControl<TextBlock>("SourceWatchSummary")!.Text!.Contains("1"), "Sources lost active watch counts.");
                    Require(page.FindControl<TextBlock>("SourceCaptureAvailability")!.Text == Texts.T("sources.captureAvailable"),
                        "Capture support and initialized session availability were not shown.");
                    RequireIconLabel(page.FindControl<Button>("SourceBackButton")!, MeowChan.Desktop.Commands.CommandCatalog.Label("sources.back"));
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
            SelectionListItem selectedSource = (SelectionListItem)page.FindControl<SelectionList>("SourceCards")!.Items[1]!;
            MergedSourcesSmoke.OpenSourceRow(page, selectedSource);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
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
            await CheckBadgesAsync(window, page, view, targetWidth);
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
    private static async Task CheckMergedCommandRoutesAsync(MainWindow window, SourcesPage page)
    {
        var original = window.SessionHost.Config.ShortcutBindings;
        Button origin = window.FindControl<Button>("DownloadsNavigationButton")!;
        try
        {
            // Disposable routing snapshot only: no preference save or hidden Plugins button.
            ShortcutService.Publish(original.Set("navigation.plugins", new[] { ShortcutGesture.Parse("Ctrl+Shift+F11") }));
            origin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            origin.Focus();
            var compatibility = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = origin,
                Key = Key.F11, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift };
            origin.RaiseEvent(compatibility);
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Sources
                && page.PluginManagement.Filter == SourcesPluginsFilter.AllPlugins);
            Require(compatibility.Handled && ReferenceEquals(window.FindControl<ContentControl>("SourcesPageHost")!.Content, page),
                "The saved navigation.plugins shortcut did not reach All plugins in the real merged host.");
            Require(MainPageOrder.Digit("navigation.status") == 7 && MainPageOrder.Digit("navigation.plugins") == null,
                "Removing Plugins displaced Status+7 or gave the compatibility route a digit slot.");
            // Default main-page digits come only from MainPageOrder, not from the page's ordinal position.
            ShortcutPlatform platform = OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS
                : OperatingSystem.IsWindows() ? ShortcutPlatform.Windows : ShortcutPlatform.Linux;
            KeyGesture sourceGesture = ShortcutGesture.Parse(MainPageOrder.DefaultShortcut("navigation.sources")).Resolve(platform);
            origin.Focus();
            var source = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = origin,
                Key = sourceGesture.Key, KeyModifiers = sourceGesture.KeyModifiers };
            origin.RaiseEvent(source);
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Sources
                && page.PluginManagement.Filter == SourcesPluginsFilter.Sources);
            Require(source.Handled && window.FindControl<ContentControl>("PluginsPageHost") == null,
                "The normal Sources command did not restore the Sources dropdown in the merged workspace.");
        }
        finally { ShortcutService.Publish(original); }
    }

    private static async Task CheckSourceListKeyboardAsync(SourcesPage page)
    {
        SelectionList list = page.FindControl<SelectionList>("SourceCards")!;
        Require(list.Items.Count == 2, "The source keyboard fixture requires both actual source rows.");
        if (page.FindControl<Border>("SourceDetailsPanel")!.IsVisible && page.FindControl<Button>("SourceBackButton")!.IsVisible)
            page.FindControl<Button>("SourceBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach ((Key key, int index) in new[] { (Key.Home, 0), (Key.Down, 1), (Key.Up, 0), (Key.End, 1), (Key.Home, 0) })
        {
            list.Focus();
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = list, Key = key };
            list.RaiseEvent(input);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(input.Handled && list.SelectedIndex == index && list.SelectedItem is SelectionListItem row && row.IsFocused
                && page.FindControl<StackPanel>("SourcesListPane")!.IsEffectivelyVisible,
                "Source list keyboard navigation lost row selection/focus or opened narrow details for " + key);
        }
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
        Exception? failureBeforeCleanup = null;
        try
        {
            window.Width = 1440; window.Height = 920;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 1440) <= 1 && Math.Abs(window.Bounds.Height - 920) <= 1);
            foreach (string wideLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(wideLanguage);
                page.Refresh(providers, view, true);
                MergedSourcesSmoke.OpenSourceRow(page, (SelectionListItem)page.FindControl<SelectionList>("SourceCards")!.Items[0]!);
                page.FindControl<ScrollViewer>("SourcesScroll")!.ScrollToHome();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await CaptureBothThemesAsync(window, $"smoke-sources-wide-{wideLanguage}");
            }
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                Texts.SetLanguage(language);
                window.Width = restoreWidth; window.Height = restoreHeight;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - restoreWidth) <= 1);
                page.Refresh(providers, view, true);
                page.SelectSource("thread-only");
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in SourcesChecks: " + exception));
        }
    }
    // Maturity order, chips only for deviations, the selection bar instead of a text marker, the complete meta line and the
    // "Experimental" sentence; at 1440 x 920 in both themes and both languages.
    private static async Task CheckBadgesAsync(MainWindow window, SourcesPage page, DesktopSessionView view, double restoreWidth)
    {
        static ProviderDescriptor Provider(string id, string name) => new(id, name, new Uri("https://" + id + ".invalid/"),
            ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog, [id + ".invalid"], "fixture", true);
        static SourceMediaIntegrityPolicyView Policy(string id, string name, SourceWordingMaintenance maintenance,
            SourceWordingMaturity maturity, bool active) => new(new("profile", "owner." + id, id, false, 1), false, active,
            SourceMaturity.Unspecified, new SourceWordingClassification(id, name, maintenance, maturity));
        ProviderDescriptor[] providers =
        [
            Provider("zeta", "Zeta stable"), Provider("alpha", "Alpha experimental"), Provider("mid", "Mid plain"), Provider("third", "Third stable")
        ];
        SourceMediaIntegrityPolicyView[] policies =
        [
            Policy("zeta", "Zeta stable", SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Stable, true),
            Policy("alpha", "Alpha experimental", SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Experimental, true),
            Policy("mid", "Mid plain", SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified, false),
            Policy("third", "Third stable", SourceWordingMaintenance.ThirdParty, SourceWordingMaturity.Stable, true)
        ];
        Func<IReadOnlyList<SourceMediaIntegrityPolicyView>>? original = page.GetSourceMediaIntegrityPolicies;
        string language = Texts.Language;
        double restoreHeight = window.Height;
        SelectionList cards = page.FindControl<SelectionList>("SourceCards")!;
        static string NameOf(SelectionListItem card) => ((StackPanel)card.Content!).Children.OfType<TextBlock>().First().Text ?? string.Empty;
        static string[] ChipsOf(SelectionListItem card) => ((StackPanel)card.Content!).Children.OfType<WrapPanel>().Single().Children.OfType<Border>()
            .Select(chip => chip.GetLogicalDescendants().OfType<TextBlock>().Single().Text ?? string.Empty).ToArray();
        Exception? failureBeforeCleanup = null;
        try
        {
            page.GetSourceMediaIntegrityPolicies = () => policies;
            window.Width = 1440; window.Height = 920;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - 1440) <= 1 && Math.Abs(window.Bounds.Height - 920) <= 1);
            foreach (string badgeLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(badgeLanguage);
                page.Refresh(providers, view, true);
                SelectionListItem[] ordered = cards.Items.OfType<SelectionListItem>().ToArray();
                Require(ordered.Select(NameOf).SequenceEqual(["Third stable", "Zeta stable", "Alpha experimental", "Mid plain"]),
                    "The source list was not sorted active first, then stable/unclassified/experimental and alphabetical: " + string.Join(", ", ordered.Select(NameOf)));
                Require(ChipsOf(ordered[0]).Length == 0 && ChipsOf(ordered[1]).Length == 0
                    && ChipsOf(ordered[3]).SequenceEqual([Texts.T("sources.maintenance.unverified"), Texts.T("sources.badge.status.inactive")])
                    && ChipsOf(ordered[2]).SequenceEqual([Texts.T("sources.maturity.experimental")]),
                    "A source that follows the norm showed a chip, or a deviating one did not: " + string.Join(" / ", ordered.Select(card => string.Join(",", ChipsOf(card)))));
                Border warning = ((StackPanel)ordered[2].Content!).Children.OfType<WrapPanel>().Single().Children.OfType<Border>().Single();
                Require(warning.Classes.Contains("chip-warning") && warning.Classes.Contains("chip-badge"),
                    "The Experimental chip lost its warning tone.");
                Require(!ordered.Select(NameOf).Any(name => name.Contains('●')), "A source name still carries the old text marker.");

                // Zeta is the norm: complete meta line, no sentence. Alpha is experimental: the sentence explains it.
                MergedSourcesSmoke.OpenSourceRow(page, ordered[1]);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(ordered[1].Classes.Contains("selected") && !ordered[2].Classes.Contains("selected")
                    && page.FindControl<TextBlock>("SourceMeta")!.Text == SourceWording.DescribeMeta(SourceWording.Badges(
                        policies[0].Classification, true, false))
                    && !page.FindControl<TextBlock>("SourceExperimental")!.IsVisible,
                    "A source that follows the norm did not show its complete meta line without the Experimental sentence: " + page.FindControl<TextBlock>("SourceMeta")!.Text);
                MergedSourcesSmoke.OpenSourceRow(page, ordered[2]);
                page.FindControl<ScrollViewer>("SourcesScroll")!.ScrollToHome();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(ordered[2].Classes.Contains("selected") && !ordered[1].Classes.Contains("selected")
                    && page.FindControl<TextBlock>("SourceExperimental")!.IsVisible
                    && page.FindControl<TextBlock>("SourceExperimental")!.Text == Texts.T("sources.experimentalNote")
                    && page.FindControl<TextBlock>("SourceMeta")!.Text!.Contains(Texts.T("sources.maturity.experimental")),
                    "The Experimental source did not show its meta line and the sentence that explains it.");
                await CaptureBothThemesAsync(window, $"smoke-sources-badges-{badgeLanguage}");
                // A refresh keeps the cards (and their focus) and only moves them when the order changes.
                SelectionListItem[] again = cards.Items.OfType<SelectionListItem>().ToArray();
                page.Refresh(providers, view, true);
                Require(cards.Items.OfType<SelectionListItem>().SequenceEqual(again), "A refresh with the same order rebuilt or moved the source cards.");
                policies[2] = Policy("mid", "Mid plain", SourceWordingMaintenance.MeowChan, SourceWordingMaturity.Stable, true);
                page.Refresh(providers, view, true);
                Require(cards.Items.OfType<SelectionListItem>().Select(NameOf).SequenceEqual(["Mid plain", "Third stable", "Zeta stable", "Alpha experimental"])
                    && cards.Items.OfType<SelectionListItem>().First() == again[3] && ChipsOf(again[3]).Length == 0,
                    "A source whose maturity changed was not moved with its card, or kept a stale chip.");
                policies[2] = Policy("mid", "Mid plain", SourceWordingMaintenance.Unknown, SourceWordingMaturity.Unclassified, false);
            }
            // The switch point between the one-pane and the two-pane layout is a page width of 886 DIP; capture just above it
            // (two panes, the list column at its narrowest) so the chips are seen where the room is tightest.
            window.Width += 892 - page.Bounds.Width;
            await Program.WaitUntilAsync(() => Math.Abs(page.Bounds.Width - 892) <= 1.5);
            foreach (string narrowLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(narrowLanguage);
                page.Refresh(providers, view, true);
                page.SelectSource("alpha");
                page.FindControl<ScrollViewer>("SourcesScroll")!.ScrollToHome();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(page.FindControl<Border>("SourceDetailsPanel")!.IsEffectivelyVisible && page.FindControl<StackPanel>("SourcesListPane")!.IsEffectivelyVisible,
                    "At 892 DIP the Sources page was not in its two-pane layout.");
                await CaptureBothThemesAsync(window, $"smoke-sources-886-{narrowLanguage}");
            }
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                page.GetSourceMediaIntegrityPolicies = original;
                Texts.SetLanguage(language);
                window.Width = restoreWidth; window.Height = restoreHeight;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - restoreWidth) <= 1);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in SourcesChecks: " + exception));
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
