using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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
using MeowChan.Desktop.MediaActions;
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
    private readonly DispatcherTimer _expansionTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly MediaFactGrid _extraGrid = new("ReaderMediaSidebarExtraFacts");
    private readonly StackPanel _extraFacts = new() { Spacing = 4 };
    private readonly WrapPanel _allTags = new() { Name = "ReaderMediaSidebarAllTags", ItemSpacing = 4, LineSpacing = 4 };
    private readonly ScrollViewer _expandedRows = new() { Name = "ReaderMediaSidebarExpanded", IsVisible = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Border _factArea = new()
    {
        Name = "ReaderMediaSidebarFactArea", MinHeight = 60, Padding = new Thickness(0, 8),
        BorderThickness = new Thickness(0, 1, 0, 0),
        [!BorderBrushProperty] = new DynamicResourceExtension("Workspace.CardEdge")
    };
    private readonly Border _dockedExpansion = new();
    private readonly Border _hoverSurface = new() { [!BackgroundProperty] = new DynamicResourceExtension("Workspace.Card") };
    private readonly Popup _hoverPopup = new() { Placement = PlacementMode.TopEdgeAlignedLeft, IsLightDismissEnabled = false };
    private readonly MenuFlyout _settingsMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight };
    private bool _hovered;
    private int? _expandedTagsRevision;
    private bool Pinned => _infoPreferences.MediaSidebarPinned;
    private bool TagsRequested => _expandedTagsRevision == _mediumRevision;
    private bool FactsVisible => _infoPreferences.MediaSidebarInfo != "Off" && _infoPreferences.MediaSidebarFacts;
    private bool SizesDiffer => _savedBytes.HasValue && _sourceBytes.HasValue && _savedBytes != _sourceBytes;
    private readonly Grid _layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
    private readonly OverlayLayer _overlayLayer = new() { ZIndex = 2 };
    private readonly Grid _dockedHead = new();
    internal ToggleButton SettingsButton { get; } = new() { Name = "ReaderMediaSidebarSettings" };
    internal Action<Action<Config>>? SaveSidebarSetting { get; set; }
    private bool _barsHidden;

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

    internal ReaderMediaPreferences CopyBarSettingsTo(ReaderMediaPreferences preferences) => preferences with
    {
        MediaSidebarInfo = _infoPreferences.MediaSidebarInfo,
        MediaSidebarFacts = _infoPreferences.MediaSidebarFacts,
        MediaSidebarPinned = Pinned,
        MediaSidebarAutoHide = _infoPreferences.MediaSidebarAutoHide
    };

    internal string? RunBarCommand(string id, bool wasShown)
    {
        if (id == "media.sidebar.autohide")
        {
            ToggleAutoHide();
            return wasShown ? null : CommandCatalog.Label(id) + " · " +
                Texts.T(_infoPreferences.MediaSidebarAutoHide ? "workview.column.on" : "settings.reader.mediaSidebarInfo.off");
        }
        if (wasShown || !FactsVisible) ToggleDetails();
        return null;
    }

    internal void ToggleDetails()
    {
        bool facts = !FactsVisible;
        ConfigureInfo(_infoPreferences with
        {
            MediaSidebarInfo = _infoPreferences.MediaSidebarInfo == "Off" ? "Auto" : _infoPreferences.MediaSidebarInfo,
            MediaSidebarFacts = facts
        });
        SaveSidebarSetting?.Invoke(config => config.SetSidebarFacts(facts));
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

    private void InitializeSettingsMenu()
    {
        // MenuFlyout's presenter takes the backing collection before Opening.
        MenuItem autoHide = AddCheck("autoHide", "ReaderMediaSidebarSettingsAutoHide", ToggleAutoHide);
        MenuItem facts = AddCheck("facts", "ReaderMediaSidebarSettingsFacts", ToggleDetails);
        SettingsButton.Flyout = _settingsMenu;
        _settingsMenu.OverlayInputPassThroughElement = SettingsButton;
        _settingsMenu.Opening += (_, _) =>
        {
            RefreshCheck(autoHide, "media.sidebar.autohide", Texts.T("viewer.playback.autoHide"), _infoPreferences.MediaSidebarAutoHide);
            RefreshCheck(facts, "media.sidebar.info", CommandCatalog.Label("media.sidebar.info"), FactsVisible);
        };
        _settingsMenu.Opened += (_, _) => SettingsButton.IsChecked = true;
        _settingsMenu.Closed += (_, _) => SettingsButton.IsChecked = false;

        MenuItem AddCheck(string tag, string automationId, Action toggle)
        {
            MenuItem item = new() { Tag = tag, ToggleType = MenuItemToggleType.CheckBox, Command = new DelegateCommand(toggle) };
            AutomationProperties.SetAutomationId(item, automationId);
            _settingsMenu.Items.Add(item);
            return item;
        }

        void RefreshCheck(MenuItem item, string commandId, string label, bool isChecked)
        {
            string? keyContext = _decision.KeyContext;
            var bindings = ShortcutService.Current;
            item.Header = label;
            item.Icon ??= CommandBindings.Icon(commandId);
            item.IsChecked = isChecked;
            item.InputGesture = bindings.For(commandId)
                .FirstOrDefault(gesture => keyContext == null || ShortcutHelp.ActsHere(commandId, gesture, keyContext, bindings))?.Resolve(bindings.Platform);
            string tip = CommandBindings.Tooltip(commandId, visibleLabel: label, keyContext: keyContext);
            ToolTip.SetTip(item, tip);
            AutomationProperties.SetName(item, label);
            AutomationProperties.SetHelpText(item, tip);
        }
    }

    internal bool RouteInfoKey(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key) ||
            !CommandRouter.IsRoutingContextActive(this, e.Source)) return false;
        var bindings = ShortcutService.Current;
        if (bindings.Matches(e, "media.sidebar.autohide", _decision.KeyContext)) ToggleAutoHide();
        else if (bindings.Matches(e, "media.sidebar.info", _decision.KeyContext)) ToggleDetails();
        else return false;
        e.Handled = true;
        return true;
    }

    internal bool IsInfoMenuSource(object? source) =>
        source is Visual visual && (InBar(InfoToggle, visual) || InBar(SettingsButton, visual));

    internal bool IsOverlaySource(object? source) => source is Visual visual &&
        (InBar(_headBar, visual) || InBar(_details, visual) || InBar(_closeFace, visual));

    private static bool InBar(Visual bar, Visual source) => bar == source || bar.IsVisualAncestorOf(source);

    private static ThemeVariantScope DarkScope(Control content) => new() { RequestedThemeVariant = ThemeVariant.Dark, Child = content };

    private void InitializeInfo()
    {
        InfoToggle.Click += (_, _) => ToggleDetails();
        _headBar.Child = DarkScope(_header);
        _info.Margin = new Thickness(8, 0);
        _factArea.Child = _info;
        foreach (Control control in new Control[] { _expandedRows, _infoBar.Expand, _extraGrid, _allTags })
            AutomationProperties.SetAutomationId(control, control.Name);
        _extraFacts.Children.Insert(0, _extraGrid);
        _expandedRows.Content = new StackPanel { Margin = new Thickness(8), Spacing = 8, Children = { _allTags, _extraFacts } };
        _dockedExpansion.Child = _expandedRows;
        _details.Child = DarkScope(new StackPanel { Children = { _dockedExpansion, _infoBar, _factArea, _decisionError } });
        _hoverPopup.PlacementTarget = _details;
        _hoverPopup.Child = _hoverSurface;
        _layout.Children.Add(_hoverPopup);
        _infoBar.Expand.Click += (_, _) =>
        {
            if (TagsRequested && !Pinned)
            {
                _expandedTagsRevision = null;
                UpdateExpansion();
                return;
            }
            bool pinned = !Pinned;
            bool focusTags = pinned && _infoBar.Expand.IsFocused;
            _expandedTagsRevision = null;
            _hovered = false;
            _expansionTimer.Stop();
            ConfigureInfo(_infoPreferences with { MediaSidebarPinned = pinned });
            SaveSidebarSetting?.Invoke(config => config.MediaSidebarPinned = pinned);
            if (focusTags && _allTags.Children.FirstOrDefault() is Button chip)
            {
                TopLevel.GetTopLevel(this)?.UpdateLayout();
                if (chip.Focus(NavigationMethod.Tab)) chip.BringIntoView();
            }
        };
        foreach (Control surface in new[] { _details, _hoverSurface })
        {
            surface.PointerEntered += (_, _) => ScheduleExpansion();
            surface.PointerExited += (_, _) => ScheduleExpansion();
        }
        _expansionTimer.Tick += (_, _) =>
        {
            if (HasBarPopup() || _pressedPointers.Count != 0) return;
            _expansionTimer.Stop();
            _hovered = _details.IsPointerOver || _hoverSurface.IsPointerOver;
            UpdateExpansion();
        };
        DetachedFromVisualTree += (_, _) => CloseHover();
        _facts.SizeChanged += (_, _) => UpdateExpansion(refreshFacts: true);
        _details.SizeChanged += (_, _) => _hoverSurface.Width = _details.Bounds.Width;
        SizeChanged += (_, _) => UpdateExpansion();
        _closeFace.Child = DarkScope(Close);
        _frame.Children.Add(_overlayLayer);
        _layout.Children.Add(_dockedHead);
        InitializeSettingsMenu();
        InfoToggle.ContextRequested += (_, e) =>
        {
            new ContextMenuBuilder().Check("facts", CommandCatalog.Label("media.sidebar.info"), FactsVisible, ToggleDetails)
                .Build()!.Open(InfoToggle);
            e.Handled = true;
        };
        _frame.SizeChanged += (_, _) => PositionBars();
        PositionBars();
        AddHandler(PointerMovedEvent, (_, _) => OnPointerActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerEntered += (_, _) => OnPointerActivity();
        PointerExited += (_, _) => _idle = new(Stopwatch.GetTimestamp(), Leaving: true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            _pressedPointers.Add(e.Pointer);
            OnPointerActivity();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            _pressedPointers.Remove(e.Pointer);
            _idle = new(Stopwatch.GetTimestamp());
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, e) => _pressedPointers.Remove(e.Pointer),
            RoutingStrategies.Direct, handledEventsToo: true);
        GotFocus += (_, _) => RevealBars();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_barsHidden && e.Key == Key.Tab) RevealBars();
        }, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => RevealBars();
        DetachedFromVisualTree += (_, _) => _pressedPointers.Clear();
        PropertyChanged += (_, change) =>
        {
            if (change.Property != IsVisibleProperty) return;
            if (IsVisible) RevealBars();
            else
            {
                _hideOverlay.Stop();
                CloseHover();
                _pressedPointers.Clear();
            }
        };
        _hideOverlay.Tick += (_, _) => ObserveIdle(Stopwatch.GetTimestamp());
        _frame.Focusable = true;
    }

    private void ScheduleExpansion()
    {
        _expansionTimer.Stop();
        if (Pinned || TagsRequested || !_details.IsVisible || !_infoBar.Expand.IsVisible) return;
        _expansionTimer.Start();
    }

    private void ShowAllTags()
    {
        if (_tags is not { Count: > 0 }) return;
        _expandedTagsRevision = _mediumRevision;
        _expansionTimer.Stop();
        _hovered = false;
        RevealBars();
        _expandedRows.Offset = default;
        _frame.Focus();
    }

    internal MenuItem? ShowAllTagsMenuItem(MediaMenuSession session, Func<Func<Task>, Task> run)
    {
        if (_tags is not { Count: > 0 }) return null;
        const string key = "media.sidebar.showAllTags";
        int revision = _mediumRevision;
        MenuItem item = MediaMenuBuilder.ContextItem(session, key, () =>
        {
            if (revision == _mediumRevision) ShowAllTags();
            return Task.CompletedTask;
        }, () => revision == _mediumRevision, run, icon: "tag");
        AutomationProperties.SetAutomationId(item, key);
        return item;
    }

    private void PopulateExpandedTags()
    {
        _allTags.Children.Clear();
        foreach (string tag in _tags ?? Array.Empty<string>())
        {
            Button chip = MediaInfoBar.CreateTagChip("ReaderMediaSidebarExpandedTag", tag);
            InstallTagMenu(chip, tag);
            _allTags.Children.Add(chip);
        }
        _allTags.IsVisible = _allTags.Children.Count > 0;
    }

    private void CloseHover()
    {
        _expansionTimer.Stop();
        _hovered = false;
        _expandedTagsRevision = null;
        UpdateExpansion();
    }

    private void UpdateExpansion(bool refreshFacts = false)
    {
        bool content = _fileName.Length > 0 && (_tags?.Count > 0 || !FactsVisible || !_facts.ShowsFileAndSize ||
            SizesDiffer || _sourceHint != null || !string.IsNullOrEmpty(_provenance) || _historical || !string.IsNullOrEmpty(_hash));
        bool docked = Pinned || TagsRequested;
        bool open = content && _details.IsVisible && (docked || _hovered);
        bool hover = open && !docked;
        if (open && !_expandedRows.IsVisible) PopulateExpandedTags();
        if (open && (!_expandedRows.IsVisible || refreshFacts)) RefreshAdditionalFacts();
        _extraFacts.IsVisible = _extraGrid.Children.Count > 0 || _provenanceNote.IsVisible;
        _expandedRows.MaxHeight = Math.Max(0, Bounds.Height * _infoPreferences.MediaSidebarInfoMaximumShare / 100);
        _expandedRows.IsVisible = open;
        // A separate popup also layers above native video. Only pinning contributes to the docked height.
        if (!hover) _hoverPopup.IsOpen = false;
        Border parent = hover ? _hoverSurface : _dockedExpansion;
        if (!ReferenceEquals(parent.Child, _expandedRows))
        {
            if (_expandedRows.Parent is Border previous) previous.Child = null;
            parent.Child = _expandedRows;
        }
        if (hover) _hoverPopup.IsOpen = true;
        _infoBar.Expand.IsVisible = content;
        _infoBar.ShowExpansion(open, docked);
    }

    private bool HasBarPopup() => _cancelTagEditor != null || TopLevel.GetTopLevel(this)?.OpenedPopups.Any(p =>
        p != _hoverPopup && p.Child is not ToolTip && p.PlacementTarget is Control anchor &&
        (InBar(this, anchor) || InBar(_hoverSurface, anchor))) == true;

    // A native Windows video draws over sibling controls. Keep them reachable while that player owns the frame.
    private bool UseOverlayBars => _infoPreferences.MediaSidebarAutoHide && !(OperatingSystem.IsWindows() && _factPlayer != null);

    private void PositionBars()
    {
        bool overlay = UseOverlayBars;
        Panel head = overlay ? _overlayLayer : _dockedHead;
        bool dockFoot = !overlay || Pinned;
        Panel foot = dockFoot ? _layout : _overlayLayer;
        if (_headBar.Parent != head || _details.Parent != foot)
        {
            foreach (Control bar in new Control[] { _headBar, _details, _closeFace })
                (bar.Parent as Panel)?.Children.Remove(bar);
            head.Children.Add(_headBar);
            foot.Children.Add(_details);
            head.Children.Add(_closeFace);
            Grid.SetRow(_details, dockFoot ? 2 : 0);
            _headBar.Classes.Set("media-sidebar-overlay", overlay);
            _details.Classes.Set("media-sidebar-overlay", !dockFoot);
            _headBar[!BackgroundProperty] = new DynamicResourceExtension(overlay ? "Workspace.MediaScrim" : "Workspace.Card");
            _details[!BackgroundProperty] = new DynamicResourceExtension(dockFoot ? "Workspace.Card" : "Workspace.MediaScrim");
            ((ThemeVariantScope)_headBar.Child!).RequestedThemeVariant = overlay ? ThemeVariant.Dark : ThemeVariant.Default;
            ((ThemeVariantScope)_details.Child!).RequestedThemeVariant = dockFoot ? ThemeVariant.Default : ThemeVariant.Dark;
            ((ThemeVariantScope)_closeFace.Child!).RequestedThemeVariant = overlay ? ThemeVariant.Dark : ThemeVariant.Default;
            _card.Margin = overlay ? default : new Thickness(0, 8, 0, 0);
            _details.Margin = dockFoot ? new Thickness(0, 8, 0, 0) : default;
        }
        double inset = _frame.Bounds.Width < 260 ? 4 : 8;
        _headBar.Padding = new Thickness(inset, 4);
        _closeFace.Margin = new Thickness(0, 4, inset, 0);
    }

    private void OnPointerActivity()
    {
        if (!UseOverlayBars) return;
        if (_barsHidden || _fadeStarted.HasValue) RevealBars();
        else _idle = new(Stopwatch.GetTimestamp());
    }

    internal void RevealBars()
    {
        _idle = new(Stopwatch.GetTimestamp());
        _fadeStarted = null;
        _barsHidden = false;
        _headBar.Opacity = _details.Opacity = 1;
        UpdateInfoLayout();
        if (UseOverlayBars && IsEffectivelyVisible && this.IsAttachedToVisualTree()) _hideOverlay.Start();
        else _hideOverlay.Stop();
    }

    internal void ObserveIdle(long now)
    {
        if (!UseOverlayBars) return;
        bool popup = ((Pinned || TagsRequested) && _expandedRows.IsVisible) || _hoverPopup.IsOpen || HasBarPopup() || _details.IsKeyboardFocusWithin;
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
        ToolTip.SetTip(SettingsButton, Texts.T("media.sidebar.settings"));
        AutomationProperties.SetName(SettingsButton, Texts.T("media.sidebar.settings"));
        CommandBindings.Apply(InfoToggle, "media.sidebar.info", iconOnly: true);
        CommandBindings.Apply(Open, "gallery.open", iconOnly: true);
        ToolTip.SetTip(InfoToggle, CommandBindings.Tooltip("media.sidebar.info"));
        UpdateInfoLayout();
    }

    private void UpdateInfoLayout()
    {
        _headBar.IsVisible = !_barsHidden;
        _details.IsVisible = !_barsHidden && _fileName.Length > 0;
        bool factsChanged = _factArea.IsVisible != FactsVisible;
        _factArea.IsVisible = FactsVisible;
        InfoToggle.IsChecked = FactsVisible;
        _closeFace.Classes.Set("floating", _barsHidden);
        if (!_details.IsVisible)
        {
            _expansionTimer.Stop();
            _hovered = false;
        }
        UpdateExpansion(refreshFacts: factsChanged);
        UpdateBadges();
    }
}
