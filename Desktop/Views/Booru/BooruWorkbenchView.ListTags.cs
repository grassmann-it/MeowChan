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
        List<(int Start, int End, double Top, double Height)> rows = new();
        double x = 0, y = 0, rowHeight = 0;
        int start = 0;
        bool splitSingleTagRow = _columns == 2 && shown >= 2 && TagWidth(shown) <= width;
        for (int i = 0; i < count; i++)
        {
            Size size = i == shown ? toggleSize : _items[i].DesiredSize;
            size = size.WithWidth(Math.Min(width, size.Width));
            // With two columns, two or more tags should use both even when a single row would fit.
            bool breakOneRow = splitSingleTagRow && i == (shown + 1) / 2;
            if (i > start && (x + size.Width > width || breakOneRow))
            {
                rows.Add((start, i, y, rowHeight));
                y += rowHeight + Gap;
                x = rowHeight = 0;
                start = i;
            }
            slots[i] = new Rect(x, y, size.Width, size.Height);
            x += size.Width + Gap;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        rows.Add((start, count, y, rowHeight));
        int split = _columns == 2 ? (rows.Count + 1) / 2 : rows.Count;
        // A trailing row may contain only the fold button. Keep a row of chips in each column.
        if (_columns == 2 && shown >= 2 && rows[^1].Start == shown) split = Math.Min(split, rows.Count - 2);
        double height = 0;
        for (int row = 0; row < rows.Count; row++)
        {
            var line = rows[row];
            bool second = row >= split;
            double columnX = second ? width + ColumnGap : 0;
            double dy = top - (second ? rows[split].Top : 0);
            for (int item = line.Start; item < line.End; item++)
            {
                Rect slot = slots[item];
                slots[item] = slot.Translate(new Vector(columnX, dy + (line.Height - slot.Height) / 2));
            }
            height = Math.Max(height, line.Top + dy + line.Height);
        }
        return (slots, height);
    }

    private double TagWidth(int shown)
    {
        double width = 0;
        for (int i = 0; i < shown; i++) width += _items[i].DesiredSize.Width;
        return width + (shown - 1) * Gap;
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
