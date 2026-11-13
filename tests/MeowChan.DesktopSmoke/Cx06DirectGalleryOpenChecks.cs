using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Feedback;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

// Called by the already registered media gallery-return journey. Offline, deterministic managed
// boundaries only: the frozen window's first resolver enumeration and the shell's cleanup barrier.
// Neither the page's host delegate nor the production resolver/gallery admission is replaced.
internal static partial class MediaPageChecks
{
    private static async Task DirectGalleryOpenGuardAsync(MainWindow shell, MediaPage page)
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo cleanup = typeof(MainWindow).GetField("_galleryCleanupTask", PrivateInstance)!;
        FieldInfo request = typeof(MainWindow).GetField("_galleryRequest", PrivateInstance)!;
        MethodInfo present = typeof(MediaPage).GetMethod("Present", PrivateInstance)!;
        SessionFeedbackStore feedback = (SessionFeedbackStore)typeof(MainWindow).GetField("_feedback", PrivateInstance)!.GetValue(shell)!;
        ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
        ContentControl mediaHost = shell.FindControl<ContentControl>("MediaPageHost")!;
        Grid downloads = shell.FindControl<Grid>("WorkspaceBody")!;
        Button media = shell.FindControl<Button>("MediaNavigationButton")!;
        Button away = shell.FindControl<Button>("DownloadsNavigationButton")!;
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        int Failures() => feedback.Entries.Where(entry => entry.Operation == "media.gallery").Sum(entry => entry.Count);
        int Request() => (int)request.GetValue(shell)!;
        void RePresent()
        {
            object? rows = page.Grid.ItemsSource;
            MediaLibraryEntry[] shown = page.ShownEntries.ToArray();
            present.Invoke(page, null);
            Require(!ReferenceEquals(rows, page.Grid.ItemsSource) && shown.SequenceEqual(page.ShownEntries),
                "CX-06 must replace the rows without changing the frozen logical presentation.");
        }
        async Task EnterAsync()
        {
            page.Grid.Focus();
            page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
        }
        async Task CloseToMediaAsync()
        {
            GalleryViewer opened = (GalleryViewer)galleryHost.Content!;
            // A kept image is shown in the same turn the gallery opens, before the first layout has attached its controls.
            await UntilAsync(() => opened.GetGalleryVisualDescendants().OfType<Button>().Any(item => item.Name == "GalleryClose"));
            GalleryViewerChecksAccess.Click(opened, "GalleryClose");
            await UntilAsync(() => !galleryHost.IsVisible);
            await shell.GalleryCleanupTask.WaitAsync(Deadline);
        }
        async Task OnMediaAsync()
        {
            if (!mediaHost.IsVisible) Click(media);
            await UntilAsync(() => mediaHost.IsVisible);
            await page.BuildTask.WaitAsync(Deadline);
            Require(ReferenceEquals(shell.MediaPageForTests, page), "CX-06 must keep the same media page.");
        }
        void NoGallery(int failures, string context)
        {
            Require(!galleryHost.IsVisible && galleryHost.Content is not GalleryViewer,
                "CX-06 " + context + ": a stale direct request opened a gallery.");
            Require(Failures() == failures, "CX-06 " + context + ": stale completion emitted gallery feedback.");
        }

        // The indexer supplies the chosen tile immediately. The resolver enumerates the frozen
        // window only after opening its local reader; navigate there, before success/missing/error.
        foreach (string outcome in new[] { "success", "missing", "error", "roundtrip", "presentation", "presentation-roundtrip", "valid-presentation" })
        {
            await OnMediaAsync();
            int failures = Failures(), entered = 0;
            ComboBox sort = C<ComboBox>(page, "MediaSort");
            int oldSort = sort.SelectedIndex;
            MediaLibraryEntry[] frozen = [page.ShownEntries[0]];
            if (outcome == "missing") frozen[0] = frozen[0] with { AssetId = "cx06-no-longer-present" };
            ResolverBoundaryWindow window = new(frozen, () =>
            {
                entered++;
                if (outcome == "valid-presentation") RePresent();
                else if (outcome is "presentation" or "presentation-roundtrip")
                {
                    MediaLibrarySort order = page.Sort;
                    sort.SelectedIndex = oldSort == 0 ? 1 : 0;
                    Require(page.Sort != order, "CX-06 sort must change the requested presentation, even if the entries keep their order.");
                    if (outcome == "presentation-roundtrip") sort.SelectedIndex = oldSort;
                }
                else
                {
                    Click(away);
                    Require(downloads.IsVisible && !mediaHost.IsVisible, "CX-06 must really navigate away during resolution.");
                    if (outcome == "roundtrip") Click(media);
                }
            }, fail: outcome == "error");
            await page.OpenGallery!(window, 0).WaitAsync(Deadline);
            Require(entered == 1, "CX-06 resolver boundary was not exercised exactly once: " + outcome);
            if (outcome == "valid-presentation")
            {
                Require(galleryHost.IsVisible && galleryHost.Content is GalleryViewer { ClosesToMedia: true },
                    "CX-06 rejected a current tile after resolver re-presentation.");
                GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
                await UntilAsync(() => viewer.CaptureState().PostId == frozen[0].PostId);
                Require(Failures() == failures, "CX-06 current tile emitted gallery failure feedback.");
                await CloseToMediaAsync();
                Require(mediaHost.IsVisible, "CX-06 current tile did not close back to media.");
            }
            else
            {
                NoGallery(failures, "resolver " + outcome);
                if (outcome == "presentation") sort.SelectedIndex = oldSort;
                else if (outcome is not ("roundtrip" or "presentation-roundtrip"))
                    Require(downloads.IsVisible, "CX-06 completion changed the destination page.");
            }
        }

        // A current failure still belongs to the page and must not be silently swallowed.
        await OnMediaAsync();
        int beforeFailure = Failures();
        await page.OpenGallery!(new ResolverBoundaryWindow([page.ShownEntries[0]], () => { }, fail: true), 0).WaitAsync(Deadline);
        Require(Failures() == beforeFailure + 1, "CX-06 must retain feedback for a current resolver failure.");

        // Native cleanup is already independently tested. Hold its existing shell barrier here,
        // and prove that the original page/menu request survives all the way to admission.
        foreach (string outcome in new[] { "navigation", "roundtrip", "presentation", "cleanup-error", "menu-request", "valid-menu", "valid-enter", "valid-selection" })
        {
            await OnMediaAsync();
            page.Grid.Selection.Clear();
            page.Grid.Selection.Select(0);
            if (outcome != "valid-enter") page.Grid.Selection.Select(1);
            int selectedCount = page.SelectedTiles.Count;
            MediaLibraryEntry chosen = page.ShownEntries[0];
            ComboBox sort = C<ComboBox>(page, "MediaSort");
            int oldSort = sort.SelectedIndex;
            int failures = Failures(), priorRequest = Request();
            Task previousCleanup = shell.GalleryCleanupTask;
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cleanup.SetValue(shell, release.Task);
            Task? opening = null;
            bool menuCurrent = true;
            bool valid = outcome is "valid-menu" or "valid-enter" or "valid-selection";
            try
            {
                opening = outcome is "menu-request" or "valid-menu"
                    ? page.ExecuteMediaMenuActionAsync!("media.open", page.ShownEntries[0], () => menuCurrent)
                    : outcome == "valid-enter" ? EnterAsync()
                    : outcome == "valid-selection" ? page.OpenSelectionGalleryAsync() : page.OpenSelectedAsync();
                await UntilAsync(() => Request() != priorRequest || opening!.IsCompleted);
                Require(!opening.IsCompleted && Request() != priorRequest && !galleryHost.IsVisible,
                    "CX-06 did not reach the held gallery cleanup boundary: " + outcome);
                if (outcome == "menu-request") menuCurrent = false;
                else if (outcome == "presentation") sort.SelectedIndex = oldSort == 0 ? 1 : 0;
                else if (!valid)
                {
                    Click(away);
                    if (outcome == "roundtrip") Click(media);
                }
                else if (outcome != "valid-menu") RePresent();
                if (outcome == "cleanup-error") release.TrySetException(new IOException("CX-06 delayed cleanup failure"));
                else release.TrySetResult();
                await opening.WaitAsync(Deadline);
                if (valid)
                {
                    Require(galleryHost.IsVisible && galleryHost.Content is GalleryViewer { ClosesToMedia: true },
                        "CX-06 suppressed a current selection gallery after cleanup.");
                    Require(page.SelectedTiles.Count == selectedCount, "CX-06 changed the valid gallery selection.");
                    GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
                    await UntilAsync(() => viewer.CaptureState().PostId == chosen.PostId);
                    Require(Failures() == failures, "CX-06 current gallery emitted failure feedback.");
                    await CloseToMediaAsync();
                    Require(mediaHost.IsVisible && page.SelectedTiles.Count == selectedCount,
                        "CX-06 selection gallery did not close back to the same media selection.");
                }
                else
                {
                    NoGallery(failures, outcome);
                    if (outcome is not ("menu-request" or "roundtrip" or "presentation"))
                        Require(downloads.IsVisible, "CX-06 cleanup changed the destination page.");
                }
            }
            finally
            {
                release.TrySetResult();
                try
                {
                    if (opening != null) await opening.WaitAsync(Deadline);
                    // Observe the deliberately faulted aggregate before removing only our fake barrier.
                    try { await shell.GalleryCleanupTask.WaitAsync(Deadline); }
                    catch (IOException) when (outcome == "cleanup-error") { }
                }
                finally
                {
                    cleanup.SetValue(shell, previousCleanup);
                    if (outcome == "presentation") sort.SelectedIndex = oldSort;
                }
            }
        }
        Console.WriteLine("SMOKE CX-06 direct media gallery: stale resolver success/missing/error, navigation and presentation roundtrips suppressed; current tile survives resolver re-presentation; current error retained; cleanup navigation/roundtrip/presentation/error and stale menu request suppressed; current menu and re-presented Enter/selection galleries still open and close to media. Existing nested reader-return journeys also ran.");
    }

    private sealed class ResolverBoundaryWindow : IReadOnlyList<MediaLibraryEntry>
    {
        private readonly IReadOnlyList<MediaLibraryEntry> _entries;
        private readonly Action _resolving;
        private readonly bool _fail;
        private int _entered;

        internal ResolverBoundaryWindow(IReadOnlyList<MediaLibraryEntry> entries, Action resolving, bool fail = false)
        { _entries = entries; _resolving = resolving; _fail = fail; }

        public int Count => _entries.Count;
        public MediaLibraryEntry this[int index] => _entries[index];
        public IEnumerator<MediaLibraryEntry> GetEnumerator()
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                if (Dispatcher.UIThread.CheckAccess()) _resolving();
                else Dispatcher.UIThread.InvokeAsync(_resolving).GetAwaiter().GetResult();
                if (_fail) throw new IOException("CX-06 delayed resolver failure");
            }
            return _entries.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
