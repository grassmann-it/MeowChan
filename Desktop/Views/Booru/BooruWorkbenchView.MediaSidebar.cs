using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private const double SidebarGap = 8;
    private const double SidebarHitsMinimum = 480;
    private const double SidebarPageMinimum = SidebarHitsMinimum + SidebarGap + Config.MediaPanelWidthDefault;

    private readonly BooruMediaSidebar _mediaSidebar = new();
    private readonly ToggleButton _mediaToggle = new() { Name = "BooruMediaToggle", Focusable = false, IsTabStop = false };
    private readonly List<Button> _sidebarWorkViewButtons = new();
    private readonly List<ContextMenuHost> _sidebarTagMenus = new();
    private readonly List<(Button Button, ChipOutline Outline, BooruTag Tag, string Category)> _sidebarTags = new();
    private readonly PressOpenedContextGate _sidebarContextPress = new();
    private ContextMenuHost? _sidebarViewMenu;
    private ContextMenuHost? _sidebarPictureMenu;
    private (BooruResultRevision Revision, BooruResultRowView Row, string Language, bool Saved, int MaximumPixels)? _sidebarContent;
    private (BooruResultTarget Target, string Language)? _sidebarTagContent;
    private (BooruInputMode Mode, string Input)? _sidebarTagDraft;
    private bool _mediaSidebarOpen;
    private double _mediaSidebarPageWidth;
    private int _savingSidebarFacts;
    private Grid? _mediaLayout;
    // Created when the sidebar is first shown: the GridSplitter type needs the platform cursor factory (R1, no window = no handle).
    private GridSplitter? _mediaHandle;
    // Without a host this is the width in use (the default, then the last dragged one); with one, ReadMediaSidebarWidth wins.
    private int _mediaSidebarWidth = Config.MediaPanelWidthDefault;
    // The column values last assigned (null = hidden); a search render with the same values leaves a dragged column alone.
    private (double Width, double Maximum)? _mediaSidebarColumns;

    internal Action<bool>? SaveMediaSidebar { get; set; }
    // The sidebar shares the media panel width with Media and Tagging (MediaLayout "pwidth", setting 240-900 DIP).
    internal Func<int>? ReadMediaSidebarWidth { get; set; }
    internal Action<int>? SaveMediaSidebarWidth { get; set; }
    internal QuickSettingsHost? MediaSidebarSettings { get; set; }

    internal void RestoreMediaSidebar(bool open)
    {
        if (_closed) return;
        _mediaSidebarOpen = open;
        RenderMediaSidebar(_model.Search.View);
    }

    private void InstallMediaSidebar(Grid layout)
    {
        _mediaLayout = layout;
        Grid.SetColumn(_mediaSidebar, 2);
        Grid.SetRow(_mediaSidebar, 1);
        AutomationProperties.SetAutomationId(_mediaToggle, _mediaToggle.Name);
        // The sidebar reports through this that the page is shown again: a width changed in Media, Tagging or the settings arrives here.
        _mediaSidebar.AllowsMedia = _model.Search.IsMediaAllowed;
        _mediaSidebar.FactsRefreshRequested = () =>
        {
            RefreshSidebarFacts();
            if (!_closed) ApplyMediaSidebarColumns(_mediaSidebar.IsVisible);
        };
        _mediaSidebar.FactsToggled = () =>
        {
            if (MediaSidebarSettings is not { } host) return;
            bool visible = _mediaSidebar.Info.IsChecked == true;
            _savingSidebarFacts++;
            OpenActions.Start(async () =>
            {
                try
                {
                    await host.SaveAsync(config => config.SetSidebarFacts(visible));
                    host.Applied?.Invoke([SettingsFieldId.MediaSidebarInfo]);
                }
                finally
                {
                    _savingSidebarFacts--;
                    RefreshSidebarFacts();
                }
            }, "booru.saveSidebarFacts");
        };
        layout.Children.Add(_mediaSidebar);
        layout.SizeChanged += (_, e) =>
        {
            if (_closed || !e.WidthChanged) return;
            _mediaSidebarPageWidth = e.NewSize.Width;
            RenderMediaSidebar(_model.Search.View);
        };
        _mediaSidebar.DetachedFromVisualTree += (_, _) => ReleaseMediaSidebar();
        _mediaSidebar.AttachedToVisualTree += (_, _) =>
        {
            if (!_closed) RenderMediaSidebar(_model.Search.View);
        };
        _mediaToggle.Click += (_, _) => ToggleMediaSidebar();
        _sidebarViewMenu = new ContextMenuHost(_viewBar, () => _mediaToggle.IsVisible
            ? new ContextMenuBuilder().Check("booru.sidebar.toggle", Texts.T("media.layout.panel"), _mediaSidebarOpen,
                ToggleMediaSidebar, icon: "panel-right")
            : null, resolveOrigin: () => _viewToggle);
        _mediaSidebar.Close.Click += (_, _) => SetMediaSidebarOpen(open: false, returnFocus: true);
        KeyDown += OnMediaSidebarKeyDown;
        _mediaSidebar.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || ShortcutServiceCapturing()) return;
            e.Handled = true;
            SetMediaSidebarOpen(open: false, returnFocus: true);
        };
        Button picture = _mediaSidebar.OpenGallery;
        picture.Click += (_, _) =>
        {
            if (CurrentSidebarResult() is { } result) OpenGallery(result.PostId);
        };
        picture.AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (e.TryGetPosition(picture, out Point _) && _sidebarContextPress.TryConsume()) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        picture.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _sidebarContextPress.BeginPress();
            if (e.Handled || !ContextClick.IsPress(e, picture)) return;
            _sidebarContextPress.MenuOpenedByPress(ContextClick.Button(e, picture));
            e.Handled = true;
            _sidebarPictureMenu?.Show(atPointer: true);
        }, RoutingStrategies.Tunnel);
        picture.AddHandler(PointerReleasedEvent, (_, e) => _sidebarContextPress.Released(e.InitialPressMouseButton),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _sidebarPictureMenu = new ContextMenuHost(picture, () =>
        {
            if (CurrentSidebarResult() is not { } result || ComposeResultMenu(result, () => SidebarResultIsCurrent(result)) is not { } menu) return null;
            FocusSidebarHit(result.PostId);
            return menu;
        }, fallbackFocus: () =>
        {
            if (CurrentSidebarResult() is { } result) FocusSidebarHit(result.PostId);
        });
    }

    private ResultActions? CurrentSidebarResult()
    {
        BooruSearchView view = _model.Search.View;
        if (_sidebarContent is not { } content || content.Revision != view.ResultRevision) return null;
        ResultActions result = new(this, BooruResultTarget.From(view, content.Row));
        return SidebarResultIsCurrent(result) ? result : null;
    }

    private void RefreshSidebarFacts()
    {
        if (!_closed && _savingSidebarFacts == 0 && (MediaSidebarSettings?.CurrentConfig() ?? _listProfile) is { } config)
            _mediaSidebar.ShowFacts(config.SidebarFactsVisible);
    }

    private void OnMediaSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_mediaToggle.IsEffectivelyVisible || ShortcutServiceCapturing()
            || CommandRouter.IsNativeInput(e.Source, e.Key) || !CommandRouter.IsRoutingContextActive(this, e.Source)) return;
        EffectiveShortcutBindings bindings = ShortcutService.Current;
        if (bindings.Matches(e, "media.panel.toggle", "booru"))
        {
            e.Handled = true;
            ToggleMediaSidebar();
        }
        else if (bindings.Matches(e, "media.sidebar.info", "booru"))
        {
            e.Handled = true;
            bool wasShown = _mediaSidebar.IsEffectivelyVisible;
            SetMediaSidebarOpen(open: true, returnFocus: false);
            if (wasShown || _mediaSidebar.Info.IsChecked != true) _mediaSidebar.ToggleFacts();
        }
    }

    private void ToggleMediaSidebar()
    {
        if (_mediaToggle.IsEffectivelyVisible) SetMediaSidebarOpen(!_mediaSidebarOpen, _mediaSidebar.IsKeyboardFocusWithin);
    }

    private void SetMediaSidebarOpen(bool open, bool returnFocus)
    {
        if (_closed || open == _mediaSidebarOpen) return;
        string? focusedPost = _sidebarContent?.Row.Post.PostId;
        _mediaSidebarOpen = open;
        RenderMediaSidebar(_model.Search.View);
        SaveMediaSidebar?.Invoke(open);
        if (returnFocus && focusedPost != null) FocusSidebarHit(focusedPost);
    }

    private void FocusSidebarHit(string postId)
    {
        SetCursor(postId);
        FocusHit(postId);
    }

    private void RenderMediaSidebar(BooruSearchView view, bool requestSample = false)
    {
        bool supported = !_closed && _resultView != BooruResultView.Gallery && IsSearchItem(_tabs.SelectedItem) && !view.Rows.IsEmpty;
        _mediaToggle.IsVisible = supported;
        _mediaToggle.IsChecked = _mediaSidebarOpen;
        bool widthAllows = _mediaSidebarPageWidth >= SidebarPageMinimum;
        if (supported)
        {
            string label = Texts.T("media.layout.panel");
            IconButtons.Apply(_mediaToggle, "panel-right", label);
            // The same "on" look as the sidebar switches of Media and the Reader (toggle surface, accent edge, white icon), not the solid accent.
            _mediaToggle.Classes.Set("toolbar-toggle", true);
            string? reason = widthAllows ? null : Texts.T("media.layout.panel.collapsed", SidebarPageMinimum);
            ToolTip.SetTip(_mediaToggle, CommandBindings.Tooltip("media.panel.toggle", reason, label, keyContext: "booru"));
        }

        bool visible = supported && _mediaSidebarOpen && widthAllows;
        // Gone while the handle or the sidebar has the focus (the window got narrower, the shortcut): the focus goes back to the hit.
        string? reclaimFocus = !visible && supported && (_mediaSidebar.IsKeyboardFocusWithin || _mediaHandle?.IsKeyboardFocusWithin == true)
            ? _sidebarContent?.Row.Post.PostId ?? _cursorPostId : null;
        _mediaSidebar.IsVisible = visible;
        ApplyMediaSidebarColumns(visible);
        if (!visible)
        {
            ReleaseMediaSidebar();
            if (reclaimFocus != null) FocusSidebarHit(reclaimFocus);
            return;
        }

        RefreshSidebarFacts();
        int cursor = view.Rows.IndexWhere(row => row.Post.PostId == _cursorPostId);
        int index = Math.Max(cursor, 0);
        BooruResultRowView row = view.Rows[index];
        BooruResultTarget target = BooruResultTarget.From(view, row);
        ResultActions result = new(this, target);
        _mediaSidebar.PresentPosition(index, view.Rows.Length, _selectedResults.Contains(result.PostId));
        int maximumPixels = ReadGallerySampleSettings().MaximumPixels;
        bool saved = result.IsAvailable(ResultAction.ShowInMedia, row);
        var content = (view.ResultRevision, row, Texts.Language, saved, maximumPixels);
        if (_sidebarContent != content)
        {
            if (_sidebarContent is { } previous
                && (previous.Revision != view.ResultRevision || !ReferenceEquals(previous.Row.Post, row.Post)))
                _sidebarPictureMenu?.Close();
            _sidebarContent = content;
            _mediaSidebar.Present(target, row, saved, maximumPixels, view.ProviderName);
            BuildSidebarActions(row, result);
        }
        if (_sidebarTagContent != (target, Texts.Language))
        {
            ReleaseSidebarTags();
            _sidebarTagContent = (target, Texts.Language);
            bool hasTags = row.Post.Post.Fields.HasFlag(BooruFieldPresence.Tags);
            _mediaSidebar.Tags.Content = hasTags ? BuildSidebarTags(row, result) : null;
            _mediaSidebar.Tags.IsVisible = hasTags;
        }
        RenderSidebarTagDraft(view);
        if (requestSample && cursor >= 0)
            _mediaSidebar.RequestSample(_model.Search.Samples, target, ReadGallerySampleSettings,
                () => SidebarResultIsCurrent(result), () =>
                {
                    if (SidebarResultIsCurrent(result) && result.CurrentRow() is { } current)
                        _mediaSidebar.Present(target, current, result.IsAvailable(ResultAction.ShowInMedia, current),
                            ReadGallerySampleSettings().MaximumPixels, _model.Search.View.ProviderName);
                });
    }

    private double MediaSidebarRoom => _mediaSidebarPageWidth - SidebarHitsMinimum - SidebarGap;

    private int SavedMediaSidebarWidth => Math.Clamp(ReadMediaSidebarWidth?.Invoke() ?? _mediaSidebarWidth,
        Config.MediaPanelWidthMinimum, Config.MediaPanelWidthMaximum);

    /// <summary>The saved width, given up first when the window gets narrower; the hits keep 480 DIP. Shrinking saves nothing.</summary>
    private double DisplayedMediaSidebarWidth => Math.Clamp(SavedMediaSidebarWidth, Config.MediaPanelWidthMinimum,
        Math.Max(Config.MediaPanelWidthMinimum, Math.Min(Config.MediaPanelWidthMaximum, MediaSidebarRoom)));

    private void ApplyMediaSidebarColumns(bool visible)
    {
        if (_mediaLayout is not { } layout) return;
        GridSplitter? handle = visible ? EnsureMediaHandle(layout) : _mediaHandle;
        if (handle != null)
        {
            string resize = Texts.T("media.panel.resize");
            AutomationProperties.SetName(handle, resize);
            ToolTip.SetTip(handle, resize);
            handle.IsVisible = visible;
        }

        (double Width, double Maximum)? columns = visible
            ? (DisplayedMediaSidebarWidth, Math.Max(Config.MediaPanelWidthMinimum, Math.Min(Config.MediaPanelWidthMaximum, MediaSidebarRoom)))
            : null;
        if (columns == _mediaSidebarColumns) return;
        _mediaSidebarColumns = columns;
        ColumnDefinitions definitions = layout.ColumnDefinitions;
        double margin = BooruMediaSidebar.HandleMargin;
        definitions[0].MinWidth = visible ? SidebarHitsMinimum : 0;
        definitions[1].Width = new GridLength(visible ? MediaPage.HandleWidth : 0);
        definitions[2].MinWidth = visible ? Config.MediaPanelWidthMinimum + margin : 0;
        definitions[2].MaxWidth = columns is { } shown ? shown.Maximum + margin : double.PositiveInfinity;
        definitions[2].Width = new GridLength(columns is { } target ? target.Width + margin : 0);
    }

    private GridSplitter EnsureMediaHandle(Grid layout)
    {
        if (_mediaHandle is { } existing) return existing;
        GridSplitter handle = _mediaHandle = new() { Name = "BooruMediaHandle", Width = MediaPage.HandleWidth, IsVisible = false };
        AutomationProperties.SetAutomationId(handle, handle.Name);
        Grid.SetColumn(handle, 1);
        Grid.SetRow(handle, 1);
        handle.AttachedToVisualTree += (_, _) =>
        {
            handle.ResizeDirection = GridResizeDirection.Columns;
            handle.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        };
        handle.DragCompleted += (_, _) => CommitMediaSidebarWidth();
        handle.KeyUp += (_, _) => CommitMediaSidebarWidth();
        // Tab order: hits, pager, handle, sidebar.
        layout.Children.Insert(layout.Children.IndexOf(_mediaSidebar), handle);
        return handle;
    }

    /// <summary>One save after dragging or arrow keys; compared with the shown width, so a key on the handle in a narrow window saves nothing.</summary>
    private void CommitMediaSidebarWidth()
    {
        if (_closed || _mediaLayout is not { } layout || !_mediaSidebar.IsVisible) return;
        ColumnDefinition column = layout.ColumnDefinitions[2];
        double actual = (column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth) - BooruMediaSidebar.HandleMargin;
        if (!double.IsFinite(actual) || actual <= 0 || Math.Abs(DisplayedMediaSidebarWidth - actual) < 1) return;
        StoreMediaSidebarWidth(Math.Clamp((int)Math.Round(actual), Config.MediaPanelWidthMinimum, Config.MediaPanelWidthMaximum));
    }

    private void StoreMediaSidebarWidth(int width)
    {
        _mediaSidebarWidth = width;
        SaveMediaSidebarWidth?.Invoke(width);
        ApplyMediaSidebarColumns(_mediaSidebar.IsVisible);
    }

    /// <summary>The same path as a save after dragging, for tests that need a given sidebar width.</summary>
    internal void ApplyMediaSidebarWidthForTests(int width) => StoreMediaSidebarWidth(width);

    private bool SidebarResultIsCurrent(ResultActions result) =>
        _mediaSidebar.IsEffectivelyVisible && _sidebarContent is { } content && content.Revision == result.Target.Revision
        && ReferenceEquals(content.Row.Post, result.Target.Post) && result.IsCurrent();

    private void BuildSidebarActions(BooruResultRowView row, ResultActions result)
    {
        _mediaSidebar.ClearActions();
        _sidebarWorkViewButtons.Clear();
        ResultCommand[] commands = result.Commands(row).ToArray();
        ResultCommand? primary = commands.FirstOrDefault(command => command.Action == ResultAction.ShowInMedia)
            ?? commands.FirstOrDefault(command => command.Action is ResultAction.Load or ResultAction.Cancel or ResultAction.Retry);
        // Tom, 25.11. 14:55: download first, then copy post link, then the small open-in-browser icon at the end.
        if (primary != null) Add(primary, primary: true);
        foreach (ResultAction action in new[] { ResultAction.Copy, ResultAction.Open })
            if (commands.FirstOrDefault(command => command.Action == action) is { } link) Add(link, primary: false);

        void Add(ResultCommand command, bool primary)
        {
            Button button = new()
            {
                Name = "BooruPanel" + command.Prefix + result.PostId,
                Focusable = false, IsTabStop = false, IsEnabled = result.IsAvailable(command.Action, row)
            };
            button.Classes.Add(primary ? "primary" : "ghost");
            AutomationProperties.SetAutomationId(button, button.Name);
            AutomationProperties.SetName(button, command.Name);
            AutomationProperties.SetHelpText(button, command.Reason);
            ToolTip.SetTip(button, command.Tip);
            ToolTip.SetShowOnDisabled(button, true);
            button.Click += (_, _) => { if (SidebarResultIsCurrent(result)) result.Run(command.Action); };
            if (command.WorkViewId is { } id) RegisterWorkViewAction(button, id, _sidebarWorkViewButtons);
            string? shortLabel = command.Action switch
            {
                ResultAction.Load => Texts.T("booru.ui.result.short.load"),
                ResultAction.Retry => Texts.T("booru.ui.result.short.again"),
                ResultAction.Copy => Texts.T("booru.ui.result.short.copy"),
                ResultAction.ShowInMedia => Texts.T("booru.ui.result.short.media"),
                _ => null
            };
            _mediaSidebar.AddAction(button, command.Icon, command.Label, shortLabel, compact: command.Action == ResultAction.Open);
        }
    }

    private Control BuildSidebarTags(BooruResultRowView row, ResultActions result)
    {
        StackPanel groups = new() { Spacing = 8 };
        groups.Children.Add(new TextBlock
        {
            Text = Texts.T("booru.tags.sourceLabel"), Classes = { "section-title" }, TextWrapping = TextWrapping.Wrap
        });
        if (row.Post.Tags.IsEmpty)
            groups.Children.Add(new TextBlock { Text = Texts.T("media.tags.none"), TextWrapping = TextWrapping.Wrap });
        foreach (BooruTagCategory category in ListTagOrder)
        {
            BooruTag[] tags = row.Post.Tags.Where(tag => tag.Category == category).ToArray();
            if (tags.Length == 0) continue;
            string label = Texts.T("booru.ui.tagCategory." + category);
            groups.Children.Add(new TextBlock { Text = label, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap });
            WrapPanel chips = new() { ItemSpacing = 4, LineSpacing = 4 };
            foreach (BooruTag tag in tags)
            {
                ChipOutline outline = WorkspaceChips.CreateSourceTag(tag.Name, category, compact: true);
                DraftTagButton chip = new()
                {
                    Name = "BooruPanelTag:" + tag.Name, Content = outline, Focusable = true, IsTabStop = true,
                    Classes = { WorkspaceChips.SourceButton }, MaxWidth = 200
                };
                AutomationProperties.SetAutomationId(chip, chip.Name);
                chip.Click += (_, _) =>
                {
                    if (!SidebarResultIsCurrent(result)) return;
                    EditTagDraft(tag.Name, chip.ExcludeOnClick ? BooruTagDraftAction.Exclude : BooruTagDraftAction.Include,
                        CaptureResultLink(result.Target));
                    if (chip.IsPointerClick) FocusSidebarHit(result.PostId);
                };
                _sidebarTagMenus.Add(new ContextMenuHost(chip,
                    () => SidebarResultIsCurrent(result) ? ComposeTagMenu(tag.Name, result.Target) : null,
                    () => { if (SidebarResultIsCurrent(result)) FocusSidebarHit(result.PostId); }));
                _sidebarTags.Add((chip, outline, tag, label));
                chips.Children.Add(chip);
            }
            groups.Children.Add(chips);
        }
        return groups;
    }

    private void RenderSidebarTagDraft(BooruSearchView view)
    {
        if (_sidebarTagDraft == (view.Mode, view.Input)) return;
        _sidebarTagDraft = (view.Mode, view.Input);
        var draft = view.Mode == BooruInputMode.Tags ? BooruInput.ReadTagDraft(view.Input, out _) : null;
        foreach (var (button, outline, tag, category) in _sidebarTags)
        {
            BooruTagDraftState state = BooruTagDraft.State(draft, tag.Name);
            outline.Classes.Set(DraftIncluded, state == BooruTagDraftState.Included);
            outline.Classes.Set(DraftExcluded, state is BooruTagDraftState.Excluded or BooruTagDraftState.Both);
            string tagAndCategory = Texts.T("media.breakdown.summary", category, tag.Name);
            string included = Texts.T("booru.ui.tags.alreadyInSearch", tagAndCategory);
            string excluded = Texts.T("media.filter.combo.excluded", tagAndCategory);
            string name = state switch
            {
                BooruTagDraftState.Included => included,
                BooruTagDraftState.Excluded => excluded,
                BooruTagDraftState.Both => included + " " + excluded,
                _ => Texts.T("booru.ui.tags.addFor", tag.Name, category)
            };
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }
    }

    private void ReleaseSidebarTags()
    {
        foreach (ContextMenuHost menu in _sidebarTagMenus) menu.Dispose();
        _sidebarTagMenus.Clear();
        _sidebarTags.Clear();
        _sidebarTagContent = null;
        _sidebarTagDraft = null;
    }

    private void ReleaseMediaSidebar()
    {
        if (_sidebarContent is null) return;
        _sidebarContent = null;
        _sidebarPictureMenu?.Close();
        ReleaseSidebarTags();
        _mediaSidebar.Tags.Content = null;
        _mediaSidebar.ClearActions();
        _sidebarWorkViewButtons.Clear();
        _mediaSidebar.Release();
    }

    private void DisposeMediaSidebar()
    {
        ReleaseMediaSidebar();
        _sidebarPictureMenu?.Dispose();
        _sidebarViewMenu?.Dispose();
        _mediaSidebar.Dispose();
    }
}
