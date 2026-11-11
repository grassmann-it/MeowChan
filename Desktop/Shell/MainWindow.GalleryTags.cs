using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// Own media tags from the gallery. The target is resolved once from the exact entry and frozen in
// the dialog's session; gallery navigation while the dialog is open cannot redirect a change.
public partial class MainWindow
{
    private bool _galleryTagDialogOpen;
    internal MediaTagDialog? GalleryMediaTagWindow { get; private set; }

    private void RefreshGalleryMediaTags()
    {
        if (_gallery == null || _galleryIndex < 0 || _galleryIndex >= _galleryEntries.Count) return;
        GalleryEntry current = _galleryEntries[_galleryIndex];
        PrivateMediaTagTarget? target = GalleryMediaTagTarget.Resolve(current, out GalleryMediaTagUnavailable reason);
        if (target == null)
        {
            _gallery.SetMediaTags(null, Texts.T(reason == GalleryMediaTagUnavailable.NoPostVersion
                ? "tags.media.unbound" : "tags.media.unsupported"));
            return;
        }
        PrivateTagLoadSnapshot loaded = _session.GetPublishedPrivateTagSnapshot();
        if (loaded.State != PrivateTagLoadState.Ready || loaded.Snapshot == null)
        {
            _gallery.SetMediaTags(null, Texts.T("tags.unavailable"));
            return;
        }
        _gallery.SetMediaTags(loaded.Snapshot.TagsOf(target).Select(tag => tag.Name.Display).ToArray());
    }

    private async Task EditGalleryMediaTagsAsync(GalleryEntry entry)
    {
        if (_isClosing || _galleryTagDialogOpen) return;
        PrivateMediaTagTarget? target = GalleryMediaTagTarget.Resolve(entry, out _);
        if (target == null) { RefreshGalleryMediaTags(); return; }
        Control? origin = FocusManager?.GetFocusedElement() as Control;
        _galleryTagDialogOpen = true;
        try
        {
            MediaTagSession session = new(target, _session.GetPublishedPrivateTagSnapshot,
                _session.DefinePrivateTagAsync, _session.SetMediaTagAsync);
            MediaTagDialog dialog = new(session, entry.Asset.OriginalFileName ?? System.IO.Path.GetFileName(entry.Asset.Path));
            GalleryMediaTagWindow = dialog;
            await dialog.ShowDialog(this);
        }
        finally
        {
            GalleryMediaTagWindow = null;
            _galleryTagDialogOpen = false;
            if (!_isClosing)
            {
                // Media tags protect threads from cleanup and appear on the archive page.
                RefreshGalleryMediaTags();
                if (origin?.IsEffectivelyVisible == true) origin.Focus();
            }
        }
    }
}
