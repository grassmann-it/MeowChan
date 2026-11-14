using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// The right-click tag popover of the media page, the reader attachments and the archive tiles (TP3). The page hands over the exact
// entry, the tile it opened on and the context of the suggestions; the shell resolves the file with the same rules as the tag
// dialog (a changed or unbound file is refused), owns the tag session and opens QuickTagPopover. "Advanced…" opens the former
// dialog for the same file; the popover's session is not reused there, the dialog reads the published tags itself.
public partial class MainWindow
{
    internal QuickTagPopover? MediaPageTagPopover { get; private set; }
    internal QuickTagPopover? ArchiveTagPopover { get; private set; }

    /// <summary>The saved quick tag limits (TP5) and the panel's ranking adapter over <paramref name="context"/>, for every popover host.</summary>
    internal QuickTagPopoverOptions TagPopoverDefaults(TagContext context) => QuickTagSettings.PopoverOptions(_config, context, QuickTagRecent, QuickTagStampSet);

    private QuickTagPopoverOptions PopoverOptions(TagContext context, Func<bool> isCurrent, bool restoreFocus, string name) => TagPopoverDefaults(context) with
    {
        ReadSourceTags = _session.ReadBooruCatalog,
        IsCurrent = isCurrent,
        RestoreFocus = restoreFocus,
        Name = name
    };

    private async Task OpenMediaLibraryTagPopoverAsync(MediaLibraryEntry indexed, Control anchor, TagContext context, Func<bool> presentationCurrent)
    {
        Func<bool> hostCurrent = CaptureMediaPageMenuGuard();
        bool Current() => hostCurrent() && presentationCurrent();
        if (!Current() || _mediaTagDialogOpen) return;
        _mediaTagDialogOpen = true;
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        GalleryEntry? match = null;
        MediaTagSession? session = null;
        try
        {
            match = await MediaMenuIdentity.ResolveAsync(indexed, _session.OpenArchiveReaderAsync, Storage.MediaAssetReader.OpenAsync, Current,
                System.Threading.CancellationToken.None, _session.ReadBooruCatalog, _session.ReadManualImportCatalog);
            if (!Current()) return;
            MediaTagSubject? target = MediaTagSubjects.Of(match, out GalleryMediaTagUnavailable unavailable);
            if (match == null || target == null)
            {
                ShowNotification(Texts.T("notification.error"), match == null ? Texts.T("media.galleryFailed")
                    : MediaMenuReasons.Tags(unavailable, BooruMediaIdentity.IsBooruThread(match.Key.Thread)),
                    NotificationType.Warning, operation: "media.tags");
                return;
            }
            session = new(target, _session.GetPublishedPrivateTagSnapshot, _session.DefineAndSetMediaSubjectTagAsync, _session.SetMediaSubjectTagAsync);
            QuickTagPopoverOptions options = PopoverOptions(context, hostCurrent, restoreFocus: false, "MediaTagPopover");
            QuickTagPopover popover = QuickTagPopover.Open(anchor, options.NewSession(QuickTagTarget.For(session)),
                match.Asset.OriginalFileName ?? Path.GetFileName(match.Asset.Path), options);
            origin = popover.Origin ?? origin;
            MediaPageTagPopover = popover;
            reason = await popover.Result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            if (Current()) ShowNotification(Texts.T("notification.error"), Texts.T("media.galleryFailed"), NotificationType.Error,
                operation: "media.tags");
        }
        finally
        {
            MediaPageTagPopover = null;
            _mediaTagDialogOpen = false;
            // The page rebuilds its tiles after a change; it places the focus again (a tile that is gone gives it to the grid).
            if (reason != QuickTagPopoverCloseReason.Advanced && !_isClosing)
            {
                if (session?.Changed == true) RefreshMediaPageAfterMenuAction(origin, hostCurrent, presentationCurrent);
                else _mediaPage?.RestoreMediaMenuActionFocus(origin, () => hostCurrent() && !OwnedWindows.Any(window => window.IsVisible));
            }
        }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_isClosing && match != null)
            await EditMediaLibraryTagsForMenuAsync(indexed, () => true, match, origin);
    }

    private async Task OpenArchiveMediaTagPopoverAsync(GalleryEntry entry, Control anchor, TagContext context, Func<bool> isCurrent)
    {
        if (_isClosing || _galleryTagDialogOpen || !isCurrent()) return;
        MediaTagSubject? target = MediaTagSubjects.Of(entry, out GalleryMediaTagUnavailable unavailable);
        string label = entry.Asset.OriginalFileName ?? Path.GetFileName(entry.Asset.Path);
        if (target == null)
        {
            QuickTagPopover.OpenBlocked(anchor, label, MediaMenuReasons.Tags(unavailable, BooruMediaIdentity.IsBooruThread(entry.Key.Thread)),
                new QuickTagPopoverOptions { IsCurrent = isCurrent });
            return;
        }
        _galleryTagDialogOpen = true;
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        Control? origin = null;
        try
        {
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot, _session.DefineAndSetMediaSubjectTagAsync, _session.SetMediaSubjectTagAsync);
            QuickTagPopoverOptions options = PopoverOptions(context, isCurrent, restoreFocus: true, "ArchiveTagPopover");
            QuickTagPopover popover = QuickTagPopover.Open(anchor, options.NewSession(QuickTagTarget.For(session)), label, options);
            origin = popover.Origin;
            ArchiveTagPopover = popover;
            reason = await popover.Result;
        }
        finally
        {
            ArchiveTagPopover = null;
            _galleryTagDialogOpen = false;
        }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_isClosing) await EditGalleryMediaTagsAsync(entry, origin);
    }
}
