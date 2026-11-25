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
    private ContextMenuHost? _sidebarViewMenu;
    private (BooruResultRevision Revision, BooruResultRowView Row, string Language, bool Saved, int MaximumPixels)? _sidebarContent;
    private (BooruResultTarget Target, string Language)? _sidebarTagContent;
    private (BooruInputMode Mode, string Input)? _sidebarTagDraft;
    private bool _mediaSidebarOpen;
    private double _mediaSidebarPageWidth;

    internal Action<bool>? SaveMediaSidebar { get; set; }

    internal void RestoreMediaSidebar(bool open)
    {
        if (_closed) return;
        _mediaSidebarOpen = open;
        RenderMediaSidebar(_model.Search.View);
    }

    private void InstallMediaSidebar(Grid layout)
    {
        Grid.SetColumn(_mediaSidebar, 1);
        Grid.SetRow(_mediaSidebar, 1);
        _mediaSidebar.Width = Config.MediaPanelWidthDefault;
        AutomationProperties.SetAutomationId(_mediaToggle, _mediaToggle.Name);
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
        _mediaSidebar.OpenGallery.Click += (_, _) =>
        {
            BooruSearchView view = _model.Search.View;
            if (!_mediaSidebar.IsEffectivelyVisible || _sidebarContent is not { } content
                || content.Revision != view.ResultRevision) return;
            ResultActions result = new(this, BooruResultTarget.From(view, content.Row));
            if (SidebarResultIsCurrent(result)) OpenGallery(result.PostId);
        };
    }

    private void OnMediaSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_mediaSidebar.IsEffectivelyVisible || ShortcutServiceCapturing()
            || !ShortcutService.Current.Matches(e, "media.sidebar.info", "booru")
            || CommandRouter.IsNativeInput(e.Source, e.Key) || !CommandRouter.IsRoutingContextActive(this, e.Source)) return;
        e.Handled = true;
        _mediaSidebar.ToggleFacts();
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
            IconButtons.Apply(_mediaToggle, "panel-right", Texts.T("media.layout.panel"));
            if (!widthAllows)
                ToolTip.SetTip(_mediaToggle, Texts.T("media.layout.panel.collapsed", SidebarPageMinimum));
        }

        _mediaSidebar.IsVisible = supported && _mediaSidebarOpen && widthAllows;
        if (!_mediaSidebar.IsVisible)
        {
            ReleaseMediaSidebar();
            return;
        }

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
            _sidebarContent = content;
            _mediaSidebar.Present(target, row, saved, maximumPixels);
            BuildSidebarActions(row, result);
        }
        if (_sidebarTagContent != (target, Texts.Language))
        {
            ReleaseSidebarTags();
            _sidebarTagContent = (target, Texts.Language);
            _mediaSidebar.Tags.Content = BuildSidebarTags(row, result);
        }
        RenderSidebarTagDraft(view);
        if (requestSample && cursor >= 0)
            _mediaSidebar.RequestSample(_model.Search.Samples, target, ReadGallerySampleSettings,
                () => SidebarResultIsCurrent(result), () =>
                {
                    if (SidebarResultIsCurrent(result) && result.CurrentRow() is { } current)
                        _mediaSidebar.Present(target, current, result.IsAvailable(ResultAction.ShowInMedia, current),
                            ReadGallerySampleSettings().MaximumPixels);
                });
    }

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
        if (primary != null) Add(primary, primary: true);
        foreach (ResultCommand command in commands.Where(command => command.Action is ResultAction.Open or ResultAction.Copy))
            Add(command, primary: false);

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
            _mediaSidebar.AddAction(button, command.Icon, command.Label);
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
        ReleaseSidebarTags();
        _mediaSidebar.Tags.Content = null;
        _mediaSidebar.ClearActions();
        _sidebarWorkViewButtons.Clear();
        _mediaSidebar.Release();
    }

    private void DisposeMediaSidebar()
    {
        ReleaseMediaSidebar();
        _sidebarViewMenu?.Dispose();
        _mediaSidebar.Dispose();
    }
}
