using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The tag part of a filter surface: a button "Add tag…" that opens a popover with a search field over the own tags that are not
/// in the filter yet. It replaces the tag combo box with its two text buttons: Enter (or a click) adds the highlighted tag as an
/// inclusion, Shift+Enter, Shift+click or a leading "-" as an exclusion, and the "Add as" choice does the same without a modifier.
/// The tags the filter already holds are shown as chips by the page (with their include/exclude state); this control only adds.
/// The ranking is the tag fields' own (<see cref="TagSuggestionIndex"/>: exact, prefix, word start, anywhere; more uses first).
/// </summary>
internal sealed class TagFilterPicker : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private static readonly Lazy<Geometry> Plus = new(() => Geometry.Parse("M 8,3 L 8,13 M 3,8 L 13,8"));

    // Fixed layout measures of the popover (dip), not user limits and therefore not settings: the list scrolls beyond
    // 220 so the popover stays small however many tags exist, a tag name is trimmed at 320, and the popover keeps 280-360 wide
    // so the search box and the include/exclude row stay readable.
    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly SearchBox _search = new() { Name = "TagFilterPickerSearch", MinWidth = 260 };
    private readonly ListBox _list = new() { Name = "TagFilterPickerList", MaxHeight = 220, SelectionMode = SelectionMode.Single };
    private readonly RadioButton _asInclude = new() { Name = "TagFilterPickerInclude", IsChecked = true };
    private readonly RadioButton _asExclude = new() { Name = "TagFilterPickerExclude" };
    private readonly TextBlock _empty = new() { Name = "TagFilterPickerEmpty", TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
    private readonly TextBlock _hint = new() { Name = "TagFilterPickerHint", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock _error = new() { Name = "TagFilterPickerError", TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "workspace-error" } };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _plus = new() { Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private bool _geometryApplied, _refilling;

    public TagFilterPicker()
    {
        Classes.Add("subtle");
        MinHeight = 32;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        _plus[!Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Text");
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _plus, _label } };
        string group = "tag-filter-mode-" + Guid.NewGuid().ToString("N");
        _asInclude.GroupName = _asExclude.GroupName = group;
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TagSuggestion>((item, _) =>
            new TextBlock { Text = item?.Name.Display ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });
        StackPanel modes = new() { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _asInclude, _asExclude } };
        StackPanel body = new() { Spacing = 8, MinWidth = 280, MaxWidth = 360, Children = { _search, _list, _empty, modes, _hint, _error } };
        _flyout.Content = new Border { Padding = new Thickness(4), Child = body };
        Flyout = _flyout;
        _flyout.Opened += (_, _) =>
        {
            _error.IsVisible = false;
            if (!KeepQueryOnDismiss) _search.SetTextSilently(string.Empty);
            Refill();
            _search.Focus();
            if (KeepQueryOnDismiss) _search.SelectAll();
        };
        _flyout.Closed += (_, _) => Refresh();
        _search.QueryChanged += (_, _) => { Refill(); PendingChanged?.Invoke(this, EventArgs.Empty); };
        _search.KeyDown += OnSearchKeyDown;
        _list.AddHandler(TappedEvent, OnListTapped, RoutingStrategies.Bubble);
        ApplyTexts();
    }

    /// <summary>The own tags as published (null while the store is unavailable).</summary>
    internal Func<PrivateTagStoreSnapshot?> Snapshot { get; set; } = () => null;

    /// <summary>The tags the filter already holds (included or excluded); they are not offered again.</summary>
    internal Func<IReadOnlySet<TagId>> Used { get; set; } = () => new HashSet<TagId>();

    /// <summary>Adds a tag (id, exclude); returns null when it was added, else the localized reason it was refused (shown in the popover).</summary>
    internal Func<Guid, bool, string?> Picked { get; set; } = (_, _) => null;

    /// <summary>Descriptions of the two "Add as" choices (tooltip and help text); set by the page, whose wording says what the filter hides.</summary>
    internal string? IncludeHint { get; set; }
    internal string? ExcludeHint { get; set; }

    /// <summary>
    /// A page whose filter is applied by a separate step (the Archive draft) keeps what was typed when the popover is dismissed
    /// without picking: that is an unfinished entry (<see cref="HasPending"/>), not something to drop silently. Pages whose filter
    /// applies at once leave this off, and the popover starts empty every time.
    /// </summary>
    internal bool KeepQueryOnDismiss { get; set; }

    /// <summary>Raised when the typed text changes or is cleared, so a page can clear or refresh its "unfinished entry" mark.</summary>
    internal event EventHandler? PendingChanged;

    /// <summary>
    /// True while <see cref="KeepQueryOnDismiss"/> is on and the typed text names at least one tag that could still be added: the entry
    /// is not finished, because the app cannot guess Include or Exclude for it.
    /// </summary>
    internal bool HasPending
    {
        get
        {
            if (!KeepQueryOnDismiss) return false;
            string query = ParseQuery(_search.Text, false).Query;
            return TagSuggestionIndex.QueryKey(query) != null && Choices(Snapshot(), Used(), query).Count > 0;
        }
    }

    /// <summary>Drops the unfinished entry (the typed text).</summary>
    internal void ClearPending()
    {
        if (!_search.HasText) return;
        _search.SetTextSilently(string.Empty);
        _search.CancelPendingQuery();
        if (_flyout.IsOpen) Refill();
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool IsPopoverOpen => _flyout.IsOpen;
    internal SearchBox Search => _search;
    internal ListBox List => _list;
    internal IReadOnlyList<TagSuggestion> Shown => _list.ItemsSource as IReadOnlyList<TagSuggestion> ?? Array.Empty<TagSuggestion>();

    /// <summary>
    /// What the typed text asks for: a leading "-" means an exclusion and is not part of the name. <paramref name="exclude"/> is the
    /// "Add as" choice, which the dash can only switch on.
    /// </summary>
    internal static (string Query, bool Exclude) ParseQuery(string? text, bool exclude)
    {
        string trimmed = (text ?? string.Empty).TrimStart();
        if (trimmed.StartsWith('-')) return (trimmed[1..].TrimStart(), true);
        return (text ?? string.Empty, exclude);
    }

    /// <summary>The tags to offer for the typed text: the most used while it is empty, else the ranked matches; never one in <paramref name="used"/>.</summary>
    internal static IReadOnlyList<TagSuggestion> Choices(PrivateTagStoreSnapshot? snapshot, IReadOnlySet<TagId> used, string? query)
    {
        if (snapshot == null) return Array.Empty<TagSuggestion>();
        TagSuggestionIndex index = TagSuggestionIndex.For(snapshot);
        return TagSuggestionIndex.QueryKey(query) == null ? index.MostUsed(used, int.MaxValue) : index.Match(query, used);
    }

    /// <summary>Enabled only while an own tag is left to add; says why otherwise. Call when the tags or the filter change.</summary>
    internal void Refresh()
    {
        PrivateTagStoreSnapshot? snapshot = Snapshot();
        string name = Texts.T("tags.filter.add");
        string? reason = snapshot == null ? Texts.T("tags.filter.unavailable")
            : snapshot.Tags.Count == 0 ? Texts.T("tags.filter.none")
            : Choices(snapshot, Used(), null).Count == 0 ? Texts.T("tags.filter.add.allUsed") : null;
        IsEnabled = reason == null;
        _label.Text = name;
        ToolTip.SetTip(this, reason == null ? name : name + " · " + reason);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, reason ?? string.Empty);
    }

    internal void ApplyTexts()
    {
        _search.PlaceholderText = Texts.T("tags.filter.add.search");
        AutomationProperties.SetName(_list, Texts.T("tags.filter.add.search"));
        _asInclude.Content = Texts.T("tags.filter.include");
        _asExclude.Content = Texts.T("tags.filter.exclude");
        AutomationProperties.SetName(_asInclude, Texts.T("tags.filter.add.mode") + ": " + Texts.T("tags.filter.include"));
        AutomationProperties.SetName(_asExclude, Texts.T("tags.filter.add.mode") + ": " + Texts.T("tags.filter.exclude"));
        ToolTip.SetTip(_asInclude, IncludeHint); ToolTip.SetTip(_asExclude, ExcludeHint);
        AutomationProperties.SetHelpText(_asInclude, IncludeHint ?? string.Empty); AutomationProperties.SetHelpText(_asExclude, ExcludeHint ?? string.Empty);
        _hint.Text = Texts.T("tags.filter.add.hint");
        Refresh();
    }

    /// <summary>Opens the popover (the button's click does the same).</summary>
    internal void Open() { if (IsEnabled) _flyout.ShowAt(this); }

    internal void Close() => _flyout.Hide();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_geometryApplied) return;
        _geometryApplied = true;
        _plus.Data = Plus.Value;
    }

    private void Refill()
    {
        if (_refilling) return;
        _refilling = true;
        try
        {
            (string query, _) = ParseQuery(_search.Text, _asExclude.IsChecked == true);
            PrivateTagStoreSnapshot? snapshot = Snapshot();
            IReadOnlyList<TagSuggestion> choices = Choices(snapshot, Used(), query);
            _list.ItemsSource = choices;
            _list.IsVisible = choices.Count > 0;
            if (choices.Count > 0) _list.SelectedIndex = 0;
            _empty.IsVisible = choices.Count == 0;
            _empty.Text = snapshot == null ? Texts.T("tags.filter.unavailable")
                : snapshot.Tags.Count == 0 ? Texts.T("tags.filter.none")
                : TagSuggestionIndex.QueryKey(query) == null ? Texts.T("tags.filter.add.allUsed")
                : Texts.T("tags.filter.add.noMatch", query.Trim());
        }
        finally { _refilling = false; }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up when e.KeyModifiers == KeyModifiers.None && Shown.Count > 0:
                int next = _list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                _list.SelectedIndex = Math.Clamp(next, 0, Shown.Count - 1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                e.Handled = true;
                PickSelected(e.KeyModifiers == KeyModifiers.Shift);
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None && !_search.HasText:
                e.Handled = true;
                _flyout.Hide();
                Focus(NavigationMethod.Tab);
                break;
        }
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is not { DataContext: TagSuggestion suggestion }) return;
        e.Handled = true;
        Pick(suggestion, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private void PickSelected(bool shift)
    {
        if (_list.SelectedItem is TagSuggestion suggestion) Pick(suggestion, shift);
    }

    private void Pick(TagSuggestion suggestion, bool shift)
    {
        if (suggestion.Id is not { } id) return;
        bool exclude = shift || ParseQuery(_search.Text, _asExclude.IsChecked == true).Exclude;
        string? refusal = Picked(id.Value, exclude);
        if (refusal != null)
        {
            _error.Text = refusal;
            _error.IsVisible = true;
            return;
        }
        _error.IsVisible = false;
        if (KeepQueryOnDismiss) ClearPending();
        _flyout.Hide();
        Focus(NavigationMethod.Tab);
    }
}
