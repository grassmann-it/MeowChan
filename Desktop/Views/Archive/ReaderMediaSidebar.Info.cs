using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;

namespace MeowChan.Desktop;

internal readonly record struct SidebarOverlayIdle(long LastActivity, bool Held = false, bool Leaving = false)
{
    internal SidebarOverlayIdle Observe(long now, int idleMs, bool overBar, bool pressed, bool popup, out bool hide)
    {
        long last = LastActivity;
        bool held = Held;
        var activity = new GalleryOverlayActivity(true, true, true, true, false, pressed, popup, false, Hovered: overBar);
        hide = activity.CanHideAfterHold(now, idleMs, ref last, ref held);
        bool leaving = Leaving && !held;
        if (leaving) hide = Stopwatch.GetElapsedTime(last, now) >= TimeSpan.FromMilliseconds(400);
        return new(last, held, leaving);
    }
}

internal sealed partial class ReaderMediaSidebar
{
    // Overlay children use the final frame for arrangement, without contributing to its decode size.
    private sealed class OverlayLayer : Grid
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            base.MeasureOverride(availableSize);
            return default;
        }
    }

    private readonly ArchiveFavoriteButton _headerFavorite = new() { Name = "ReaderMediaSidebarFavorite" };
    internal ToggleButton InfoToggle { get; } = new() { Name = "ReaderMediaSidebarInfoToggle" };
    private readonly Border _headBar = OverlayBar("ReaderMediaSidebarHeadOverlay", VerticalAlignment.Top, new CornerRadius(4, 4, 0, 0));
    private readonly Border _details = OverlayBar("ReaderMediaSidebarOverlay", VerticalAlignment.Bottom, new CornerRadius(0, 0, 4, 4));
    private readonly Border _closeFace = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Classes = { "media-sidebar-close" }, CornerRadius = new CornerRadius(4)
    };
    internal Control Details => _details;
    private readonly DispatcherTimer _hideOverlay = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly HashSet<IPointer> _pressedPointers = new();
    private ReaderMediaPreferences _infoPreferences = ReaderMediaPreferences.Default;
    private SidebarOverlayIdle _idle;
    private long? _fadeStarted;
    private readonly Grid _layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
    private readonly OverlayLayer _overlayLayer = new() { ZIndex = 2 };
    private readonly Grid _dockedHead = new();
    internal Button SettingsButton { get; } = new() { Name = "ReaderMediaSidebarSettings" };
    internal Action<Action<Config>>? SaveSidebarSetting { get; set; }
    private bool _barsHidden;
    internal Action<string>? SaveInfoMode { get; set; }

    private static Border OverlayBar(string name, VerticalAlignment alignment, CornerRadius corners) => new()
    {
        Name = name, VerticalAlignment = alignment, CornerRadius = corners, Classes = { "media-sidebar-overlay" },
        [!BackgroundProperty] = new DynamicResourceExtension("Workspace.MediaScrim")
    };

    internal void ConfigureInfo(ReaderMediaPreferences preferences)
    {
        _infoPreferences = preferences;
        PositionBars();
        RevealBars();
    }

    private bool InfoEnabled => _infoPreferences.MediaSidebarInfo != "Off";

    internal void ToggleDetails()
    {
        bool facts = !_infoPreferences.MediaSidebarFacts;
        ConfigureInfo(_infoPreferences with { MediaSidebarFacts = facts });
        SaveSidebarSetting?.Invoke(config => config.MediaSidebarFacts = facts);
        _live.Text = ReaderMediaSidebarRules.HeaderText(_postLine, CommandCatalog.Label("media.sidebar.info"),
            Texts.T(facts ? "workview.column.on" : "settings.reader.mediaSidebarInfo.off"));
    }

    internal void ToggleAutoHide()
    {
        bool hide = !_infoPreferences.MediaSidebarAutoHide;
        ConfigureInfo(_infoPreferences with { MediaSidebarAutoHide = hide });
        SaveSidebarSetting?.Invoke(config => config.MediaSidebarAutoHide = hide);
    }

    internal void ResetInfoOverride() => UpdateInfoLayout();

    private void ToggleInfoBar()
    {
        string mode = InfoEnabled ? "Off" : "Always";
        ConfigureInfo(_infoPreferences with { MediaSidebarInfo = mode });
        SaveInfoMode?.Invoke(mode);
    }

    private ContextMenu SettingsMenu()
    {
        var menu = new ContextMenuBuilder()
            .Check("autoHide", Texts.T("viewer.playback.autoHide"), _infoPreferences.MediaSidebarAutoHide, ToggleAutoHide)
            .Check("facts", CommandCatalog.Label("media.sidebar.info"), _infoPreferences.MediaSidebarFacts, ToggleDetails)
            .Check("bar", Texts.T("settings.reader.mediaSidebarInfo"), InfoEnabled, ToggleInfoBar);
        return menu.Build()!;
    }

    private void RestoreDetailsAfterTagEditor()
    {
        _infoBar.PinEditorAnchor(false);
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
        source is Visual visual && (InBar(InfoToggle, visual) || InBar(SettingsButton, visual));

    private bool IsOverlaySource(object? source) => source is Visual visual &&
        (InBar(_headBar, visual) || InBar(_details, visual) || InBar(_closeFace, visual));

    private static bool InBar(Visual bar, Visual source) => bar == source || bar.IsVisualAncestorOf(source);

    private static ThemeVariantScope DarkScope(Control content) => new() { RequestedThemeVariant = ThemeVariant.Dark, Child = content };

    private void InitializeInfo()
    {
        InfoToggle.Click += (_, _) => ToggleDetails();
        _headBar.Child = DarkScope(_header);
        _details.Child = DarkScope(new StackPanel { Children = { _infoBar, _decisionError } });
        _closeFace.Child = DarkScope(Close);
        _frame.Children.Add(_overlayLayer);
        _layout.Children.Add(_dockedHead);
        SettingsButton.Click += (_, _) => SettingsMenu().Open(SettingsButton);
        InfoToggle.ContextRequested += (_, e) => { SettingsMenu().Open(InfoToggle); e.Handled = true; };
        _frame.SizeChanged += (_, _) => PositionBars();
        PositionBars();
        AddHandler(PointerMovedEvent, (_, _) => RevealBars(), RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerEntered += (_, _) => RevealBars();
        PointerExited += (_, _) => _idle = new(Stopwatch.GetTimestamp(), Leaving: true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            _pressedPointers.Add(e.Pointer);
            RevealBars();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            _pressedPointers.Remove(e.Pointer);
            _idle = new(Stopwatch.GetTimestamp());
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, e) => _pressedPointers.Remove(e.Pointer),
            RoutingStrategies.Direct, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => RevealBars();
        DetachedFromVisualTree += (_, _) => _pressedPointers.Clear();
        PropertyChanged += (_, change) =>
        {
            if (change.Property != IsVisibleProperty) return;
            if (IsVisible) RevealBars();
            else
            {
                _hideOverlay.Stop();
                _pressedPointers.Clear();
            }
        };
        _hideOverlay.Tick += (_, _) => ObserveIdle(Stopwatch.GetTimestamp());
        _frame.Focusable = true;
    }

    private void PositionBars()
    {
        bool overlay = _infoPreferences.MediaSidebarAutoHide;
        Panel head = overlay ? _overlayLayer : _dockedHead;
        Panel foot = overlay ? _overlayLayer : _layout;
        if (_headBar.Parent != head)
        {
            foreach (Control bar in new Control[] { _headBar, _details, _closeFace })
                (bar.Parent as Panel)?.Children.Remove(bar);
            head.Children.Add(_headBar);
            foot.Children.Add(_details);
            head.Children.Add(_closeFace);
            Grid.SetRow(_details, overlay ? 0 : 2);
            _headBar[!BackgroundProperty] = new DynamicResourceExtension(overlay ? "Workspace.MediaScrim" : "Workspace.Card");
            _details[!BackgroundProperty] = new DynamicResourceExtension(overlay ? "Workspace.MediaScrim" : "Workspace.Card");
            ((ThemeVariantScope)_headBar.Child!).RequestedThemeVariant = overlay ? ThemeVariant.Dark : ThemeVariant.Default;
            ((ThemeVariantScope)_details.Child!).RequestedThemeVariant = overlay ? ThemeVariant.Dark : ThemeVariant.Default;
            ((ThemeVariantScope)_closeFace.Child!).RequestedThemeVariant = overlay ? ThemeVariant.Dark : ThemeVariant.Default;
            _card.Margin = overlay ? default : new Thickness(0, 8, 0, 0);
            _details.Margin = overlay ? default : new Thickness(0, 8, 0, 0);
        }
        double inset = _frame.Bounds.Width < 260 ? 4 : 8;
        _headBar.Padding = new Thickness(inset, 4);
        _closeFace.Margin = new Thickness(0, 4, inset, 0);
    }

    internal void RevealBars()
    {
        _idle = new(Stopwatch.GetTimestamp());
        _fadeStarted = null;
        _barsHidden = false;
        _headBar.Opacity = _details.Opacity = 1;
        UpdateInfoLayout();
        if (_infoPreferences.MediaSidebarAutoHide && IsEffectivelyVisible && this.IsAttachedToVisualTree()) _hideOverlay.Start();
        else _hideOverlay.Stop();
    }

    internal void ObserveIdle(long now)
    {
        if (!_infoPreferences.MediaSidebarAutoHide) return;
        bool popup = _cancelTagEditor != null || TopLevel.GetTopLevel(this)?.OpenedPopups.Any(p =>
            p.Child is not ToolTip && p.PlacementTarget is Control anchor && InBar(this, anchor)) == true;
        _idle = _idle.Observe(now, _infoPreferences.ControlsIdleHideMs, _headBar.IsPointerOver || _details.IsPointerOver || _closeFace.IsPointerOver,
            _pressedPointers.Count != 0, popup, out bool hide);
        if (!hide || _decisionError.IsVisible || _status.IsVisible)
        {
            if (_fadeStarted.HasValue) RevealBars();
            return;
        }
        _headBar.Opacity = _details.Opacity = 0;
        _fadeStarted ??= now;
        if (!MotionPolicy.IsOff && Stopwatch.GetElapsedTime(_fadeStarted.Value, now) < TimeSpan.FromMilliseconds(120)) return;
        _barsHidden = true;
        _hideOverlay.Stop();
        UpdateInfoLayout();
    }

    private void LocalizeInfo()
    {
        _header.Localize();
        SettingsButton.Content = CommandBindings.IconFromKey("settings", 16);
        ToolTip.SetTip(SettingsButton, Texts.T("settings.title"));
        AutomationProperties.SetName(SettingsButton, Texts.T("settings.title"));
        CommandBindings.Apply(InfoToggle, "media.sidebar.info", iconOnly: true);
        CommandBindings.Apply(Open, "gallery.open", iconOnly: true);
        ToolTip.SetTip(InfoToggle, CommandBindings.Tooltip("media.sidebar.info"));
        UpdateInfoLayout();
    }

    private void UpdateInfoLayout()
    {
        _headBar.IsVisible = !_barsHidden;
        _details.IsVisible = !_barsHidden && _fileName.Length > 0 && (InfoEnabled || _infoBar.EditorAnchorPinned || _decisionError.IsVisible);
        InfoToggle.IsChecked = _infoPreferences.MediaSidebarFacts;
        _closeFace.Classes.Set("floating", _barsHidden);
        UpdateBadges();
    }
}
