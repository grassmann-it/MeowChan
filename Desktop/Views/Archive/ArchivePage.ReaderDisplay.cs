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
        set => ApplyReaderDisplayPreferences(value, ReaderDateStyle, ReaderPageSize);
    }

    internal ReaderDateStyle ReaderDateStyle
    {
        get => _readerDisplayPreferences.ReaderDateStyle;
        set => ApplyReaderDisplayPreferences(ReaderThumbnailWidth, value, ReaderPageSize);
    }

    /// <summary>
    /// Capture before changing even an inherited measure property. All four settings share one queued projection/render
    /// and one passive restore, including date-only applies and asynchronous conversation depth changes.
    /// </summary>
    internal void ApplyReaderDisplayPreferences(int thumbnailWidth, ReaderDateStyle dateStyle, int pageSize,
        int? conversationDepth = null)
    {
        int width = global::MeowChan.Desktop.ReaderDisplayPreferences.ClampThumbnailWidth(thumbnailWidth);
        ReaderDateStyle style = Enum.IsDefined(dateStyle) ? dateStyle : ReaderDateStyle.System;
        int size = Math.Clamp(pageSize, ReaderPageSizeMinimum, ReaderPageSizeMaximum);
        int depth = ReaderConversationTree.ClampDepth(conversationDepth ?? ReaderThreadingDepth);
        if (width == ReaderThumbnailWidth && style == ReaderDateStyle && size == ReaderPageSize
            && depth == ReaderThreadingDepth) return;

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
        _readerPageSize = size;
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

    // Called only once the final projection is available. Page choice is guarded BEFORE RenderThread consumes it.
    private ReaderSettingsApply? TakeReaderSettingsApply(ReaderPostProjection projection)
    {
        ReaderSettingsApply? apply = _readerSettingsApply;
        _readerSettingsApply = null;
        if (apply != null && ReaderSettingsApplyCurrent(apply) && apply.Anchor is { } anchor && projection.Count > 0)
            _projectionPagePost = anchor.PostId;
        return apply;
    }

    private async Task RestoreReaderSettingsAsync(ReaderSettingsApply apply)
    {
        try
        {
            if (!ReaderSettingsApplyCurrent(apply) || apply.Anchor is not { } anchor
                || apply.Tab != 0 || Projection is not { Count: > 0 } shown) return;
            bool Current() => ReaderSettingsApplyCurrent(apply) && !IsProjectionPending && ReferenceEquals(Projection, shown);
            int nearest = shown.NearestPosition(anchor.PostId);
            if (nearest < 0) return;
            string postId = shown[nearest].Post.PostId;
            ListBox list = ArchivePosts;
            // Settings are not a jump: never call ShowPost/FocusPostAsync (which activate Posts), Focus, or Reveal.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!Current()) return;
            ArchivePostRow? row = list.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(item => item.Id == postId);
            if (row == null) return;
            ++_readerLayoutRevision;
            ScrollViewer? scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll != null) scroll.Offset = new Vector(scroll.Offset.X, 0);
            list.UpdateLayout();
            list.ScrollIntoView(row);
            list.UpdateLayout();
            if (BeforeReaderSettingsRestoreAsync is { } beforeRestore) await beforeRestore();
            // One layout completion and correction, not a render/restore retry loop.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current()) return;
                ++_readerLayoutRevision;
                ArchivePostRow? selected = list.ItemsSource?.Cast<ArchivePostRow>()
                    .FirstOrDefault(item => item.Id == anchor.SelectedPostId);
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
    }
}
