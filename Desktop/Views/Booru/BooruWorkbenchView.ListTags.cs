using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    private BooruListTagFlow CreateTags(BooruPostView post, ResultActions result)
    {
        bool expanded = _expandedTagPosts.Contains(post.PostId);
        var ordered = ListTagOrder.SelectMany(category => post.Tags.Where(tag => tag.Category == category)).ToArray();
        Button[] buttons = new Button[expanded ? ordered.Length : Math.Min(TagsShown(), ordered.Length)];
        Control[] items = new Control[buttons.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            BooruTag tag = ordered[i];
            string label = Texts.T("booru.ui.tagCategory." + tag.Category);
            Button chip = new()
            {
                Name = ResultTagPrefix + post.PostId + ":" + tag.Name,
                Content = WorkspaceChips.CreateSourceTag(tag.Name, tag.Category, compact: true),
                Classes = { WorkspaceChips.SourceButton }, MaxWidth = 240
            };
            string accessible = Texts.T("booru.ui.tags.addFor", tag.Name, label);
            AutomationProperties.SetName(chip, accessible);
            ToolTip.SetTip(chip, accessible);
            chip.Click += (_, _) => { if (result.IsCurrent()) AddTagToSearch(tag.Name); };
            _resultTagMenus.Add(new ContextMenuHost(chip, () => ComposeTagMenu(tag.Name, result.Target), () => chip.Focus(NavigationMethod.Tab)));
            buttons[i] = chip;
            items[i] = chip;
            if (i > 0 && ordered[i - 1].Category == tag.Category) continue;
            TextBlock category = new()
            {
                Text = label + ":", VerticalAlignment = VerticalAlignment.Center, Classes = { "workspace-meta" },
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            AutomationProperties.SetAccessibilityView(category, AccessibilityView.Raw);
            Grid group = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4 };
            Grid.SetColumn(chip, 1);
            group.Children.AddRange([category, chip]);
            items[i] = group;
        }
        Button toggle = new()
        {
            Name = MoreTagsPrefix + post.PostId, Classes = { "link", "tnum" }, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left
        };
        CommandBindings.EnsureIconBeforeText(toggle, expanded ? "chevron-up" : "chevron-down", 12);
        toggle.Click += (_, _) =>
        {
            if (!result.IsCurrent()) return;
            if (expanded) _expandedTagPosts.Remove(post.PostId);
            else _expandedTagPosts.Add(post.PostId);
            Render(force: true);
        };
        return new BooruListTagFlow(post.PostId, ordered, items, buttons, toggle, expanded);
    }
}

/// <summary>Wraps complete tag chips. The same ordered rows are read top to bottom in each column.</summary>
internal sealed class BooruListTagFlow : Panel
{
    private const double Gap = 4;
    private const double ColumnGap = 16;
    private const int MaximumHiddenTagsNamed = 60;

    private readonly BooruTag[] _tags;
    private readonly Control[] _items;
    private readonly Button[] _buttons;
    private readonly Button _toggle;
    private readonly bool _expanded;
    private readonly TextBlock _heading;
    private readonly TextBlock _empty;
    private Rect[] _slots = [];
    private int _shown = -1;
    private int _columns = 1;
    private double _heightBudget = double.PositiveInfinity;

    internal BooruListTagFlow(string postId, BooruTag[] tags, Control[] items, Button[] buttons, Button toggle, bool expanded)
    {
        Name = "BooruListTags:" + postId;
        _tags = tags;
        _items = items;
        _buttons = buttons;
        _toggle = toggle;
        _expanded = expanded;
        _heading = new TextBlock
        {
            Text = Texts.T("booru.ui.tags.label"), Classes = { "workspace-meta" },
            FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap
        };
        _empty = new TextBlock { Text = Texts.T("booru.ui.tags.none"), Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
        Children.Add(_heading);
        if (tags.Length == 0) Children.Add(_empty);
        else
        {
            Children.AddRange(items);
            Children.Add(toggle);
            UpdateToggle(items.Length);
        }
    }

    internal void SetLayout(int columns, double heightBudget)
    {
        if (_columns == columns && _heightBudget == heightBudget) return;
        _columns = columns;
        _heightBudget = heightBudget;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 640;
        _heading.Measure(new Size(width, double.PositiveInfinity));
        double top = _heading.DesiredSize.Height + Gap;
        if (_tags.Length == 0)
        {
            _empty.Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, top + _empty.DesiredSize.Height);
        }

        double columnWidth = _columns == 1 ? width : Math.Max(0, Math.Floor((width - ColumnGap) / 2));
        Size rowSpace = new(columnWidth, double.PositiveInfinity);
        foreach (Control item in _items) item.Measure(rowSpace);
        _toggle.Measure(rowSpace);
        // Reserve room for the count to grow if narrower rows fold more tags.
        Size toggleSize = _toggle.DesiredSize;
        if (!_expanded)
        {
            int totalDigits = _tags.Length.ToString(Texts.Culture).Length;
            int currentDigits = Math.Max(0, _tags.Length - Math.Max(0, _shown)).ToString(Texts.Culture).Length;
            toggleSize = toggleSize.WithWidth(Math.Min(columnWidth, toggleSize.Width + (totalDigits - currentDigits) * 8 + Gap));
        }

        int shown = _items.Length;
        var layout = Flow(shown, columnWidth, toggleSize, top);
        if (!_expanded && layout.Height > _heightBudget)
        {
            int low = 0, high = shown;
            while (low < high)
            {
                int candidate = (low + high + 1) / 2;
                if (Flow(candidate, columnWidth, toggleSize, top).Height <= _heightBudget) low = candidate;
                else high = candidate - 1;
            }
            shown = low;
            layout = Flow(shown, columnWidth, toggleSize, top);
        }
        if (_shown != shown) UpdateToggle(shown);
        _slots = layout.Slots;
        _toggle.Measure(rowSpace);
        return new Size(width, layout.Height);
    }

    private (Rect[] Slots, double Height) Flow(int shown, double width, Size toggleSize, double top)
    {
        int count = shown + (_expanded || shown < _tags.Length ? 1 : 0);
        Rect[] slots = new Rect[count];
        int split = count;
        double height = FlowColumn(0, count, 0);
        if (_columns == 2)
        {
            // The heading travels with its category. Only a boundary between visible categories can start column two.
            for (int i = 1; i < shown; i++)
            {
                if (_tags[i - 1].Category == _tags[i].Category) continue;
                double candidate = Math.Max(FlowColumn(0, i, 0), FlowColumn(i, count, width + ColumnGap));
                if (split != count && candidate >= height) continue;
                split = i;
                height = candidate;
            }
        }
        FlowColumn(0, split, 0, slots);
        if (split < count) FlowColumn(split, count, width + ColumnGap, slots);
        return (slots, height);

        double FlowColumn(int first, int end, double columnX, Rect[]? output = null)
        {
            double x = 0, y = top, rowHeight = 0;
            int rowStart = first;
            for (int i = first; i < end; i++)
            {
                Size size = i == shown ? toggleSize : _items[i].DesiredSize;
                size = size.WithWidth(Math.Min(width, size.Width));
                if (i > rowStart && x + size.Width > width)
                {
                    CenterRow(i);
                    y += rowHeight + Gap;
                    x = rowHeight = 0;
                    rowStart = i;
                }
                if (output != null) output[i] = new Rect(columnX + x, y, size.Width, size.Height);
                x += size.Width + Gap;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            CenterRow(end);
            return y + rowHeight;

            void CenterRow(int rowEnd)
            {
                if (output == null) return;
                for (int i = rowStart; i < rowEnd; i++)
                    output[i] = output[i].Translate(new Vector(0, (rowHeight - output[i].Height) / 2));
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _heading.Arrange(new Rect(_heading.DesiredSize));
        if (_tags.Length == 0)
        {
            _empty.Arrange(new Rect(0, _heading.DesiredSize.Height + Gap, finalSize.Width, _empty.DesiredSize.Height));
            return finalSize;
        }

        bool showToggle = _slots.Length > _shown;
        Button? hiddenFocus = _toggle.IsFocused && !showToggle ? _toggle : null;
        SetDisplayed(_toggle, _toggle, showToggle, showToggle ? _slots[^1] : default);
        for (int i = 0; i < _items.Length; i++)
        {
            bool show = i < _shown;
            if (_buttons[i].IsFocused && !show) hiddenFocus = _buttons[i];
            SetDisplayed(_items[i], _buttons[i], show, show ? _slots[i] : default);
        }
        if (hiddenFocus != null)
        {
            Button target = showToggle ? _toggle : _buttons[_shown - 1];
            Dispatcher.UIThread.Post(() =>
            {
                if (hiddenFocus.IsFocused && target.IsTabStop) target.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Loaded);
        }
        return finalSize;
    }

    private static void SetDisplayed(Control item, Button button, bool visible, Rect slot)
    {
        // We still measure folded chips when the viewport or the font changes.
        item.Opacity = visible ? 1 : 0;
        item.IsHitTestVisible = visible;
        button.IsTabStop = visible;
        AutomationProperties.SetAccessibilityView(button, visible ? AccessibilityView.Content : AccessibilityView.Raw);
        item.Arrange(slot);
    }

    private void SetToggleText(int count) =>
        _toggle.Content = _expanded ? Texts.T("booru.ui.tags.less") : Texts.T("booru.ui.tags.more", count);

    private void UpdateToggle(int shown)
    {
        _shown = shown;
        int hidden = _tags.Length - shown;
        SetToggleText(hidden);
        string label = (string)_toggle.Content!;
        AutomationProperties.SetName(_toggle, label);
        if (_expanded)
        {
            ToolTip.SetTip(_toggle, label);
            return;
        }
        string names = string.Join(", ", _tags.Skip(shown).Take(MaximumHiddenTagsNamed).Select(tag => tag.Name))
            + (hidden > MaximumHiddenTagsNamed ? "…" : string.Empty);
        string tip = Texts.T("booru.ui.tags.showAll", names);
        ToolTip.SetTip(_toggle, tip);
        AutomationProperties.SetHelpText(_toggle, tip);
    }
}
