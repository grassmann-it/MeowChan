using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Dialog gallery scene "reader": one private reader window over the post filter fixture. Opens the post filter dialog,
// the external-link confirmation, the post menu, the reader View menu and a quote preview; every one is closed again
// with Cancel/Close and nothing is launched (the link launcher and the clipboard are replaced by no-ops).
internal static partial class ReaderPostFilterChecks
{
    internal static async Task CaptureForDialogGalleryAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-gallery-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ArchivePage? page = null;
        Window? window = null;
        try
        {
            ResourceId id = new("4chan", "g", ThreadId);
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + ThreadId, "Post filter " + ThreadId,
                ThreadSourceState.Active, DateTimeOffset.UtcNow, profile, Posts()));
            PostFilterSession filters = new(new StoreService(new PostFilterStore(profile, lease)));
            await filters.LoadAsync();
            page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            page.PostFilters = filters;
            page.ExternalLinkLauncher = _ => Task.FromResult(true);
            page.CopyTextForTests = _ => Task.CompletedTask;
            window = new Window { Width = 1100, Height = 760, Content = page.DetachReader(), Title = "Reader" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow[] Rows() => list.ItemsSource?.Cast<ArchivePostRow>().ToArray() ?? [];
            await Program.WaitUntilAsync(() => !page.IsBusy && Rows().Length == 30, () => "rows " + Rows().Length);
            await filters.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "PLACEHOLDER me", PostFilterAction.Stub, false));
            await filters.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "highlight me", PostFilterAction.Highlight, false, "4chan", "g"));
            await filters.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "remove me", PostFilterAction.Hide, true, "4chan"));
            Require(await page.HidePostAsync(id, "709", replies: false), "709 was not hidden.");
            await Program.WaitUntilAsync(() => !page.IsProjectionPending, () => "post filter view pending");
            await page.WaitForReaderViewAsync();
            await DialogFrameChecks.Settle();

            Task dialog = page.OpenPostFiltersAsync();
            await Program.WaitUntilAsync(() => page.PostFilterWindow is { IsVisible: true } shown && !shown.Model.IsBusy, () => "post filter dialog not open");
            PostFilterDialog filterDialog = page.PostFilterWindow!;
            await DialogGalleryChecks.RecordWindowAsync(filterDialog, "post-filters-dialog");
            filterDialog.Close();
            await dialog;

            page.RequestExternalLink(new Uri("https://example.org/articles/cats?ref=thread#top"));
            await Program.WaitUntilAsync(() => page.IsExternalLinkDialogOpen, () => "link confirmation not open");
            await DialogFrameChecks.Settle();
            DialogGallery.Record(window, "link-confirmation", "default size", "in-page confirmation over the reader; Cancel closes it, nothing is launched");
            page.CloseExternalLinkDialog();
            await DialogFrameChecks.Settle();

            ArchivePostRow target = Rows().First(row => row.Id == "706");
            await DialogGalleryChecks.RecordMenuAsync(page.BuildPostMenu(target, list), list, "post-context-menu");
            // RL1: the former View menu is the settings popover; its capture keeps the gallery slot, now as a popover.
            QuickSettingsSurface settings = await ReaderSettingsProbe.OpenAsync(page);
            DialogGallery.RecordSurface(settings, "reader-settings-popover", "popover", "the gear's settings popover with the session rows and the post filter action");
            await ReaderSettingsProbe.CloseAsync(page);
            await DialogFrameChecks.Settle();

            ArchivePostRow quoting = Rows().First(row => row.Id == "705");
            page.RequestQuotePreview(quoting.Quotes.Single(), list, default, immediate: true);
            await Program.WaitUntilAsync(() => page.IsQuotePreviewOpen, () => "quote preview not open");
            await DialogFrameChecks.Settle();
            Popup preview = page.FindControl<Popup>("QuotePreviewPopup")!;
            if (preview.Child is Control card && card.Bounds.Width > 0) DialogGallery.RecordSurface(card, "quote-preview-popup", "popup", "hover/preview surface over a reply");
            else DialogGallery.Skip("quote-preview-popup", "the popup child had no size");
            page.CloseQuotePreview();
        }
        finally
        {
            page?.EndLifetime();
            window?.Close();
            await catalog.DisposeAsync();
            lease.Dispose();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }
}
