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
