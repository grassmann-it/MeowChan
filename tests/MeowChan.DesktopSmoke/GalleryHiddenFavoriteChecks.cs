using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Complements GalleryFavoriteChecks' queue/failure matrix with a laid-out journey:
// a held save for A completes while the identical bytes in thread B are current.
// Uses its OWN temporary profile, verified PNG, viewer and window. No shell profile
// favorites/media, video/audio, native input injection or private-field mutation.
internal static class GalleryHiddenFavoriteChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(20);
    // A failed isolated smoke must not release its lease/media beneath live work.
    // Keep the entire incomplete scope rooted until Root terminates this process.
    private sealed record IncompleteCleanup(string Root, ProfileLease? Lease, GalleryViewer Viewer,
        Window Window, CancellationTokenSource Lifetime, Task Settlement);
    private static readonly List<IncompleteCleanup> IncompleteCleanups = new();

    // Root may call this from the isolated DesktopSmoke UI thread. No Program wiring here.
    internal static async Task RunAsync(MainWindow owner)
    {
        if (OperatingSystem.IsLinux()
            ? Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            : Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
            throw new PlatformNotSupportedException("Hidden-favorite smoke needs the authorized disposable desktop; never a personal display.");
        Dispatcher.UIThread.VerifyAccess();
        var oldBindings = owner.SessionHost.Config.ShortcutBindings;
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-hidden-favorite-");
        GalleryViewer viewer = new();
        Window window = new()
        {
            Width = 1000, Height = 700, MinWidth = 640, MinHeight = 480,
            Content = viewer, Title = "Synthetic hidden-controls favorite smoke"
        };
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task save = Task.CompletedTask, transition = Task.CompletedTask;
        var saves = new List<Task>();
        var shows = new List<Task>();
        CancellationTokenSource lifetime = new();
        ProfileLease? lease = null;
        Exception? failure = null;
        bool holdFirstSave = true;
        void CleanupFailed(string phase, Exception error)
        {
            var cleanup = new InvalidOperationException($"SMOKE hidden favorite cleanup {phase}; fixture: {root}", error);
            Console.Error.WriteLine(cleanup);
            failure = failure == null ? cleanup : new AggregateException("Hidden-favorite smoke and cleanup failures.", failure, cleanup);
        }
        try
        {
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            lease = ProfileLease.Acquire(profile);
            FavoriteStore favorites = new(profile, lease);
            GalleryEntry first = await CreateImageAsync(root);
            // Same asset, directory, SHA and post; ONLY thread differs. This must not
            // alias in FavoriteAssetKey even though GalleryImage reads identical bytes.
            GalleryEntry second = first with { Key = first.Key with { Thread = new ResourceId("4chan", "g", "902") } };
            GalleryEntry[] entries = [first, second];
            FavoriteAssetKey firstKey = KeyOf(first), secondKey = KeyOf(second);
            Require(firstKey != secondKey && firstKey.Sha256 == secondKey.Sha256,
                "Fixture must distinguish favorite identities for identical bytes across threads.");
            int index = 0, moves = 0, toggles = 0;
            var requests = new List<(FavoriteAssetKey Key, bool Desired)>();
            var changes = new List<bool>();
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();

            void Refresh() => viewer.SetFavorite(favorites.Contains(KeyOf(entries[index])),
                queue.IsPending(KeyOf(entries[index])));
            void Set(GalleryEntry entry, bool desired)
            {
                FavoriteAssetKey key = KeyOf(entry);
                requests.Add((key, desired));
                save = queue.SetAsync(key, desired, async value =>
                {
                    if (holdFirstSave) { holdFirstSave = false; await hold.Task.WaitAsync(lifetime.Token); }
                    lifetime.Token.ThrowIfCancellationRequested();
                    changes.Add(favorites.SetFavorite(FavoriteStore.From(entry.Key.Thread, entry.Asset), value));
                }, Refresh);
                saves.Add(save);
            }
            void Toggle(GalleryEntry _) => toggles++;
            async Task ShowAsync(int target)
            {
                index = target;
                GalleryEntry entry = entries[target];
                viewer.SetPosition($"{target + 1} / 2", target > 0, target < 1);
                // Retain the original producer task: WaitAsync timing out is not its completion.
                Task showing = viewer.ShowAsync(entry, lifetime.Token);
                shows.Add(showing);
                await showing.WaitAsync(Deadline);
                Require(!Badge().IsVisible, "New selection showed an old/unknown favorite before rehydration.");
                await WaitAsync(() => Find<Image>("GalleryImage").Source is Bitmap,
                    "Verified synthetic image did not decode.");
                Refresh();
                RequireTarget(entry);
            }
            void Move(int delta)
            {
                Require(transition.IsCompleted, "A second navigation overlapped the fixture's prior transition.");
                Require(index + delta is 0 or 1, "One navigation escaped the two-entry fixture.");
                moves++;
                transition = ShowAsync(index + delta);
            }

            ShortcutService.Publish(new());
            viewer.Configure(contextOpen: false, showShortcutHelp: true, videoAutoplay: false, videoStartMuted: true, videoVolume: 0);
            // Keep MediaFocus constant during hide setup: changing it deliberately
            // reveals controls, so it is not evidence of automatic collapse.
            viewer.ConfigurePlayback(new(MediaFocus: true));
            viewer.FavoriteSetRequested += Set;
            viewer.FavoriteToggleRequested += Toggle;
            viewer.MoveRequested += Move;
            try
            {
                window.Show(owner);
                window.Activate();
                await WaitAsync(() => window.IsActive && viewer.Bounds.Width > 500,
                    "Disposable favorite viewer did not become active/laid out.");
                await ShowAsync(0);

                await HideAsync();
                RequireBadge(false);
                Press(Key.M); // first hidden-controls key must perform the action, not only reveal
                Require(requests.SequenceEqual(new[] { (firstKey, true) }) && !save.IsCompleted,
                    "Hidden pick did not request exactly A, or the held save was not pending.");
                RequireBadge(false, pending: true);

                await HideAsync();
                Press(Key.Right);
                await transition.WaitAsync(Deadline);
                Require(moves == 1 && viewer.CaptureState().Key == second.Key,
                    "One hidden Right did not select exactly B.");
                await HideAsync();
                RequireBadge(false);
                Require(!save.IsCompleted && queue.IsPending(firstKey) && !queue.IsPending(secondKey)
                    && changes.Count == 0 && !favorites.Contains(firstKey) && !favorites.Contains(secondKey),
                    "A's save did not remain held and identity-local until B was current.");
                hold.SetResult();
                await save.WaitAsync(Deadline);
                await LayoutAsync();
                Require(favorites.Contains(firstKey) && !favorites.Contains(secondKey),
                    "Completing A's held save changed B's exact favorite identity.");
                RequireTarget(second);
                RequireBadge(false); // A's late completion MUST NOT mark current B
                Program.CaptureWindow(window, "smoke-hidden-favorite-late-a-current-b.png");

                await HideAsync();
                Press(Key.M);
                await save.WaitAsync(Deadline);
                RequireBadge(true);
                byte[] markedBytes = await File.ReadAllBytesAsync(Path.Combine(profile, "media-favorites.json"));
                await HideAsync();
                Press(Key.M);
                await save.WaitAsync(Deadline);
                RequireBadge(true);
                byte[] repeatedMarkedBytes = await File.ReadAllBytesAsync(Path.Combine(profile, "media-favorites.json"));
                Require(markedBytes.SequenceEqual(repeatedMarkedBytes),
                    "Repeated explicit pick mutated the persisted same-value favorite index.");

                await HideAsync();
                Press(Key.U);
                await save.WaitAsync(Deadline);
                RequireBadge(false);
                byte[] unmarkedBytes = await File.ReadAllBytesAsync(Path.Combine(profile, "media-favorites.json"));
                await HideAsync();
                Press(Key.U);
                await save.WaitAsync(Deadline);
                RequireBadge(false);
                byte[] repeatedUnmarkedBytes = await File.ReadAllBytesAsync(Path.Combine(profile, "media-favorites.json"));
                Require(unmarkedBytes.SequenceEqual(repeatedUnmarkedBytes),
                    "Repeated explicit unpick mutated the persisted same-value favorite index.");
                Require(requests.SequenceEqual(new[] { (firstKey, true), (secondKey, true),
                    (secondKey, true), (secondKey, false), (secondKey, false) }) && toggles == 0
                    && changes.Count(change => change) == 3
                    && changes.SequenceEqual(new[] { true, true, false, true, false }),
                    "M/U lost exact targets, toggled, or changed the store on an idempotent repeat.");

                await HideAsync();
                Press(Key.Left);
                await transition.WaitAsync(Deadline);
                Require(moves == 2, "One hidden Left did not produce exactly one reverse navigation.");
                RequireTarget(first);
                await HideAsync();
                RequireBadge(true);
                Require(favorites.List().Count == 1 && favorites.List()[0].Key == firstKey
                    && File.Exists(first.Asset.Path), "Unpick of B altered A or removed the shared original.");
                Program.CaptureWindow(window, "smoke-hidden-favorite-return-a.png");
            }
            finally
            {
                // Close fixture ingress before the outer cancellation/settlement snapshot.
                viewer.FavoriteSetRequested -= Set;
                viewer.FavoriteToggleRequested -= Toggle;
                viewer.MoveRequested -= Move;
            }

            ToggleButton Badge() => Find<ToggleButton>("GalleryFavoriteIndicator");
            T Find<T>(string name) where T : Control => viewer.GetGalleryVisualDescendants().OfType<T>()
                .Single(control => control.Name == name);
            async Task LayoutAsync() => await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            void RequireTarget(GalleryEntry entry) => Require(viewer.CaptureState().Key == entry.Key
                && viewer.CaptureState().PostId == entry.Post!.PostId, "Viewer lost the exact fixture target/post association.");
            async Task HideAsync()
            {
                viewer.FocusGallerySurface();
                viewer.ConfigurePlayback(new(OverlayControls: false, MediaFocus: true));
                viewer.ConfigurePlayback(new(OverlayControls: true, AutoHideControls: true, MediaFocus: true));
                await LayoutAsync();
                Require(!Find<StackPanel>("GalleryOverlayFooter").IsEffectivelyVisible
                    && !Find<ToggleButton>("GalleryFavorite").IsEffectivelyVisible,
                    "Favorite key precondition did not actually hide the toolbar and favorite button.");
                RequireBadge(favorites.Contains(KeyOf(entries[index])), queue.IsPending(KeyOf(entries[index])));
            }
            void RequireBadge(bool saved, bool pending = false)
            {
                ToggleButton badge = Badge();
                // Hidden controls leave the picture clean: the badge is fully hidden (or flashing after a key), never dimmed or half-operable.
                bool shown = badge.Opacity == 1 && badge.IsHitTestVisible && badge.Focusable;
                bool clean = badge.Opacity == 0 && !badge.IsHitTestVisible && !badge.Focusable;
                Require(badge.IsEffectivelyVisible && (shown || clean),
                    "Current favorite badge is neither operable nor completely hidden.");
                Require(GalleryViewer.FavoriteBadgeState(badge) == (saved, pending) && badge.IsChecked == saved
                    && (AutomationProperties.GetName(badge) ?? "").StartsWith(
                        Texts.T("gallery.decision.favorite"), StringComparison.Ordinal),
                    "Current favorite glyph/accessibility label does not represent persisted truth.");
                var origin = badge.TranslatePoint(new Point(0, 0), viewer);
                Require(origin is { } point && point.X >= 0 && point.Y >= 0
                    && badge.Bounds.Width > 0 && badge.Bounds.Height > 0
                    && point.X + badge.Bounds.Width <= viewer.Bounds.Width + 1
                    && point.Y + badge.Bounds.Height <= viewer.Bounds.Height + 1,
                    "Favorite badge is not laid out within the visible gallery.");
            }
            void Press(Key key)
            {
                var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = viewer };
                viewer.RaiseEvent(down);
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = viewer });
                Require(down.Handled, "Gallery shortcut was not routed: " + key);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            window.IsEnabled = false;
            hold.TrySetResult();
            Task cancellation = lifetime.CancelAsync();
            Task stopped = viewer.StopAsync();
            // One separate diagnostic budget covers ALL saves, the original Show tasks,
            // navigation, cancellation callbacks and viewer stop, even after an earlier timeout.
            Task settlement = Task.WhenAll(saves.Concat(shows).Append(transition).Append(cancellation).Append(stopped));
            try { await settlement.WaitAsync(CleanupDeadline); }
            catch (OperationCanceledException) when (failure != null && settlement.IsCompleted && lifetime.IsCancellationRequested)
            { /* Expected cancellation of fixture work after the primary failure; all tasks settled. */ }
            catch (Exception error) { CleanupFailed("settlement failed", error); }

            if (settlement.IsCompleted && stopped.IsCompletedSuccessfully)
            {
                // A faulted/cancelled save is terminal too; it remains a failure, not a PASS.
                try { window.Content = null; window.Close(); }
                catch (Exception error) { CleanupFailed("window close failed", error); }
                try { lease?.Dispose(); lifetime.Dispose(); Directory.Delete(root, recursive: true); }
                catch (Exception error) { CleanupFailed("resource release failed", error); }
            }
            else
            {
                IncompleteCleanups.Add(new(root, lease, viewer, window, lifetime, settlement));
                // Observe any eventual fault without retrying/claiming a successful retirement.
                _ = settlement.ContinueWith(task => Console.Error.WriteLine("SMOKE hidden favorite late settlement failure: " + task.Exception),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                CleanupFailed("cleanup-incomplete: lease, media and viewer retained until process exit",
                    new TimeoutException($"Cleanup budget {CleanupDeadline}; settlement={settlement.Status}, viewer-stop={stopped.Status}. Root must terminate the isolated process."));
            }
            try { ShortcutService.Publish(oldBindings); }
            catch (Exception error) { CleanupFailed("shortcut restoration failed", error); }
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("SMOKE hidden favorite companion passed: laid-out passive badge; A-save completion while identical bytes in B are current; hidden M/M/U/U with byte-stable no-ops; one Right/Left exact target. Managed routed input, private PNG/profile; not shell wiring or native video acceptance.");
    }

    private static FavoriteAssetKey KeyOf(GalleryEntry entry) => FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);

    private static async Task<GalleryEntry> CreateImageAsync(string root)
    {
        string directory = Path.Combine(root, "synthetic-media");
        Directory.CreateDirectory(directory);
        using SKBitmap bitmap = new(320, 180);
        bitmap.Erase(SKColors.DarkOrange);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();
        const string file = "hidden-favorite.png";
        await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
        var record = new { MediaIdentifier = "hidden-favorite", SourceUrl = "https://offline.invalid/hidden-favorite.png",
            FileName = file, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        { Version = 1, Entries = new Dictionary<string, object> { [file] = record } }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage().Single();
        return new(new GalleryAssetKey(new ResourceId("4chan", "g", "901"), directory, asset.AssetId), manifest, asset,
            new CapturedPost("903", 0, PostFieldPresence.Html, Html: "<p>Source-built hidden-favorite fixture</p>"), null, false, false);
    }

    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
            await Task.Delay(25);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
