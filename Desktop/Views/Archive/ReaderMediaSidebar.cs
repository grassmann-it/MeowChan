using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The rules of the reader's media sidebar (R8) that need no window: when it is shown, which medium of the focused post it presents,
/// what it announces and how large it decodes. <see cref="ReaderMediaSidebar"/> is the view; <c>ArchivePage.MediaSidebar</c> drives it.
/// </summary>
internal static class ReaderMediaSidebarRules
{
    /// <summary>
    /// While ↑/↓ is held the sidebar follows at most once per this many milliseconds (the first change shows at once). A pacing
    /// constant of the decode path, not a user limit.
    /// </summary>
    internal const int FollowIntervalMs = 120;
    /// <summary>The screen reader hears the medium only after the selection rested this long (polite live region).</summary>
    internal const int AnnounceDelayMs = 300;
    /// <summary>A medium that is not ready after this long shows its placeholder (a warm cache answers well before).</summary>
    internal const int PlaceholderDelayMs = 100;
    /// <summary>The posts keep at least this width beside the sidebar (layout constant; the reader-width setting hides the sidebar first).</summary>
    internal const double PostsMinimumWidth = 480;
    internal const double SplitterWidth = 6;

    /// <summary>
    /// The archive view keeps a gap of this width on each side of the splitter beside the posts card (as the archive split does between
    /// the thread list and the reader); the large reader's frame has none. Layout constant.
    /// </summary>
    internal const double ArchiveViewGap = 6;

    /// <summary>
    /// Switched on for this view: the large reader shows the sidebar when it is on (Large reader only or Always), the archive view only
    /// with Always.
    /// </summary>
    internal static bool IsOn(ReaderMediaPreferences media, bool largeReader) => media.Sidebar && (largeReader || media.SidebarAlways);

    /// <summary>
    /// Shown when switched on for this view (<see cref="IsOn"/>) and only while the reader is at least the configured width. An unknown
    /// width (not laid out yet) counts as wide enough, so opening the reader does not lay the posts out twice.
    /// </summary>
    internal static bool IsShown(ReaderMediaPreferences media, bool largeReader, double readerWidth) =>
        IsOn(media, largeReader) && !IsCollapsedByWidth(media, readerWidth);

    /// <summary>
    /// The sidebar switch (View option, Primary+Shift+M, × and Escape in the sidebar) acts on the view it is used in: in the large reader it
    /// turns the sidebar on or off (the archive-view choice is kept for the next time it is on); in the archive view it chooses between
    /// Always (on, also here) and Large reader only, so hiding it beside the posts never takes it from the large reader.
    /// </summary>
    internal static ReaderMediaPreferences Toggled(ReaderMediaPreferences media, bool largeReader)
    {
        if (largeReader) return media with { Sidebar = !media.Sidebar };
        return IsOn(media, largeReader: false) ? media with { SidebarAlways = false } : media with { Sidebar = true, SidebarAlways = true };
    }

    /// <summary>Chosen, but hidden because the reader is narrower than the setting.</summary>
    internal static bool IsCollapsedByWidth(ReaderMediaPreferences media, double readerWidth) =>
        media.Sidebar && double.IsFinite(readerWidth) && readerWidth > 0 && readerWidth < media.Clamped().SidebarMinimumReaderWidth;

    /// <summary>A dragged width as the setting stores it: whole pixels within the setting's bounds.</summary>
    internal static int ClampWidth(double width) => double.IsFinite(width)
        ? (int)Math.Clamp(Math.Round(width), ReaderDisplayPreferences.MediaSidebarWidthMinimum, ReaderDisplayPreferences.MediaSidebarWidthMaximum)
        : ReaderDisplayPreferences.MediaSidebarWidthDefault;

    /// <summary>The width the sidebar gets in a reader this wide: the setting, but never so wide that the posts drop below their minimum
    /// (<paramref name="gaps"/>: the room the archive view keeps beside the splitter).</summary>
    internal static double EffectiveWidth(int configured, double readerWidth, double gaps = 0)
    {
        double width = ClampWidth(configured);
        if (!double.IsFinite(readerWidth) || readerWidth <= 0) return width;
        double room = readerWidth - SplitterWidth - Math.Max(0, gaps) - PostsMinimumWidth;
        return Math.Max(ReaderDisplayPreferences.MediaSidebarWidthMinimum, Math.Min(width, room));
    }

    /// <summary>
    /// Warms the cache for the neighbouring media: each is resolved and handed to <paramref name="warm"/>. Only the shown medium may
    /// report an error, so a neighbour that cannot be resolved or warmed is skipped without a word (it is simply not ready early);
    /// cancellation still ends the whole run.
    /// </summary>
    internal static async Task PreloadNeighboursAsync<T>(IEnumerable<Func<CancellationToken, Task<T?>>?> neighbours,
        Func<T, CancellationToken, Task> warm, CancellationToken token) where T : class
    {
        foreach (Func<CancellationToken, Task<T?>>? resolve in neighbours)
        {
            token.ThrowIfCancellationRequested();
            if (resolve == null) continue;
            try
            {
                if (await resolve(token) is { } entry) await warm(entry, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Serilog.Log.Debug(error, "A media sidebar neighbour could not be preloaded"); }
        }
    }

    /// <summary>The shown medium of a post with <paramref name="count"/> attachments: the index kept within them, 0 without any.</summary>
    internal static int ClampIndex(int index, int count) => count <= 0 ? 0 : Math.Clamp(index, 0, count - 1);

    /// <summary>One step through the post's media; no wrap-around (the keys do nothing at the ends).</summary>
    internal static int Step(int index, int count, int delta) => ClampIndex(ClampIndex(index, count) + Math.Sign(delta), count);

    internal static bool CanStep(int index, int count, int delta) => count > 1 && Step(index, count, delta) != ClampIndex(index, count);

    /// <summary>
    /// The sidebar's one header line: "No. 75 · 1745622964284451.mp4 · 880 KiB". Parts that are not known are left out; with none of them the
    /// line is empty and the view shows the plain title.
    /// </summary>
    internal static string HeaderText(string postLine, string fileName, string detail) =>
        string.Join(" · ", new[] { postLine, fileName, detail }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>"cat.jpg, 1.2 MiB": the file name and, when the capture knows it, the size (binary units).</summary>
    internal static string FileText(CapturedAttachmentText attachment)
    {
        string name = attachment.Name;
        return attachment.Size is > 0 and long size ? (name.Length > 0 ? name + ", " : string.Empty) + SizeText.Format(size) : name;
    }

    /// <summary>The live-region and region name: "Media of post No. 124, 2 of 3, cat.jpg, 1.2 MiB".</summary>
    internal static string RegionText(string postId, int index, int count, CapturedAttachmentText attachment) =>
        Texts.T("reader.sidebar.region", postId, ClampIndex(index, count) + 1, count, FileText(attachment));

    /// <summary>The decode target in physical pixels for a frame of this logical size (DPI-aware, never below one pixel).</summary>
    internal static (double Width, double Height) PhysicalTarget(Size frame, double scaling)
    {
        double scale = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        return (Math.Max(1, Math.Ceiling(frame.Width * scale)), Math.Max(1, Math.Ceiling(frame.Height * scale)));
    }

    /// <summary>
    /// Whether a shown picture of <paramref name="shown"/> pixels is still sharp enough for a frame needing <paramref name="needed"/>:
    /// a little smaller is fine (one decode per real resize, not per pixel of a drag); a picture whose source is smaller never asks again.
    /// </summary>
    internal static bool NeedsLargerDecode(PixelSize shown, (double Width, double Height) needed, PixelSize? source)
    {
        if (shown.Width <= 0 || shown.Height <= 0) return true;
        if (source is { } original && shown.Width >= original.Width && shown.Height >= original.Height) return false;
        return shown.Width < needed.Width * 0.9 && shown.Height < needed.Height * 0.9;
    }

    /// <summary>
    /// The largest logical size the picture is drawn at, from the gallery's fit limit: unlimited (fills the frame) unless the gallery
    /// limits enlarging, then the source size times that percentage, as the gallery's Fit does.
    /// </summary>
    internal static double MaximumDisplay(int sourcePixels, GalleryFitPreferences fit) => fit.Limit && sourcePixels > 0
        ? sourcePixels * GalleryFitPreferences.ClampPercent(fit.MaxUpscalePercent) / 100.0 : double.PositiveInfinity;
}

/// <summary>The file facts of one attachment as the sidebar shows them (testable without a capture).</summary>
internal readonly record struct CapturedAttachmentText(string Name, long? Size)
{
    internal static CapturedAttachmentText Of(ArchiveAttachmentRow row) =>
        new(row.Attachment.OriginalFileName ?? row.Attachment.MediaId ?? string.Empty, row.Attachment.ExpectedSize);
}

/// <summary>
/// "Follow the selection, but not on every key repeat": the first change after a quiet moment is shown at once, later ones within
/// <see cref="ReaderMediaSidebarRules.FollowIntervalMs"/> wait for the next tick, and only the latest of them is shown then.
/// Times are in milliseconds of any monotonic clock.
/// </summary>
internal sealed class ReaderMediaFollowThrottle
{
    private readonly long _interval;
    private long _last;
    private bool _shownOnce;

    internal ReaderMediaFollowThrottle(long intervalMs = ReaderMediaSidebarRules.FollowIntervalMs) => _interval = Math.Max(0, intervalMs);

    /// <summary>A change waits for <see cref="Tick"/>.</summary>
    internal bool Pending { get; private set; }

    /// <summary>A new selection: true when it may be shown now, false when it waits (then the caller schedules a tick).</summary>
    internal bool Request(long nowMs)
    {
        if (!_shownOnce || nowMs - _last >= _interval)
        {
            _shownOnce = true;
            _last = nowMs;
            Pending = false;
            return true;
        }
        Pending = true;
        return false;
    }

    /// <summary>A change that should wait for the next tick in any case (a selection that was only cleared).</summary>
    internal void MarkPending() => Pending = true;

    /// <summary>The scheduled tick: true when a waiting change should be shown now.</summary>
    internal bool Tick(long nowMs)
    {
        if (!Pending) return false;
        Pending = false;
        _shownOnce = true;
        _last = nowMs;
        return true;
    }

    /// <summary>Milliseconds until a waiting change may be shown.</summary>
    internal long Remaining(long nowMs) => Math.Max(1, _interval - (nowMs - _last));

    internal void Reset()
    {
        Pending = false;
        _shownOnce = false;
    }
}

/// <summary>
/// The reader's media sidebar (R8): the focused post's media large and at full height beside the posts, with its file line, the
/// thumbnail strip for a post with several media, the favourite star and "Open in gallery". A passive view: <c>ArchivePage</c> tells it what to show and
/// owns the decode, the lease and the timers. It holds at most one bitmap and gives it back through the release it was handed with it.
/// Theme tokens only; the picture frame has the accent edge of <see cref="ReaderFocusedMedia.CreateFrameEdge"/>, as the enlarged card.
/// </summary>
internal sealed class ReaderMediaSidebar : Border
{
    // The header line: "No. 75 · file name · 880 KiB", trimmed with an ellipsis (the full text is its tip).
    private readonly TextBlock _title = new() { Name = "ReaderMediaSidebarHeader", TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private string _postLine = string.Empty, _fileName = string.Empty, _detail = string.Empty;
    private readonly Grid _frame = new() { ClipToBounds = true, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _placeholder = new() { IsHitTestVisible = false };
    private readonly TextBlock _status = new TextBlock
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16)
    }.MetaText();
    // Centred on the frame, which is centred on the picture; its size follows the picture that is shown (VideoPlayMark).
    private readonly VideoPlayMarkView _play = new() { Name = "ReaderMediaSidebarPlay", IsVisible = false };
    // MB1a demo of the shared media badges: the star of a favourite on the picture's corner, over exactly the shown picture. The sidebar
    // keeps its own play button (MediaPlayMarkMode.External); duration, queue and tag marks follow with the migration of this surface (MB1b).
    private readonly MediaBadgeView _badges = new() { Name = "ReaderMediaSidebarBadges", Options = MediaBadgeOptions.Sidebar };
    private bool _badgesOn, _playable;
    private readonly Border _edge = ReaderFocusedMedia.CreateFrameEdge();
    // R10: the row under the picture for an inline video's transport, error line and live region (empty unless a video plays here).
    private readonly Panel _videoRow = new() { Name = "ReaderMediaSidebarVideoRow" };
    // Polite live region: the page sets its text once the selection rested (AnnounceDelayMs).
    private readonly TextBlock _live = new() { Name = "ReaderMediaSidebarLive", Opacity = 0, IsHitTestVisible = false, Height = 0 };
    private readonly ArchiveFavoriteButton _favorite = new() { VerticalAlignment = VerticalAlignment.Center };
    // R11: the thumbnail strip of a post with several files: a single tab stop, ← → move, the counter beside it.
    private readonly ListBox _strip = new()
    {
        Name = "ReaderMediaSidebarStrip", SelectionMode = SelectionMode.Single, Background = Brushes.Transparent,
        Padding = new Thickness(0), BorderThickness = new Thickness(0),
        ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = ReaderMediaMultiRules.ThumbnailSpacing })
    };
    private readonly TextBlock _stripCounter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap }.MetaText();
    private readonly Grid _stripRow = new() { Name = "ReaderMediaSidebarStripRow", ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, IsVisible = false };
    private readonly List<StripThumbnail> _thumbnails = new();
    private readonly Dictionary<int, (Bitmap Bitmap, Action<Bitmap> Release)> _stripBitmaps = new();
    private int _stripStart = -1, _stripCount, _stripCurrent;
    private bool _stripSyncing;
    private Bitmap? _bitmap;
    private Action<Bitmap>? _release;
    private bool _hover;

    // The texts and the toggle command of the host: the reader's sidebar says "post", the Media page's panel says "medium" (same view, other words).
    /// <summary>The key of the region name and of the header line while nothing is known (the host sets it before it shows the view).</summary>
    internal string TitleKey { get; set; } = "reader.sidebar.title";
    /// <summary>The key of the text shown while nothing is selected.</summary>
    internal string EmptyKey { get; set; } = "reader.sidebar.empty";
    /// <summary>The key of the cross's name and tip.</summary>
    internal string CloseKey { get; set; } = "reader.sidebar.close";
    /// <summary>The command whose shortcut the cross's tip names; null for a host whose cross has no command.</summary>
    internal string? ToggleCommandId { get; set; } = "reader.sidebar.toggle";

    /// <summary>One thumbnail of the strip: the list item, its frame (the current one has the accent edge) and its picture.</summary>
    private sealed class StripThumbnail
    {
        internal required int Index;
        internal required ListBoxItem Item;
        internal required Border Frame;
        internal required Image Picture;
        internal required Border Placeholder;
    }

    // In the header row, icon only: the words are its tooltip and name.
    internal Button Open { get; } = new() { Name = "ReaderMediaSidebarOpen", Classes = { "icon-square", "ghost" } };
    internal Button Close { get; } = new() { Name = "ReaderMediaSidebarClose", Classes = { "icon-square", "ghost" } };

    /// <summary>The picture frame: its size is the decode target (smoke and page seam).</summary>
    internal Control Frame => _frame;
    /// <summary>The picture frame as a panel: an inline video (R10) puts its activator and player over the picture here.</summary>
    internal Panel FrameArea => _frame;
    /// <summary>The row under the picture for the inline video's transport (R10).</summary>
    internal Panel VideoRow => _videoRow;
    internal Image Picture => _image;
    internal Bitmap? Shown => _bitmap;
    internal string LiveText => _live.Text ?? string.Empty;
    internal string StatusText => _status.IsVisible ? _status.Text ?? string.Empty : string.Empty;
    internal bool IsDimmed => _image.Opacity < 1;

    /// <summary>Raised by a click on the picture (the page opens the gallery, as "Open").</summary>
    internal event EventHandler? PictureActivated;
    /// <summary>A double click on the picture: the gallery opens, whatever "Clicking a medium" is set to.</summary>
    internal event EventHandler? GalleryRequested;

    /// <summary>The strip is on screen (a post with several shown files).</summary>
    internal bool StripVisible => _stripRow.IsVisible;
    internal ListBox Strip => _strip;
    /// <summary>The counter beside the strip ("2 of 4"); empty while the strip is hidden.</summary>
    internal string StripCounterText => _stripRow.IsVisible ? _stripCounter.Text ?? string.Empty : string.Empty;
    /// <summary>The files (0-based, within the post) the strip shows now, in order.</summary>
    internal IReadOnlyList<int> StripIndexes => _thumbnails.ConvertAll(thumbnail => thumbnail.Index);
    /// <summary>The current file as the strip marks it (-1 without a strip).</summary>
    internal int StripCurrent => _stripRow.IsVisible ? _stripCurrent : -1;
    /// <summary>Whether the thumbnail of this file already holds a picture.</summary>
    internal bool HasStripPicture(int index) => _stripBitmaps.ContainsKey(index);

    /// <summary>A thumbnail was chosen (click, Enter, Space or an arrow key): the file's index within the post.</summary>
    internal event EventHandler<int>? StripSelected;
    /// <summary>A thumbnail was opened by a double click (interaction audit 1.1.0): the page shows it and opens it in the gallery.</summary>
    internal event EventHandler<int>? StripActivated;

    internal ReaderMediaSidebar()
    {
        Name = "ReaderMediaSidebar";
        Padding = new Thickness(16);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        _placeholder[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Card");
        _placeholder[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _image.SizeChanged += (_, e) => { _play.Diameter = VideoPlayMark.Diameter(e.NewSize); UpdateBadges(); };
        _edge.IsVisible = false;
        _frame.Children.Add(_placeholder);
        _frame.Children.Add(_image);
        _frame.Children.Add(_edge);
        _frame.Children.Add(_play);
        _frame.Children.Add(_badges);
        _frame.Children.Add(_status);
        _frame.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _bitmap == null && !_status.IsVisible) return;
            e.Handled = true;
            PictureActivated?.Invoke(this, EventArgs.Empty);
        };

        _frame.DoubleTapped += (_, e) =>
        {
            if (_bitmap == null && !_status.IsVisible) return;
            e.Handled = true;
            GalleryRequested?.Invoke(this, EventArgs.Empty);
        };

        // Icon only: the command's label is the name and the tooltip (with its shortcut); the catalog follows language and bindings.
        CommandBindings.Apply(Open, "gallery.open", iconOnly: true);
        Close.Content = CommandBindings.NamedIcon("x");
        Close.Opacity = 0; // shown on hover and keyboard focus, like the reader's navigation cross (edge rule: top right)
        // One compact row: the info line, the favourite star, "Open in gallery" and the cross. It replaces the title and the info box.
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        header.Children.Add(_title);
        Grid.SetColumn(_favorite, 1); header.Children.Add(_favorite);
        Grid.SetColumn(Open, 2); header.Children.Add(Open);
        Grid.SetColumn(Close, 3); header.Children.Add(Close);

        ScrollViewer.SetHorizontalScrollBarVisibility(_strip, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_strip, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        KeyboardNavigation.SetTabNavigation(_strip, KeyboardNavigationMode.Once);
        _strip.SelectionChanged += OnStripSelectionChanged;
        _strip.AddHandler(KeyDownEvent, OnStripKeyDown, RoutingStrategies.Tunnel);
        _strip.DoubleTapped += (_, e) =>
        {
            if (!_stripRow.IsVisible || !MeowChan.Desktop.Controls.ItemActivation.IsItemDoubleClick(e, _strip)
                || MeowChan.Desktop.Controls.ItemActivation.ContainerAt(e.Source, _strip) is not ListBoxItem { Tag: int index }) return;
            e.Handled = true;
            StripActivated?.Invoke(this, index);
        };
        _stripRow.Children.Add(_strip);
        Grid.SetColumn(_stripCounter, 1);
        // The strip's position text ("2 of 4") stays for readers (name and tooltip) without a visible counter.
        _stripCounter.IsVisible = false;
        _stripRow.Children.Add(_stripCounter);

        // Header, the medium, the strip of a post with several files, and under the medium the video's controls (empty for an image).
        // No row that is not needed takes space (an empty row has no height).
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 8 };
        layout.Children.Add(header);
        Grid.SetRow(_frame, 1); layout.Children.Add(_frame);
        Grid.SetRow(_stripRow, 2); layout.Children.Add(_stripRow);
        Grid.SetRow(_videoRow, 3); layout.Children.Add(_videoRow);
        Grid.SetRow(_live, 4); layout.Children.Add(_live);
        Child = layout;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
        AutomationProperties.SetLiveSetting(_live, AutomationLiveSetting.Polite);
        PointerEntered += (_, _) => { _hover = true; UpdateClose(); };
        PointerExited += (_, _) => { _hover = false; UpdateClose(); };
        GotFocus += (_, _) => UpdateClose();
        LostFocus += (_, _) => UpdateClose();
        AttachedToVisualTree += (_, _) => { Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        Localize();
        ShowEmpty();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Localize();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Localize);
    }

    internal void Localize()
    {
        string title = Texts.T(TitleKey);
        AutomationProperties.SetName(this, title);
        RefreshHeader();
        string stripName = Texts.T("reader.sidebar.strip");
        AutomationProperties.SetName(_strip, stripName);
        ToolTip.SetTip(_strip, stripName);
        string close = Texts.T(CloseKey);
        ToolTip.SetTip(Close, ToggleCommandId == null ? close : CommandBindings.Tooltip(ToggleCommandId, null, close));
        AutomationProperties.SetName(Close, close);
    }

    private void UpdateClose() => Close.Opacity = _hover || IsKeyboardFocusWithin ? 1 : 0;

    /// <summary>
    /// The header line and the controls of the medium in its post; shown at once, before the picture is ready.
    /// </summary>
    internal void Describe(string postLine, string fileLine, string detailLine, bool canOpen)
    {
        _postLine = postLine; _fileName = fileLine; _detail = detailLine;
        RefreshHeader();
        Open.IsEnabled = canOpen;
        Open.IsVisible = true;
        AutomationProperties.SetName(_image, fileLine);
    }

    /// <summary>The text of the header line now (smokes and tests read it); the plain title while nothing is known.</summary>
    internal string HeaderLine => _title.Text ?? string.Empty;

    private void RefreshHeader()
    {
        string text = ReaderMediaSidebarRules.HeaderText(_postLine, _fileName, _detail);
        _title.Text = text.Length > 0 ? text : Texts.T(TitleKey);
        ToolTip.SetTip(_title, _title.Text);
    }

    internal void ConfigureFavorite(ArchiveAttachmentRow? row)
    {
        _favorite.IsVisible = row != null;
        if (row != null) _favorite.Configure(row.FavoriteAvailable, row.IsFavorite, row.ToggleFavoriteAsync, row.FavoriteError);
        UpdateBadges();
    }

    /// <summary>The star for a medium that is no reader attachment (the Media page): the same button with the host's own state and toggle.</summary>
    internal void ConfigureFavorite(Func<bool>? available, Func<bool>? marked, Func<Task>? toggle, Func<string?>? error = null)
    {
        _favorite.IsVisible = available != null;
        if (available != null) _favorite.Configure(available, marked, toggle, error);
        UpdateBadges();
    }

    internal void RefreshFavorite() { _favorite.Refresh(); UpdateBadges(); }

    /// <summary>The badges of the shown picture (smokes read it).</summary>
    internal MediaBadgeView Badges => _badges;

    private void UpdateBadges()
    {
        if (!_badgesOn || !_image.IsVisible) { _badges.Clear(); return; }
        bool favorite = _favorite.IsVisible && _favorite.IsChecked == true;
        _badges.Apply(new MediaBadgeState(Favorite: favorite, Kind: _playable ? MediaBadgeKind.Video : MediaBadgeKind.Image), _image.Bounds.Size);
    }

    internal void SetDetail(string detailLine)
    {
        _detail = detailLine;
        RefreshHeader();
    }

    /// <summary>Shows a decoded picture; the previous one is given back. <paramref name="release"/> gives this one back later.</summary>
    internal void ShowPicture(Bitmap bitmap, Action<Bitmap> release, bool playable, double maximumWidth, double maximumHeight)
    {
        ReleasePicture();
        _bitmap = bitmap;
        _release = release;
        _image.Source = bitmap;
        _image.MaxWidth = maximumWidth;
        _image.MaxHeight = maximumHeight;
        _image.Opacity = 1;
        _image.IsVisible = true;
        _edge.IsVisible = true;
        _play.IsVisible = playable;
        _placeholder.IsVisible = false;
        _status.IsVisible = false;
        _badgesOn = true; _playable = playable;
        UpdateBadges();
    }

    /// <summary>The placeholder with a short text (loading, cannot be shown); the previous picture is given back.</summary>
    internal void ShowPlaceholder(string text)
    {
        ReleasePicture();
        _image.IsVisible = false;
        _edge.IsVisible = false;
        _play.IsVisible = false;
        _placeholder.IsVisible = true;
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
        _badgesOn = false;
        UpdateBadges();
    }

    /// <summary>
    /// A post without (shown) media: the last picture stays, half transparent, under the reason, so moving through text posts does not
    /// make the column flash. Without a previous picture the placeholder carries the reason.
    /// </summary>
    internal void Dim(string text)
    {
        _play.IsVisible = false;
        _edge.IsVisible = false;
        _badgesOn = false;
        UpdateBadges();
        if (_bitmap != null)
        {
            _image.Opacity = 0.5;
            _placeholder.IsVisible = false;
        }
        else _placeholder.IsVisible = true;
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    /// <summary>No post is selected (or the reader closed): nothing kept.</summary>
    internal void ShowEmpty()
    {
        ShowPlaceholder(Texts.T(EmptyKey));
        ClearStrip();
        Describe(string.Empty, string.Empty, string.Empty, false);
        ConfigureFavorite(null);
    }

    // ---- the thumbnail strip (R11) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the strip for a post with <paramref name="count"/> files, marking <paramref name="current"/>. Returns true when the thumbnails
    /// were laid out anew (another post, another window): the caller then loads the pictures of <see cref="StripIndexes"/> that
    /// <see cref="HasStripPicture"/> does not have. Stepping within the window only moves the mark. One file or none hides the strip.
    /// </summary>
    internal bool ShowStrip(int count, int current, int limit, Func<int, string> nameOf, bool samePost)
    {
        if (!ReaderMediaMultiRules.ShowsStrip(count)) { ClearStrip(); return false; }
        current = ReaderMediaSidebarRules.ClampIndex(current, count);
        (int start, int length) = ReaderMediaMultiRules.Window(current, count, limit, samePost && _stripCount == count ? _stripStart : -1);
        bool rebuilt = !_stripRow.IsVisible || !samePost || count != _stripCount || start != _stripStart || length != _thumbnails.Count;
        bool hadFocus = _strip.IsKeyboardFocusWithin;
        _stripSyncing = true;
        try
        {
            if (rebuilt) RebuildStrip(start, length, count, nameOf);
            _stripCount = count;
            _stripCurrent = current;
            _stripCounter.Text = ReaderMediaMultiRules.CounterText(current, count);
            _stripRow.IsVisible = true;
            MarkStripCurrent();
        }
        finally { _stripSyncing = false; }
        if (hadFocus && _strip.SelectedItem is Control selected) selected.Focus();
        return rebuilt;
    }

    private void RebuildStrip(int start, int length, int count, Func<int, string> nameOf)
    {
        // Pictures of files that stay in the window stay; the others are given back.
        HashSet<int> keep = new();
        for (int index = start; index < start + length; index++) keep.Add(index);
        foreach (int index in new List<int>(_stripBitmaps.Keys))
        {
            if (keep.Contains(index)) continue;
            (Bitmap bitmap, Action<Bitmap> release) = _stripBitmaps[index];
            _stripBitmaps.Remove(index);
            release(bitmap);
        }
        _strip.Items.Clear();
        _thumbnails.Clear();
        for (int index = start; index < start + length; index++)
        {
            Border placeholder = new() { IsHitTestVisible = false };
            placeholder[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Card");
            Image picture = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false, IsVisible = false };
            Grid content = new() { ClipToBounds = true };
            content.Children.Add(placeholder);
            content.Children.Add(picture);
            Border frame = new()
            {
                Name = "ReaderMediaSidebarThumbnail" + (index + 1), Width = ReaderMediaMultiRules.ThumbnailWidth,
                Height = ReaderMediaMultiRules.ThumbnailHeight, Child = content, ClipToBounds = true
            };
            frame[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
            string name = ReaderMediaMultiRules.ItemName(index, count, nameOf(index));
            ListBoxItem item = new() { Content = frame, Padding = new Thickness(2), MinWidth = 0, MinHeight = 0, Tag = index };
            AutomationProperties.SetName(item, name);
            ToolTip.SetTip(item, name);
            if (_stripBitmaps.TryGetValue(index, out (Bitmap Bitmap, Action<Bitmap> Release) held))
            {
                picture.Source = held.Bitmap;
                picture.IsVisible = true;
                placeholder.IsVisible = false;
            }
            _thumbnails.Add(new StripThumbnail { Index = index, Item = item, Frame = frame, Picture = picture, Placeholder = placeholder });
            _strip.Items.Add(item);
        }
        _stripStart = start;
    }

    // The current thumbnail has the accent edge and is the list's selection; the others have the quiet card edge.
    private void MarkStripCurrent()
    {
        foreach (StripThumbnail thumbnail in _thumbnails)
        {
            bool current = thumbnail.Index == _stripCurrent;
            thumbnail.Frame.BorderThickness = new Thickness(current ? 2 : 1);
            thumbnail.Frame[!Border.BorderBrushProperty] = new DynamicResourceExtension(current ? "Workspace.Accent" : "Workspace.CardEdge");
            thumbnail.Item.Opacity = current ? 1 : 0.8;
        }
        int at = _thumbnails.FindIndex(thumbnail => thumbnail.Index == _stripCurrent);
        _strip.SelectedIndex = at;
        if (at >= 0) _thumbnails[at].Item.BringIntoView();
    }

    /// <summary>The picture of one thumbnail; given back at once when that file is no longer in the strip.</summary>
    internal void SetStripPicture(int index, Bitmap bitmap, Action<Bitmap> release)
    {
        StripThumbnail? thumbnail = _thumbnails.Find(candidate => candidate.Index == index);
        if (thumbnail == null || !_stripRow.IsVisible || _stripBitmaps.ContainsKey(index)) { release(bitmap); return; }
        _stripBitmaps[index] = (bitmap, release);
        thumbnail.Picture.Source = bitmap;
        thumbnail.Picture.IsVisible = true;
        thumbnail.Placeholder.IsVisible = false;
    }

    /// <summary>Hides the strip and gives every thumbnail back.</summary>
    internal void ClearStrip()
    {
        foreach (StripThumbnail thumbnail in _thumbnails) thumbnail.Picture.Source = null;
        foreach ((Bitmap bitmap, Action<Bitmap> release) in _stripBitmaps.Values) release(bitmap);
        _stripBitmaps.Clear();
        _thumbnails.Clear();
        _stripSyncing = true;
        try { _strip.Items.Clear(); }
        finally { _stripSyncing = false; }
        _stripRow.IsVisible = false;
        _stripStart = -1;
        _stripCount = 0;
        _stripCurrent = 0;
        _stripCounter.Text = string.Empty;
    }

    private void OnStripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_stripSyncing || _strip.SelectedItem is not ListBoxItem { Tag: int index } || index == _stripCurrent) return;
        StripSelected?.Invoke(this, index);
    }

    // ← → move to the neighbouring file of the whole post (also beyond the window), Home and End to its ends; Enter and Space choose
    // the focused thumbnail. A single selection model, so the list's own arrow handling steps aside.
    private void OnStripKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || !_stripRow.IsVisible) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            e.Handled = true;
            int target = ReaderMediaMultiRules.KeyTarget(_stripCurrent, _stripCount, e.Key);
            if (target != _stripCurrent) StripSelected?.Invoke(this, target);
        }
        else if (e.Key is Key.Enter or Key.Space && e.Source is ListBoxItem { Tag: int index })
        {
            e.Handled = true;
            if (index != _stripCurrent) StripSelected?.Invoke(this, index);
        }
    }

    /// <summary>The polite announcement of the shown medium (and the region's name).</summary>
    internal void Announce(string text)
    {
        _live.Text = text;
        AutomationProperties.SetName(this, text.Length > 0 ? text : Texts.T(TitleKey));
    }

    /// <summary>Gives the shown bitmap back (decoded images to the gallery cache, posters to the thumbnail cache).</summary>
    internal void ReleasePicture()
    {
        Bitmap? bitmap = _bitmap;
        Action<Bitmap>? release = _release;
        _bitmap = null;
        _release = null;
        _image.Source = null;
        if (bitmap != null) release?.Invoke(bitmap);
    }
}
