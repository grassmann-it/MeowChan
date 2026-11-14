using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Dialog gallery scene "booru-workbench": the Danbooru window over the fake ports of the booru-workbench smoke (no
// provider, queue, scheduler or network). Captures the idle window and one result page, then closes the window.
internal static partial class BooruWorkbenchChecks
{
    internal static async Task CaptureForDialogGalleryAsync(MainWindow owner)
    {
        BooruWorkbenchWindow? window = null;
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
            window = await OpenAsync(owner, menu);
            await DialogGalleryChecks.RecordWindowAsync(window, "booru-workbench-idle");
            Find<TextBox>(window, BooruWorkbenchWindow.InputName).Text = "cat blue_eyes";
            await SettleAsync(window);
            Click(Find<Button>(window, BooruWorkbenchWindow.SubmitName));
            await Program.WaitUntilAsync(() => Cards(window, "BooruResult:").Count == 3 && Previews(window) == 3,
                () => "cards=" + Cards(window, "BooruResult:").Count + " previews=" + Previews(window));
            await SettleAsync(window);
            DialogGallery.Record(window, "booru-workbench-results", "default size", "fake provider page, three result cards");
        }
        finally
        {
            window?.Close();
            owner.SessionHost.BooruServices = null;
            await SettleAsync();
        }
    }
}
