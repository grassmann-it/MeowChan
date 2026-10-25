using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input;
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
            Require(selectedText.Length >= 2 && selectedText[0] == "● " + selectedSource.DisplayName
                && selectedText[1] == selectedSource.BaseUri.AbsoluteUri,
                "The productive Sources details disagreed with the selected source card.");
            livePage.FindControl<Button>("SourceRulesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine("SMOKE productive source rules: selected=" + selectedSource.SourceId
                + "; actual=" + window.RulesPage.SelectedScopeChoice?.Scope + ".");
            Require(window.SelectedPage == MainWindow.WorkspacePage.Rules
                && window.RulesPage.SelectedScopeChoice?.Scope == MeowChan.Rules.MediaRuleScope.ForSource(selectedSource.SourceId),
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
                await Program.WaitUntilAsync(() => page.Bounds.Width > 0 && page.Bounds.Width <= 760
                        && page.FindControl<Grid>("SourcesSplit")!.ColumnDefinitions[0].ActualWidth < 1
                        && page.FindControl<Grid>("SourcesSplit")!.ColumnSpacing == 0);
                foreach (string language in new[] { "en", "de" })
                {
                    Texts.SetLanguage(language);
                    page.Refresh(providers, view, true);
                    page.FindControl<TextBox>("SourceUrlInput")!.Text = "https://unknown.invalid/g/thread/123";
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
                    RequireIconLabel(page.FindControl<Button>("SourceBackButton")!, Texts.T("nav.sources"));
                    RequireIconLabel(page.FindControl<Button>("SourceWebsiteButton")!,
                        Texts.T("sources.website", "https://boards.4chan.org/"));
                    RequireIconLabel(page.FindControl<Button>("SourceRulesButton")!, Texts.T("sources.rules"));
                    RequireIconLabel(page.FindControl<Button>("SourceDiagnosticsButton")!, Texts.T("sources.diagnostics"));
                    RequireIconLabel(page.FindControl<Button>("SourceDownloadsButton")!, Texts.T("nav.downloads"));
                    RequireIconLabel(page.FindControl<Button>("SourceAddButton")!, Texts.T("action.add"));
                    RequireIconLabel(page.FindControl<Button>("SourceOpenWatchButton")!, Texts.T("diagnostics.openWatch"));
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
                TextBox url = page.FindControl<TextBox>("SourceUrlInput")!;
                url.Focus();
                window.Width = 760;
                await Program.WaitUntilAsync(() => selectedSource.IsFocused && page.FindControl<StackPanel>("SourcesListPane")!.IsVisible);
                Require(url.Text == submitted, "Responsive focus recovery changed the source URL draft.");
                window.Width = targetWidth;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - targetWidth) <= 1);
            }
            await CheckSubmitFocusAsync(host, providers, view);
            await CheckOwnedModalReturnDeactivationAsync(window, host, providers, view);
            await CheckNavigationAsync(window, wide: targetWidth >= 1120);
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
    private static async Task CheckSubmitFocusAsync(ContentControl host,
        ProviderDescriptor[] providers, DesktopSessionView view)
    {
        object? original = host.Content;
        Func<Task<bool>> result = () => Task.FromResult(false);
        int submits = 0;
        SourcesPage fixture = new(_ => false, _ => { }, () => { }, () => { }, _ => { submits++; return result(); });
        host.Content = fixture;
        try
        {
            fixture.Refresh(providers, view, true);
            fixture.SelectSource("4chan");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            TextBox editor = fixture.FindControl<TextBox>("SourceUrlInput")!;
            async Task EnterAsync()
            {
                editor.Text = "retained rejected URL";
                editor.BringIntoView();
                Require(editor.Focus(), "Source submit focus fixture could not focus its actual editor.");
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                await Program.WaitUntilAsync(() => !fixture.IsBusy);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            await EnterAsync();
            Require(submits == 1 && editor.IsFocused && editor.Text == "retained rejected URL",
                "Rejected ordinary Enter must submit once and restore the retained URL editor.");
            result = () => Task.FromException<bool>(new InvalidOperationException("Expected submit focus fixture failure"));
            await EnterAsync();
            Require(submits == 2 && editor.IsFocused && editor.Text == "retained rejected URL"
                && fixture.FindControl<Ursa.Controls.Banner>("SourcesActionErrorBanner")!.IsVisible,
                "A failed source submit did not retain its editor focus and local error.");
            result = () => Task.FromResult(true);
            await EnterAsync();
            Require(submits == 3 && editor.Text == string.Empty && !editor.IsFocused,
                "Accepted source submit must retain its existing clear/no-restore behavior.");

            TaskCompletionSource<bool> pending = new();
            result = () => pending.Task;
            editor.Text = "delayed rejected URL"; editor.BringIntoView(); editor.Focus();
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Require(fixture.IsBusy && !editor.IsEnabled, "Delayed submit did not enter its guarded busy state.");
            Button laterChoice = fixture.FindControl<Button>("SourcePluginButton")!;
            // The fixture has no plugin callback; a focusable sibling outside the
            // busy source guards is the genuine later keyboard decision.
            fixture.OpenPluginSettings = _ => { };
            fixture.Refresh(providers, view, true);
            Require(laterChoice.Focus(), "The later focus choice was not reachable.");
            pending.SetResult(false);
            await Program.WaitUntilAsync(() => !fixture.IsBusy);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(submits == 4 && laterChoice.IsFocused && editor.Text == "delayed rejected URL",
                "A rejected source submit stole focus from a later user choice.");

            pending = new(); result = () => pending.Task;
            editor.BringIntoView(); editor.Focus();
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            fixture.SelectSource("thread-only"); fixture.SelectSource("4chan");
            pending.SetResult(false);
            await Program.WaitUntilAsync(() => !fixture.IsBusy);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(submits == 5 && !editor.IsFocused && editor.Text == "delayed rejected URL",
                "A stale submit restored focus after a source identity changed away and back.");

            pending = new(); result = () => pending.Task;
            editor.BringIntoView(); editor.Focus();
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            host.Content = original;
            host.Content = fixture;
            pending.SetResult(false);
            await Program.WaitUntilAsync(() => !fixture.IsBusy);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(submits == 6 && !editor.IsFocused && editor.Text == "delayed rejected URL",
                "A stale submit restored focus after its source page detached and reattached.");
        }
        finally { fixture.EndLifetime(); host.Content = original; }
    }
    private static async Task CheckOwnedModalReturnDeactivationAsync(MainWindow window, ContentControl host,
        ProviderDescriptor[] providers, DesktopSessionView view)
    {
        object? original = host.Content;
        TaskCompletionSource<bool> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? ownedModal = null;
        Window external = new() { Width = 320, Height = 180, Title = "External activation fixture",
            Content = new TextBlock { Text = "Separate native activation", Margin = new Thickness(16) } };
        bool modalClosed = false, sawInitialOwnerDeactivation = false, sawExternalOwnerDeactivation = false;
        int postClosedOwnerActivations = 0, newerOwnerFocusEvents = 0;
        bool externalShown = false;
        void OnOwnerActivated(object? sender, EventArgs args)
        { if (modalClosed) postClosedOwnerActivations++; }
        void OnOwnerDeactivated(object? sender, EventArgs args)
        {
            if (!modalClosed) sawInitialOwnerDeactivation = true;
            if (externalShown && postClosedOwnerActivations > 0) sawExternalOwnerDeactivation = true;
        }
        void OnOwnerFocus(object? sender, FocusChangedEventArgs args) => newerOwnerFocusEvents++;
        SourcesPage fixture = new(_ => false, _ => { }, () => { }, () => { }, async (_, observeModal) =>
        {
            ownedModal = new Window { Width = 320, Height = 180, Title = "Owned admission fixture",
                Content = new TextBlock { Text = "Controlled rejected admission", Margin = new Thickness(16) } };
            observeModal(ownedModal);
            ownedModal.Closed += (_, _) => modalClosed = true;
            await ownedModal.ShowDialog<object?>(window);
            // Keep submit busy through the later external activation/return so
            // fallback cannot finish before the negative lifetime transition.
            return await outcome.Task;
        });
        host.Content = fixture;
        try
        {
            fixture.Refresh(providers, view, true); fixture.SelectSource("4chan");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            TextBox editor = fixture.FindControl<TextBox>("SourceUrlInput")!;
            editor.Text = "retained owned-modal URL"; editor.BringIntoView();
            Require(editor.Focus(), "Owned-modal return fixture could not focus its real editor.");
            window.Activated += OnOwnerActivated; window.Deactivated += OnOwnerDeactivated;
            window.AddHandler(InputElement.GotFocusEvent, OnOwnerFocus, RoutingStrategies.Bubble, handledEventsToo: true);
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Program.WaitUntilAsync(() => ownedModal is { IsVisible: true } && fixture.IsBusy && !editor.IsEnabled);
            ownedModal!.Activate();
            await Program.WaitUntilAsync(() => ownedModal.IsActive && !window.IsActive && sawInitialOwnerDeactivation);
            ownedModal.Close();
            await Program.WaitUntilAsync(() => modalClosed && !ownedModal.IsVisible && window.IsActive && postClosedOwnerActivations > 0);
            Require(fixture.IsBusy && !editor.IsEnabled && newerOwnerFocusEvents == 0,
                "The controlled submit completed or gained newer owner focus before the external deactivation premise.");
            externalShown = true; external.Show(window); external.Activate();
            await Program.WaitUntilAsync(() => external.IsActive && !window.IsActive && sawExternalOwnerDeactivation);
            Require(newerOwnerFocusEvents == 0,
                "A newer owner focus choice masked the external deactivation guard under test.");
            int beforeExternalReturn = postClosedOwnerActivations;
            external.Close();
            await Program.WaitUntilAsync(() => !external.IsVisible && window.IsActive
                && postClosedOwnerActivations > beforeExternalReturn);
            // Explicitly establish the negative null-focus premise before submit
            // completion. Focus(null) raises no GotFocus and cannot itself cancel
            // this claim; the preceding external deactivation must have done so.
            window.FocusManager!.Focus(null);
            Require(fixture.IsBusy && !editor.IsEnabled && newerOwnerFocusEvents == 0
                && window.FocusManager.GetFocusedElement() == null,
                "The external-return no-newer-focus/null premise was not established.");
            outcome.SetResult(false);
            await Program.WaitUntilAsync(() => !fixture.IsBusy && editor.IsEnabled);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(window.IsActive && !editor.IsFocused && window.FocusManager.GetFocusedElement() == null
                && editor.Text == "retained owned-modal URL",
                "A later external deactivation after the owned-modal return revived the rejected editor's focus claim.");
            Console.WriteLine("SMOKE owned-modal external deactivation: actual initial owner deactivation, normal Closed, first post-Closed activation, later external deactivation and native return, no newer GotFocus, retained null focus after delayed rejection passed.");
        }
        finally
        {
            window.Activated -= OnOwnerActivated; window.Deactivated -= OnOwnerDeactivated;
            window.RemoveHandler(InputElement.GotFocusEvent, OnOwnerFocus);
            outcome.TrySetResult(false); ownedModal?.Close(); external.Close();
            fixture.EndLifetime(); host.Content = original;
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
