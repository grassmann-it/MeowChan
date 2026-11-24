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
    private readonly TextBlock _count = new();
    private readonly Control _toggleArrow = Chevron();
    private readonly TagLine _chips;
    private readonly Grid _actions;
    private readonly StackPanel _editContent;
    private int _tagCount;
    private string? _blocked;
    private bool _expanded = true;
    private bool _canEdit;
    private bool _inline;

    internal MediaInfoBar(GalleryDecisionRow rating, Button editTags, Action<Control> edit)
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
        _toggle.Content = Horizontal(CommandBindings.IconFromKey("tag", 16), _count, _toggleArrow);
        _empty.Content = Horizontal(CommandBindings.IconFromKey("tag", 16), new TextBlock { Text = "0" });
        foreach (Button button in new Button[] { _toggle, _empty })
        {
            button.Classes.Add("ghost");
            button.Focusable = button.IsTabStop = false;
            button.Height = 24;
            button.MinHeight = button.MinWidth = 0;
            button.Padding = new Thickness(8, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetShowOnDisabled(button, true);
        }
        _chips = new TagLine(edit) { Name = "ReaderMediaSidebarTags" };
        foreach (Control control in new Control[] { _edit, _toggle, _empty, _chips })
            AutomationProperties.SetAutomationId(control, control.Name);
        _toggle.Click += (_, _) =>
        {
            _expanded = _toggle.IsChecked == true;
            UpdateTagState();
        };
        _empty.Click += (_, _) => edit(_empty);
        _actions = CreateActionGrid(rating, new Grid { Children = { _toggle, _empty } }, _edit);
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
    internal bool EditorAnchorPinned { get; private set; }
    internal void PinEditorAnchor(bool pinned) => EditorAnchorPinned = pinned;

    internal void SetTags(IReadOnlyList<string>? tags, string? blocked, bool canEdit)
    {
        _tagCount = tags?.Count ?? 0;
        _blocked = blocked;
        _canEdit = canEdit;
        _edit.IsEnabled = canEdit;
        _chips.SetTags(tags);
        UpdateTagState();
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
        _toggleArrow.RenderTransform = new RotateTransform(_expanded ? 180 : 0);
        _chips.IsVisible = _tagCount > 0 && (_expanded || _inline);
        foreach (Button button in new Button[] { _toggle, _empty })
        {
            button.IsEnabled = _blocked == null && (_tagCount > 0 || _canEdit);
            ToolTip.SetTip(button, button.IsEnabled ? label : reason);
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetHelpText(button, button.IsEnabled ? (_tagCount == 0 ? Texts.T("tags.quick.assigned.empty") : label) : reason);
        }
        _chips.IsEnabled = _blocked == null && _canEdit;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double inset = availableSize.Width < 260 ? 4 : 8;
        double width = Math.Max(0, availableSize.Width - 2 * inset);
        const double stars = 5 * WorkspaceMetrics.RatingStarMinWidth;
        const double gap = 4;
        Button count = _tagCount == 0 ? _empty : _toggle;
        double countInset = width < 232 ? 0 : 8;
        _toggle.Padding = _empty.Padding = new Thickness(countInset, 0);
        _toggleArrow.IsVisible = width >= 232;
        count.Measure(Size.Infinity);
        var text = new FormattedText(_editLabel.Text ?? "", Texts.Culture, FlowDirection,
            new Typeface(_editLabel.FontFamily, _editLabel.FontStyle, _editLabel.FontWeight), _editLabel.FontSize, _editLabel.Foreground);
        double editWidth = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 16 + 8 + 24 + 2;
        double countWidth = Math.Max(64, count.DesiredSize.Width);
        bool label = width > 288 && width >= stars + countWidth + gap + editWidth + 8;
        _edit.Classes.Set(WorkspaceIcons.SquareClass, !label);
        _editLabel.IsVisible = label;
        _editContent.Spacing = label ? 8 : 0;
        _edit.Width = label ? editWidth : 32;
        _edit.Padding = new Thickness(label ? 12 : 0, 0);
        _inline = _tagCount > 0 && width - stars - countWidth - _edit.Width - 2 * gap >= 200;
        _toggle.IsVisible = _tagCount > 0 && !_inline;
        _chips.IsVisible = _tagCount > 0 && (_expanded || _inline);
        _actions.Margin = new Thickness(inset, 4);
        SetRow(_chips, _inline ? 0 : 1);
        _chips.Margin = _inline ? new Thickness(inset + stars + gap, 0, inset + _edit.Width + gap, 0)
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

        internal TagLine(Action<Control> edit)
        {
            AutomationProperties.SetAutomationId(_rest, _rest.Name);
            _rest.Focusable = _rest.IsTabStop = false;
            Children.Add(_rest);
            Height = 24;
            Orientation = Orientation.Horizontal;
            Spacing = 8;
            _rest.Click += (_, _) => edit(_rest);
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
            ToolTip.SetTip(_rest, CommandBindings.Tooltip("media.tags.edit"));
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
                AutomationProperties.SetName(_rest, _rest.Content + " · " + CommandCatalog.Label("media.tags.edit"));
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
