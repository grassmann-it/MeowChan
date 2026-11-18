using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// The favourite keys of the reader and the archive's media (Tom's decision of 15.11.2026, defaults revision 20): M adds, U removes, F toggles (FavoriteKeys).
// The target follows the focus: the archive's media tiles and list write every selected file; the media sidebar (its picture, strip or header) the medium it
// shows; a post the medium of the shared resolver (ReaderMediaActivation.Choose: a live player, else the sidebar's medium, else the focused tile, else the
// first one). A post without a saved medium leaves the key alone. The write is the same favourite call as the stars' and the gallery's
// (ToggleArchiveMediaFavoriteAsync); every star and badge follows through the page's refresh. M and U write an explicit target and F is relative to the
// effective value (the pending target when a write is pending, else the saved state), so a repeated key cannot undo itself. A held key acts once
// (HeldKeyLatch, released by the key-up or a lost activation) and a key pressed while the last key write runs is ignored.
public partial class ArchivePage
{
    /// <summary>The last favourite key's write (tests and smokes wait for it).</summary>
    internal Task FavoriteKeyTask { get; private set; } = Task.CompletedTask;

    /// <summary>The favourite value of a medium once its pending write lands (the pending target, else the saved state); the keys decide from it. Null: the saved state alone.</summary>
    internal Func<GalleryEntry, bool>? EffectiveArchiveFavorite { get; set; }

    /// <summary>Writes the favourite state of a medium to an explicit value (what M and U send); null: the page only has the toggle.</summary>
    internal Func<GalleryEntry, bool, Task>? SetArchiveFavoriteAsync { get; set; }

    private readonly HeldKeyLatch _favoriteKeyLatch = new();

    private bool TryRouteFavoriteKey(Control owner, KeyEventArgs e)
    {
        if (e.Handled || e.Source is not Control source || CommandRouter.IsNativeInput(e.Source, e.Key) || IsReaderDialogOpen || ShortcutService.IsCapturing) return false;
        FavoriteKeyAction action = FavoriteKeys.Classify(id => CommandBindings.Matches(e, id));
        if (action == FavoriteKeyAction.None) return false;
        if (_ended || !_available || _busy || _thread == null || _selectedThread == null || ToggleArchiveFavoriteAsync == null || IsArchiveFavorite == null) return false;
        IReadOnlyList<GalleryEntry> targets = FavoriteKeyTargets(owner, source);
        if (targets.Count == 0) return false;
        e.Handled = true;
        _favoriteKeyLatch.Observe(this);
        // A held key repeats KeyDown: only its first press acts. The latch is taken even while the last write runs, so the key does not act when it ends.
        if (_favoriteKeyLatch.IsRepeat(e.Key) || !FavoriteKeyTask.IsCompleted) return true;
        FavoriteKeyTask = WriteFavoriteKeyAsync(action, targets);
        return true;
    }

    /// <summary>The media a favourite key pressed with the focus on <paramref name="source"/> acts on (empty: nothing to act on).</summary>
    private IReadOnlyList<GalleryEntry> FavoriteKeyTargets(Control owner, Control source)
    {
        if (_thread == null) return Array.Empty<GalleryEntry>();
        ResourceId threadId = _thread.Summary.Id;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        // The archive's media tiles and list: every selected file (the focused one when nothing is selected).
        if (owner.Name is "ArchiveMediaGrid" or "ArchiveMediaTiles")
        {
            IReadOnlyList<ArchiveAssetRow> rows = SelectedMediaRows();
            if (rows.Count == 0 && ancestors.Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault() is { } focused) rows = new[] { focused };
            return rows.Select(row => AssetEntry(threadId, row)).ToArray();
        }
        // The media sidebar: the medium it shows (a thumbnail of its strip: that file).
        if (SidebarStripContext(ancestors) is { } thumbnail)
            return AttachmentEntry(threadId, thumbnail.PostId, thumbnail.Attachment) is { } stripped ? new[] { stripped } : Array.Empty<GalleryEntry>();
        if (SidebarMediaContext(ancestors) is { } shown)
            return AttachmentEntry(threadId, shown.PostId, shown.Attachment) is { } entry ? new[] { entry } : Array.Empty<GalleryEntry>();
        // A post: the medium of the shared resolver, the one Space and Enter act on.
        if (ArchivePosts.SelectedItem is not ArchivePostRow post || !ancestors.Contains(ArchivePosts)) return Array.Empty<GalleryEntry>();
        ArchivePostRow? sourcePost = ancestors.Select(control => control.DataContext).OfType<ArchivePostRow>().FirstOrDefault();
        if (sourcePost != null && !ReferenceEquals(sourcePost, post)) return Array.Empty<GalleryEntry>(); // a stale realized row never acts for the selected one
        PostMediaSnapshot? snapshot = SnapshotPostMedia(post, source, videosOnly: false);
        if (snapshot == null || ReaderMediaActivation.Choose(snapshot.Media, snapshot.SidebarIndex, snapshot.SidebarLive, snapshot.SidebarPlaying, snapshot.FocusedIndex) is not { } choice)
            return Array.Empty<GalleryEntry>();
        return AttachmentEntry(threadId, post.Id, snapshot.Rows[choice.Index]) is { } chosen ? new[] { chosen } : Array.Empty<GalleryEntry>();
    }

    private GalleryEntry AssetEntry(ResourceId threadId, ArchiveAssetRow row) =>
        new(new GalleryAssetKey(threadId, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, !row.Asset.IsCurrent, true)
        { ThreadUrl = _thread?.Summary.Url };

    // A reader attachment's saved file (null while it is not saved or the scan is not known yet).
    private GalleryEntry? AttachmentEntry(ResourceId threadId, string postId, ArchiveAttachmentRow attachment) =>
        FindMatchingAsset(threadId, postId, attachment.Attachment, _assets) is { } asset ? AssetEntry(threadId, asset) : null;

    // One decision for the whole target (FavoriteKeys.Writes), then the explicit write for each medium that changes, one after the other.
    private async Task WriteFavoriteKeyAsync(FavoriteKeyAction action, IReadOnlyList<GalleryEntry> targets)
    {
        if ((EffectiveArchiveFavorite ?? IsArchiveFavorite) is not { } isFavorite) return;
        Action<string> reportError = CaptureActionErrorReporter();
        // The stars are off for an unreadable favorites file, so the keys say the same and write nothing (the file is never rewritten from here).
        if (ArchiveFavoritesUnreadable?.Invoke() == true) { reportError(Texts.T("archive.favoritesUnreadable")); return; }
        bool?[] marked = targets.Select(entry => (bool?)isFavorite(entry)).ToArray();
        foreach ((int index, bool selected) in FavoriteKeys.Writes(action, marked))
        {
            if (_ended) return;
            try { await ToggleArchiveMediaFavoriteAsync(targets[index], selected); }
            catch (Exception error) { reportError(error.Message); return; }
        }
    }
}
