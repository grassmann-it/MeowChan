using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Archive group: cleanup, HTML export overlay, URL text export, favorites overview, original
// export and read-only metadata. Private fixtures only; no profile data is changed or deleted.
internal static partial class DialogFrameChecks
{
    private static partial async Task RunArchiveAsync(MainWindow owner)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-dialog-frame-archive-");
        try
        {
            await CleanupFrameAsync(owner);
            await UrlTextFrameAsync(owner, root);
            ArchivePage page = new(_ => Task.FromException<ArchiveReader>(new IOException("fixture archive reader failure")),
                (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true, LastExportDirectoryPreference = root };
            Window host = new() { Title = "Archive dialog frame fixture", Width = 1000, Height = 750, Content = page };
            try
            {
                host.Show(owner);
                await Program.WaitUntilAsync(() => host.IsVisible && page.Bounds.Height > 0);
                await ExportOverlayFrameAsync(owner, host, page);
                await FavoritesFrameAsync(host, page);
                await OriginalExportFrameAsync(host, page, root);
                await MetadataFrameAsync(host, page);
            }
            finally { host.Close(); }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static async Task CleanupFrameAsync(MainWindow owner)
    {
        int previews = 0;
        ArchiveCleanupDialog dialog = new((ids, token) => ++previews == 1
                ? Task.FromException<ArchiveCleanupPreview>(new IOException("fixture cleanup inventory failure"))
                : owner.SessionHost.PrepareArchiveCleanupAsync(ids, token),
            (_, _, _) => Task.FromException<ArchiveCleanupExecutionPlan>(new InvalidOperationException("No cleanup plan in frame smoke.")),
            (_, _) => Task.FromException<ArchiveCleanupExecutionResult>(new InvalidOperationException("No cleanup execution in frame smoke.")),
            () => []);
        Button refresh = dialog.FindControl<Button>("ArchiveCleanupRefresh")!;
        Task closed = dialog.ShowDialog(owner);
        await Program.WaitUntilAsync(() => dialog.IsVisible && previews == 1 && refresh.IsEnabled);
        await VerifyErrorStripAsync(dialog, "archive cleanup inventory failure", "fixture cleanup inventory failure");
        Capture(dialog, "archive-cleanup-error");
        Click(refresh);
        await Program.WaitUntilAsync(() => previews == 2 && refresh.IsEnabled);
        Require(string.IsNullOrEmpty(dialog.FindControl<TextBlock>("ArchiveCleanupError")!.Text), "Cleanup kept a stale inventory error after a clean check.");
        await VerifyFrameAsync(dialog, "archive cleanup", "archive-cleanup");
        await ResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
        await VerifyFrameAsync(dialog, "archive cleanup minimum", "archive-cleanup-min");
        Escape(dialog);
        await closed;
    }

    private static async Task UrlTextFrameAsync(MainWindow owner, string root)
    {
        ResourceId id = new("4chan", "g", "123");
        ArchiveThreadSummary summary = new(id, "https://boards.4chan.org/g/thread/123", "Dialog frame fixture",
            "", ThreadSourceState.Archived, false, DateTimeOffset.UnixEpoch, null, 1, 1);
        ArchiveUrlTextExportPlan plan = ArchiveUrlTextExportPlan.TryCreate(id, summary, out _)
            ?? throw new InvalidOperationException("URL text fixture plan was rejected.");
        string target = Path.Combine(root, "urltext-target.txt");
        int writes = 0;
        UrlTextExportDialog dialog = new(plan, () => Task.FromResult<string?>(target), (_, _, _) =>
        {
            writes++;
            return Task.FromResult(new UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus.Failed, UrlTextExportFileWriteError.DestinationExists));
        }, CancellationToken.None);
        Task closed = dialog.ShowDialog(owner);
        await Program.WaitUntilAsync(() => dialog.IsVisible);
        await VerifyFrameAsync(dialog, "url text export", "urltext-export");
        await ResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
        await VerifyFrameAsync(dialog, "url text export minimum", "urltext-export-min");
        Click(Find<Button>(dialog, "UrlTextExportSave")!);
        await Program.WaitUntilAsync(() => writes == 1 && dialog.Operation.StatusKey == "urltext.export.error.exists");
        await VerifyErrorStripAsync(dialog, "url text export failure", Texts.T("urltext.export.error.exists"));
        Require(string.IsNullOrEmpty(Find<TextBlock>(dialog, "UrlTextExportStatus")!.Text), "URL text failure was repeated in the footer status.");
        Capture(dialog, "urltext-export-error");
        Require(!File.Exists(target), "URL text frame fixture wrote a file.");
        Escape(dialog);
        await closed;
    }

    private static async Task ExportOverlayFrameAsync(MainWindow owner, Window host, ArchivePage page)
    {
        await page.OpenExportForThreadAsync(new ResourceId("4chan", "g", "123"), null);
        Border overlay = page.FindControl<Border>("ArchiveExportDialog")!;
        Button start = page.FindControl<Button>("ArchiveExportStart")!;
        await Program.WaitUntilAsync(() => overlay.IsEffectivelyVisible && overlay.Child is Border && start.IsEnabled);
        Border card = (Border)overlay.Child!;
        await VerifyFrameAsync(card, "archive html export", "archive-export");
        Click(start);
        await Program.WaitUntilAsync(() => !page.IsExportRunning
            && page.FindControl<TextBlock>("ArchiveExportError")!.Text?.Contains("fixture archive reader failure") == true);
        MeowChan.Desktop.Controls.WorkspaceBanner banner = page.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("ArchiveExportErrorBanner")!;
        Require(banner.IsEffectivelyVisible && card.GetVisualDescendants().Contains(banner), "Export failure banner is not shown in the card.");
        await VerifyErrorStripAsync(card, "archive html export failure", "fixture archive reader failure");
        Capture(card, "archive-export-error");
        await ResizeAsync(host, owner.MinWidth, owner.MinHeight);
        await VerifyErrorStripAsync(card, "archive html export minimum", "fixture archive reader failure");
        Capture(card, "archive-export-min");
        Escape(page.FindControl<TextBox>("ArchiveExportDestination")!);
        await Program.WaitUntilAsync(() => !overlay.IsVisible);
        await ResizeAsync(host, 1000, 750);
    }

    private static async Task FavoritesFrameAsync(Window host, ArchivePage page)
    {
        FavoriteEntry[] entries = Enumerable.Range(0, 40).Select(index => new FavoriteEntry(
            new FavoriteAssetKey(new ResourceId("4chan", "g", "123"), "asset" + index, "original", new string('a', 64)),
            1024 + index, "dialog-frame-" + index + ".png")).ToArray();
        bool fail = true;
        page.GetFavorites = () => entries;
        page.ResolveFavoriteAssets = (requested, _) => fail
            ? Task.FromException<FavoriteResolutionSnapshot>(new IOException("fixture favorite resolution failure"))
            : Task.FromResult(new FavoriteResolutionSnapshot(Array.Empty<FavoriteExportAsset>(), Array.Empty<FavoriteEntry>()));
        Task shown = page.OpenFavoritesOverviewAsync();
        await Program.WaitUntilAsync(() => host.OwnedWindows.Any(window => window.Name == "FavoritesOverviewDialog"));
        Window dialog = host.OwnedWindows.Single(window => window.Name == "FavoritesOverviewDialog");
        await Program.WaitUntilAsync(() => Find<TextBlock>(dialog, "FavoritesError")?.Text?.Contains("fixture favorite resolution failure") == true);
        await VerifyErrorStripAsync(dialog, "favorites overview failure", "fixture favorite resolution failure");
        Capture(dialog, "favorites-overview-error");
        fail = false;
        ComboBox scope = Find<ComboBox>(dialog, "FavoritesScope")!;
        scope.SelectedIndex = 0; scope.SelectedIndex = 3;
        ListBox list = Find<ListBox>(dialog, "FavoritesList")!;
        await Program.WaitUntilAsync(() => list.ItemCount == entries.Length && string.IsNullOrEmpty(Find<TextBlock>(dialog, "FavoritesError")!.Text));
        await VerifyFrameAsync(dialog, "favorites overview", "favorites-overview");
        await ResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
        await VerifyFrameAsync(dialog, "favorites overview minimum", "favorites-overview-min");
        Escape(dialog);
        await shown;
        page.GetFavorites = null; page.ResolveFavoriteAssets = null;
    }

    private static async Task OriginalExportFrameAsync(Window host, ArchivePage page, string root)
    {
        int launches = 0;
        page.OpenExportFolder = _ => { launches++; return Task.FromException(new IOException("fixture folder launch failure")); };
        Task shown = page.OpenOriginalExportAsync(Array.Empty<FavoriteExportAsset>(), null, missingCount: 2, scopeLabel: "Dialog frame fixture");
        await Program.WaitUntilAsync(() => host.OwnedWindows.Any(window => Find<Button>(window, "OriginalExportClose") != null));
        Window dialog = host.OwnedWindows.Single(window => Find<Button>(window, "OriginalExportClose") != null);
        await VerifyFrameAsync(dialog, "original export", "original-export");
        string openLabel = CommandCatalog.Label("location.open-folder");
        Button open = dialog.GetVisualDescendants().OfType<Button>().First(button =>
            AutomationProperties.GetName(button) == openLabel || Equals(button.Content, openLabel));
        Require(open.IsEnabled, "Original export folder action is unavailable for an existing destination " + root);
        Click(open);
        await Program.WaitUntilAsync(() => launches == 1
            && Find<TextBlock>(dialog, "OriginalExportError")?.Text == Texts.T("export.openFailed", "fixture folder launch failure"));
        await VerifyErrorStripAsync(dialog, "original export failure", "fixture folder launch failure");
        Capture(dialog, "original-export-error");
        await ResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
        await VerifyFrameAsync(dialog, "original export minimum", "original-export-min");
        Escape(dialog);
        await shown;
        page.OpenExportFolder = null;
    }

    private static async Task MetadataFrameAsync(Window host, ArchivePage page)
    {
        const string title = "Dialog frame metadata fixture";
        string content = string.Join(Environment.NewLine, Enumerable.Range(1, 40).Select(line => "Metadata line " + line));
        MethodInfo show = typeof(ArchivePage).GetMethod("ShowArchiveMetadataAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Archive metadata dialog entry is missing.");
        Task shown = (Task)show.Invoke(page, [title, content])!;
        await Program.WaitUntilAsync(() => host.OwnedWindows.Any(window => window.Title == title));
        Window dialog = host.OwnedWindows.Single(window => window.Title == title);
        await VerifyFrameAsync(dialog, "archive metadata", "archive-metadata");
        await ResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
        await VerifyFrameAsync(dialog, "archive metadata minimum", "archive-metadata-min");
        Escape(dialog);
        await shown;
    }

    private static async Task ResizeAsync(Window window, double width, double height)
    {
        window.Width = width; window.Height = height;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && (Math.Abs(window.ClientSize.Width - width) > 1 || Math.Abs(window.ClientSize.Height - height) > 1))
            await Settle();
        await Settle();
        Console.WriteLine("SMOKE dialog frame resize: requested=" + width + "x" + height + "; client=" + window.ClientSize);
    }

    private static void Click(Button button)
    {
        Require(button.IsEffectivelyEnabled, "Cannot click disabled " + button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Escape(Control target) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = target });
}
