using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan;

// Tagging from the tagging page (TG1): "Tag…" for one entry, "Tag together…" for several (the same quick tag popover and advanced
// dialog as the media page's selection, one write per change, thread tags never involved) and "Apply tag stamp" to the selection. The
// entries come from the queue (every queue entry carries its bytes, so no file is hashed here); a Danbooru medium has no own-tag
// target and is left out of the write. The shell hands over the tag services; without them the commands are off.
public sealed partial class TaggingPage
{
    private QuickTagPopover? _tagPopover;
    private MediaSelectionTagDialog? _tagDialog;
    private bool _tagBusy;

    /// <summary>The tag store's writes, as the media page's selection uses them. Null: tagging from this page is off.</summary>
    internal MediaSelectionTagServices? TagServices { get; set; }
    /// <summary>The shell's saved quick tag limits, ranking and shared stamp (unbound: the built-in defaults).</summary>
    internal Func<TagContext, QuickTagPopoverOptions>? TagPopoverOptions { get; set; }
    /// <summary>The Danbooru catalog for the source tag group of the popover's field.</summary>
    internal Func<BooruCatalogSnapshot?>? ReadBooruCatalog { get; set; }

    /// <summary>The quick tag popover while it is open (smokes).</summary>
    internal QuickTagPopover? TagPopover => _tagPopover;
    /// <summary>The advanced dialog while it is open (smokes).</summary>
    internal MediaSelectionTagDialog? TagDialog => _tagDialog;

    private bool CanTag => TagServices != null && !_tagBusy && !_queueWork.Ended && _queueWork.Loaded && _queueWork.LoadError == null;

    private MediaSelectionTagSession? NewTagSession(IReadOnlyList<TaggingQueueRow> taggable, out IReadOnlyList<MediaLibraryTile> tiles)
    {
        tiles = taggable.Select(row => TaggingThumbnails.TileOf(row.Item)).ToArray();
        if (TagServices is not { } services || tiles.Count == 0) return null;
        return new MediaSelectionTagSession(MediaLibraryTagging.Candidates(tiles), services.Load, services.DefineAndSetMany, services.SetMany);
    }

    private TagContext TagContextFor(TaggingQueueRow lead, bool selection)
    {
        MediaLibraryEntry[] shown = _rows.Select(row => row.Item.ToLibraryEntry()).ToArray();
        int index = _rows.ToList().FindIndex(row => row.Key == lead.Key);
        return QuickTagPopoverContext.ContextOf(lead.Item.ToLibraryEntry(), shown, index, selection);
    }

    /// <summary>
    /// "Tag…" and "Tag together…": the quick tag popover over the selected entries (the lead for one), anchored at <paramref name="anchor"/>
    /// (the list by default). "Advanced…" opens the dialog that lists every occurrence.
    /// </summary>
    internal async Task TagSelectionAsync(Control? anchor = null)
    {
        if (!CanTag || Lead is not { } lead) return;
        IReadOnlyList<TaggingQueueRow> taggable = TaggingRowCommands.Taggable(Selected);
        if (NewTagSession(taggable, out IReadOnlyList<MediaLibraryTile> tiles) is not { } session) return;
        bool together = taggable.Count > 1;
        static bool Usable(Control? control) => control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;
        Control host = Usable(anchor) ? anchor! : ActiveList;
        TagContext context = TagContextFor(taggable.Contains(lead) ? lead : taggable[0], selection: together);
        QuickTagPopoverOptions options = TagPopoverOptions?.Invoke(context) ?? QuickTagPopoverOptions.Defaults(context);
        QuickTagSession quick = options.NewSession(QuickTagTarget.For(session));
        string label = together ? Texts.T("media.selection.count", tiles.Count) : taggable[0].Name;
        _tagBusy = true;
        UpdateActions();
        QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Stale;
        try
        {
            QuickTagPopover popover = QuickTagPopover.Open(host, quick, label, options with
            {
                ReadSourceTags = ReadBooruCatalog,
                IsCurrent = () => !_queueWork.Ended && IsEffectivelyVisible,
                RestoreFocus = true,
                Name = "TaggingTagPopover"
            });
            _tagPopover = popover;
            reason = await popover.Result;
        }
        finally
        {
            _tagPopover = null;
            _tagBusy = false;
            if (!_queueWork.Ended)
            {
                if (session.Changed) Show(); // the new tags show in the counts at once
                else UpdateActions();
            }
        }
        if (reason == QuickTagPopoverCloseReason.Advanced && !_queueWork.Ended) await TagSelectionDialogAsync();
    }

    /// <summary>Adds or removes an own tag on the occurrences of the selection; the dialog lists them and tags all by default.</summary>
    internal async Task TagSelectionDialogAsync()
    {
        if (!CanTag || TopLevel.GetTopLevel(this) is not Window owner) return;
        IReadOnlyList<TaggingQueueRow> taggable = TaggingRowCommands.Taggable(Selected);
        if (NewTagSession(taggable, out IReadOnlyList<MediaLibraryTile> tiles) is not { } session) return;
        MediaSelectionTagDialog dialog = new(session, tiles.Count, ReadBooruCatalog);
        _tagDialog = dialog;
        _tagBusy = true;
        UpdateActions();
        try { await dialog.ShowDialog(owner); }
        finally
        {
            _tagDialog = null;
            _tagBusy = false;
            if (!_queueWork.Ended)
            {
                if (session.Changed) Show();
                else UpdateActions();
            }
        }
    }

    /// <summary>
    /// "Apply tag stamp": gives every stamp tag the selected entries do not all carry to all of them (one write per tag, each its own
    /// undo step in the session's popover history); the status line says what happened.
    /// </summary>
    internal async Task ApplyStampToSelectionAsync()
    {
        if (!CanTag || Lead is not { } lead) return;
        IReadOnlyList<TaggingQueueRow> taggable = TaggingRowCommands.Taggable(Selected);
        if (NewTagSession(taggable, out _) is not { } session) return;
        TagContext context = TagContextFor(taggable.Contains(lead) ? lead : taggable[0], selection: taggable.Count > 1);
        QuickTagPopoverOptions options = TagPopoverOptions?.Invoke(context) ?? QuickTagPopoverOptions.Defaults(context);
        using QuickTagSession quick = options.NewSession(QuickTagTarget.For(session));
        _tagBusy = true;
        UpdateActions();
        try { await quick.ApplyStampWithOutcomeAsync(); }
        finally
        {
            _tagBusy = false;
            if (!_queueWork.Ended)
            {
                ShowStatus(quick.Status.Kind == QuickTagStatusKind.None ? null : quick.Status.Text);
                if (session.Changed) Show();
                else UpdateActions();
            }
        }
    }

    private void EndTagging()
    {
        _tagPopover?.Close(QuickTagPopoverCloseReason.Stale);
        _tagDialog?.CloseForLifetime();
    }
}
