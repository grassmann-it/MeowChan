using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

public sealed partial class TaggingPage
{
    private const int StampPreviewCount = 2;
    private readonly Button _stampButton = new TaggingActivationButton() { Name = "TaggingStamp", Classes = { "bar-button" } };
    private readonly TextBlock _stampSummary = new() { Name = "TaggingStampSummary", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _stampPreview = new() { Name = "TaggingStampPreview",
        Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout _stampFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, OverlayDismissEventPassThrough = true };
    private readonly TrimTipTextBlock _stampHeading = new() { Name = "TaggingStampHeading", VerticalAlignment = VerticalAlignment.Center, Classes = { "card-title" } };
    private readonly HelpButton _stampHelp = new() { Name = "TaggingStampHelp", HelpKey = "help.taggingStamp" };
    private readonly Button _stampClose = new() { Name = "TaggingStampClose", Classes = { "subtle" } };
    private readonly WrapPanel _stampChips = new() { Name = "TaggingStampChips",
        [!WrapPanel.ItemSpacingProperty] = new DynamicResourceExtension("Workspace.Space.8"),
        [!WrapPanel.LineSpacingProperty] = new DynamicResourceExtension("Workspace.Space.8") };
    private readonly TextBox _stampInput = new() { Name = "TaggingStampInput", MinWidth = 160 };
    private readonly Button _stampAdd = new() { Name = "TaggingStampAdd", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stampFeedback = new() { Name = "TaggingStampFeedback", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private int _stampCapacity = QuickTagOptions.DefaultStampSize;
    private int _stampVisit;
    private bool _compactStamp;
    private (string? GroupId, bool Ungrouped) _stampScope;
    private QuickTagStamp? _sharedStamp;

    /// <summary>The configured stamp limit. A lower value never cuts a saved stamp here.</summary>
    internal int StampCapacity
    {
        get => _stampCapacity;
        set => _stampCapacity = Math.Clamp(value, QuickTagOptions.MinimumStampSize, QuickTagOptions.MaximumStampSize);
    }

    internal QuickTagStamp? Stamp
    {
        get => _sharedStamp;
        set
        {
            if (_sharedStamp != null) _sharedStamp.Changed -= OnSharedStampChanged;
            _sharedStamp = value;
            _queueWork.SharedStamp = value;
            if (value != null) { value.Changed += OnSharedStampChanged; _stampCapacity = value.Capacity; }
            RenderStamp();
        }
    }

    private void OnSharedStampChanged()
    {
        if (_queueWork.Ended) return;
        RenderStamp();
        UpdateSelectionBar();
    }

    internal WrapPanel StampChips => _stampChips;
    internal TextBox StampInput => _stampInput;
    internal Button StampButton => _stampButton;
    internal bool StampOpen => _stampFlyout.IsOpen;

    private Button BuildStamp()
    {
        WorkViewElement.SetId(_stampButton, "tagging.stamp");
        StackPanel trigger = new() { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _stampSummary, _stampPreview, WorkspaceIcons.Create(WorkspaceIcons.ChevronDown) } };
        AutomationProperties.SetAccessibilityView(trigger, AccessibilityView.Raw);
        _stampButton.Content = trigger;
        _stampButton.Flyout = _stampFlyout;

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8,
            Children = { _stampHeading, _stampHelp, _stampClose } };
        Grid.SetColumn(_stampHelp, 1);
        Grid.SetColumn(_stampClose, 2);
        Grid entry = new() { Name = "TaggingStampEditor", ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8,
            Children = { _stampInput, _stampAdd } };
        Grid.SetColumn(_stampAdd, 1);
        ScrollViewer chips = new() { Content = _stampChips, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        chips.Bind(IsVisibleProperty, _stampChips.GetObservable(IsVisibleProperty));
        StackPanel body = new() { Name = "TaggingStampPopover", MinWidth = 320, MaxWidth = 432, Spacing = 8,
            Children = { header, chips, entry, _stampFeedback } };
        AutomationProperties.SetHeadingLevel(_stampHeading, 2);
        AutomationProperties.SetLiveSetting(_stampFeedback, AutomationLiveSetting.Polite);
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        body.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
                e.Handled = DismissStampByEscape();
        };
        _stampFlyout.Content = body;
        return _stampButton;
    }

    private bool StampInputIsComposing() => _stampInput.IsKeyboardFocusWithin &&
        _stampInput.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));

    private void InitializeStamp()
    {
        _stampFlyout.Opened += (_, _) =>
        {
            int visit = _stampVisit;
            Dispatcher.UIThread.Post(() =>
            {
                if (StampOpen && visit == _stampVisit && !_stampInput.Focus(NavigationMethod.Tab))
                    _stampClose.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Input);
        };
        _stampFlyout.Closed += (_, _) =>
        {
            _stampVisit++;
            _stampHelp.Popover.Hide();
            _stampInput.Text = string.Empty;
            ShowStampFeedback(null);
        };
        _stampClose.Click += (_, _) => CloseStamp(restoreFocus: true);
        _stampAdd.Click += async (_, _) => await AddStampAsync(_stampInput.Text);
        TagSuggestionInput.AttachPaste(_stampInput);
        _stampInput.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || StampInputIsComposing()) return;
            e.Handled = true;
            await AddStampAsync(_stampInput.Text);
        };
    }

    internal bool DismissStampByEscape()
    {
        if (!StampOpen || StampInputIsComposing()) return false;
        if (_stampHelp.Popover.IsOpen) _stampHelp.Popover.Hide();
        else CloseStamp(restoreFocus: true);
        return true;
    }

    internal void CloseStamp(bool restoreFocus = false)
    {
        if (!StampOpen) return;
        _stampHelp.Popover.Hide();
        _stampFlyout.Hide();
        if (restoreFocus && _stampButton.IsEffectivelyVisible) _stampButton.Focus(NavigationMethod.Tab);
    }

    private void LocalizeStamp()
    {
        _stampHelp.Refresh();
        WorkspaceIcons.MakeSquare(_stampClose, WorkspaceIcons.CloseSmall, Texts.T("action.close"));
        _stampInput.PlaceholderText = Texts.T("tags.input.several");
        AutomationProperties.SetName(_stampInput, Texts.T("tagging.page.stamp.input"));
        _stampAdd.Content = Texts.T("tagging.page.stamp.add");
        ShowStampFeedback(null);
    }

    private void SetStampCompact(bool compact)
    {
        _compactStamp = compact;
        UpdateStampSummary(_queueWork.StampNames.Count);
    }

    private void UpdateStampSummary(int count)
    {
        _stampPreview.IsVisible = !_compactStamp && count > 0;
        _stampSummary.Text = _stampPreview.IsVisible ? Texts.T("help.taggingStamp.title") : Texts.T("tagging.page.stamp.summary", count);
    }

    private void RenderStamp()
    {
        var scope = (_selectedGroupId, _ungroupedSelected);
        if (_stampScope != scope)
        {
            CloseStamp();
            _stampScope = scope;
        }

        IReadOnlyList<string> names = _queueWork.StampNames;
        UpdateStampSummary(names.Count);
        _stampHeading.Text = _queueWork.ActiveGroup is { } group
            ? Texts.T("tagging.page.stamp.editor.group", group.Name) : Texts.T("tagging.page.stamp.window");
        string description = Texts.T("tagging.page.stamp.describe", _stampHeading.Text, names.Count);
        AutomationProperties.SetName(_stampButton, description);
        string tip = names.Count == 0 ? description : description + "\n" + string.Join(Texts.Culture.TextInfo.ListSeparator + " ", names);
        ToolTip.SetTip(_stampButton, tip);
        AutomationProperties.SetHelpText(_stampButton, tip);

        _stampPreview.Children.Clear();
        foreach (string name in names.Take(StampPreviewCount))
        {
            Border chip = WorkspaceChips.MarkReadOnly(WorkspaceChips.CreateBadge("TaggingStampPreviewChip", name, compact: true));
            chip.MaxWidth = 88;
            if (chip.Child is TextBlock label) label.TextTrimming = TextTrimming.CharacterEllipsis;
            _stampPreview.Children.Add(chip);
        }
        if (names.Count > StampPreviewCount)
            _stampPreview.Children.Add(new TextBlock { Text = "+" + (names.Count - StampPreviewCount).ToString(Texts.Culture), VerticalAlignment = VerticalAlignment.Center });

        _stampChips.Children.Clear();
        foreach (string name in names) _stampChips.Children.Add(StampChip(name));
        _stampChips.IsVisible = names.Count > 0;
        string? emptyHint = names.Count == 0 ? Texts.T("tagging.page.stamp.empty") : null;
        ToolTip.SetTip(_stampInput, emptyHint);
        if (!_stampFeedback.IsVisible) AutomationProperties.SetHelpText(_stampInput, emptyHint);
        _stampAdd.IsEnabled = CanChange;
        _stampInput.IsReadOnly = !CanChange;
    }

    private StackPanel StampChip(string name)
    {
        Border chip = WorkspaceChips.MarkReadOnly(WorkspaceChips.CreateBadge("TaggingStampChip", name));
        chip.VerticalAlignment = VerticalAlignment.Center;
        Button remove = new() { IsEnabled = CanChange, Classes = { "subtle" } };
        WorkspaceIcons.MakeSquare(remove, WorkspaceIcons.CloseSmall, Texts.T("tagging.page.stamp.remove", name));
        remove.Click += async (_, _) => await RemoveStampAsync(name);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { chip, remove } };
    }

    private void ShowStampFeedback(string? message)
    {
        _stampFeedback.Text = message;
        _stampFeedback.IsVisible = !string.IsNullOrEmpty(message);
        AutomationProperties.SetHelpText(_stampInput, message ?? ToolTip.GetTip(_stampInput) as string);
    }

    internal async Task AddStampAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !CanChange) return;
        int visit = _stampVisit;
        Task<bool> added = _queueWork.AddStampAsync(text, _sharedStamp?.Capacity ?? _stampCapacity);
        _stampAdd.IsEnabled = CanChange;
        UpdateActions();
        bool done = await added;
        if (_queueWork.Ended) return;
        string? feedback = _queueWork.Status;
        Show();
        if (!StampOpen || visit != _stampVisit) return;
        if (done && string.Equals(_stampInput.Text, text, StringComparison.Ordinal)) _stampInput.Text = string.Empty;
        ShowStampFeedback(feedback);
        _stampInput.Focus();
    }

    internal async Task RemoveStampAsync(string name)
    {
        if (!CanChange) return;
        int visit = _stampVisit;
        bool done = await _queueWork.RemoveStampAsync(name);
        if (_queueWork.Ended) return;
        string? feedback = _queueWork.Status;
        Show();
        if (!StampOpen || visit != _stampVisit) return;
        if (done) _stampInput.Focus();
        ShowStampFeedback(feedback);
    }
}
