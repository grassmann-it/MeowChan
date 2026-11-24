using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal readonly record struct SidebarInfoLayout(bool Docked, double CardHeight, double DetailsLimit, bool TooShort);

internal static partial class ReaderMediaSidebarRules
{
    internal const double MinimumPictureHeight = 240;
    internal const double DockHysteresis = 24;
    internal const double OverlayHeight = 40;

    // Inner panel size excludes padding and the host's border. The picture has a one-DIP edge.
    internal static SidebarInfoLayout InfoLayout(ReaderMediaPreferences preferences, bool? detailsOverride, Size inner,
        PixelSize? source, double rows, bool wasDocked, double maximumImageHeight, double panelHeight)
    {
        double card = Math.Max(0, inner.Height - 56);
        double picture = Math.Max(0, card - rows - 2);
        if (detailsOverride ?? preferences.MediaSidebarInfo == "Always")
        {
            double limit = Math.Min(panelHeight * preferences.MediaSidebarInfoMaximumShare / 100, picture - MinimumPictureHeight - 8);
            return new(limit >= 96, double.NaN, Math.Max(0, limit), limit < 96);
        }
        if (detailsOverride == false || preferences.MediaSidebarInfo != "Auto" || source is not { Width: > 0, Height: > 0 } px)
            return default;
        double height = Math.Min(picture, Math.Min(maximumImageHeight, Math.Max(0, inner.Width - 2) * px.Height / px.Width));
        double free = picture - height;
        bool dock = free >= preferences.MediaSidebarInfoMinimumFree - (wasDocked ? DockHysteresis : 0);
        return new(dock, height + rows + 2, Math.Max(0, free - 8), false);
    }
}

internal sealed partial class ReaderMediaSidebar
{
    private readonly ArchiveFavoriteButton _headerFavorite = new() { Name = "ReaderMediaSidebarFavorite" };
    internal ToggleButton InfoToggle { get; } = new() { Name = "ReaderMediaSidebarInfoToggle" };
    private readonly Button _detailOpen = new() { Name = "ReaderMediaSidebarDetailOpen" };
    private readonly ScrollViewer _details = new()
    {
        Name = "ReaderMediaSidebarDetails", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), Focusable = false, IsVisible = false
    };
    private readonly Border _overlay = new()
    {
        Name = "ReaderMediaSidebarOverlay", Height = ReaderMediaSidebarRules.OverlayHeight, Padding = new Thickness(8, 4),
        VerticalAlignment = VerticalAlignment.Top, IsVisible = false, Classes = { "media-sidebar-overlay" },
        [!BackgroundProperty] = new DynamicResourceExtension("Workspace.MediaScrim")
    };
    private readonly GalleryDecisionRow _overlayRating = new("ReaderMediaSidebarOverlayRating", compact: true, favorite: false);
    private readonly Button _tagCount = new()
    {
        Height = 24, MinHeight = 0, MinWidth = 48, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center,
        Classes = { WorkspaceChips.Chip, WorkspaceChips.Compact }
    };
    private readonly Button _expand = new() { Width = WorkspaceMetrics.InputHeight, Height = WorkspaceMetrics.InputHeight };
    private readonly DispatcherTimer _hideOverlay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ReaderMediaPreferences _infoPreferences = ReaderMediaPreferences.Default;
    private bool? _detailsOverride;
    private bool _wasDocked, _engaged;
    private PixelSize? _sourcePixels;
    internal Action<string>? SaveInfoMode { get; set; }

    internal void ConfigureInfo(ReaderMediaPreferences preferences)
    {
        if (_infoPreferences.MediaSidebarInfo == preferences.MediaSidebarInfo &&
            _infoPreferences.MediaSidebarInfoMinimumFree == preferences.MediaSidebarInfoMinimumFree &&
            _infoPreferences.MediaSidebarInfoMaximumShare == preferences.MediaSidebarInfoMaximumShare) return;
        if (_infoPreferences.MediaSidebarInfo != preferences.MediaSidebarInfo) _detailsOverride = null;
        _infoPreferences = preferences;
        _wasDocked = false;
        UpdateInfoLayout();
    }

    internal void ToggleDetails()
    {
        _detailsOverride = !_details.IsVisible;
        UpdateInfoLayout();
        _live.Text = ReaderMediaSidebarRules.HeaderText(_postLine, CommandCatalog.Label("media.sidebar.info"),
            Texts.T(_details.IsVisible ? "workview.column.on" : "settings.reader.mediaSidebarInfo.off"));
    }

    internal bool RouteInfoKey(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key) ||
            !ShortcutService.Current.Matches(e, "media.sidebar.info", _decision.KeyContext) ||
            !CommandRouter.IsRoutingContextActive(this, e.Source)) return false;
        ToggleDetails();
        e.Handled = true;
        return true;
    }

    internal bool IsInfoMenuSource(object? source) =>
        source is Visual visual && (ReferenceEquals(visual, InfoToggle) || InfoToggle.IsVisualAncestorOf(visual));

    private void InitializeInfo()
    {
        _details.Content = _sections;
        _details.AddHandler(GotFocusEvent, (_, e) => { if (e.Source is Control control) control.BringIntoView(); });
        InfoToggle.Click += (_, _) => ToggleDetails();
        _expand.Click += (_, _) => ToggleDetails();
        _detailOpen.Click += (_, _) => GalleryRequested?.Invoke(this, EventArgs.Empty);
        _tagCount.Click += (_, _) => OpenActions.Start(EditTagsAsync, "media.tags.edit");
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,4,Auto") };
        content.Children.Add(_overlayRating);
        Grid.SetColumn(_tagCount, 2); content.Children.Add(_tagCount);
        Grid.SetColumn(_expand, 4); content.Children.Add(_expand);
        _overlay.Child = new ThemeVariantScope { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Child = content };
        // Canvas keeps the transient controls out of the picture's desired size.
        Canvas layer = new() { Children = { _overlay }, ClipToBounds = true };
        _frame.Children.Add(layer);
        _frame.SizeChanged += (_, _) => PositionOverlay();
        _card.PointerEntered += (_, _) => UpdateEngagement();
        _card.PointerExited += (_, _) => UpdateEngagement();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsKeyboardFocusWithinProperty) UpdateEngagement();
            if (change.Property == IsVisibleProperty && !IsVisible)
            {
                _detailsOverride = null;
                _wasDocked = _engaged = false;
                _hideOverlay.Stop();
                UpdateInfoLayout();
            }
        };
        _hideOverlay.Tick += (_, _) => { _hideOverlay.Stop(); _engaged = false; UpdateInfoLayout(); };
        SizeChanged += (_, _) => UpdateInfoLayout();
        _stripRow.SizeChanged += (_, _) => UpdateInfoLayout();
        _videoRow.SizeChanged += (_, _) => UpdateInfoLayout();
        _sections.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateInfoLayout(); };
        _frame.Focusable = true;
        _frame.KeyDown += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _frame) && e.Key == Key.Enter && Open.IsEnabled)
            {
                GalleryRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
    }

    private void LocalizeInfo()
    {
        CommandBindings.Apply(InfoToggle, "media.sidebar.info", iconOnly: true);
        CommandBindings.Apply(Open, "gallery.open", iconOnly: true);
        WorkspaceIcons.MakeSquare(_expand, WorkspaceIcons.ChevronUp, CommandCatalog.Label("media.sidebar.info"));
        ToolTip.SetTip(_tagCount, CommandCatalog.Label("media.tags.edit"));
        AutomationProperties.SetName(_tagCount, CommandCatalog.Label("media.tags.edit"));
        ContextMenu menu = new();
        foreach (string mode in new[] { "Auto", "Overlay", "Always", "Off" })
        {
            MenuItem item = new()
            {
                Header = Texts.T("settings.reader.mediaSidebarInfo." + mode.ToLowerInvariant()),
                ToggleType = MenuItemToggleType.Radio, GroupName = "SidebarInfoMode"
            };
            item.Click += (_, _) =>
            {
                ConfigureInfo(_infoPreferences with { MediaSidebarInfo = mode });
                SaveInfoMode?.Invoke(mode);
            };
            menu.Opening += (_, _) => item.IsChecked = mode == _infoPreferences.MediaSidebarInfo;
            menu.Items.Add(item);
        }
        InfoToggle.ContextMenu = menu;
        UpdateInfoLayout();
    }

    private void UpdateEngagement()
    {
        _hideOverlay.Stop();
        if (_card.IsPointerOver || IsKeyboardFocusWithin)
        {
            _engaged = true;
            UpdateInfoLayout();
        }
        else if (_engaged) _hideOverlay.Start();
    }

    private void PositionOverlay()
    {
        _overlay.Width = _frame.Bounds.Width;
        Canvas.SetTop(_overlay, Math.Max(0, _image.Bounds.Bottom - _overlay.Height));
    }

    private void UpdateInfoLayout()
    {
        Size inner = Bounds.Size.Deflate(Padding + BorderThickness);
        double rows = (_stripRow.IsVisible ? _stripRow.DesiredSize.Height : 0) + (_videoRow.IsVisible ? _videoRow.DesiredSize.Height : 0);
        var layout = ReaderMediaSidebarRules.InfoLayout(_infoPreferences, _detailsOverride, inner, _sourcePixels, rows,
            _wasDocked, _image.MaxHeight, Bounds.Height);
        _wasDocked = layout.Docked && _sections.IsVisible;
        _details.IsVisible = _wasDocked;
        _details.MaxHeight = _wasDocked ? layout.DetailsLimit : double.PositiveInfinity;
        bool auto = _wasDocked && double.IsFinite(layout.CardHeight);
        if (Child is Grid grid)
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            // Let the card fill its pixel-aligned row so its bottom and the detail gap share one edge.
            grid.RowDefinitions[1].Height = auto
                ? new GridLength(Math.Ceiling((layout.CardHeight + _card.Margin.Top) * scale) / scale)
                : GridLength.Star;
        }
        InfoToggle.IsChecked = _wasDocked;
        string tip = CommandBindings.Tooltip("media.sidebar.info");
        ToolTip.SetTip(InfoToggle, layout.TooShort ? tip + " · " + Texts.T("settings.reader.mediaSidebarInfo.shortHeight") : tip);
        _overlay.IsVisible = !_wasDocked && _engaged && _badgesOn && _image.IsVisible && !_videoRow.IsVisible &&
            _infoPreferences.MediaSidebarInfo is "Auto" or "Overlay";
        PositionOverlay();
        UpdateBadges();
    }
}
