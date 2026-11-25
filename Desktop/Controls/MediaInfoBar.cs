using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

internal sealed class MediaInfoBar : Grid
{
    private readonly GalleryDecisionRow _rating;
    private readonly Button _edit;
    private readonly TextBlock _editLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _toggle = new() { Name = "ReaderMediaSidebarTagsToggle" };
    private readonly Button _empty = new() { Name = "ReaderMediaSidebarTagsEmpty" };
    private readonly Control _tagIcon = CommandBindings.IconFromKey("tag", 16);
    private readonly Control _emptyTagIcon = CommandBindings.IconFromKey("tag", 16);
    private readonly TextBlock _count = new();
    private readonly TextBlock _zero = new() { Text = "0" };
    internal ToggleButton Expand { get; } = new() { Name = "ReaderMediaSidebarExpand", Width = 32, Height = 32, MinWidth = 0,
        Padding = new Thickness(0), Focusable = false, IsTabStop = false, Classes = { WorkspaceIcons.SquareClass } };
    private readonly Control _expansionArrow = Chevron();
    private readonly TagLine _chips;
    private readonly Grid _actions;
    private readonly StackPanel _editContent;
    private int _tagCount;
    private string? _blocked;
    private bool _expanded = true;
    private bool _canEdit;
    private bool _inline;

    internal MediaInfoBar(GalleryDecisionRow rating, Button editTags, Action<Control> edit, Action showAllTags)
    {
        Name = "ReaderMediaSidebarInfoBar";
        RowDefinitions = new RowDefinitions("Auto,Auto");
        _rating = rating;
        _edit = editTags;
        _rating.FavoriteVisible = false;
        _rating.ClearButton.IsVisible = false;
        _rating.ClearGap = 0;
        _edit.Focusable = _edit.IsTabStop = false;
        _edit.Height = 32;
        _edit.MinWidth = 0;
        _edit.Classes.Add("panel-action");
        _editContent = Horizontal(CommandBindings.IconFromKey(CommandCatalog.Get("media.tags.edit").Icon, 16), _editLabel);
        _edit.Content = _editContent;
        _edit.Click += (_, _) => edit(_edit);
        Expand.Content = _expansionArrow;
        _toggle.Content = Horizontal(_tagIcon, _count);
        _empty.Content = Horizontal(_emptyTagIcon, _zero);
        foreach (Button button in new Button[] { _toggle, _empty })
        {
            button.Classes.Add("ghost");
            button.Focusable = button.IsTabStop = false;
            button.Height = 24;
            button.MinHeight = 0;
            button.MinWidth = WorkspaceMetrics.RatingStarMinWidth;
            button.Padding = new Thickness(8, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetShowOnDisabled(button, true);
        }
        _chips = new TagLine(showAllTags) { Name = "ReaderMediaSidebarTags" };
        foreach (Control control in new Control[] { _edit, _toggle, _empty, _chips })
            AutomationProperties.SetAutomationId(control, control.Name);
        _toggle.Click += (_, _) =>
        {
            _expanded = _toggle.IsChecked == true;
            UpdateTagState();
        };
        _empty.Click += (_, _) => edit(_empty);
        _actions = CreateActionGrid(rating, new Grid { Children = { _toggle, _empty } }, Horizontal(_edit, Expand));
        Children.Add(_actions);
        SetRow(_chips, 1);
        Children.Add(_chips);

        SetTags(null, null, false);
        Localize();
    }

    internal static Grid CreateActionGrid(GalleryDecisionRow rating, Control tags, Control action)
    {
        SetColumn(tags, 2);
        SetColumn(action, 4);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,4,Auto"), Children = { rating, tags, action } };
    }

    internal Control EditorAnchor => _edit;

    internal void SetTags(IReadOnlyList<string>? tags, string? blocked, bool canEdit)
    {
        _tagCount = tags?.Count ?? 0;
        _blocked = blocked;
        _canEdit = canEdit;
        _edit.IsEnabled = canEdit;
        _chips.SetTags(tags);
        UpdateTagState();
    }

    internal void ShowExpansion(bool open, bool pinned)
    {
        Expand.IsChecked = pinned;
        _expansionArrow.RenderTransform = new RotateTransform(open ? 0 : 180);
        string label = Texts.T(pinned ? "media.sidebar.unpin" : "media.sidebar.pin");
        ToolTip.SetTip(Expand, label);
        AutomationProperties.SetName(Expand, label);
    }

    internal void Localize()
    {
        _editLabel.Text = CommandCatalog.Label("media.tags.edit");
        AutomationProperties.SetName(_edit, _editLabel.Text);
        ToolTip.SetShowOnDisabled(_edit, true);
        _chips.RefreshLanguage();
        UpdateTagState();
        InvalidateMeasure();
    }

    private void UpdateTagState()
    {
        string label = Texts.T("tags.media.buttonCount", _tagCount);
        string? reason = _blocked ?? (_canEdit ? null : Texts.T("tags.filter.unavailable"));
        string editTip = CommandBindings.Tooltip("media.tags.edit", reason, keyContext: _rating.KeyContext);
        ToolTip.SetTip(_edit, editTip);
        AutomationProperties.SetHelpText(_edit, editTip);
        _count.Text = _tagCount.ToString(Texts.Culture);
        _toggle.IsVisible = _tagCount > 0 && !_inline;
        _empty.IsVisible = _tagCount == 0;
        _toggle.IsChecked = _expanded;

        _chips.IsVisible = _tagCount > 0 && (_expanded || _inline);
        foreach (Button button in new Button[] { _toggle, _empty })
        {
            button.IsEnabled = _blocked == null && (_tagCount > 0 || _canEdit);
            ToolTip.SetTip(button, button.IsEnabled ? label : reason);
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetHelpText(button, button.IsEnabled ? (_tagCount == 0 ? Texts.T("tags.quick.assigned.empty") : label) : reason);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double inset = availableSize.Width < 260 ? 4 : 8;
        double width = Math.Max(0, availableSize.Width - 2 * inset);
        const double stars = 5 * WorkspaceMetrics.RatingStarMinWidth;
        const double gap = 4;
        bool narrow = availableSize.Width < 260;
        double iconWidth = narrow ? WorkspaceMetrics.RatingStarMinWidth : 32;
        Expand.Width = iconWidth;
        double countInset = narrow ? 0 : 8;
        _toggle.Padding = _empty.Padding = new Thickness(countInset, 0);
        double expandWidth = Expand.IsVisible ? Expand.Width + gap : 0;
        width = Math.Max(0, width - expandWidth);
        var digits = new FormattedText(_count.Text ?? "0", Texts.Culture, FlowDirection,
            new Typeface(_count.FontFamily, _count.FontStyle, _count.FontWeight), _count.FontSize, _count.Foreground);
        double tagButtonWidth = 16 + gap + Math.Ceiling(digits.WidthIncludingTrailingWhitespace) + 2 * countInset + 2;
        var text = new FormattedText(_editLabel.Text ?? "", Texts.Culture, FlowDirection,
            new Typeface(_editLabel.FontFamily, _editLabel.FontStyle, _editLabel.FontWeight), _editLabel.FontSize, _editLabel.Foreground);
        double editWidth = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 16 + 8 + 24 + 2;
        double countWidth = Math.Max(64, tagButtonWidth);
        bool label = width > 288 && width >= stars + countWidth + gap + editWidth + 8;
        _edit.Classes.Set(WorkspaceIcons.SquareClass, !label);
        _editLabel.IsVisible = label;
        _editContent.Spacing = label ? 8 : 0;
        _edit.Width = label ? editWidth : iconWidth;
        // Preserve the count and its pointer target when the icon and digits no longer fit together.
        double tagSpace = Math.Max(WorkspaceMetrics.RatingStarMinWidth, width - stars - gap - _edit.Width);
        _toggle.MaxWidth = _empty.MaxWidth = tagSpace;
        _tagIcon.IsVisible = _emptyTagIcon.IsVisible = tagSpace >= tagButtonWidth;
        _edit.Padding = new Thickness(label ? 12 : 0, 0);
        _inline = _tagCount > 0 && width - stars - countWidth - _edit.Width - 2 * gap >= 200;
        _toggle.IsVisible = _tagCount > 0 && !_inline;
        _chips.IsVisible = _tagCount > 0 && (_expanded || _inline);
        _actions.Margin = new Thickness(inset, 4);
        SetRow(_chips, _inline ? 0 : 1);
        _chips.Margin = _inline ? new Thickness(inset + stars + gap, 0, inset + _edit.Width + gap + expandWidth, 0)
            : new Thickness(inset, 0, inset, 4);
        return base.MeasureOverride(availableSize);
    }

    private static StackPanel Horizontal(params Control[] items)
    {
        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (Control item in items) panel.Children.Add(item);
        return panel;
    }

    private static Avalonia.Controls.Shapes.Path Chevron()
    {
        var icon = WorkspaceIcons.Create(WorkspaceIcons.ChevronDown);
        icon.Width = icon.Height = 12;
        return icon;
    }

    private sealed class TagLine : StackPanel
    {
        private readonly Button _rest = new() { Name = "ReaderMediaSidebarMoreTags", Height = 24, MinHeight = 0, MinWidth = 0,
            Classes = { WorkspaceChips.Chip, WorkspaceChips.Compact }, Padding = new Thickness(8, 0) };
        private double _lastWidth = double.NaN;

        private string[] _tags = [];

        internal TagLine(Action showAllTags)
        {
            AutomationProperties.SetAutomationId(_rest, _rest.Name);
            _rest.Focusable = _rest.IsTabStop = false;
            Children.Add(_rest);
            Height = 24;
            Orientation = Orientation.Horizontal;
            Spacing = 8;
            _rest.Click += (_, _) => showAllTags();
        }

        internal void SetTags(IReadOnlyList<string>? tags)
        {
            tags ??= Array.Empty<string>();
            if (_tags.SequenceEqual(tags)) return;
            _tags = tags.ToArray();
            while (Children.Count > 1) Children.RemoveAt(Children.Count - 2);
            _lastWidth = double.NaN;
            InvalidateMeasure();
        }

        internal void RefreshLanguage()
        {
            ToolTip.SetTip(_rest, Texts.T("media.sidebar.showAllTags"));
            _lastWidth = double.NaN;
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_lastWidth != availableSize.Width)
            {
                _lastWidth = availableSize.Width;
                int count = _tags.Length;
                int visible = 0;
                double used = 0;
                _rest.IsVisible = true;
                for (; visible < count; visible++)
                {
                    if (visible == Children.Count - 1)
                        Children.Insert(visible, WorkspaceChips.CreateBadge("ReaderMediaSidebarTag", _tags[visible], compact: true));
                    Control chip = Children[visible];
                    chip.IsVisible = true;
                    chip.Measure(Size.Infinity);
                    double next = used + (visible == 0 ? 0 : Spacing) + chip.DesiredSize.Width;
                    if (next > availableSize.Width) break;
                    used = next;
                }
                // A short final badge can fit even when a +1 button would not. Reserve the overflow
                // button only once we know there are genuinely hidden tags.
                while (visible > 0 && visible < count)
                {
                    _rest.Content = "+" + (count - visible).ToString(Texts.Culture);
                    _rest.Measure(Size.Infinity);
                    if (used + Spacing + _rest.DesiredSize.Width <= availableSize.Width) break;
                    visible--;
                    used -= Children[visible].DesiredSize.Width + (visible == 0 ? 0 : Spacing);
                }
                for (int i = visible; i < Children.Count - 1; i++) Children[i].IsVisible = false;
                _rest.IsVisible = visible < count;
                _rest.Content = "+" + (count - visible).ToString(Texts.Culture);
                AutomationProperties.SetName(_rest, _rest.Content + " · " + Texts.T("media.sidebar.showAllTags"));
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
