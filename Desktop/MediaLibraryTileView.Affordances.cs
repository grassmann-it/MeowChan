using System;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Tags;

namespace MeowChan.Desktop;

// The parts of a media tile that look like something to click and now are (dead-affordance audit, package D): the "+N" after the tag chips opens the
// tag editor, the untagged mark and the tag count of the badges open it too, the queue badge opens the Tagging page, and a right click on a tag chip
// offers that tag's filter instead of the tile's menu. The favourite star and the rating are other packages' work and are not touched here.
internal sealed partial class MediaLibraryTileView
{
    /// <summary>Set by the page: opens the tag editor of the tile's medium on <paramref name="anchor"/> (the tile's "Tag…", with its reason when the medium cannot be tagged).</summary>
    internal Action<MediaLibraryTileRow, Control>? EditTags { get; set; }

    /// <summary>Set by the page: opens the Tagging page (the queue badge).</summary>
    internal Action? OpenTagging { get; set; }

    /// <summary>Set by the page: whether the Tagging page can be opened now (asked at every pointer move over the badge).</summary>
    internal Func<bool>? CanOpenTagging { get; set; }

    private void InitializeAffordances()
    {
        _moreTagsLink.Click += (_, e) => { e.Handled = true; RequestTagEditor(); };
        _badges.Activatable = IsBadgeAction;
        _badges.Activated += OnBadgeActivated;
        foreach (Button chip in _tagChips)
        {
            Button own = chip;
            WorkspaceChips.AttachMenu(own, () => ComposeChipMenu(own));
        }
    }

    /// <summary>The "+N" link (tests and smokes).</summary>
    internal Button MoreTagsLink => _moreTagsLink;

    private void RequestTagEditor()
    {
        if (DataContext is MediaLibraryTileRow row) EditTags?.Invoke(row, this);
    }

    // The badges that act: the untagged mark and the tag count edit the tags, the queue badge opens the Tagging page; every other badge is only drawn.
    internal bool IsBadgeAction(MediaBadgeId id) => id switch
    {
        MediaBadgeId.Untagged or MediaBadgeId.TagCount => EditTags != null && DataContext is MediaLibraryTileRow,
        MediaBadgeId.Queued => OpenTagging != null && CanOpenTagging?.Invoke() != false,
        _ => false
    };

    internal void OnBadgeActivated(MediaBadgeId id)
    {
        if (id == MediaBadgeId.Queued) OpenTagging?.Invoke(); else RequestTagEditor();
    }

    // The menu of a tag chip: show only the media with the tag, hide them, copy the name (the same entries as the tag picker's).
    private ContextMenuBuilder? ComposeChipMenu(Button chip)
    {
        if (chip.Tag is not PrivateTagDefinition tag) return null;
        Guid id = tag.Id.Value;
        string name = tag.Name.Display;
        return TagPickerMenu.Compose(name, () => TagFilter?.Invoke(id), () => TagExclude?.Invoke(id), () => _ = MenuClipboard.CopyAsync(this, name));
    }
}
