using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

// Viewport eviction releases pixels, but never changes the post's preview geometry: the picture's aspect stays on the row.
public sealed class ArchiveAttachmentView : UserControl
{
    /// <summary>
    /// File line, frame and provenance as one block that is as wide as the drawn picture (the file line keeps a modest minimum). The
    /// frame is measured first; the lines above and below take its width and wrap or trim to it instead of widening the block, so the
    /// text beside a portrait picture starts right after the picture and the star sits at the picture's right edge.
    /// </summary>
    private sealed class AttachmentColumn : Panel
    {
        private const double Spacing = 4;
        internal Control Lead = null!, Frame = null!, Trail = null!;
        /// <summary>Compact list rows: the picture is centred in the slot the owner gives (a uniform slot for portrait and landscape).</summary>
        internal bool CenterFrame;
        /// <summary>The inline video's row (R10: transport at the frame's width, error, live region) under the frame; null until a video plays here.</summary>
        internal ReaderInlineVideoRow? Video;

        /// <summary>The height of everything but the frame (file line, video row, provenance and the gaps between): what a picture's height shares with.</summary>
        internal double NonFrameHeight =>
            Lead.DesiredSize.Height + Spacing + (Video?.IsVisible == true ? Spacing + Video.DesiredSize.Height : 0)
            + (Trail.IsVisible ? Spacing + Trail.DesiredSize.Height : 0);

        protected override Size MeasureOverride(Size availableSize)
        {
            Frame.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            double width = MediaSlotGeometry.BlockWidth(Frame.DesiredSize.Width, availableSize.Width);
            bool video = Video?.IsVisible == true;
            // The video's bar is exactly as wide as the picture's frame (never wider); its error line may use the block's width.
            if (video) Video!.FrameWidth = Frame.DesiredSize.Width;
            Size line = new(width, double.PositiveInfinity);
            Lead.Measure(line); Trail.Measure(line);
            double height = Lead.DesiredSize.Height + Spacing + Frame.DesiredSize.Height;
            if (video) { Video!.Measure(line); height += Spacing + Video.DesiredSize.Height; }
            if (Trail.IsVisible) height += Spacing + Trail.DesiredSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = Math.Min(finalSize.Width, DesiredSize.Width), y = 0;
            Lead.Arrange(new Rect(0, y, width, Lead.DesiredSize.Height)); y += Lead.DesiredSize.Height + Spacing;
            double frameWidth = Math.Min(width, Frame.DesiredSize.Width);
            Frame.Arrange(new Rect(CenterFrame ? Math.Max(0, (width - frameWidth) / 2) : 0, y, frameWidth, Frame.DesiredSize.Height)); y += Frame.DesiredSize.Height + Spacing;
            if (Video?.IsVisible == true)
            {
                Video.FrameWidth = Frame.DesiredSize.Width;
                Video.Arrange(new Rect(CenterFrame ? Math.Max(0, (width - frameWidth) / 2) : 0, y, CenterFrame ? frameWidth : width, Video.DesiredSize.Height));
                y += Video.DesiredSize.Height + Spacing;
            }
            if (Trail.IsVisible) Trail.Arrange(new Rect(0, y, width, Trail.DesiredSize.Height));
            return finalSize;
        }
    }

    private readonly MediaFramePanel _slot = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }.MetaText();
    // MB1b: the shared media badges lie over exactly the shown picture (the frame panel gives the view its size on every layout): the play
    // mark of a video (scaled to the picture, 20 to 44 dip, centred, dark in both themes: the archive-play fix, now drawn by the badge view),
    // the star of a favourite, the GIF chip and a missing file. Not hit-testable, not in the accessibility tree: the picture button stays the
    // click target at any size and carries the words (MediaBadgeText.Describe).
    private readonly MediaBadgeView _badges = new() { Name = "ArchiveMediaBadges", Options = MediaBadgeOptions.Archive };
    private bool _missing;
    // Imageboard file line above the preview: "name.ext (size)" when the capture knows the size.
    // It takes the block's width and wraps to two lines, then trims (one trimmed line in a compact list row): a long name never widens the block beyond the picture.
    private readonly TextBlock _label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }.MetaText();
    // A quiet slot surface only while no thumbnail is shown (loading, missing, unsupported).
    private readonly Border _placeholder = new() { CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
    private readonly TextBlock _provenance = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false }.MetaText();
    private readonly Button _button;
    // The accent edge of an enlarged (focused) picture: the look the media sidebar shares (ReaderFocusedMedia).
    private readonly Border _edge = ReaderFocusedMedia.CreateFrameEdge();
    private Bitmap? _enlarged;
    private int _enlargedWidth;
    private CancellationTokenSource? _enlarging;
    private int _enlargeRequest;
    private readonly ArchiveFavoriteButton _favorite = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Bounded placeholder/loaded-preview height retained for the existing reader geometry contract.</summary>
    internal static double PreviewHeight(double width, double maximumHeight, Avalonia.PixelSize? loadedSize)
    {
        if (loadedSize is { Width: > 0, Height: > 0 } size)
            return Math.Min(maximumHeight, width * size.Height / size.Width);
        return width * 9d / 16;
    }
    /// <summary>
    /// Inherited switch set by an owner whose content is covered (for example by the gallery overlay): attachments in
    /// its subtree start no new thumbnail loads while it is true. Waiting work is canceled cooperatively;
    /// an executing native decoder retains ownership until it really returns. Resuming loads visible empty slots once.
    /// </summary>
    public static readonly AttachedProperty<bool> LoadingSuspendedProperty =
        AvaloniaProperty.RegisterAttached<ArchiveAttachmentView, Control, bool>("LoadingSuspended", false, inherits: true);
    public static bool GetLoadingSuspended(Control control) => control.GetValue(LoadingSuspendedProperty);
    public static void SetLoadingSuspended(Control control, bool value) => control.SetValue(LoadingSuspendedProperty, value);
    internal static long LoadsSkippedWhileSuspended;

    /// <summary>
    /// Inherited: the reader's hover preview. An attachment in its subtree offers the enlarged preview while the pointer rests on it,
    /// when the option is on; without one (lists, gallery strips) nothing happens on hover.
    /// </summary>
    internal static readonly AttachedProperty<ReaderHoverEnlarge?> HoverEnlargeProperty =
        AvaloniaProperty.RegisterAttached<ArchiveAttachmentView, Control, ReaderHoverEnlarge?>("HoverEnlarge", null, inherits: true);

    /// <summary>
    /// Inherited: the reader's inline video players (R10). With one whose setting is on, a click or Enter on a video attachment plays
    /// it here instead of opening the gallery; without one (lists, other pages) or with the setting off nothing changes.
    /// </summary>
    internal static readonly AttachedProperty<ReaderInlineVideoController?> InlineVideoProperty =
        AvaloniaProperty.RegisterAttached<ArchiveAttachmentView, Control, ReaderInlineVideoController?>("InlineVideo", null, inherits: true);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LoadingSuspendedProperty)
        {
            if (change.NewValue is true) CancelPendingLoad();
            else if (_visible) _ = LoadAsync();
        }
        if (change.Property == IsVisibleProperty && change.NewValue is false) SetViewportVisible(false);
        // The focused card (the list's selected item) and the enlargement percentage arrive by inheritance from the post list.
        if (change.Property == ReaderPostLayout.FocusedProperty || change.Property == ReaderPostLayout.FocusScalePercentProperty)
            RefreshFocus();
        // The reader's visible height caps the enlarged picture; it only matters while this card is enlarged or the picture is expanded in its post.
        if (change.Property == ReaderPostLayout.VisibleHeightProperty && _column != null && (FocusFactor() > 1 || _expanded))
        {
            ApplyFrameLimits(DataContext as ArchiveAttachmentRow, (DataContext as ArchiveAttachmentRow)?.LargePreview == true);
            _slot.InvalidateMeasure();
        }
        // (Inherited values arrive once attached; the guard only covers a property set before the constructor finished.)
        if (change.Property == InlineVideoProperty && _column != null)
        {
            if (change.OldValue is ReaderInlineVideoController old) old.OptionsChanged -= OnInlineOptionsChanged;
            if (change.NewValue is ReaderInlineVideoController current) current.OptionsChanged += OnInlineOptionsChanged;
            RefreshInlineVideo(); UpdateButtonName(DataContext as ArchiveAttachmentRow);
        }
        if (change.Property == ReaderPostLayout.FocusedProperty && _column != null) ScheduleInlineAutoplay();
    }

    // The choice "Clicking a medium" changed: a post that no longer opens media in place puts its image back and offers no video.
    private void OnInlineOptionsChanged()
    {
        if (_expanded && GetValue(InlineVideoProperty)?.Options.InPost != true) SetExpanded(false);
        RefreshInlineVideo();
        UpdateButtonName(DataContext as ArchiveAttachmentRow);
    }

    private double FocusFactor() =>
        ReaderPostLayout.FocusFactor(GetValue(ReaderPostLayout.FocusedProperty), GetValue(ReaderPostLayout.FocusScalePercentProperty));

    // The frame limits: the row's own width (textless posts are wider), times the focus factor while this card is focused.
    // Without either the legacy limits stay exactly as they were (no height override).
    private void ApplyFrameLimits(ArchiveAttachmentRow? row, bool large)
    {
        if (_expanded && row != null)
        {
            // "Open in the post": the picture takes the post column's width (the frame never exceeds what the owner gives), as high as the
            // reader shows at once, so a tall picture is narrower rather than longer than the window.
            _slot.MaximumWidthOverride = ExpandedMaximumWidth;
            _slot.MaximumHeightOverride = ExpandedMaximumHeight;
            _edge.IsVisible = false;
            return;
        }
        double factor = FocusFactor();
        double width = row?.ThumbnailWidth > 0 ? row.ThumbnailWidth : MediaFramePanel.MaximumWidth(large);
        _slot.MaximumWidthOverride = width * factor;
        double height = row?.ThumbnailMaxHeight > 0 ? row.ThumbnailMaxHeight : MediaFramePanel.MaximumHeight(large);
        double cap = row?.ThumbnailMaxHeight > 0 || factor > 1 ? height * factor : double.NaN;
        // An enlarged picture is shown whole: its height is capped by what the visible reader leaves under the post header (ReaderMediaFit).
        if (factor > 1 && VisibleFrameHeight() is var visible && double.IsFinite(visible))
            cap = ReaderMediaFit.HeightCap(height, factor, visible, MediaSlotGeometry.Fit(_slot.Aspect, double.PositiveInfinity, width, height).Height);
        _slot.MaximumHeightOverride = cap;
        _edge.IsVisible = factor > 1 && row != null;
    }

    // ---- "Open in the post": an image expands in its post --------------------------------------------------------------------------

    private bool _expanded;
    private const double ExpandedMaximumWidth = 8192, ExpandedFallbackHeight = 720, ExpandedMinimumHeight = 240;

    // As high as the visible reader leaves under the post's chrome (the shared ReaderMediaFit helper of the focus enlargement, glue wave8),
    // at least 240 dip; 720 dip while the reader's visible height is not known yet.
    private double ExpandedMaximumHeight =>
        VisibleFrameHeight() is var visible && double.IsFinite(visible) && visible > 0 ? Math.Max(ExpandedMinimumHeight, visible) : ExpandedFallbackHeight;

    /// <summary>Whether the picture is expanded in its post (the choice "Open in the post"; a second click puts it back).</summary>
    internal bool IsExpanded => _expanded;

    /// <summary>The picture expands to the post column's width, or goes back to its thumbnail size.</summary>
    internal void SetExpanded(bool expanded)
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        if (expanded && !ReaderInlineVideoRules.CanExpand(GetValue(InlineVideoProperty)?.Options ?? default, row)) expanded = false;
        if (_expanded == expanded) return;
        _expanded = expanded;
        GetValue(HoverEnlargeProperty)?.Leave(this);
        RefreshFocus();
        _column.InvalidateMeasure();
        Avalonia.Automation.AutomationProperties.SetHelpText(_button, expanded ? Texts.T("reader.media.collapseHint") : null);
    }

    private bool WantsEnlarged => _expanded || FocusFactor() > 1;

    /// <summary>
    /// The height around this picture inside the reader card: the post header above the media row, the file line (and a video row or
    /// provenance line), this view's margins and the card's bottom padding. A card that was not arranged yet (freshly realized by a
    /// jump) uses what its list's last arranged card measured (<see cref="ReaderMediaFit.ChromeMemo"/>), the header estimate before any.
    /// </summary>
    internal double ChromeHeight()
    {
        double above = double.NaN;
        ReaderMediaFit.ChromeMemo? memo = null;
        if (this.FindAncestorOfType<ReaderPostLayout>() is { } layout && layout.FindAncestorOfType<ListBoxItem>() is { } card)
        {
            if (card.FindAncestorOfType<ListBox>() is { } list) memo = ChromeMemos.GetValue(list, _ => new ReaderMediaFit.ChromeMemo());
            // Before its first arrange the layout sits at the card's origin: that is no measurement (the header is always above it).
            if (layout.TranslatePoint(default, card) is { Y: > 0 } at) above = at.Y;
        }
        double header = memo?.Header(above) ?? (above > 0 ? above : ReaderMediaFit.HeaderEstimate);
        double column = _column.NonFrameHeight;
        // The file line always has a line of text once measured; zero means the column was not measured yet.
        if (memo != null) column = memo.Column(column, _column.Lead.DesiredSize.Height > 0);
        return header + Margin.Top + Margin.Bottom + column + ReaderMediaFit.CardBottomPadding;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, ReaderMediaFit.ChromeMemo> ChromeMemos = new();

    /// <summary>
    /// The height a picture's frame may use in the visible reader (viewport minus <see cref="ChromeHeight"/> and the air a card aligned by
    /// a jump keeps above and below, <see cref="ReaderMediaFit.AlignedAir"/>); NaN while the viewport is unknown.
    /// </summary>
    internal double VisibleFrameHeight() =>
        ReaderMediaFit.VisibleHeight(GetValue(ReaderPostLayout.VisibleHeightProperty), ChromeHeight(), ReaderMediaFit.AlignedAir);

    /// <summary>Raised (bubbling) when the frame of an enlarged picture changed size, so the reader can keep the whole picture in view.</summary>
    internal static readonly Avalonia.Interactivity.RoutedEvent<Avalonia.Interactivity.RoutedEventArgs> FocusedFrameChangedEvent =
        Avalonia.Interactivity.RoutedEvent.Register<ArchiveAttachmentView, Avalonia.Interactivity.RoutedEventArgs>(nameof(FocusedFrameChangedEvent), Avalonia.Interactivity.RoutingStrategies.Bubble);

    /// <summary>The frame's rectangle in the coordinates of <paramref name="ancestor"/>; null while it cannot be placed.</summary>
    internal Rect? FrameBoundsIn(Visual ancestor) =>
        _slot.TranslatePoint(default, ancestor) is { } at ? new Rect(at, _slot.Bounds.Size) : null;

    private void RefreshFocus()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        ApplyFrameLimits(row, row?.LargePreview == true);
        _slot.InvalidateMeasure();
        if (WantsEnlarged) _ = EnlargeAsync();
        else DropEnlarged();
    }

    /// <summary>
    /// What a click (or Enter on the post) does with this medium, by the choice "Clicking a medium": "Open in the post" plays a video or
    /// expands an image here (a second time puts it back); "In the sidebar" shows it there (the gallery while the sidebar is closed); "In the
    /// gallery" opens the gallery. A double click always opens the gallery.
    /// </summary>
    internal void ActivateMedium()
    {
        if (DataContext is not ArchiveAttachmentRow row) return;
        ReaderInlineVideoOptions options = GetValue(InlineVideoProperty)?.Options ?? default;
        // The sidebar is assumed to be shown here; whether it took the tile is answered by the page (TileActivation), else the gallery opens.
        switch (ReaderMediaClickRules.Click(options, InlineVideoOffered, ReaderInlineVideoRules.CanExpand(options, row), sidebarShown: true))
        {
            case ReaderMediaClickRules.Outcome.PlayInPost:
                GetValue(HoverEnlargeProperty)?.Leave(this);
                ReaderInlineVideoSlot slot = EnsureInlineVideo();
                _ = slot.IsLive ? slot.ToggleAsync() : slot.PlayAsync();
                return;
            case ReaderMediaClickRules.Outcome.ExpandInPost:
                SetExpanded(!_expanded);
                return;
            case ReaderMediaClickRules.Outcome.ShowInSidebar when GetValue(TileActivationProperty)?.Invoke(row) == true:
                return;
        }
        row.OpenPreview?.Invoke();
    }

    // The focused card's picture is decoded again at its larger size, only for this card and only when the ordinary decode is smaller.
    // A failure keeps the ordinary picture: enlarging is never an error.
    private async Task EnlargeAsync()
    {
        double factor = FocusFactor();
        if (!WantsEnlarged || !_visible || _bitmap == null || GetLoadingSuspended(this)
            || DataContext is not ArchiveAttachmentRow { LoadSized: { } load } row) return;
        // Expanded: the post column's width (what the frame can get), else the focus factor times the thumbnail width.
        int target = _expanded ? (int)Math.Ceiling(Math.Max(row.ThumbnailWidth, Math.Min(ExpandedMaximumWidth, Bounds.Width > 1 ? Bounds.Width : row.ThumbnailWidth)))
            : (int)Math.Ceiling(Math.Max(1, row.ThumbnailWidth) * factor);
        ReaderDisplayPreferences.ThumbnailDecodeBounds bounds = ReaderDisplayPreferences.DecodeBoundsForWidth(target, row.LargePreview);
        if (ReaderDisplayPreferences.DecodeBoundsForWidth(row.ThumbnailWidth, row.LargePreview).MaximumWidth >= bounds.MaximumWidth
            || _enlarged != null && _enlargedWidth >= bounds.MaximumWidth) return;
        CancelEnlarging();
        int request = ++_enlargeRequest;
        CancellationTokenSource enlarging = new();
        _enlarging = enlarging;
        Task<Bitmap?>? pending = null;
        try
        {
            pending = load(bounds.MaximumWidth, bounds.MaximumHeight, enlarging.Token);
            Bitmap? bitmap = await pending.WaitAsync(LoadTimeout, enlarging.Token);
            if (request != _enlargeRequest || enlarging.IsCancellationRequested || !_visible || !WantsEnlarged)
            { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            if (bitmap == null) return;
            Bitmap? previous = _enlarged;
            _enlarged = bitmap; _enlargedWidth = bounds.MaximumWidth;
            _image.Source = bitmap;
            if (previous != null) MediaTileThumbnails.Release(previous);
        }
        catch (Exception)
        {
            enlarging.Cancel();
            if (pending != null) await DisposeLateAsync(pending);
        }
        finally
        {
            if (ReferenceEquals(_enlarging, enlarging)) _enlarging = null;
            enlarging.Dispose();
        }
    }

    private void CancelEnlarging()
    {
        CancellationTokenSource? running = _enlarging;
        _enlarging = null;
        try { running?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // The ordinary picture is shown again before the enlarged one is given back.
    private void DropEnlarged()
    {
        CancelEnlarging();
        ++_enlargeRequest;
        if (_enlarged == null) return;
        _image.Source = _bitmap;
        MediaTileThumbnails.Release(_enlarged);
        _enlarged = null; _enlargedWidth = 0;
    }

    private CancellationTokenSource? _loading;
    private bool _reloadAfterDrain;
    private Bitmap? _bitmap;
    private bool _visible;
    private bool _wasEffectivelyVisible;
    private int _request;
    internal TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public ArchiveAttachmentView()
    {
        _placeholder[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Raised");
        _placeholder.Bind(IsVisibleProperty, _status.GetObservable(IsVisibleProperty));
        // Flat: no raised box around the thumbnail. Semi's hover/pressed tint and the shared focus adorner remain the interaction
        // feedback of the native button; the button is the frame, so both run around the drawn picture and nothing else.
        Grid surface = new();
        surface.Children.Add(_placeholder); surface.Children.Add(_image); surface.Children.Add(_status);
        _button = new Button { Content = surface, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        _button.Click += (_, _) => ActivateMedium();
        // A double click always opens the gallery, whatever "Clicking a medium" is set to.
        _button.DoubleTapped += (_, e) =>
        {
            if (DataContext is not ArchiveAttachmentRow { OpenPreview: { } open }) return;
            e.Handled = true;
            if (_expanded) SetExpanded(false);
            open();
        };
        // The file name is a click target of the same medium: it does what a click on the thumbnail does (a double click opens the gallery).
        ConfigureFileNameClick();
        // The play mark is not part of the click target; it is arranged on the same rectangle as the picture, so it is centred on it.
        _slot.Children.Add(_button); _slot.Children.Add(_badges); _slot.Children.Add(_edge);
        _slot.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && FocusFactor() > 1) RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(FocusedFrameChangedEvent));
        };
        // The hover preview of the reader (off unless the option is on; the owner supplies it through HoverEnlargeProperty).
        _button.PointerEntered += (_, _) =>
        {
            if (DataContext is ArchiveAttachmentRow row && FocusFactor() <= 1 && !_expanded) GetValue(HoverEnlargeProperty)?.Enter(this, row, _button);
        };
        _button.PointerExited += (_, _) => GetValue(HoverEnlargeProperty)?.Leave(this);
        _button.AddHandler(PointerPressedEvent, (_, _) => GetValue(HoverEnlargeProperty)?.Leave(this), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _button.AddHandler(PointerWheelChangedEvent, (_, _) => GetValue(HoverEnlargeProperty)?.Leave(this), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // The file line and the star share one row above the thumbnail (the star used to take a row of its own and
        // pushed the picture down); the thumbnail below it is the click target for the preview. The row is as wide as the picture.
        Grid fileLine = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 0 };
        Grid.SetColumn(_favorite, 1);
        fileLine.Children.Add(_label); fileLine.Children.Add(_favorite);
        _column = new AttachmentColumn { Lead = fileLine, Frame = _slot, Trail = _provenance,
            Children = { fileLine, _slot, _provenance } };
        Content = _column;
        DataContextChanged += (_, _) => { _missing = false; _expanded = false; WatchAspect(DataContext as ArchiveAttachmentRow); Reset(); UpdateLabel(); RefreshInlineVideo(); if (_visible) _ = LoadAsync(); };
        EffectiveViewportChanged += (_, e) =>
        {
            Rect bounds = new(Bounds.Size);
            bool intersects = bounds.Intersects(e.EffectiveViewport) && IsEffectivelyVisible;
            SetViewportVisible(intersects);
            _inlineVideo?.ApplyViewport(intersects, intersects && e.EffectiveViewport.Contains(bounds));
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(RefreshViewport, DispatcherPriority.Loaded);
        SizeChanged += (_, _) => RefreshViewport();
        // Ancestor visibility can change without resizing this reserved slot.
        LayoutUpdated += (_, _) =>
        {
            bool visible = VisualRoot != null && IsEffectivelyVisible;
            if (_wasEffectivelyVisible == visible) return;
            _wasEffectivelyVisible = visible;
            RefreshViewport();
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); _autoplay?.Stop(); _ = _inlineVideo?.ReleaseAsync(); };
    }

    // The file name beside the thumbnail: a click is the thumbnail's click (ActivateMedium), a double click its double click. Pointer only: the thumbnail
    // button is the keyboard route. The hand shows over the name.
    private void ConfigureFileNameClick()
    {
        _label.Cursor = PointerCursors.Hand;
        _label.Tapped += (_, e) =>
        {
            if (DataContext is not ArchiveAttachmentRow || e.Handled) return;
            e.Handled = true;
            ActivateMedium();
        };
        _label.DoubleTapped += (_, e) =>
        {
            if (DataContext is not ArchiveAttachmentRow { OpenPreview: { } open }) return;
            e.Handled = true;
            if (_expanded) SetExpanded(false);
            open();
        };
    }

    private readonly AttachmentColumn _column;
    private ArchiveAttachmentRow? _aspectSource;
    private ReaderInlineVideoSlot? _inlineVideo;

    // A size the reader learns while the row is shown (header read, preload) reserves the frame at once, before the pixels arrive.
    private void WatchAspect(ArchiveAttachmentRow? row)
    {
        if (_aspectSource != null) _aspectSource.KnownAspectChanged -= OnKnownAspectChanged;
        _aspectSource = row;
        if (row != null) row.KnownAspectChanged += OnKnownAspectChanged;
    }

    private void OnKnownAspectChanged()
    {
        if (DataContext is ArchiveAttachmentRow row && ReferenceEquals(row, _aspectSource)) SetFrame(row.LargePreview, row.KnownAspect);
    }
    private DispatcherTimer? _autoplay;

    /// <summary>The inline video of this attachment (smoke seam); null until it was offered once.</summary>
    internal ReaderInlineVideoSlot? InlineVideo => _inlineVideo;

    private bool InlineVideoOffered => ReaderInlineVideoRules.CanOfferInPost(GetValue(InlineVideoProperty)?.Options ?? default, DataContext as ArchiveAttachmentRow);

    private ReaderInlineVideoSlot EnsureInlineVideo()
    {
        if (_inlineVideo != null) return _inlineVideo;
        // Posts keep their own thumbnail button as the idle click target (hover preview, media menu); the slot's activator appears
        // only over a paused older video, whose picture covers that button.
        ReaderInlineVideoSlot slot = _inlineVideo = new ReaderInlineVideoSlot(_slot, activatorWhenIdle: false);
        _column.Video = slot.Row;
        _column.Children.Add(slot.Row);
        slot.Row.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) _column.InvalidateMeasure(); };
        RefreshInlineVideo();
        return slot;
    }

    // The key is the attachment, not the row object: a rebuilt page (same thread, same post) keeps a running player.
    private void RefreshInlineVideo()
    {
        if (_inlineVideo == null && !InlineVideoOffered) return;
        ReaderInlineVideoSlot slot = EnsureInlineVideo();
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        bool offered = InlineVideoOffered;
        slot.Offer(offered ? GetValue(InlineVideoProperty) : null, offered ? ReaderInlineVideoSlot.KeyOf(row!.Attachment) : null,
            row?.ResolveEntry, row?.Attachment.OriginalFileName ?? row?.Attachment.MediaId ?? string.Empty, row?.OpenPreview);
    }

    /// <summary>The inline video of this attachment for the reader's video keys: its slot when the video can play here, else none.</summary>
    internal ReaderInlineVideoSlot? InlineVideoForKeys() => InlineVideoOffered ? EnsureInlineVideo() : null;

    /// <summary>
    /// Inherited: the reader's media sidebar takes a click on a tile of its focused post (the tile is then shown there instead of opening
    /// the gallery or playing in the post). It returns true when it took the click.
    /// </summary>
    internal static readonly AttachedProperty<Func<ArchiveAttachmentRow, bool>?> TileActivationProperty =
        AvaloniaProperty.RegisterAttached<ArchiveAttachmentView, Control, Func<ArchiveAttachmentRow, bool>?>("TileActivation", null, inherits: true);

    // Autoplay (opt-in, muted) of the focused card's first video, once the selection rested; never while the sidebar presents it.
    private void ScheduleInlineAutoplay()
    {
        _autoplay?.Stop();
        if (!GetValue(ReaderPostLayout.FocusedProperty) || GetValue(InlineVideoProperty) is not { AutoplayInPosts: true } controller
            || !ReaderInlineVideoRules.ShouldAutoplay(controller.Options, _visible, presentedElsewhere: false) || !InlineVideoOffered
            || !IsFirstVideoOfPost()) return;
        _autoplay ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReaderInlineVideoRules.AutoplayDelayMs) };
        _autoplay.Tick -= OnAutoplayTick;
        _autoplay.Tick += OnAutoplayTick;
        _autoplay.Start();
    }

    private void OnAutoplayTick(object? sender, EventArgs e)
    {
        _autoplay?.Stop();
        if (GetValue(ReaderPostLayout.FocusedProperty) && _visible && InlineVideoOffered && _inlineVideo?.IsLive != true
            && GetValue(InlineVideoProperty) is { AutoplayInPosts: true })
            _ = EnsureInlineVideo().PlayAsync(muted: true);
    }

    private bool IsFirstVideoOfPost() =>
        this.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not ArchivePostRow post
        || ReferenceEquals(post.Attachments.FirstOrDefault(item => ReaderInlineVideoRules.IsVideo(item.Attachment.OriginalFileName ?? item.Attachment.MediaId)), DataContext);

    internal void RefreshFavorite()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync, row?.FavoriteError);
        UpdateBadges(row);
        UpdateButtonName(row);
    }

    /// <summary>The shared badge view over the picture (smokes and tests read its state and plan).</summary>
    internal MediaBadgeView Badges => _badges;

    // The badges follow the row (favourite, kind) and a load that found the file gone. The view is told the shown picture's size; the frame
    // panel repeats that on every layout, so a state change before the first layout is picked up there. Nothing is allocated when the state
    // did not change (the view compares, the sentence is cached).
    private void UpdateBadges(ArchiveAttachmentRow? row)
    {
        if (row == null) { _badges.Clear(); return; }
        _badges.Apply(ArchiveMediaBadges.ForAttachment(row, _missing), _slot.PictureSize);
    }

    internal Image Thumbnail => _image;
    internal Button PreviewButton => _button;
    internal string? PreviewStatus => _status.IsVisible ? _status.Text : null;

    private void UpdateLabel()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync, row?.FavoriteError);
        bool large = row?.LargePreview == true;
        ApplyFrameLimits(row, large);
        SetFrame(large, row?.KnownAspect);
        _slot.InvalidateMeasure();
        _label.Text = row?.FileLine ?? string.Empty;
        // A compact list row keeps the file line to one trimmed line (the full text is its tooltip); reader posts wrap it to two.
        bool compact = row?.Compact == true;
        _label.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        _label.MaxLines = compact ? 1 : 2;
        ToolTip.SetTip(_label, compact ? _label.Text : null);
        _column.CenterFrame = compact;
        // The thread list never wires the file favourite (the thread's own star is on the right): no dead, always disabled star there,
        // the file line takes the full width. Reader posts keep it.
        _favorite.IsVisible = !compact;
        _column.InvalidateArrange();
        _provenance.Text = MediaProvenanceText.Summary(row?.Binding);
        _provenance.IsVisible = !string.IsNullOrEmpty(_provenance.Text);
        UpdateBadges(row);
        _status.Text = Texts.T("archive.loadingThumbnail");
        ToolTip.SetTip(_button, row?.Label ?? string.Empty);
        UpdateButtonName(row);
    }

    // R10: with inline video on, the thumbnail of a video is its play control and says so.
    private void UpdateButtonName(ArchiveAttachmentRow? row)
    {
        bool inline = row != null && InlineVideoOffered;
        // MB1b: what the badges show (favourite, video or GIF, file missing) is part of the button's name and help, as one short sentence.
        string badges = row == null ? string.Empty : MediaBadgeSentences.Get(_badges.State, _badges.Options);
        Avalonia.Automation.AutomationProperties.SetName(_button, ArchiveMediaBadges.Append(inline
            ? Texts.T("reader.video.play", row!.Attachment.OriginalFileName ?? row.Attachment.MediaId ?? Texts.T("reader.video.unnamed"))
            : row?.Label ?? string.Empty, badges, "\n"));
        Avalonia.Automation.AutomationProperties.SetHelpText(_button, inline ? ArchiveMediaBadges.Append(Texts.T("reader.video.playHint"), badges, "\n")
            : badges.Length > 0 ? badges : null);
    }

    // The frame follows the drawn picture: the wide-screen reservation until one is known, then its aspect. The row remembers it, so a
    // preview that was scrolled out (pixels released) or a recycled container keeps the geometry and the list does not jump again.
    private void SetFrame(bool large, double? aspect)
    {
        if (_slot.Large == large && _slot.Aspect == aspect) return;
        _slot.Large = large; _slot.Aspect = aspect;
        _slot.InvalidateMeasure();
    }

    private void RefreshViewport()
    {
        if (VisualRoot == null || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
        { SetViewportVisible(false); return; }
        ScrollViewer? scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Point? position = scroll == null ? null : this.TranslatePoint(default, scroll);
        if (scroll != null && position != null)
            SetViewportVisible(new Rect(position.Value, Bounds.Size).Intersects(new Rect(scroll.Bounds.Size)));
    }

    private void SetViewportVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (visible) { UpdateLabel(); _ = LoadAsync(); }
        else
        {
            Reset();
            // R10: out of view (scrolled away, another tab, a hidden reader) a video pauses, or is released on a native surface.
            _inlineVideo?.ApplyViewport(intersects: false, fullyVisible: false);
        }
    }

    private async Task LoadAsync()
    {
        if (!_visible || !IsEffectivelyVisible || _bitmap != null || DataContext is not ArchiveAttachmentRow { LoadThumbnail: not null } row) return;
        if (GetLoadingSuspended(this))
        {
            Interlocked.Increment(ref LoadsSkippedWhileSuspended);
            PerformanceDiagnostics.Record("gallery.thumbnail", "suppressed");
            return;
        }
        if (_loading != null)
        {
            // The old data context/viewport can have canceled a native operation that has not
            // returned yet. Remember one demand; do not relinquish its ownership early.
            if (_loading.IsCancellationRequested) _reloadAfterDrain = true;
            return;
        }
        _reloadAfterDrain = false;
        int request = _request;
        using CancellationTokenSource loading = new();
        _loading = loading;
        Task<Bitmap?>? pending = null;
        long loadId = PerformanceDiagnostics.NextId();
        using var measurement = PerformanceDiagnostics.Measure("gallery.thumbnail.load", loadId);
        try
        {
            pending = row.LoadThumbnail(loading.Token);
            Bitmap? bitmap = await pending.WaitAsync(LoadTimeout, loading.Token);
            if (request != _request || loading.IsCancellationRequested || !_visible)
            { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            _missing = false;
            UpdateLabel();
            // A reserved size this picture agrees with stays exact (no fractional move); only a different picture changes the frame.
            if (bitmap != null && ReaderPreviewReservation.Settle(row.KnownAspect, bitmap.PixelSize.Width, bitmap.PixelSize.Height) is { } aspect)
            { row.KnownAspect = aspect; SetFrame(row.LargePreview, aspect); }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _image.IsVisible = bitmap != null;
            if (bitmap != null && WantsEnlarged) _ = EnlargeAsync();
            _status.IsVisible = bitmap == null;
            _status.Text = Texts.T(MeowChan.Gallery.SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty)
                == MeowChan.Gallery.SavedMediaKind.Video ? "reader.video" : "archive.inlineUnsupported");
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            PerformanceDiagnostics.Record("gallery.thumbnail", "canceled", loadId);
            if (pending != null) await DisposeLateAsync(pending);
        }
        catch (Exception exception)
        {
            loading.Cancel();
            if (request == _request && _visible)
            {
                // A file that is gone is a safety fact the badges show (a failure to decode is not).
                _missing = ArchiveMediaBadges.IsMissingFile(exception);
                UpdateLabel();
                // Report the timeout now; draining a noncancelable codec can take longer.
                _status.Text = exception is OperationCanceledException or TimeoutException
                    ? Texts.T("archive.previewLoadFailed")
                    : Texts.T("archive.previewLoadFailedDetail", exception.Message);
                _status.IsVisible = true;
            }
            if (pending != null) await DisposeLateAsync(pending);
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) _loading = null;
            if (_reloadAfterDrain && _visible && !GetLoadingSuspended(this)) _ = LoadAsync();
        }
    }

    private static async Task DisposeLateAsync(Task<Bitmap?> pending)
    {
        try { if (await pending is { } late) MediaTileThumbnails.Release(late); }
        catch (Exception) { } // The visible request already reports the original failure/cancellation.
    }

    private void CancelPendingLoad()
    {
        if (_loading == null || _loading.IsCancellationRequested) return;
        ++_request;
        _reloadAfterDrain = true;
        _loading.Cancel();
    }

    private void Reset()
    {
        ++_request;
        CancelPendingLoad();
        _image.Source = null; _image.IsVisible = false;
        CancelEnlarging(); ++_enlargeRequest;
        if (_enlarged != null) MediaTileThumbnails.Release(_enlarged); _enlarged = null; _enlargedWidth = 0;
        if (_bitmap != null) MediaTileThumbnails.Release(_bitmap); _bitmap = null;
        GetValue(HoverEnlargeProperty)?.Leave(this);
        _status.IsVisible = true;
    }
}
