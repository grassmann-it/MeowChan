using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

// Controlled work uses the real viewer/control tree. No native video is needed to model a decoder
// that ignores cancellation: the task and its eventual bitmap remain owned until explicitly released.
internal static class GalleryWorkloadChecks
{
    internal static async Task RunAsync(MainWindow owner, GalleryEntry saved)
    {
        GalleryViewer viewer = new();
        Window host = new() { Width = 1100, Height = 700, Content = viewer, Title = "PF05 gallery workload" };
        string language = Texts.Language;
        try
        {
            host.Show(owner);
            host.Activate();
            await Program.WaitUntilAsync(() => host.IsActive);
            await SettleAsync();
            GalleryEntry first = saved with
            {
                Asset = saved.Asset with { OriginalFileName = "context-only.pdf" },
                Post = saved.Post! with { Html = "first mapping" }
            };
            GalleryEntry second = first with { Post = first.Post! with { PostId = "987654", Html = "second mapping" } };
            viewer.Configure(false, false, false, true, 0);
            await viewer.ShowAsync(first, new[] { first, second }, 1, CancellationToken.None);
            await viewer.ShowAsync(second, new[] { first, second }, 1, CancellationToken.None);
            Require(viewer.ContextBuildCount == 0 && viewer.AssociationIndexBuildCount == 1,
                "Closed context navigation rebuilt controls or reindexed an unchanged mapping.");
            Click(Control<Button>(viewer, "GalleryToggleContext"));
            await SettleAsync();
            Require(viewer.ContextBuildCount == 1 && Text(viewer).Contains("second mapping"),
                "Opening context did not materialize only the newest entry.");
            Button obsoleteRead = Control<Button>(viewer, "GalleryReadPost");
            GalleryEntry? requested = null;
            viewer.ReadPostRequested += value => requested = value;
            Require(obsoleteRead.Focus() && obsoleteRead.IsKeyboardFocusWithin,
                "Cannot establish context-action focus before mapping replacement. " + DescribeFocus(host));
            GalleryEntry revised = second with { Post = second.Post! with { Html = "revised mapping" } };
            long builds = viewer.ContextBuildCount;
            await viewer.ShowAsync(revised, new[] { first, revised }, 2, CancellationToken.None);
            await SettleAsync();
            Require(viewer.ContextBuildCount == builds + 1 && viewer.AssociationIndexBuildCount == 2,
                "A combined entry/mapping handoff rebuilt the old context or missed its revision.");
            Require(Text(viewer).Contains("revised mapping") && !Text(viewer).Contains("second mapping"),
                "Same-key mapping replacement retained old post text.");
            Require(Control<Button>(viewer, "GalleryReadPost").IsKeyboardFocusWithin,
                "Context replacement lost focus on the equivalent action.");
            Click(obsoleteRead);
            Require(requested == null, "An obsolete context control invoked an old post action.");
            Click(Control<Button>(viewer, "GalleryReadPost"));
            Require(ReferenceEquals(requested, revised), "The current context action did not use the current entry.");

            builds = viewer.ContextBuildCount;
            Require(Control<Button>(viewer, "GalleryReadPost").IsKeyboardFocusWithin,
                "Context-action focus was lost before localization. " + DescribeFocus(host));
            Texts.SetLanguage(language == "de" ? "en" : "de");
            await owner.ArchivePage.ReaderLocalizationTask;
            await SettleAsync();
            Require(viewer.ContextBuildCount == builds + 1,
                $"Open-context localization expected one rebuild, observed {viewer.ContextBuildCount - builds}.");
            Require(Control<Button>(viewer, "GalleryReadPost").IsKeyboardFocusWithin,
                "Open-context localization lost focus. Gallery host: " + DescribeFocus(host)
                + "; underlying shell: " + DescribeFocus(owner));
            Click(Control<Button>(viewer, "GalleryToggleContext"));
            builds = viewer.ContextBuildCount;
            Texts.SetLanguage(language);
            await owner.ArchivePage.ReaderLocalizationTask;
            await viewer.ShowAsync(first, new[] { first, revised }, 2, CancellationToken.None);
            Require(viewer.ContextBuildCount == builds, "Closed context was rebuilt by language or navigation changes.");
            Click(Control<Button>(viewer, "GalleryToggleContext"));
            await SettleAsync();
            Require(viewer.ContextBuildCount == builds + 1 && Text(viewer).Contains("first mapping"),
                "Reopening context retained obsolete language/entry data.");
            ComboBox selector = Control<ComboBox>(viewer, "GalleryPostAssociation");
            selector.SelectedIndex = 1;
            Require(viewer.CaptureState().PostId == revised.Post!.PostId, "The indexed association selector lost post identity.");
            // Media focus must keep a failed write reachable even though normal metadata is hidden.
            viewer.SetFavoriteError("PF05 visible failure");
            Click(Control<Button>(viewer, "GalleryToolsToggle"));
            Require(Control<SelectableTextBlock>(viewer, "GalleryFavoriteError").IsEffectivelyVisible,
                "Media focus hid an actionable write failure.");

            // Supersede an executing decoder, then supersede its waiting replacement. Only the latest
            // request may decode/publish; the canceled native request still owns its task until release.
            Click(Control<Button>(viewer, "GalleryToggleContext"));
            static (Bitmap Bitmap, GalleryImageInfo Info) DecodeFixture(string path)
            {
                Bitmap bitmap = new(path);
                return (bitmap, new GalleryImageInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height, "PNG", false));
            }
            TaskCompletionSource<(Bitmap, GalleryImageInfo)> release = new();
            int decodes = 0;
            CancellationToken executing = default;
            viewer.DecodeImageAsync = (_, _, token) =>
            {
                if (++decodes == 1) { executing = token; return release.Task; }
                return Task.FromResult(DecodeFixture(saved.Asset.Path));
            };
            Task old = viewer.ShowAsync(saved, new[] { saved }, 3, CancellationToken.None);
            await Program.WaitUntilAsync(() => decodes == 1);
            Task skipped = viewer.ShowAsync(saved with { Post = second.Post }, new[] { saved }, 3, CancellationToken.None);
            Task latest = viewer.ShowAsync(saved with { Post = first.Post }, new[] { saved }, 3, CancellationToken.None);
            Require(executing.IsCancellationRequested && decodes == 1, "Navigation relinquished an executing decoder early.");
            release.SetResult(DecodeFixture(saved.Asset.Path));
            await Task.WhenAll(old, skipped, latest);
            Require(decodes == 2 && viewer.CaptureState().PostId == first.Post!.PostId,
                "Rapid navigation decoded a superseded waiter or published an obsolete entry.");
            await viewer.StopAsync();
            await CheckThumbnailOwnershipAsync(host, saved);
        }
        finally
        {
            await viewer.StopAsync();
            host.Close();
            Texts.SetLanguage(language);
            await owner.ArchivePage.ReaderLocalizationTask;
            // Closing an owned native window does not synchronously reactivate its owner.
            // Drain language/layout work before handing later shell commands back to it.
            await SettleAsync();
            owner.Activate();
            await Program.WaitUntilAsync(() => !host.IsVisible && owner.IsActive);
            await SettleAsync();
            Require(owner.IsActive && Texts.Language == language,
                "The workload fixture did not restore its owner's native activation/language. " + DescribeFocus(owner));
        }
    }

    private static async Task CheckThumbnailOwnershipAsync(Window host, GalleryEntry saved)
    {
        ArchiveAttachmentView view = new();
        StackPanel panel = new();
        panel.Children.Add(view);
        host.Content = new ScrollViewer { Content = panel };
        int loads = 0;
        CancellationToken executing = default;
        TaskCompletionSource<Bitmap?> release = new();
        ArchiveAttachmentRow Row() => new(saved.Attachment ?? new CapturedAttachment(0, "fixture"))
        {
            LoadThumbnail = token =>
            {
                loads++;
                if (loads == 1) { executing = token; return release.Task; }
                return Task.FromResult<Bitmap?>(new Bitmap(saved.Asset.Path));
            }
        };
        ArchiveAttachmentView.SetLoadingSuspended(panel, true);
        view.DataContext = Row();
        await SettleAsync();
        Require(loads == 0, "A covered attachment admitted new thumbnail work.");
        ArchiveAttachmentView.SetLoadingSuspended(panel, false);
        await Program.WaitUntilAsync(() => loads == 1);
        ArchiveAttachmentView.SetLoadingSuspended(panel, true);
        Require(executing.IsCancellationRequested, "Coverage did not cancel a queued/running thumbnail cooperatively.");
        ArchiveAttachmentView.SetLoadingSuspended(panel, false);
        view.DataContext = Row();
        await SettleAsync();
        Require(loads == 1, "A replacement thumbnail started before the native owner actually returned.");
        release.SetResult(new Bitmap(saved.Asset.Path));
        await Program.WaitUntilAsync(() => loads == 2 && view.Thumbnail.Source is Bitmap);
        await SettleAsync();
        Require(loads == 2, "Uncovering did not coalesce to exactly one current thumbnail demand.");

        // Cancel a waiter before it enters the backend decoder. Releasing its queue slot while
        // covered must not admit it; uncovering gets a fresh, current request instead.
        TaskCompletionSource allowDecode = new();
        int waiters = 0, nativeDecodes = 0;
        view.DataContext = new ArchiveAttachmentRow(saved.Attachment ?? new CapturedAttachment(0, "fixture"))
        {
            LoadThumbnail = async token =>
            {
                waiters++;
                await allowDecode.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                nativeDecodes++;
                return new Bitmap(saved.Asset.Path);
            }
        };
        await Program.WaitUntilAsync(() => waiters == 1);
        ArchiveAttachmentView.SetLoadingSuspended(panel, true);
        allowDecode.SetResult();
        await SettleAsync();
        Require(nativeDecodes == 0, "A covered thumbnail waiter entered the backend decoder.");
        ArchiveAttachmentView.SetLoadingSuspended(panel, false);
        await Program.WaitUntilAsync(() => nativeDecodes == 1 && view.Thumbnail.Source is Bitmap);
        Require(waiters == 2, "A canceled thumbnail waiter was not replaced exactly once.");

        // A diagnostic timeout reports an error immediately, while the real owner still drains.
        TaskCompletionSource<Bitmap?> timedOut = new();
        int timeoutLoads = 0;
        view.LoadTimeout = TimeSpan.FromMilliseconds(20);
        view.DataContext = new ArchiveAttachmentRow(saved.Attachment ?? new CapturedAttachment(0, "fixture"))
        {
            LoadThumbnail = _ => ++timeoutLoads == 1 ? timedOut.Task
                : Task.FromResult<Bitmap?>(new Bitmap(saved.Asset.Path))
        };
        await Program.WaitUntilAsync(() => view.PreviewStatus == Texts.T("archive.previewLoadFailed"));
        ArchiveAttachmentView.SetLoadingSuspended(panel, true);
        ArchiveAttachmentView.SetLoadingSuspended(panel, false);
        Require(timeoutLoads == 1, "A timed-out native owner was replaced before it returned.");
        // Remove the failed view before any retry: detached late results must still be released.
        host.Content = null;
        timedOut.SetResult(new Bitmap(saved.Asset.Path));
        await SettleAsync();
        Require(timeoutLoads == 1 && view.Thumbnail.Source == null, "Detaching retained/restarted a late thumbnail.");
    }

    private static string DescribeFocus(Window window)
    {
        var focused = window.FocusManager?.GetFocusedElement();
        string target = focused is Control control
            ? control.GetType().Name + "#" + control.Name + "; top-level="
                + (TopLevel.GetTopLevel(control) as Window)?.Title + "; ancestors="
                + string.Join("/", control.GetVisualAncestors().OfType<Control>().Select(value => value.GetType().Name + "#" + value.Name))
            : focused?.GetType().Name ?? "none";
        return "active=" + window.IsActive + "; focused=" + target;
    }

    private static string Text(GalleryViewer viewer) => string.Join("\n", viewer.GetGalleryVisualDescendants()
        .OfType<SelectableTextBlock>().Select(value => value.Text));
    private static T Control<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(value => value.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
