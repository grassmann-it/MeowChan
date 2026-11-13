using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// D1 drop target. Raw drag events go through Avalonia's DragDropDevice exactly as the X11 XDND
// target hands them over (hit test, inherited AllowDrop, routed Enter/Over/Leave/Drop), but no
// XDND client or selection transfer is involved: native X11/Wayland/Windows/macOS drags are untested.
internal static class UrlDropChecks
{
    private const DragDropEffects BrowserEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    public static async Task RunAsync(MainWindow window)
    {
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        Grid overlay = window.FindControl<Grid>("DropOverlay")!;
        TextBlock title = window.FindControl<TextBlock>("DropOverlayTitle")!;
        Button add = window.FindControl<Button>("AddUrlButton")!;
        Expander report = (Expander)window.FindControl<Border>("AdmissionResultsPanel")!.Child!;
        int? clipboardReads = ClipboardChecks.FixtureReadCount;
        const string draft = "https://unsupported.invalid/draft";
        input.Text = draft;

        // Start on another page: the drop returns to Downloads, where the Add results are.
        SmokeActionClick.Execute(window.FindControl<Button>("ArchiveNavigationButton")!);
        await WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Archive);
        Control archive = window.FindControl<Control>("ArchivePageHost")!;
        // The platform hit-tests the real layout; an unarranged page would put the point on the resize grip.
        await WaitUntilAsync(() => archive.IsEffectivelyVisible && archive.Bounds.Width > 0 && archive.Bounds.Height > 0);
        const string links = "https://unsupported.invalid/dropped-one\r\nnot a URL\nhttps://unsupported.invalid/dropped-two";
        using DataTransfer text = Transfer(DataTransferItem.CreateText(links));
        await WaitUntilAsync(() => window.DropAvailabilityForTests.Length == 0, () => "drop availability: " + window.DropAvailabilityForTests);
        Interactive? reached = null;
        void Probe(object? sender, DragEventArgs e) => reached = e.Source as Interactive;
        window.AddHandler(DragDrop.DragEnterEvent, Probe, handledEventsToo: true);
        DragDropEffects entered = Raise(window, RawDragEventType.DragEnter, archive, text);
        window.RemoveHandler(DragDrop.DragEnterEvent, Probe);
        if (entered != DragDropEffects.Copy || !overlay.IsVisible || title.Text != Texts.T("drop.links.title"))
            throw new InvalidOperationException($"A link drag did not offer Copy with the drop overlay: effect={entered}, overlay={overlay.IsVisible}, "
                + $"title={title.Text}, target={reached?.GetType().Name ?? "none"}, refused={window.DropAvailabilityForTests}, {HitDiagnostics(window, archive)}.");
        if (Raise(window, RawDragEventType.DragOver, window.FindControl<Control>("WorkspaceTitleBar")!, text) != DragDropEffects.Copy)
            throw new InvalidOperationException("Moving the link drag across controls lost the drop target.");
        report.IsExpanded = true;
        if (Raise(window, RawDragEventType.Drop, null, text) != DragDropEffects.Copy || overlay.IsVisible)
            throw new InvalidOperationException("The link drop was not accepted or left the overlay visible.");
        await WaitUntilAsync(() => add.IsEnabled && ReportLines(window).Length == 3);
        string[] expected = [Texts.T("admission.unsupported") + " · https://unsupported.invalid/dropped-one",
            Texts.T("admission.invalid") + " · not a URL", Texts.T("admission.unsupported") + " · https://unsupported.invalid/dropped-two"];
        if (!ReportLines(window).SequenceEqual(expected, StringComparer.Ordinal) || window.SelectedPage != MainWindow.WorkspacePage.Downloads
            || input.Text != draft || ClipboardChecks.FixtureReadCount != clipboardReads)
            throw new InvalidOperationException("Dropped links did not get the Add line's results on Downloads, kept no draft or read the clipboard: "
                + string.Join(" | ", ReportLines(window)));

        // A folder as Avalonia's X11 target reports it (file: item) plus its path as text, as file managers add.
        string folder = Path.Combine(Path.GetTempPath(), "meowchan drop folder");
        using DataTransfer files = Transfer(DataTransferItem.CreateFile(new FixtureFolder(new Uri(folder))), DataTransferItem.CreateText(folder));
        // Files and folders are the import's drop: Copy with the import overlay, and the drop opens the import dialog
        // with exactly that folder selected (here a missing one, so the check reports it unreadable). No admission.
        if (Raise(window, RawDragEventType.DragEnter, input, files) != DragDropEffects.Copy
            || !overlay.IsVisible || title.Text != Texts.Count("drop.files.title", 1))
            throw new InvalidOperationException("A folder drag was not offered as an import: " + title.Text);
        if (Raise(window, RawDragEventType.Drop, null, files) != DragDropEffects.Copy || overlay.IsVisible)
            throw new InvalidOperationException("The folder drop was not accepted or left the overlay visible.");
        await WaitUntilAsync(() => window.ManualImportWindow is { IsVisible: true } dialog && dialog.Session.Plan != null);
        ManualImportDialog import = window.ManualImportWindow!;
        if (!import.Session.Roots.SequenceEqual(new[] { folder }) || import.Session.UnreadableCount != 1 || import.Session.CanImport
            || ReportLines(window).Length != 3)
            throw new InvalidOperationException("The folder drop did not open the import dialog with that folder only.");
        import.Close();
        await WaitUntilAsync(() => window.ManualImportWindow == null);

        using DataTransfer blank = Transfer(DataTransferItem.CreateText(" \r\n "));
        if (Raise(window, RawDragEventType.DragEnter, input, blank) != DragDropEffects.None || title.Text != Texts.T("drop.empty.title"))
            throw new InvalidOperationException("Blank text was not refused as nothing to add.");
        Raise(window, RawDragEventType.DragLeave, null, blank);
        await WaitUntilAsync(() => !overlay.IsVisible);

        // Unavailable shell (as under a modal dialog): no target, no overlay.
        window.IsEnabled = false;
        try
        {
            if (Raise(window, RawDragEventType.DragEnter, input, text) != DragDropEffects.None || overlay.IsVisible)
                throw new InvalidOperationException("A disabled shell accepted a drop.");
            Raise(window, RawDragEventType.DragLeave, null, text);
        }
        finally { window.IsEnabled = true; }
        if (ClipboardChecks.FixtureReadCount != clipboardReads)
            throw new InvalidOperationException("A drop read the clipboard.");
        report.IsExpanded = false;
        foreach (NotificationCard card in window.GetVisualDescendants().OfType<NotificationCard>().ToArray()) card.Close();
        input.Text = string.Empty;
        Console.WriteLine("SMOKE URL drop: managed raw drag events; links from another page got the Add line's ordered results on Downloads without clipboard read or draft change; a folder drop opened the import dialog prefilled with it; blank and unavailable drops refused with overlay/notice; no native XDND/OLE/Cocoa drag.");
    }

    private static DragDropEffects Raise(Window window, RawDragEventType type, Control? over, IDataTransfer data)
    {
        Point location = over == null ? default
            : over.TranslatePoint(new Point(over.Bounds.Width / 2, over.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("The drag target is not in the window.");
        // Avalonia 12 keeps the input root internal; the platform drop targets use the same one.
        IInputRoot root = typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as IInputRoot
            ?? throw new InvalidOperationException("No input root for the rendered window.");
        var drag = new RawDragEvent(DragDropDevice.Instance, type, root, location, data, BrowserEffects, RawInputModifiers.None);
        DragDropDevice.Instance.ProcessRawEvent(drag);
        return drag.Effects;
    }

    private static string HitDiagnostics(Window window, Control over)
    {
        Point location = over.TranslatePoint(new Point(over.Bounds.Width / 2, over.Bounds.Height / 2), window) ?? default;
        IInputRoot root = (IInputRoot)typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var rootElement = (InputElement)typeof(IInputRoot).GetProperty("RootElement", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(root)!;
        static string Chain(IInputElement? hit) => hit is not Visual visual ? "null" : string.Join("<", visual.GetSelfAndVisualAncestors().Take(6)
            .Select(item => item.GetType().Name + (item is Interactive interactive && DragDrop.GetAllowDrop(interactive) ? "+" : "")));
        return $"at={location}, bounds={over.Bounds}, root={rootElement.GetType().Name}, windowHit={Chain(window.InputHitTest(location))}, "
            + $"rootHit={Chain(rootElement.InputHitTest(location))}, rootHitAll={Chain(rootElement.InputHitTest(location, enabledElementsOnly: false))}, "
            + $"rootAllowDrop={DragDrop.GetAllowDrop(window.FindControl<Grid>("WorkspaceRoot")!)}";
    }

    private static DataTransfer Transfer(params DataTransferItem[] items)
    {
        var transfer = new DataTransfer();
        foreach (DataTransferItem item in items) transfer.Add(item);
        return transfer;
    }

    private static string[] ReportLines(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>()
        .SingleOrDefault(box => box.Name == "AdmissionReport")?.Text?.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];

    private static string?[] Notifications(Window window) => window.GetVisualDescendants().OfType<NotificationCard>()
        .Where(card => !card.IsClosing && !card.IsClosed)
        .Select(card => card.Content is FeedbackToastContent toast ? toast.Message : (card.Content as INotification)?.Message)
        .ToArray();

    private sealed class FixtureFolder(Uri path) : IStorageFolder
    {
        public string Name => "Fixture";
        public Uri Path { get; } = path;
        public bool CanBookmark => false;
        public void Dispose() { }
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => throw new NotSupportedException();
        public Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public IAsyncEnumerable<IStorageItem> GetItemsAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> GetFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> CreateFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFolder?> CreateFolderAsync(string name) => throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? state = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("URL drop UI state did not become ready within five seconds." + (state == null ? "" : " " + state()));
            await Task.Delay(25);
        }
    }
}
