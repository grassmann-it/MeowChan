using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Dialog gallery scene "booru-workbench": the Danbooru page hosted in the shell window (it is a page of the main window, not a tool
// window any more), opened through its real app-menu command over the fake ports of the booru-workbench smoke (no provider, queue,
// scheduler or network). Captures the idle page and one result page, then ends the page's lifetime through the service boundary.
internal static partial class BooruWorkbenchChecks
{
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
            && AutomationProperties.GetName(pageInfo)!.Contains(result) && Equals(ToolTip.GetTip(pageInfo), result),
            "The gallery pager must keep the result status as accessible help and a polite announcement.");
        TextBlock position = Find<TextBlock>(gallery, "BooruGalleryPosition");
        Require(position.Text == "1 of 3" && !gallery.PreviousButton.IsEnabled, "The gallery must start at the first result.");
        Click(gallery.NextButton);
        await SettleAsync(owner);
        Require(position.Text == "2 of 3" && gallery.CurrentPostId == "102", "Gallery navigation did not update the counter.");

        Button openPost = Find<Button>(gallery, "BooruGalleryOpenPost");
        Button openWindow = Find<Button>(gallery, "BooruGalleryOpenWindow");
        Rect previous = SearchLayoutBounds(gallery.PreviousButton, owner), count = SearchLayoutBounds(position, owner);
        Rect next = SearchLayoutBounds(gallery.NextButton, owner), selection = SearchLayoutBounds(gallery.SelectBox, owner);
        Rect download = SearchLayoutBounds(gallery.DownloadButton, owner), post = SearchLayoutBounds(openPost, owner);
        Rect window = SearchLayoutBounds(openWindow, owner), bar = SearchLayoutBounds(Find<Grid>(gallery, "BooruGalleryBar"), owner);
        Require(previous.Size == new Size(32, 32) && next.Size == previous.Size && post.Size == previous.Size && window.Size == previous.Size
            && Math.Abs(download.Height - previous.Height) <= 1 && Math.Abs(selection.Center.Y - previous.Center.Y) <= 1
            && Math.Abs(count.Left - previous.Right - 4) <= 1 && Math.Abs(next.Left - count.Right - 4) <= 1
            && Math.Abs(selection.Left - next.Right - 8) <= 1 && Math.Abs(download.Left - selection.Right - 8) <= 1
            && Math.Abs(post.Left - download.Right - 4) <= 1 && Math.Abs(window.Right - bar.Right) <= 1,
            $"Gallery toolbar groups are misaligned: previous={previous}, count={count}, next={next}, selection={selection}, download={download}, post={post}, window={window}, bar={bar}");
        Require(gallery.DownloadButton.Classes.Contains("primary") && openPost.Classes.Contains("icon-only") && openWindow.Classes.Contains("icon-only")
            && AutomationProperties.GetName(openWindow) == "Open in new window" && ToolTip.GetTip(openWindow) as string == "Open in new window"
            && (ToolTip.GetTip(openPost) as string)?.Contains("/posts/102", StringComparison.Ordinal) == true,
            "The gallery toolbar actions lack their visual hierarchy or tooltips.");
        Capture(owner, shots, "gallery-toolbar-light");
        page.View.SetResultView(BooruResultView.List, save: false);
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
