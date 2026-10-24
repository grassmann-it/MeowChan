using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Favorites;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaRatingChecks
{
    // Shares the bounded rating fixture; this probes the actual media-menu favorite command.
    private static async Task RunLateMediaMenuErrorChecksAsync(ResourceId replacementThread)
    {
        ResourceId sourceThread = MediaRatingFixture.Thread;
        var originalToggle = _page.ToggleArchiveFavoriteAsync;
        try
        {
            foreach (bool leaveOrigin in new[] { false, true })
            {
                await _page.LoadThreadAsync(sourceThread);
                await _page.MediaLoadTask;
                _page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
                Control origin = await SelectAsync(Row(2));
                var key = KeyOf(Row(2));
                _page.ClearFavoriteError(key);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
                string error = "Injected media-menu favorite failure " + (leaveOrigin ? "after-source-switch" : "current-context");
                int originRequest = Field<int>(_page, "_contentRequest");
                _page.ToggleArchiveFavoriteAsync = async entry =>
                {
                    Require(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset) == key,
                        "Media-menu favorite callback changed the frozen exact identity");
                    started.SetResult();
                    await failure.Task;
                };
                try
                {
                    origin.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                    await Program.WaitUntilAsync(() => _page.ArchiveMediaMenu?.IsOpen == true);
                    ContextMenu menu = _page.ArchiveMediaMenu!;
                    MenuItem favorite = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "favorites.toggle");
                    Require(favorite.IsEnabled && favorite.Command != null, "Actual favorite menu action is unavailable");
                    Log("Start delayed favorite menu command", "actual frozen media-menu ICommand with gated fixture callback", favorite);
                    favorite.Command!.Execute(null);
                    await Program.WaitUntilAsync(() => started.Task.IsCompletedSuccessfully);
                    menu.Close();
                    if (leaveOrigin)
                    {
                        await _page.LoadThreadAsync(replacementThread);
                        await _page.MediaLoadTask;
                        Require(_page.CurrentThread?.Summary.Id == replacementThread,
                            "Late menu error probe did not actually change the reader thread");
                    }
                    failure.SetException(new IOException(error));
                    // The row-local error is set before the menu wrapper catches the exception.
                    await Program.WaitUntilAsync(() => Field<Dictionary<FavoriteAssetKey, string>>(_page, "_favoriteErrors")
                        .TryGetValue(key, out string? recorded) && recorded == error);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    string bannerText = _page.FindControl<TextBlock>("ArchiveErrorText")!.Text ?? "";
                    bool shown = bannerText.Contains(error, StringComparison.Ordinal);
                    Evidence.Add(new { sequence = Evidence.Count + 1, mediaMenuLateError = true,
                        leaveOrigin, key, sourceThread, replacementThread, actualThread = _page.CurrentThread?.Summary.Id,
                        originRequest, actualRequest = Field<int>(_page, "_contentRequest"),
                        callbackCompleted = started.Task.IsCompletedSuccessfully, rowLocalErrorRecorded = true,
                        error, bannerText, shown, expectedShown = !leaveOrigin });
                    SaveEvidence("running actual media-menu late-error scope");
                    Require(shown == !leaveOrigin,
                        "Media-menu error must remain visible only in its originating reader request/thread");
                }
                finally
                {
                    failure.TrySetResult();
                    _page.ArchiveMediaMenu?.Close();
                    _page.ClearFavoriteError(key);
                }
            }
        }
        finally
        {
            _page.ToggleArchiveFavoriteAsync = originalToggle;
            await _page.LoadThreadAsync(sourceThread);
            await _page.MediaLoadTask;
            _page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        }
    }
}
