using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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
    private readonly Button _more = new() { Name = "ReaderMediaSidebarMore" };
    private readonly Button _facts = new() { Name = "ReaderMediaSidebarFactSummary", Classes = { "ghost" },
        Height = 24, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _place = new() { Classes = { "workspace-meta" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _date = new() { Classes = { "workspace-meta" }, MaxLines = 1 };
    private readonly Control _factArrow = Chevron();
    private readonly ScrollViewer _factScroll;
    private readonly Flyout _flyout = new() { Placement = PlacementMode.TopEdgeAlignedLeft };
    private readonly TagLine _chips;
    private readonly Grid _actions;
    private readonly StackPanel _editContent;
    private int _tagCount;
    private string? _blocked;
    private bool _expanded = true;
    private bool _hasFacts;
    private bool _fallbackFacts;
    private bool _canEdit;
    private int _revision;
    private bool _returnFactsFocus;
    private readonly MenuFlyout _menu = new();

    internal MediaInfoBar(GalleryDecisionRow rating, Button editTags, Control factsContent, Action<Control> edit)
    {
        Name = "ReaderMediaSidebarInfoBar";
        RowDefinitions = new RowDefinitions("32,Auto,Auto");
        _rating = rating;
        _edit = editTags;
        _rating.FavoriteVisible = false;
        _rating.ClearButton.IsTabStop = true;
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
            button.Height = 24;
            button.MinHeight = button.MinWidth = 0;
            button.Padding = new Thickness(8, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetShowOnDisabled(button, true);
        }
        _chips = new TagLine(edit, () => _toggle.Focus(NavigationMethod.Tab)) { Name = "ReaderMediaSidebarTags", Margin = new Thickness(0, 4, 0, 0) };
        _toggle.Click += (_, _) =>
        {
            _expanded = _toggle.IsChecked == true;
            UpdateTagState();
        };
        _empty.Click += (_, _) => edit(_empty);
        _actions = CreateActionGrid(rating, new Grid { Children = { _toggle, _empty } }, new Grid { Children = { _edit, _more } });
        Children.Add(_actions);
        SetRow(_chips, 1);
        Children.Add(_chips);

        Grid summary = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(_date, 1);
        SetColumn(_factArrow, 2);
        _factArrow.Margin = new Thickness(4, 0, 0, 0);
        summary.Children.Add(_place);
        summary.Children.Add(_date);
        summary.Children.Add(_factArrow);
        _facts.Content = summary;
        // Keep the facts last in keyboard navigation even when they share the action row.
        Children.Add(_facts);
        _factScroll = new ScrollViewer { Content = factsContent, Padding = new Thickness(12), MaxHeight = 318,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _flyout.FlyoutPresenterClasses.Add("quick-tag-popover");
        _flyout.Content = _factScroll;
        _facts.Flyout = _flyout;
        _flyout.Opening += (_, _) =>
        {
            _returnFactsFocus = true;
            _factScroll.Width = Math.Max(0, Math.Min(Bounds.Width, 480) - 2);
        };
        SizeChanged += (_, _) => { if (_flyout.IsOpen) _factScroll.Width = Math.Max(0, Math.Min(Bounds.Width, 480) - 2); };
        _flyout.Opened += (_, _) => SetFlyoutOpen(true);
        _flyout.Closed += (_, _) =>
        {
            SetFlyoutOpen(false);
            if (_returnFactsFocus && _facts.IsEffectivelyVisible) _facts.Focus(NavigationMethod.Tab);
        };

        _menu.Opening += (_, _) =>
        {
            int revision = _revision;
            _menu.ItemsSource = new[]
            {
                CommandBindings.CreateMenuItem("media.rating.clear", () => { if (revision == _revision) rating.RequestRating(null); },
                    () => revision == _revision && rating.ClearButton.IsEnabled, null, null, true, rating.KeyContext),
                CommandBindings.CreateMenuItem("media.tags.edit", () => { if (revision == _revision) edit(_more); },
                    () => revision == _revision && _edit.IsEnabled, null, null, true, rating.KeyContext)
            };
        };
        _more.Flyout = _menu;
        SetTags(null, null, false);
        SetFacts(null, null, null, false);
        Localize();
    }

    internal static Grid CreateActionGrid(GalleryDecisionRow rating, Control tags, Control action)
    {
        SetColumn(tags, 2);
        SetColumn(action, 4);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,4,Auto"), Children = { rating, tags, action } };
    }

    internal Control EditorAnchor => _more.IsVisible ? _more : _edit;

    internal bool EditorAnchorPinned { get; private set; }

    internal void PinEditorAnchor(bool pinned)
    {
        EditorAnchorPinned = pinned;
        if (pinned)
        {
            // The shortcut can open before the first measure, or after a narrow layout hid Edit.
            _more.IsVisible = false;
            _edit.IsVisible = true;
        }
        InvalidateMeasure();
    }

    internal void CloseFlyouts()
    {
        _revision++;
        _returnFactsFocus = false;
        if (_flyout.IsOpen) _flyout.Hide();
        if (_menu.IsOpen) _menu.Hide();
    }

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
        if (_menu.ItemsSource is IEnumerable<MenuItem> items)
            foreach (MenuItem item in items) CommandBindings.Refresh(item);
        WorkspaceIcons.MakeSquare(_more, WorkspaceIcons.More, Texts.T("workview.more"));
        _more.Width = _more.Height = 32;
        _chips.RefreshLanguage();
        if (_fallbackFacts) SetFacts(null, null, null, true);
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
        _toggle.IsVisible = _tagCount > 0;
        _empty.IsVisible = !_toggle.IsVisible;
        _toggle.IsChecked = _expanded;
        _toggleArrow.RenderTransform = new RotateTransform(_expanded ? 180 : 0);
        _chips.IsVisible = _tagCount > 0 && _expanded;
        foreach (Button button in new Button[] { _toggle, _empty })
        {
            button.IsEnabled = _blocked == null && (_tagCount > 0 || _canEdit);
            ToolTip.SetTip(button, button.IsEnabled ? label : reason);
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetHelpText(button, button.IsEnabled ? (_tagCount == 0 ? Texts.T("tags.quick.assigned.empty") : label) : reason);
        }
        _chips.IsEnabled = _blocked == null && _canEdit;
    }

    internal void SetFacts(string? location, string? author, string? date, bool hasFacts)
    {
        string prefix = string.Join(" · ", new[] { location, author }.Where(value => !string.IsNullOrWhiteSpace(value)));
        _fallbackFacts = hasFacts && prefix.Length == 0 && string.IsNullOrWhiteSpace(date);
        if (_fallbackFacts) prefix = Texts.T("action.details");
        _place.Text = prefix;
        _date.Text = string.IsNullOrWhiteSpace(date) ? "" : prefix.Length == 0 ? date : " · " + date;
        string full = prefix + _date.Text;
        _facts.IsVisible = _hasFacts = full.Length > 0;
        ToolTip.SetTip(_facts, full);
        AutomationProperties.SetName(_facts, full);
        InvalidateMeasure();
    }

    private void SetFlyoutOpen(bool open)
    {
        _facts.Classes.Set("open", open);
        _factArrow.RenderTransform = new RotateTransform(open ? 180 : 0);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        bool tight = width < 268;
        bool overflow = tight && !EditorAnchorPinned;
        bool focusMore = overflow && (_rating.ClearButton.IsFocused || _edit.IsFocused);
        bool focusEdit = !overflow && _more.IsFocused;
        _rating.ClearButton.IsVisible = !tight;
        _rating.ClearGap = tight ? 0 : 8;
        _toggleArrow.IsVisible = !tight;
        _more.IsVisible = overflow;
        _edit.IsVisible = !overflow;
        Button tagButton = _tagCount == 0 ? _empty : _toggle;
        if (focusMore) _more.Focus(NavigationMethod.Tab);
        if (focusEdit) (_edit.IsEnabled ? _edit : tagButton).Focus(NavigationMethod.Tab);
        tagButton.Measure(Size.Infinity);
        var text = new FormattedText(_editLabel.Text ?? "", Texts.Culture, FlowDirection,
            new Typeface(_editLabel.FontFamily, _editLabel.FontStyle, _editLabel.FontWeight), _editLabel.FontSize, _editLabel.Foreground);
        double editWidth = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 16 + 8 + 24 + 2;
        // Keep the label stable when switching between zero tags and a count with a chevron.
        const double ratingWithGap = 5 * 24 + 8 + 32 + 16;
        double threshold = ratingWithGap + Math.Max(64, tagButton.DesiredSize.Width) + 8 + editWidth;
        bool label = width >= threshold;
        _edit.Classes.Set(WorkspaceIcons.SquareClass, !label);
        _editLabel.IsVisible = label;
        _editContent.Spacing = label ? 8 : 0;
        _edit.Width = label ? editWidth : 32;
        _edit.Padding = new Thickness(label ? 12 : 0, 0);
        _actions.ColumnDefinitions[3].Width = new GridLength(tight ? 4 : 8);
        _date.Measure(Size.Infinity);
        double minFacts = Math.Max(200, _date.DesiredSize.Width + 24);
        bool inline = _hasFacts && width >= threshold + 16 + minFacts;
        SetRow(_facts, inline ? 0 : 2);
        double trailingActions = tagButton.DesiredSize.Width + 8 + editWidth + 16;
        _facts.Margin = inline ? new Thickness(ratingWithGap, 0, trailingActions, 0) : new Thickness(0, 4, 0, 0);
        _facts.VerticalAlignment = VerticalAlignment.Center;
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
        private readonly Action _focusToggle;

        internal TagLine(Action<Control> edit, Action focusToggle)
        {
            _focusToggle = focusToggle;
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
                if (visible == count && _rest.IsFocused) _focusToggle();
                _rest.IsVisible = visible < count;
                _rest.Content = "+" + (count - visible).ToString(Texts.Culture);
                AutomationProperties.SetName(_rest, _rest.Content + " · " + CommandCatalog.Label("media.tags.edit"));
            }
            return base.MeasureOverride(availableSize);
        }
    }
}
