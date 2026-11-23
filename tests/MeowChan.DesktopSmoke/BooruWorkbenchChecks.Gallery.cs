using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Interfaces;
using MeowChan.PluginSdk;
using MeowChan.Testing;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Dialog gallery scene "booru-workbench": the Danbooru page hosted in the shell window (it is a page of the main window, not a tool
// window any more), opened through its real app-menu command over the fake ports of the booru-workbench smoke (no provider, queue,
// scheduler or network). Captures the idle page and one result page, then ends the page's lifetime through the service boundary.
internal static partial class BooruWorkbenchChecks
{
    private static async Task CheckPopoutBoundsAfterShutdownAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("booru-viewer-placement-");
        string media = Path.Combine(profile, "media");
        new Config
        {
            DownloadPath = media, AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Advanced", ShowBooruTools = true
        }.SaveConfig(profile);
        Directory.CreateDirectory(media);

        var saved = await WithPopoutAsync(async (shell, popout) =>
        {
            var initial = popout.NormalBounds;
            popout.Width = Math.Max(popout.MinWidth, popout.Width - 40);
            popout.Height = Math.Max(popout.MinHeight, popout.Height - 32);
            popout.Position += new PixelPoint(24, 20);
            await SettleAsync(popout);
            var current = popout.NormalBounds;
            Require(current is not null && current.Value.Position != initial?.Position,
                $"The test did not move the open popout. Initial {initial}, current {current}.");
            Require(shell.SessionHost.Config.ReadBooruViewerBounds() is null, "The fixture already had saved popout placement.");
            return current;
        });
        Require(Config.LoadConfig(profile).ReadBooruViewerBounds() == saved, "Shutdown lost the open popout's last normal bounds on disk.");
        var restored = await WithPopoutAsync((_, popout) => Task.FromResult(popout.NormalBounds));
        Require(restored == saved, $"A fresh profile host reopened at {restored}, expected {saved}.");
        Console.WriteLine("SMOKE booru popout: quitting with the popout open saved its normal bounds for the next profile host.");

        async Task<(Size Size, PixelPoint Position)?> WithPopoutAsync(
            Func<MainWindow, BooruResultViewerWindow, Task<(Size Size, PixelPoint Position)?>> check)
        {
            await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
            await using MainWindow shell = new(host);
            BooruUiHarness harness = new();
            harness.Serve(pages: 1, perPage: 1);
            host.BooruServices = harness.Services;
            try
            {
                shell.Show(owner);
                await host.StartAsync();
                await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready
                    && shell.FindControl<Button>("AddUrlButton")!.IsEnabled,
                    () => $"The popout profile shell did not finish startup: {host.State}.");
                Reconcile.Invoke(shell, null);
                BooruPage page = await OpenPageAsync(shell, shell.FindControl<MenuItem>("WorkspaceMenuItem")!);
                await page.ActivateAsync();
                page.View.ShowQuery("cat");
                await page.Model.Search.SubmitAsync();
                Require(page.View.OpenResultViewer("101"), "The profile's popout did not open.");
                BooruResultViewerWindow popout = shell.OwnedWindows.OfType<BooruResultViewerWindow>().Single();
                await SettleAsync(popout);
                return await check(shell, popout);
            }
            finally
            {
                TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                shell.Closed += (_, _) => closed.TrySetResult();
                SmokeActionClick.Execute(shell.FindControl<MenuItem>("ExitMenuItem")!);
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static async Task CheckGalleryToolbarAsync(MainWindow owner, BooruPage page, string shots)
    {
        Click(Find<Button>(page, "BooruView:101"));
        BooruGalleryPanel gallery = page.View.Gallery;
        Border resultLine = Find<Border>(page, "BooruResultsLine");
        var pager = (Grid)Find<Button>(page, BooruWorkbenchView.PreviousName).Parent!;
        TextBlock pageInfo = pager.Children.OfType<TextBlock>().Single();
        string result = BooruText.SearchStatus(page.Model.Search.View, false);
        Require(!resultLine.IsVisible && pageInfo.IsEffectivelyVisible
            && AutomationProperties.GetLiveSetting(pageInfo) == AutomationLiveSetting.Polite
            && AutomationProperties.GetName(pageInfo)!.Contains(result) && (ToolTip.GetTip(pageInfo) as string)?.Contains(result) == true,
            "The gallery pager must keep the result status as accessible help and a polite announcement.");
        TextBlock position = Find<TextBlock>(gallery, "BooruGalleryPosition");
        Require(position.Text == "1 of 3" && !gallery.PreviousButton.IsEnabled, "The gallery must start at the first result.");
        gallery.Stage.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        await SettleAsync(owner);
        Require(position.Text == "2 of 3" && gallery.CurrentPostId == "102", "Right on the stage did not advance the gallery.");

        Button openPost = Find<Button>(gallery, "BooruGalleryOpenPost");
        Button openWindow = Find<Button>(gallery, "BooruGalleryOpenWindow");
        Rect previous = SearchLayoutBounds(gallery.PreviousButton, owner), count = SearchLayoutBounds(position, owner);
        Rect next = SearchLayoutBounds(gallery.NextButton, owner), selection = SearchLayoutBounds(gallery.SelectBox, owner);
        Rect download = SearchLayoutBounds(gallery.DownloadButton, owner), post = SearchLayoutBounds(openPost, owner);
        Button fullscreen = Find<Button>(gallery, "BooruGalleryFullscreen");
        Rect window = SearchLayoutBounds(openWindow, owner), bar = SearchLayoutBounds(Find<Border>(gallery, "BooruGalleryBar"), owner);
        Rect full = SearchLayoutBounds(fullscreen, owner);
        Require(previous.Size == new Size(32, 32) && next.Size == previous.Size && post.Size == previous.Size && window.Size == previous.Size
            && Math.Abs(download.Height - previous.Height) <= 1 && Math.Abs(selection.Center.Y - previous.Center.Y) <= 1
            && Math.Abs(count.Left - previous.Right - 4) <= 1 && Math.Abs(next.Left - count.Right - 4) <= 1
            && Math.Abs((previous.Left + next.Right) / 2 - bar.Center.X) <= 1
            && selection.Right < previous.Left && next.Right < download.Left
            && Math.Abs(post.Left - download.Right - 4) <= 1 && Math.Abs(full.Right - bar.Right) <= 1,
            $"Gallery toolbar groups are misaligned: previous={previous}, count={count}, next={next}, selection={selection}, download={download}, post={post}, window={window}, bar={bar}");
        Require(gallery.DownloadButton.Classes.Contains("primary") && openPost.Classes.Contains("icon-only") && openWindow.Classes.Contains("icon-only")
            && AutomationProperties.GetName(openWindow) == "Open in new window" && ToolTip.GetTip(openWindow) as string == "Open in new window"
            && (ToolTip.GetTip(openPost) as string)?.Contains("/posts/102", StringComparison.Ordinal) == true,
            "The gallery toolbar actions lack their visual hierarchy or tooltips.");
        Capture(owner, shots, "gallery-toolbar-light");
        page.View.SetResultView(BooruResultView.List, save: false);
    }

    private static async Task CheckLiveGalleryAsync(MainWindow owner)
    {
        BooruUiHarness h = new();
        h.Serve(pages: 3, perPage: 4);
        using BooruPage page = new(h.Services);
        page.View.NewSearchTarget = () => Config.BooruSearchTargetCurrentTab;
        Window window = new() { Width = 1120, Height = 792, Content = page, ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            await SettleAsync(window);
            using MediaNativeInput native = new(window);
            TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
            TabControl tabs = Find<TabControl>(page, BooruWorkbenchView.TabsName);
            TabItem original = (TabItem)tabs.SelectedItem!;
            Button add = Find<Button>(page, BooruWorkbenchView.NewSearchTabName);
            BooruGalleryPanel gallery = page.View.Gallery;
            var toggle = page.View.ViewToggle;
            input.Text = "cat rating:general";
            await Program.WaitUntilAsync(() => Find<Button>(page, BooruWorkbenchView.SubmitName).IsEnabled);
            Button help = Find<Button>(page, BooruWorkbenchView.SearchHelpName);
            await native.ClickAsync(help);
            await Program.WaitUntilAsync(() => help.Flyout?.IsOpen == true);
            Require(AutomationProperties.GetHelpText(help)?.Contains("Draft: cat rating:general") == true,
                "The compact help does not include the unsent draft.");
            await native.KeyAsync(MediaNativeInput.Escape);
            await Program.WaitUntilAsync(() => help.Flyout?.IsOpen == false);
            await native.ClickAsync(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 4);

            IInputElement? FocusedControl() => window.FocusManager?.GetFocusedElement();
            Task Picture(string id, bool focused = true) => Program.WaitUntilAsync(
                () => gallery.CurrentPostId == id && (!focused || gallery.Stage.IsFocused),
                () => $"Expected gallery post {id}, got {gallery.CurrentPostId}. Focus: {Describe(FocusedControl())}.");
            Task GridFocus(string id) => Program.WaitUntilAsync(
                () => page.View.ResultView == BooruResultView.Grid && FocusedControl() is Border card && card.Name == "BooruResult:" + id,
                () => $"Expected grid focus on {id}. Focus: {Describe(FocusedControl())}.");

            await native.ClickAsync(toggle.Second);
            await native.ClickAsync(toggle.Third);
            await Picture("101");
            await native.KeyAsync(MediaNativeInput.Right);
            await Picture("102");
            await native.ClickAsync(gallery.NextButton);
            await Picture("103");
            await native.KeyAsync(MediaNativeInput.Right);
            await Picture("104");
            await native.KeyAsync(MediaNativeInput.Escape);
            await GridFocus("104");

            await native.KeyAsync(MediaNativeInput.Home);
            await GridFocus("101");
            await native.KeyAsync(MediaNativeInput.Enter);
            await Picture("101");
            for (int i = 0; i < 3; i++) await native.KeyAsync(MediaNativeInput.Right);
            await Picture("104");
            await native.KeyAsync(MediaNativeInput.Escape);
            await GridFocus("104");
            await native.KeyAsync(MediaNativeInput.Enter);
            await Picture("104");

            Button nextPage = Find<Button>(page, BooruWorkbenchView.NextName);
            var serve = h.Search.OnSearch!;
            TaskCompletionSource pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Search.OnSearch = async (query, number, cancellationToken) =>
            {
                if (number is 2 or 3) await pageReady.Task.WaitAsync(cancellationToken);
                return await serve(query, number, cancellationToken);
            };
            nextPage.BringIntoView();
            await native.ClickAsync(nextPage);
            await Program.WaitUntilAsync(() => page.Model.Search.View.IsBusy);
            pageReady.SetResult();
            await Picture("201");
            await native.KeyAsync(MediaNativeInput.Right);
            await Picture("202");
            await native.KeyAsync(MediaNativeInput.Space);
            Require(gallery.SelectBox.IsChecked == true, "Space did not mark page 2's second hit.");
            await native.ClickAsync(add);
            await Program.WaitUntilAsync(() => input.IsFocused && gallery.CurrentPostId is null);
            await native.ClickAsync(original);
            await Picture("202", focused: false);
            Require(gallery.SelectBox.IsChecked == true, "A visit to an empty tab lost the original mark.");

            await native.ClickAsync(Find<Button>(page, BooruWorkbenchView.OpenInNewTabName));
            await Picture("202", focused: false);
            Require(gallery.SelectBox.IsChecked == false && page.Model.Search.View.Page == 2, "The duplicate did not take the page and cursor with an independent selection.");
            gallery.NextButton.Focus(NavigationMethod.Tab);
            await native.KeyAsync(MediaNativeInput.Space);
            await Picture("203", focused: false);
            Require(gallery.NextButton.IsFocused, "Keyboard activation of the gallery toolbar took focus from the button.");
            await native.ClickAsync(original);
            await Picture("202", focused: false);
            Require(gallery.SelectBox.IsChecked == true, "Moving in the duplicate changed the original tab.");

            pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            nextPage.BringIntoView();
            await native.ClickAsync(nextPage);
            await Program.WaitUntilAsync(() => page.Model.Search.View.IsBusy);
            Require(help.Focus(NavigationMethod.Tab), "Could not move focus to the search help while paging.");
            pageReady.SetResult();
            await Picture("301", focused: false);
            await SettleAsync(window);
            Require(help.IsFocused, $"The page response stole focus from the search help: {Describe(FocusedControl())}.");

            window.Width = 760;
            await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - 760) <= 1);
            await SettleAsync(window);
            Rect mode = SearchLayoutBounds(toggle, window);
            Rect stage = SearchLayoutBounds(gallery.Stage, window);
            ScrollViewer results = Find<ScrollViewer>(page, "BooruResultScroll");
            Require(stage.Height > gallery.Stage.MinHeight && Math.Abs(results.Extent.Height - results.Viewport.Height) <= 1,
                $"At 760 × 792 DIP the stage and pager did not fill the available viewport: stage={stage}, viewport={results.Viewport}, extent={results.Extent}.");
            Console.WriteLine($"SMOKE booru gallery stage at 760 × 792 DIP: {stage.Height:F0} DIP.");
            window.Height = 520;
            await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Height - 520) <= 1);
            await SettleAsync(window);
            nextPage.BringIntoView();
            await SettleAsync(window);
            Rect shortMode = SearchLayoutBounds(toggle, window);
            Rect pager = SearchLayoutBounds(nextPage, window);
            Rect viewport = SearchLayoutBounds(results, window);
            Require(results.Offset.Y > 0 && mode == shortMode && shortMode.Bottom <= viewport.Top && pager.Bottom <= viewport.Bottom + 1,
                $"The short window cannot show the view switch and pager together: mode={shortMode}, viewport={viewport}, pager={pager}.");

            for (int i = 0; i < 4; i++)
            {
                await native.ClickAsync(add);
                await Program.WaitUntilAsync(() => input.IsFocused && string.IsNullOrEmpty(input.Text));
                input.Text = $"ordinary_subject_with_a_long_name_{i} another_subject_label";
                await SettleAsync(window);
            }
            TabItem active = (TabItem)tabs.SelectedItem!;
            Button close = active.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("booru-tab-close"));
            ScrollViewer strip = Find<ScrollViewer>(page, "BooruTabStrip");
            window.Width = 1440;
            await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - 1440) <= 1 && strip.Offset.X == 0);
            window.Width = 760;
            await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - 760) <= 1);
            await SettleAsync(window);
            double stripLeft = SearchLayoutBounds(strip, window).Left;
            Rect closeBounds = SearchLayoutBounds(close, window);
            Require(strip.Offset.X > 0 && closeBounds.Left >= stripLeft
                && closeBounds.Right <= stripLeft + strip.Viewport.Width + 1
                && SearchLayoutBounds(add, window).Right <= window.ClientSize.Width,
                "The active tab's close button or the add button was clipped at 760 DIP.");
            await native.ClickAsync(close);
            await Program.WaitUntilAsync(() => !ReferenceEquals(tabs.SelectedItem, active));
            Require(h.Search.Searches.Select(search => search.Page).SequenceEqual(new[] { 1, 2, 3 }) && h.Queue.Loads.Count == 0,
                "Gallery navigation or tab actions started unexpected provider work.");
            Console.WriteLine("SMOKE booru live gallery: native focus, return, paging, tab position and marks, and chrome at 760 DIP.");
        }
        finally { window.Close(); }
    }

    internal static async Task CaptureForDialogGalleryAsync(MainWindow owner)
    {
        BooruWorkbenchServices? overrideBefore = (BooruWorkbenchServices?)ServicesOverride.GetValue(owner.SessionHost);
        string modeBefore = owner.SessionHost.Config.WorkspaceMode;
        bool showBefore = owner.SessionHost.Config.ShowBooruTools;
        BooruPage? page = null;
        try
        {
            MenuItem menu = owner.FindControl<MenuItem>("WorkspaceMenuItem")!;
            BooruUiHarness h = new();
            h.Serve(pages: 3, perPage: 3);
            h.Watchers.Seed(
                BooruUiFixtures.Watcher("w-active", "cat blue_eyes", clock: h.Clock, hits: 6, queued: 5) with { LatestPostId = "9001" },
                BooruUiFixtures.Watcher("w-paused", "landscape", BooruWatcherStatus.Paused, h.Clock, hits: 2, queued: 2),
                BooruUiFixtures.Watcher("w-error", "ruins", BooruWatcherStatus.Error, h.Clock, errorCode: "http-network"));
            owner.SessionHost.BooruServices = h.Services;
            // As the booru-workbench smoke: the configured Advanced view shows the complete page; no visibility gate is overridden.
            await owner.SessionHost.SaveViewPreferencesAsync(config =>
            {
                config.WorkspaceMode = "Advanced";
                config.ShowBooruTools = true;
            });
            Reconcile.Invoke(owner, null);
            page = await OpenPageAsync(owner, menu);
            await SettleAsync(owner);
            DialogGallery.Record(owner, "booru-workbench-idle", "main window", "the hosted Danbooru page, nothing requested yet");
            Find<TextBox>(page, BooruWorkbenchView.InputName).Text = "cat blue_eyes";
            await SettleAsync(owner);
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 3 && Previews(page) == 3,
                () => "cards=" + Cards(page, "BooruResult:").Count + " previews=" + Previews(page));
            await SettleAsync(owner);
            DialogGallery.Record(owner, "booru-workbench-results", "main window", "fake provider page, three result cards");

            // C12: the dialog gallery also needs the hosted page's provider-error state, not only
            // the idle/results surfaces. This is distinct from the shell toast/overlay scenes.
            h.Search.OnSearch = (_, _, _) => Task.FromResult(
                BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Offline, "gallery-offline"));
            Find<TextBox>(page, BooruWorkbenchView.InputName).Text = "gallery offline";
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Text(page).Contains("Danbooru could not be reached"),
                () => "Booru offline state was not shown: " + BooruText.SearchStatus(page.Model.Search.View, false));
            await SettleAsync(owner);
            DialogGallery.Record(owner, "booru-workbench-error", "main window", "hosted Danbooru provider error state");
        }
        finally
        {
            // Navigation alone retains a page: the shell disposes it when the services return to the composed ones.
            try { Click(owner.FindControl<Button>("DownloadsNavigationButton")!); }
            finally
            {
                owner.SessionHost.BooruServices = overrideBefore;
                Reconcile.Invoke(owner, null);
                await owner.SessionHost.SaveViewPreferencesAsync(config =>
                {
                    config.WorkspaceMode = modeBefore;
                    config.ShowBooruTools = showBefore;
                });
                Reconcile.Invoke(owner, null);
                await SettleAsync(owner);
            }
        }
    }
}
