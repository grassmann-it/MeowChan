using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private static readonly BooruTagCategory[] ListTagOrder =
    [
        BooruTagCategory.Artist, BooruTagCategory.Copyright, BooruTagCategory.Character,
        BooruTagCategory.General, BooruTagCategory.Meta, BooruTagCategory.Unknown
    ];

    private bool _listTagDraftRefreshPending;

    private BooruListTagFlow CreateTags(BooruPostView post, ResultActions result, Border card)
    {
        BooruTag[] ordered = ListTagOrder.SelectMany(category => post.Tags.Where(tag => tag.Category == category)).ToArray();
        int savedLimit = TagsShown();
        int limit = savedLimit == Config.BooruResultTagsShownAutomatic ? ordered.Length : savedLimit;
        BooruListTagFlow tags = new(post.PostId, ordered, limit, ListLayout.TagLines, HiddenTagTooltipLimit, _expandedTagPosts.Contains(post.PostId),
            CreateItem, expanded =>
            {
                if (!result.IsCurrent()) return false;
                if (expanded) _expandedTagPosts.Add(post.PostId);
                else _expandedTagPosts.Remove(post.PostId);
                return true;
            }, RestoreFoldedChipFocus);

        tags.Styles.Add(new Style(selector => selector.OfType<Button>().Class(WorkspaceChips.SourceButton).Class(":pointerover")
            .Descendant().OfType<ChipOutline>())
        {
            Setters =
            {
                new Setter(ChipOutline.StrokeProperty, new DynamicResourceExtension("Workspace.Accent")),
                new Setter(ChipOutline.IsDashedProperty, false)
            }
        });
        return tags;

        void FocusCard()
        {
            if (result.IsCurrent() && card.IsAttachedToVisualTree() && card.IsEffectivelyVisible)
                card.Focus(NavigationMethod.Tab);
        }

        void RestoreFoldedChipFocus(Button chip)
        {
            if (chip.Focusable || TopLevel.GetTopLevel(card)?.FocusManager is not { } manager) return;
            if (manager.GetFocusedElement() is not { } focused || ReferenceEquals(focused, chip))
                FocusCard();
        }

        (Control Item, Button Chip, ContextMenuHost Menu) CreateItem(int index)
        {
            BooruTag tag = ordered[index];
            string label = BooruText.TagCategory(tag.Category);
            ChipOutline outline = WorkspaceChips.CreateSourceTag(tag.Name, tag.Category, compact: true);
            DraftTagButton chip = new()
            {
                Name = ResultTagPrefix + post.PostId + ":" + tag.Name,
                Content = outline, Cursor = new Cursor(StandardCursorType.Hand),
                Classes = { WorkspaceChips.SourceButton }, MaxWidth = 240
            };
            AutomationProperties.SetAutomationId(chip, chip.Name);
            string hint = Texts.T("booru.ui.tags.clickHint", PlatformKeyNames.Alt());
            AutomationProperties.SetHelpText(chip, hint);
            ToolTip.AddToolTipOpeningHandler(chip, (_, _) => ToolTip.SetTip(chip,
                Texts.T("media.breakdown.summary", label, tag.Name) + Environment.NewLine + hint));
            chip.Click += (_, _) =>
            {
                if (chip.Focusable && result.IsCurrent())
                    EditTagDraft(tag.Name, chip.ExcludeOnClick ? BooruTagDraftAction.Exclude : BooruTagDraftAction.Include,
                        CaptureResultLink(result.Target));
            };
            _resultTagChips.Add((chip, outline, tag, label));
            ContextMenuHost menu = new(chip,
                () => chip.Focusable ? ComposeTagMenu(tag.Name, result.Target) : null, FocusCard);
            _resultTagMenus.Add(menu);
            QueueListTagDraftRefresh();

            if (index > 0 && ordered[index - 1].Category == tag.Category) return (chip, chip, menu);
            TextBlock category = new()
            {
                Text = label + ":", VerticalAlignment = VerticalAlignment.Center, Classes = { "workspace-meta" },
                FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            string color = tag.Category == BooruTagCategory.Unknown ? nameof(BooruTagCategory.General) : tag.Category.ToString();
            category[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.TagCategory." + color);
            ToolTip.SetTip(category, Texts.T("booru.ui.tags.label"));
            AutomationProperties.SetAccessibilityView(category, AccessibilityView.Raw);
            Grid group = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4 };
            Grid.SetColumn(chip, 1);
            group.Children.AddRange([category, chip]);
            return (group, chip, menu);
        }
    }

    private void QueueListTagDraftRefresh()
    {
        if (_listTagDraftRefreshPending) return;
        _listTagDraftRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _listTagDraftRefreshPending = false;
            if (_closed) return;
            _shownTagDraft = null;
            RenderTagDraft(_model.Search.View);
        }, DispatcherPriority.Loaded);
    }
}

/// <summary>Wraps category marks with their first chip and folds whole chips into the available rows.</summary>
internal sealed class BooruListTagFlow : Panel
{
    private const double Gap = 4;
    private const double CategoryGap = 12;
    private const double ChipRowHeight = 20;

    private static readonly BooruTagCategory[] FoldOrder =
    [
        BooruTagCategory.General, BooruTagCategory.Unknown, BooruTagCategory.Meta,
        BooruTagCategory.Character, BooruTagCategory.Copyright, BooruTagCategory.Artist
    ];

    private sealed record Group(int Start, int Count, int FoldAfter)
    {
        internal int Hidden(int totalHidden) => Math.Clamp(totalHidden - FoldAfter, 0, Count);
    }

    private readonly BooruTag[] _tags;
    private readonly Group[] _groups;
    private readonly int _minimumHidden;
    private readonly int _lineLimit;
    private readonly int _hiddenTagTooltipLimit;
    private readonly Func<int, (Control Item, Button Chip, ContextMenuHost Menu)> _createItem;
    private readonly Func<bool, bool> _setExpanded;
    private readonly Action<Button> _restoreFocus;
    private readonly List<int> _realizedOrder = [];
    private readonly Dictionary<int, (Control Item, Button Chip, ContextMenuHost Menu)> _realized = new();
    private readonly Dictionary<int, Rect> _slots = new();
    private readonly Button _toggle;
    private readonly TextBlock? _empty;
    private bool _expanded;
    private int _hidden;
    private bool _showToggle;
    private double _heightBudget;

    internal BooruListTagFlow(string postId, BooruTag[] tags, int limit, int lineLimit, int hiddenTagTooltipLimit, bool expanded,
        Func<int, (Control Item, Button Chip, ContextMenuHost Menu)> createItem, Func<bool, bool> setExpanded, Action<Button> restoreFocus)
    {
        Name = "BooruListTags:" + postId;
        AutomationProperties.SetAutomationId(this, Name);
        AutomationProperties.SetName(this, Texts.T("booru.ui.tags.label"));
        AutomationProperties.SetControlTypeOverride(this, Avalonia.Automation.Peers.AutomationControlType.Group);
        _tags = tags;
        _minimumHidden = Math.Max(0, tags.Length - limit);
        _lineLimit = lineLimit;
        _hiddenTagTooltipLimit = hiddenTagTooltipLimit;
        _createItem = createItem;
        _setExpanded = setExpanded;
        _restoreFocus = restoreFocus;
        _expanded = expanded;
        _groups = MakeGroups(tags);
        _toggle = new Button
        {
            Name = BooruWorkbenchView.MoreTagsPrefix + postId, Classes = { "link", "tnum" },
            Focusable = false, IsTabStop = false, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(_toggle, _toggle.Name);
        if (tags.Length == 0)
        {
            _empty = new TextBlock { Text = Texts.T("booru.ui.tags.none"), Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
            Children.Add(_empty);
            return;
        }

        UpdateToggleIcon();
        _toggle.Click += (_, _) => Toggle();
        Children.Add(_toggle);
    }

    private static Group[] MakeGroups(BooruTag[] tags)
    {
        var groups = tags.GroupBy(tag => tag.Category).Select(group => (group.Key, Count: group.Count())).ToArray();
        var foldAfter = new Dictionary<BooruTagCategory, int>();
        int prior = 0;
        foreach (var category in FoldOrder)
        {
            foldAfter.Add(category, prior);
            prior += groups.FirstOrDefault(group => group.Key == category).Count;
        }
        int start = 0;
        var result = new Group[groups.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            var (category, count) = groups[i];
            result[i] = new Group(start, count, foldAfter[category]);
            start += count;
        }
        return result;
    }

    internal void SetHeightBudget(double height)
    {
        if (_heightBudget == height) return;
        _heightBudget = height;
        InvalidateMeasure();
    }

    internal void AddFoldAction(ContextMenuBuilder menu)
    {
        if (_showToggle)
            menu.Item("booru.menu.fold-tags", (string)_toggle.Content!, Toggle, icon: ToggleIcon);
    }

    private string ToggleIcon => _expanded ? "chevron-up" : "chevron-down";

    private void UpdateToggleIcon() => CommandBindings.ShowIconBeforeText(_toggle, ToggleIcon, 12);

    private void Toggle()
    {
        if (!_showToggle || !_setExpanded(!_expanded)) return;
        _expanded = !_expanded;
        UpdateToggleIcon();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        Size space = new(width, double.PositiveInfinity);
        if (_empty != null)
        {
            _empty.Measure(space);
            return new Size(width, _empty.DesiredSize.Height);
        }

        int hidden = _expanded ? 0 : _minimumHidden;
        if (!_expanded && Flow(space, hidden) == double.PositiveInfinity)
        {
            int high = _tags.Length;
            while (hidden < high)
            {
                int candidate = (hidden + high) / 2;
                if (Flow(space, candidate) < double.PositiveInfinity) high = candidate;
                else hidden = candidate + 1;
            }
        }

        _slots.Clear();
        double height = Flow(space, hidden, _slots);
        _showToggle = _expanded || hidden > 0;
        if (_showToggle)
        {
            _toggle.Content = ToggleLabel(hidden);
            if (_hidden != hidden || _expanded)
                UpdateToggleDescription(hidden);
        }
        _hidden = hidden;
        return new Size(width, height);
    }

    private double Flow(Size space, int hidden, Dictionary<int, Rect>? output = null)
    {
        double x = 0, y = 0, height = 0;
        int row = 1;
        int rowStart = 0;
        List<int>? rowItems = output != null ? [] : null;
        bool constrained = !_expanded && output == null;

        foreach (Group group in _groups)
        {
            int end = group.Start + group.Count - group.Hidden(hidden);
            for (int index = group.Start; index < end; index++)
            {
                Control item = Realize(index);
                item.Measure(space);
                if (!Place(index, item.DesiredSize, index == group.Start ? CategoryGap : Gap))
                    return double.PositiveInfinity;
            }
        }
        if (_expanded || hidden > 0)
        {
            _toggle.Content = ToggleLabel(hidden);
            _toggle.Measure(space);
            if (!Place(_tags.Length, _toggle.DesiredSize, CategoryGap))
                return double.PositiveInfinity;
        }
        CenterRow();
        return y + height;

        bool Place(int index, Size size, double gap)
        {
            Size itemSize = size.WithWidth(Math.Min(size.Width, space.Width));
            if (x > 0 && x + gap + itemSize.Width > space.Width)
            {
                CenterRow();
                y += height + Gap;
                x = height = 0;
                row++;
                rowStart = rowItems?.Count ?? 0;
            }
            height = Math.Max(Math.Max(height, ChipRowHeight), itemSize.Height);
            bool toggleAlone = index == _tags.Length && row == 1 && x == 0;
            if (constrained && !toggleAlone && (_lineLimit > 0 && row > _lineLimit || y + height > _heightBudget))
                return false;

            if (x > 0) x += gap;
            if (output != null)
            {
                output.Add(index, new Rect(x, y, itemSize.Width, itemSize.Height));
                rowItems!.Add(index);
            }
            x += itemSize.Width;
            return true;
        }

        void CenterRow()
        {
            if (output == null) return;
            for (int i = rowStart; i < rowItems!.Count; i++)
            {
                int index = rowItems[i];
                Rect slot = output[index];
                output[index] = slot.Translate(new Vector(0, (height - slot.Height) / 2));
            }
        }
    }

    private Control Realize(int index)
    {
        if (_realized.TryGetValue(index, out var existing)) return existing.Item;
        var realized = _createItem(index);
        int position = ~_realizedOrder.BinarySearch(index);
        _realizedOrder.Insert(position, index);
        _realized.Add(index, realized);
        Children.Insert(position, realized.Item);
        return realized.Item;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_empty != null)
        {
            _empty.Arrange(new Rect(finalSize));
            return finalSize;
        }

        foreach (var (index, (item, chip, menu)) in _realized)
        {
            bool show = _slots.TryGetValue(index, out Rect slot);
            bool lostFocus = !show && chip.Focusable && chip.IsFocused;
            SetDisplayed(item, chip, show, slot);
            if (show) continue;
            menu.Close();
            if (lostFocus) Dispatcher.UIThread.Post(() => _restoreFocus(chip), DispatcherPriority.Loaded);
        }
        SetDisplayed(_toggle, _toggle, _showToggle, _slots.GetValueOrDefault(_tags.Length));
        return finalSize;
    }

    private void SetDisplayed(Control item, Button button, bool visible, Rect slot)
    {
        // A folded chip can be measured again when the window or font changes.
        item.Opacity = visible ? 1 : 0;
        item.IsHitTestVisible = visible;
        bool focusable = visible && button != _toggle;
        button.Focusable = focusable;
        button.IsTabStop = focusable;
        AutomationProperties.SetAccessibilityView(button, visible ? AccessibilityView.Content : AccessibilityView.Raw);
        item.Arrange(slot);
    }

    private string ToggleLabel(int hidden) =>
        _expanded ? Texts.T("booru.ui.tags.less") : Texts.T("booru.ui.tags.more", hidden);

    private void UpdateToggleDescription(int hidden)
    {
        string label = ToggleLabel(hidden);
        AutomationProperties.SetName(_toggle, label);
        string tip = label;
        if (!_expanded)
        {
            var names = _groups.SelectMany(group =>
            {
                int count = group.Hidden(hidden);
                return _tags.Skip(group.Start + group.Count - count).Take(count);
            }).Take(_hiddenTagTooltipLimit).Select(tag => tag.Name);
            string suffix = hidden > _hiddenTagTooltipLimit ? "…" : string.Empty;
            tip = Texts.T("booru.ui.tags.showAll", string.Join(", ", names) + suffix);
        }
        ToolTip.SetTip(_toggle, tip);
        AutomationProperties.SetHelpText(_toggle, tip);
    }
}
