using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal readonly record struct SidebarInfoLayout(bool Docked, double CardHeight, bool TooShort);

internal static partial class ReaderMediaSidebarRules
{
    internal const double MinimumPictureHeight = 240;
    internal const double DockHysteresis = 24;
    internal const double OverlayHeight = 40;
    internal const double OverlayStandardWidth = 260;

    // Inner panel size excludes padding and the host's border. The picture has a one-DIP edge.
    internal static SidebarInfoLayout InfoLayout(ReaderMediaPreferences preferences, bool? detailsOverride, Size inner,
        PixelSize? source, double rows, bool wasDocked, double maximumImageHeight, double barHeight)
    {
        double card = Math.Max(0, inner.Height - 56);
        double picture = Math.Max(0, card - rows - 2);
        if (detailsOverride ?? preferences.MediaSidebarInfo == "Always")
        {
            bool fits = picture - MinimumPictureHeight >= Math.Max(68, barHeight + 8);
            return new(fits, double.NaN, !fits);
        }
        if (detailsOverride == false || preferences.MediaSidebarInfo != "Auto" || source is not { Width: > 0, Height: > 0 } px)
            return default;
        double height = Math.Min(picture, Math.Min(maximumImageHeight, Math.Max(0, inner.Width - 2) * px.Height / px.Width));
        double free = picture - height;
        bool dock = free >= Math.Max(barHeight + 8, preferences.MediaSidebarInfoMinimumFree - (wasDocked ? DockHysteresis : 0));
        return new(dock, height + rows + 2, false);
    }
}

internal sealed partial class ReaderMediaSidebar
{
    private readonly ArchiveFavoriteButton _headerFavorite = new() { Name = "ReaderMediaSidebarFavorite" };
    internal ToggleButton InfoToggle { get; } = new() { Name = "ReaderMediaSidebarInfoToggle" };
    private readonly Border _details = new()
    {
        Name = "ReaderMediaSidebarDetails", Margin = new Thickness(0, 8, 0, 0), IsVisible = false
    };
    internal Control Details => _details;
    private readonly Border _overlay = new()
    {
        Name = "ReaderMediaSidebarOverlay", Height = ReaderMediaSidebarRules.OverlayHeight, Padding = new Thickness(8, 4),
        VerticalAlignment = VerticalAlignment.Top, IsVisible = false, Classes = { "media-sidebar-overlay" },
        [!BackgroundProperty] = new DynamicResourceExtension("Workspace.MediaScrim")
    };
    private readonly GalleryDecisionRow _overlayRating = new("ReaderMediaSidebarOverlayRating", compact: true, favorite: false, sidebar: true);
    private readonly Button _tagCount = new()
    {
        Height = 24, MinHeight = 0, MinWidth = 48, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center,
        Classes = { WorkspaceChips.Chip, WorkspaceChips.Compact }
    };
    private readonly TextBlock _overlayCountLabel = new() { Classes = { "workspace-meta" } };
    private readonly Control _overlayTagIcon = CommandBindings.IconFromKey("tag", 16);
    private readonly Button _expand = new() { Width = WorkspaceMetrics.InputHeight, Height = WorkspaceMetrics.InputHeight };
    private readonly Grid _overlayActions;
    private readonly DispatcherTimer _hideOverlay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ReaderMediaPreferences _infoPreferences = ReaderMediaPreferences.Default;
    private bool? _detailsOverride;
    private bool _wasDocked, _engaged;
    private bool _narrowOverlay;
    internal Action<string>? SaveInfoMode { get; set; }

    internal void ConfigureInfo(ReaderMediaPreferences preferences)
    {
        if (_infoPreferences.MediaSidebarInfo == preferences.MediaSidebarInfo &&
            _infoPreferences.MediaSidebarInfoMinimumFree == preferences.MediaSidebarInfoMinimumFree) return;
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

    internal void ResetInfoOverride()
    {
        _detailsOverride = null;
        _wasDocked = false;
        UpdateInfoLayout();
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

    private bool IsOverlaySource(object? source) =>
        source is Visual visual && (ReferenceEquals(visual, _overlay) || _overlay.IsVisualAncestorOf(visual));

    private void InitializeInfo()
    {
        InfoToggle.Click += (_, _) => ToggleDetails();
        _expand.Click += (_, _) => ToggleDetails();
        _tagCount.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _overlayTagIcon, _overlayCountLabel } };
        _tagCount.Click += (_, _) => OpenTagEditor(_tagCount);
        _overlayRating.ClearButton.IsTabStop = false;
        ToolTip.SetShowOnDisabled(_tagCount, true);
        _overlay.Child = new ThemeVariantScope { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Child = _overlayActions };
        // Canvas keeps the transient controls out of the picture's desired size.
        Canvas layer = new() { Children = { _overlay }, ClipToBounds = true, ZIndex = 1 };
        _frame.Children.Add(layer);
        _frame.SizeChanged += (_, _) => PositionOverlay();
        _card.PointerEntered += (_, _) => UpdateEngagement();
        _card.PointerExited += (_, _) => UpdateEngagement();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsKeyboardFocusWithinProperty) UpdateEngagement();
            if (change.Property == IsVisibleProperty && !IsVisible)
            {
                _engaged = false;
                _hideOverlay.Stop();
                UpdateInfoLayout();
            }
        };
        _hideOverlay.Tick += (_, _) => { _hideOverlay.Stop(); _engaged = false; UpdateInfoLayout(); };
        SizeChanged += (_, _) => UpdateInfoLayout();
        _stripRow.SizeChanged += (_, _) => UpdateInfoLayout();
        _videoRow.SizeChanged += (_, _) => UpdateInfoLayout();

        _frame.Focusable = true;
    }

    private void LocalizeInfo()
    {
        CommandBindings.Apply(InfoToggle, "media.sidebar.info", iconOnly: true);
        CommandBindings.Apply(Open, "gallery.open", iconOnly: true);
        WorkspaceIcons.MakeSquare(_expand, WorkspaceIcons.ChevronUp, CommandCatalog.Label("media.sidebar.info"));
        RefreshOverlayTagCount();
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
        bool narrow = _overlay.Width < ReaderMediaSidebarRules.OverlayStandardWidth;
        // At 240 DIP the frame can be 220 DIP wide. Keep all seven rating/tag targets and the chevron in one row.
        if (narrow != _narrowOverlay)
        {
            _narrowOverlay = narrow;
            _overlay.Padding = new Thickness(narrow ? 4 : 8, 4);
            _overlayRating.ClearGap = narrow ? 0 : 8;
            _tagCount.MinWidth = narrow ? 28 : 48;
            _tagCount.Width = narrow ? 28 : double.NaN;
            _tagCount.Padding = new Thickness(narrow ? 0 : 8, 0);
            _overlayActions.ColumnDefinitions[3].Width = new GridLength(narrow ? 0 : 4);
            RefreshOverlayTagCount();
        }
        Canvas.SetTop(_overlay, Math.Max(0, _image.Bounds.Bottom - _overlay.Height));
    }

    private void RefreshOverlayTagCount()
    {
        int count = _tags?.Count ?? 0;
        _overlayTagIcon.IsVisible = !_narrowOverlay;
        _overlayCountLabel.Text = count.ToString(Texts.Culture);
        string label = Texts.T("tags.media.buttonCount", count);
        ToolTip.SetTip(_tagCount, _tagsBlocked ?? label);
        AutomationProperties.SetHelpText(_tagCount, _tagsBlocked ?? (count == 0 ? Texts.T("tags.quick.assigned.empty") : label));
        AutomationProperties.SetName(_tagCount, label);
    }

    // Re-evaluate the image budget in the same layout pass when chips, errors or the language change height.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height))
            UpdateInfoLayout(availableSize);
        return base.MeasureOverride(availableSize);
    }

    private void UpdateInfoLayout() => UpdateInfoLayout(Bounds.Size);

    private void UpdateInfoLayout(Size panelSize)
    {
        Size inner = panelSize.Deflate(Padding + BorderThickness);
        double rows = (_stripRow.IsVisible ? _stripRow.DesiredSize.Height : 0) + (_videoRow.IsVisible ? _videoRow.DesiredSize.Height : 0);
        PixelSize? source = _mediumKey == Load.ShownKey ? Load.ShownSource : null;
        SidebarInfoLayout layout = default;
        bool canDock = _detailsOverride ?? (_infoPreferences.MediaSidebarInfo == "Always" ||
            _infoPreferences.MediaSidebarInfo == "Auto" && source.HasValue);
        if (canDock && inner.Width > 0 && inner.Height > 0 && _fileName.Length > 0)
        {
            Size unconstrained = new(inner.Width, double.PositiveInfinity);
            _infoBar.Measure(unconstrained);
            _decisionError.Measure(unconstrained);
            layout = ReaderMediaSidebarRules.InfoLayout(_infoPreferences, _detailsOverride, inner, source, rows,
                _wasDocked, _image.MaxHeight, _infoBar.DesiredSize.Height + _decisionError.DesiredSize.Height);
        }
        _wasDocked = layout.Docked;
        bool showOverlay = !_wasDocked && _engaged && _badgesOn && _image.IsVisible && !_videoRow.IsVisible &&
            _infoPreferences.MediaSidebarInfo is "Auto" or "Overlay";
        if ((!_wasDocked && _details.IsKeyboardFocusWithin) || (!showOverlay && _overlay.IsKeyboardFocusWithin))
            InfoToggle.Focus(NavigationMethod.Tab);
        _details.IsVisible = _wasDocked;
        bool auto = _wasDocked && double.IsFinite(layout.CardHeight);
        if (Child is Grid grid)
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            // Let the card fill its pixel-aligned row so its bottom and the detail gap share one edge.
            grid.RowDefinitions[1].Height = auto
                ? new GridLength(Math.Ceiling((layout.CardHeight + _card.Margin.Top) * scale) / scale)
                : GridLength.Star;
            // Keep unused Auto-mode height below the bar so rounding preserves each measured row.
            grid.RowDefinitions[4].Height = auto ? GridLength.Star : GridLength.Auto;
        }
        InfoToggle.IsChecked = _wasDocked;
        string tip = CommandBindings.Tooltip("media.sidebar.info");
        ToolTip.SetTip(InfoToggle, layout.TooShort ? tip + " · " + Texts.T("settings.reader.mediaSidebarInfo.shortHeight") : tip);
        _overlay.IsVisible = showOverlay;
        PositionOverlay();
        UpdateBadges();
    }
}
