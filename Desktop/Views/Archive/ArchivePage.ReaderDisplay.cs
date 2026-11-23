using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly ReaderDisplayPreferences _readerDisplayPreferences = new();
    private ReaderSettingsApply? _readerSettingsApply;
    private int _readerSettingsRequest;
    private sealed record ReaderSettingsApply(ReaderAnchor? Anchor, ResourceId Thread, int Content, int Interaction,
        int Focus, int Tab, int Request, TaskCompletionSource Completion);

    /// <summary>Deterministic gate after final projection realization, before passive selection/top correction.</summary>
    internal Func<Task>? BeforeReaderSettingsRestoreAsync { get; set; }

    internal ReaderDisplayPreferences ReaderDisplayPreferences => _readerDisplayPreferences;

    internal int ReaderThumbnailWidth
    {
        get => _readerDisplayPreferences.ReaderThumbnailWidth;
        set => ApplyReaderDisplayPreferences(value, ReaderDateStyle);
    }

    internal ReaderDateStyle ReaderDateStyle
    {
        get => _readerDisplayPreferences.ReaderDateStyle;
        set => ApplyReaderDisplayPreferences(ReaderThumbnailWidth, value);
    }

    /// <summary>
    /// Capture before changing even an inherited measure property. The display settings share one queued projection/render
    /// and one passive restore, including date-only applies and asynchronous conversation depth changes.
    /// </summary>
    internal void ApplyReaderDisplayPreferences(int thumbnailWidth, ReaderDateStyle dateStyle,
        int? conversationDepth = null, ReaderMediaPreferences? media = null)
    {
        int width = global::MeowChan.Desktop.ReaderDisplayPreferences.ClampThumbnailWidth(thumbnailWidth);
        ReaderDateStyle style = Enum.IsDefined(dateStyle) ? dateStyle : ReaderDateStyle.System;
        int depth = ReaderConversationTree.ClampDepth(conversationDepth ?? ReaderThreadingDepth);
        ReaderMediaPreferences nextMedia = (media ?? _readerDisplayPreferences.Media).Clamped();
        // The focus and hover options act on the list at once; only a changed textless setting makes the posts build their images again.
        bool postImagesSame = nextMedia.SamePostImages(_readerDisplayPreferences.Media);
        bool liveOnly = width == ReaderThumbnailWidth && style == ReaderDateStyle
            && depth == ReaderThreadingDepth && postImagesSame;
        if (nextMedia != _readerDisplayPreferences.Media)
        {
            // A live switch (enlargement, hover, sidebar on/off, its side and width) resizes the focused card or re-wraps every card
            // without a rebuild: the selected card keeps its place in the viewport over the following layout passes. A rebuild has
            // its own settings anchor below.
            Func<Task>? keepSelected = liveOnly ? CaptureReaderCountLayoutCorrection() : null;
            _readerDisplayPreferences.Media = nextMedia;
            ApplyReaderMediaLive();
            if (keepSelected != null) _ = keepSelected();
        }
        if (liveOnly) return;

        DiscardStaleReaderSettingsApply();
        if (!_ended && _thread != null && _readerSettingsApply == null)
        {
            _readerSettingsApply = new(CaptureReaderAnchor(), _thread.Summary.Id, _contentRequest,
                _readerInteractionRevision, _readerFocusRequest, ArchiveContentTabs.SelectedIndex,
                ++_readerSettingsRequest, new(TaskCreationOptions.RunContinuationsAsynchronously));
            ReaderViewTask = _readerSettingsApply.Completion.Task;
            // No competing view restore may capture the intermediate layout or activate the Posts tab.
            _projectionAnchor = null;
            _projectionAnchorFocus = false;
        }
        _readerDisplayPreferences.ReaderThumbnailWidth = width;
        _readerDisplayPreferences.ReaderDateStyle = style;
        if (_conversationOrdering != null) _conversationOrdering.Depth = depth;
        ArchivePosts.SetValue(ReaderPostLayout.ThumbnailWidthProperty, width);
        RequestProjectionRebuild();
    }

    private bool ReaderSettingsApplyCurrent(ReaderSettingsApply apply) => !_ended && !IsFilenameSearchOpen
        && apply.Request == _readerSettingsRequest && apply.Thread == _thread?.Summary.Id
        && apply.Content == _contentRequest && apply.Interaction == _readerInteractionRevision
        && apply.Focus == _readerFocusRequest && apply.Tab == ArchiveContentTabs.SelectedIndex;

    private void DiscardStaleReaderSettingsApply()
    {
        if (_readerSettingsApply is not { } apply || ReaderSettingsApplyCurrent(apply)) return;
        _readerSettingsApply = null;
        apply.Completion.TrySetResult();
    }

    // Called only once the final projection is available. Selection is guarded BEFORE RenderThread consumes it.
    private ReaderSettingsApply? TakeReaderSettingsApply(ReaderPostProjection projection)
    {
        ReaderSettingsApply? apply = _readerSettingsApply;
        _readerSettingsApply = null;
        if (apply != null && ReaderSettingsApplyCurrent(apply) && apply.Anchor is { } anchor && projection.Count > 0)
            _projectionSelectionPost = anchor.PostId;
        return apply;
    }

    private async Task RestoreReaderSettingsAsync(ReaderSettingsApply apply)
    {
        try
        {
            if (!ReaderSettingsApplyCurrent(apply) || apply.Anchor is not { } anchor
                || Projection is not { Count: > 0 } shown) return;
            bool Current() => ReaderSettingsApplyCurrent(apply) && !IsProjectionPending && ReferenceEquals(Projection, shown);
            if (apply.Tab != 0)
            {
                await RestoreReaderAnchorAsync(anchor, restoreFocus: false, stillRequested: Current, preserveTab: true);
                return;
            }
            int nearest = shown.NearestPosition(anchor.PostId);
            if (nearest < 0) return;
            string postId = shown[nearest].Post.PostId;
            ListBox list = ArchivePosts;
            // Settings are not a jump: never call ShowPost/FocusPostAsync (which activate Posts), Focus, or Reveal.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!Current()) return;
            ArchivePostRow? row = FindPostRow(postId);
            if (row == null) return;
            ++_readerLayoutRevision;
            list.UpdateLayout();
            list.ScrollIntoView(nearest);
            list.UpdateLayout();
            if (BeforeReaderSettingsRestoreAsync is { } beforeRestore) await beforeRestore();
            // One layout completion and correction, not a render/restore retry loop.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current()) return;
                ++_readerLayoutRevision;
                ArchivePostRow? selected = FindPostRow(anchor.SelectedPostId);
                list.SelectedItem = selected;
                _readerPostId = selected?.Id ?? row.Id;
                list.UpdateLayout();
                ScrollViewer? currentScroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (currentScroll != null && list.ContainerFromItem(row) is Control container
                    && container.TranslatePoint(default, list) is Point position)
                    currentScroll.Offset = new Vector(currentScroll.Offset.X,
                        Math.Max(0, currentScroll.Offset.Y + position.Y - anchor.Top));
            }, DispatcherPriority.Loaded);
        }
        finally { apply.Completion.TrySetResult(); }
    }

    private void InitializeReaderDisplay()
    {
        ArchivePosts.SetValue(ReaderPostLayout.ThumbnailWidthProperty,
            _readerDisplayPreferences.ReaderThumbnailWidth);
        InitializeReaderMedia();
    }
}
