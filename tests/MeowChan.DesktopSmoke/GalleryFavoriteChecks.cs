using System.Reflection;
using Avalonia;
using MeowChan.Desktop.Feedback;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Favorites;

namespace MeowChan.DesktopSmoke;

// Real controls, real verified synthetic media and real disposable session store.
// This lane does not assert a Windows native-video overlay or physical keyboard.
internal static class GalleryFavoriteChecks
{
    internal static async Task RunAsync(MainWindow window, GalleryViewer viewer, GalleryEntry entry)
    {
        var oldBindings = window.SessionHost.Config.ShortcutBindings;
        string oldLanguage = Texts.Language;
        ShortcutService.Publish(new());
        var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
        GalleryEntry current = entry;
        Task latest = Task.CompletedTask;
        TaskCompletionSource? hold = null;
        bool failNext = false;
        int writes = 0;
        var savedIntents = new List<bool>();
        var errors = new List<GalleryDiagnosticNotice>();
        void Refresh() => viewer.SetFavorite(window.SessionHost.IsFavorite(current),
            queue.IsPending(FavoriteAssetKey.From(current.Key.Thread, current.Asset)));
        void Set(GalleryEntry requested, bool desired)
        {
            FavoriteAssetKey key = FavoriteAssetKey.From(requested.Key.Thread, requested.Asset);
            latest = queue.SetAsync(key, desired, async value =>
            {
                writes++;
                savedIntents.Add(value);
                if (hold is { } gate) await gate.Task;
                if (failNext) { failNext = false; throw new IOException("Synthetic favorite persistence failure"); }
                await window.SessionHost.SetFavoriteAsync(requested, value);
            }, Refresh);
        }
        void Toggle(GalleryEntry requested)
        {
            FavoriteAssetKey key = FavoriteAssetKey.From(requested.Key.Thread, requested.Asset);
            Set(requested, !queue.Target(key, window.SessionHost.IsFavorite(requested)));
        }
        viewer.FavoriteSetRequested += Set;
        viewer.FavoriteToggleRequested += Toggle;
        viewer.DiagnosticNotice += errors.Add;
        try
        {
            await window.SessionHost.SetFavoriteAsync(entry, false);
            Refresh();
            viewer.FocusGallerySurface();
            HideControls();
            RequireBadge(false);
            Program.CaptureWindow(window, "smoke-favorite-unmarked-hidden.png");
            hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Press(Key.M); Press(Key.M); Press(Key.M);
            Require(writes == 1 && queue.IsPending(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset)),
                "Repeated explicit marks created parallel saves.");
            RequireBadge(false, pending: true); // persisted state, NOT optimistic success
            Press(Key.U);
            hold.SetResult();
            await latest;
            hold = null;
            Require(savedIntents.SequenceEqual(new[] { true, false }) && !window.SessionHost.IsFavorite(entry),
                "Unmark during a pending mark was lost or inverted.");
            HideControls(); RequireBadge(false);
            Press(Key.M); await latest;
            HideControls(); RequireBadge(true);
            Program.CaptureWindow(window, "smoke-favorite-marked-hidden.png");
            Press(Key.M); await latest; RequireBadge(true); // idempotent repeat
            Press(Key.U); await latest; RequireBadge(false);
            Press(Key.F); await latest; RequireBadge(true); // compatible old toggle

            failNext = true;
            Press(Key.U);
            try { await latest; throw new InvalidOperationException("Synthetic persistence failure was swallowed by the queue."); }
            catch (IOException error) { viewer.SetFavoriteError(error.Message); }
            Require(!queue.IsPending(FavoriteAssetKey.From(entry.Key.Thread, entry.Asset)) && window.SessionHost.IsFavorite(entry),
                "Failed favorite save left pending state or changed persisted truth.");
            RequireBadge(true);
            Require(errors.Count == 1 && errors[0].Operation == "favorite", "Failure did not produce exactly one diagnostic.");
            Press(Key.U); await latest; viewer.SetFavoriteError(null); RequireBadge(false);

            await window.SessionHost.SetFavoriteAsync(entry, true);
            Refresh();
            GalleryEntry other = entry with { Key = new GalleryAssetKey(new("4chan", "g", "125"), entry.Key.Directory, entry.Asset.AssetId) };
            current = other;
            await viewer.ShowAsync(other, CancellationToken.None);
            Require(!Badge().IsVisible, "Navigation reused the previous item's favorite indicator before its state was read.");
            Refresh(); HideControls(); RequireBadge(false);
            current = entry;
            await viewer.ShowAsync(entry, CancellationToken.None);
            Refresh(); HideControls(); RequireBadge(true);
            Texts.SetLanguage("de");
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(Badge()) == Texts.T("viewer.favorite.saved"));
            RequireBadge(true);
            Program.CaptureWindow(window, "smoke-favorite-marked-hidden-de.png");
            await CheckShellFailureOwnershipAsync(window, entry);
            await viewer.StopAsync();
            Require(!Badge().IsVisible, "Retired gallery retained the favorite indicator.");
            Console.WriteLine("SMOKE gallery favorites: hidden controls + truthful saved/pending badge, M/U repeat/opposite, F compatibility, synthetic failure/retry, exact-key navigation and EN/DE passed; disposable session store only.");
        }
        finally
        {
            hold?.TrySetResult();
            viewer.FavoriteSetRequested -= Set;
            viewer.FavoriteToggleRequested -= Toggle;
            viewer.DiagnosticNotice -= errors.Add;
            ShortcutService.Publish(oldBindings);
            Texts.SetLanguage(oldLanguage);
        }

        Border Badge() => viewer.GetGalleryVisualDescendants().OfType<Border>().Single(c => c.Name == "GalleryFavoriteIndicator");
        void HideControls()
        {
            viewer.ConfigurePlayback(new GalleryPlaybackPreferences { OverlayControls = false, AutoHideControls = true, MediaFocus = true });
            viewer.ConfigurePlayback(new GalleryPlaybackPreferences { OverlayControls = true, AutoHideControls = true, MediaFocus = true });
            Require(!viewer.GetGalleryVisualDescendants().OfType<Control>().Single(c => c.Name == "GalleryOverlayFooter").IsVisible,
                "Favorite fixture did not actually hide the controls.");
        }
        void RequireBadge(bool saved, bool pending = false)
        {
            Border badge = Badge();
            Require(badge.IsEffectivelyVisible && !badge.IsHitTestVisible && !badge.Focusable,
                "Favorite badge is hidden or steals input.");
            string label = Texts.T(saved ? "viewer.favorite.saved" : "viewer.favorite.unsaved");
            Require(((TextBlock)badge.Child!).Text == (saved ? "★" : "☆") + (pending ? " …" : "")
                && AutomationProperties.GetName(badge)!.StartsWith(label, StringComparison.Ordinal),
                "Favorite badge misrepresents the saved state or pending write.");
        }
        void Press(Key key)
        {
            var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = viewer };
            viewer.RaiseEvent(e);
            Require(e.Handled, "Favorite gallery shortcut not routed: " + key);
        }
    }
    // Actual MainWindow command wiring with a real failed atomic publication, not
    // the local handlers above. MainWindow has no active gallery here, as after close.
    private static async Task CheckShellFailureOwnershipAsync(MainWindow window, GalleryEntry entry)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = window.SessionHost;
        var gate = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", fields)!.GetValue(host)!;
        var feedback = (SessionFeedbackStore)typeof(MainWindow).GetField("_feedback", fields)!.GetValue(window)!;
        MethodInfo set = typeof(MainWindow).GetMethod("SetGalleryFavoriteAsync", fields)!;
        string path = Path.Combine(host.ProfileDirectory, "media-favorites.json");
        string backup = path + ".bak";
        string heldBackup = backup + ".smoke-backup";
        Require(File.Exists(path) && !Directory.Exists(backup) && !File.Exists(heldBackup) && !Directory.Exists(heldBackup),
            "Favorite fault fixture is not fresh.");
        bool hadBackup = File.Exists(backup);
        int before = feedback.Entries.Where(e => e.Operation == "gallery.favorite").Sum(e => e.Count);
        bool saved = host.IsFavorite(entry);
        await gate.WaitAsync();
        Task? first = null, repeated = null;
        bool released = false;
        try
        {
            if (hadBackup) File.Move(backup, heldBackup);
            // Preserve the readable index: replacing it with a directory makes
            // FavoriteStore.Read see an empty store and unmark becomes a no-op.
            // A forbidden backup target instead fails a real attempted write.
            Directory.CreateDirectory(backup);
            first = (Task)set.Invoke(window, new object[] { entry, !saved })!;
            repeated = (Task)set.Invoke(window, new object[] { entry, !saved })!;
            Require(!first.IsCompleted && !repeated.IsCompleted, "Shell fault fixture did not hold the real command.");
            gate.Release(); released = true;
            await Task.WhenAll(first, repeated);
            SessionFeedback[] failures = feedback.Entries.Where(e => e.Operation == "gallery.favorite").ToArray();
            Console.WriteLine($"SMOKE favorite failure ownership: prior={before}, after={failures.Sum(e => e.Count)}, contexts={string.Join(";", failures.Select(e => e.Context))}.");
            Require(failures.Sum(e => e.Count) == before + 1 && failures[^1].Context!.Contains(entry.Asset.AssetId + "@"),
                "Missing or repeated operation-owned favorite failure without an active gallery.");
            Require(host.IsFavorite(entry) == saved, "Failed shell write changed the passive persisted favorite index.");
            Console.WriteLine("SMOKE actual MainWindow favorite failure: two coalesced calls, real invalid backup destination, no active gallery, exactly one attributed diagnostic, saved truth retained.");
        }
        finally
        {
            if (!released) gate.Release();
            if (first != null) try { await first; } catch { }
            if (repeated != null) try { await repeated; } catch { }
            if (Directory.Exists(backup)) Directory.Delete(backup);
            if (File.Exists(heldBackup)) File.Move(heldBackup, backup);
        }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
