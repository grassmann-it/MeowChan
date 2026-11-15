using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop.Controls;

/// <summary>One entry the filter picker offers: a filter term of a field with the label it is shown by (an own tag carries its suggestion).</summary>
internal sealed record FilterTermChoice(MediaFilterField Field, string Label, MediaFilterTerm Term, TagSuggestion? Tag = null)
{
    public override string ToString() => Label;
}

/// <summary>A group heading in the picker's list ("Type", "Board", …): shown, never selectable.</summary>
internal sealed record FilterTermGroup(int Order, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// M2: the popover that adds a filter term. A button opens it; a search field at the top filters every group at once (own tags with
/// the tag fields' ranking, then type, sound, source, board, rating and the three flags), each group under its heading. Enter or a
/// click adds the highlighted entry as an inclusion; Shift+Enter, Shift+click, a leading "-" or the "Add as: Exclude" choice add it
/// as an exclusion (only tag, type, source and board can exclude; the others say so). A comma adds the entry and keeps the popover
/// open for the next one, unless the comma belongs to an own tag's name that is still being typed. Everything works by keyboard:
/// Up/Down move over the entries (headings are skipped), Escape on an empty field closes and returns to the button.
/// <para>The entries other than tags come from the page (<see cref="Others"/>), which leaves out what its filter already holds. The
/// archive uses the tags-only form, <see cref="TagFilterPicker"/>: one component, no headings, no comma, its texts unchanged.</para>
/// </summary>
internal class FilterTermPicker : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private static readonly Lazy<Geometry> Plus = new(() => Geometry.Parse("M 8,3 L 8,13 M 3,8 L 13,8"));

    // Fixed layout measures of the popover (dip), not user limits and therefore not settings: the list scrolls beyond
    // 220 so the popover stays small however many entries exist, a name is trimmed at 320, and the popover keeps 280-360 wide
    // so the search box and the include/exclude row stay readable.
    private readonly bool _tagsOnly;
    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly SearchBox _search;
    private readonly ListBox _list;
    private readonly RadioButton _asInclude;
    private readonly RadioButton _asExclude;
    private readonly TextBlock _empty;
    private readonly TextBlock _hint;
    private readonly TextBlock _error;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _plus = new() { Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private bool _geometryApplied, _refilling;
    private MediaFilterField? _onlyField;

    /// <summary>The full picker over every group (the media page's "+ Filter").</summary>
    public FilterTermPicker() : this(tagsOnly: false, "FilterTermPicker") { }

    /// <summary><paramref name="partPrefix"/> names the parts (search field, list, choices) so tests and smokes find them.</summary>
    protected FilterTermPicker(bool tagsOnly, string partPrefix)
    {
        _tagsOnly = tagsOnly;
        _search = new SearchBox { Name = partPrefix + "Search", MinWidth = 260 };
        _list = new ListBox { Name = partPrefix + "List", MaxHeight = 220, SelectionMode = SelectionMode.Single };
        _asInclude = new RadioButton { Name = partPrefix + "Include", IsChecked = true };
        _asExclude = new RadioButton { Name = partPrefix + "Exclude" };
        _empty = new TextBlock { Name = partPrefix + "Empty", TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "muted" } };
        _hint = new TextBlock { Name = partPrefix + "Hint", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        _error = new TextBlock { Name = partPrefix + "Error", TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "workspace-error" } };
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        if (tagsOnly)
        {
            Classes.Add("subtle");
            MinHeight = 32;
        }
        else WorkspaceChips.MarkOffered(this); // "+ Filter" is the offered chip at the end of the chip row (chip-available)
        _plus[!Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Text");
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = tagsOnly ? 8 : 4, Children = { _plus, _label } };
        string group = "tag-filter-mode-" + Guid.NewGuid().ToString("N");
        _asInclude.GroupName = _asExclude.GroupName = group;
        _list.DataTemplates.Add(new FuncDataTemplate<FilterTermGroup>((item, _) =>
            new TextBlock { Text = item?.Name ?? string.Empty, Classes = { "workspace-meta" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 }));
        _list.DataTemplates.Add(new FuncDataTemplate<FilterTermChoice>((item, _) =>
            new TextBlock { Text = item?.Label ?? string.Empty, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 }));
        _list.ContainerPrepared += OnContainerPrepared;
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
        _flyout.Closed += (_, _) => { _onlyField = null; Refresh(); };
        _search.QueryChanged += (_, _) => { Refill(); PendingChanged?.Invoke(this, EventArgs.Empty); };
        _search.KeyDown += OnSearchKeyDown;
        _search.AddHandler(TextInputEvent, OnSearchTextInput, RoutingStrategies.Tunnel);
        _asInclude.IsCheckedChanged += (_, _) => { if (_flyout.IsOpen) Refill(); };
        _list.AddHandler(TappedEvent, OnListTapped, RoutingStrategies.Bubble);
        ApplyTexts();
    }

    /// <summary>True for the archive's form: own tags only, no headings, no comma, the <c>tags.filter.*</c> texts.</summary>
    internal bool TagsOnly => _tagsOnly;

    /// <summary>The own tags as published (null while the store is unavailable).</summary>
    internal Func<PrivateTagStoreSnapshot?> Snapshot { get; set; } = () => null;

    /// <summary>The tags the filter already holds (included or excluded); they are not offered again.</summary>
    internal Func<IReadOnlySet<TagId>> Used { get; set; } = () => new HashSet<TagId>();

    /// <summary>The entries of every other group the page can add now (it leaves out what its filter holds). Unused in the tags-only form.</summary>
    internal Func<IReadOnlyList<FilterTermChoice>> Others { get; set; } = () => Array.Empty<FilterTermChoice>();

    /// <summary>Adds a term (its <see cref="MediaFilterTerm.Exclude"/> says how); null when added, else the localized reason it was refused.</summary>
    internal Func<MediaFilterTerm, string?> PickedTerm { get; set; } = _ => null;

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
    /// True while <see cref="KeepQueryOnDismiss"/> is on and the typed text names at least one entry that could still be added: the
    /// entry is not finished, because the app cannot guess Include or Exclude for it.
    /// </summary>
    internal bool HasPending
    {
        get
        {
            if (!KeepQueryOnDismiss) return false;
            string query = ParseQuery(_search.Text, false).Query;
            return TagSuggestionIndex.QueryKey(query) != null && Compose(query, exclude: false).Count > 0;
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
    /// <summary>The field the popover was opened for ("Edit…" on a chip), or null when it shows every group.</summary>
    internal MediaFilterField? OnlyField => _onlyField;

    /// <summary>The entries the list shows now (headings left out), in their order.</summary>
    internal IReadOnlyList<FilterTermChoice> ShownChoices =>
        (_list.ItemsSource as IEnumerable<object> ?? Array.Empty<object>()).OfType<FilterTermChoice>().ToArray();

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

    /// <summary>The position of a field's group in the list (tags first, the flags last).</summary>
    internal static int GroupOrder(MediaFilterField field) => field switch
    {
        MediaFilterField.Tag => 0,
        MediaFilterField.Type => 1,
        MediaFilterField.Sound => 2,
        MediaFilterField.Source => 3,
        MediaFilterField.Board => 4,
        MediaFilterField.Rating => 5,
        MediaFilterField.Favorite or MediaFilterField.Missing or MediaFilterField.Untagged or MediaFilterField.Queue => 6,
        MediaFilterField.Thread => 7,
        _ => 8
    };

    /// <summary>The heading of a field's group (the field's own label; the three flags share "Other filters").</summary>
    internal static string GroupName(MediaFilterField field) => Texts.T(field switch
    {
        MediaFilterField.Tag => "media.filter.group.tag",
        MediaFilterField.Type => "media.filter.kind",
        MediaFilterField.Sound => "media.filter.sound",
        MediaFilterField.Source => "media.filter.source",
        MediaFilterField.Board => "media.filter.board",
        MediaFilterField.Rating => "media.filter.rating",
        MediaFilterField.Thread => "media.filter.onlyThread",
        MediaFilterField.Name or MediaFilterField.In => "media.filter.name", // the picker offers neither: they live in the search field
        _ => "media.filter.group.flags"
    });

    /// <summary>
    /// The entries of the other groups that match the typed text, ranked within their group: the label starts with it, then a word of
    /// the label starts with it ("g" finds "/g/ (4chan)"), then the label contains it, then the group's heading has a word that starts
    /// with it ("type" lists every type). Case and accents are ignored. An empty text keeps every entry in the page's order.
    /// </summary>
    internal static IReadOnlyList<FilterTermChoice> Match(IEnumerable<FilterTermChoice> choices, string? query)
    {
        string key = (query ?? string.Empty).Trim();
        List<FilterTermChoice> list = choices.ToList();
        if (key.Length == 0) return list.OrderBy(choice => GroupOrder(choice.Field)).ToList(); // stable: the page's order within a group
        return list.Select((choice, index) => (Choice: choice, Rank: Rank(choice, key), Index: index)).Where(item => item.Rank >= 0)
            .OrderBy(item => GroupOrder(item.Choice.Field)).ThenBy(item => item.Rank).ThenBy(item => item.Index)
            .Select(item => item.Choice).ToList();
    }

    private static int Rank(FilterTermChoice choice, string key)
    {
        CompareInfo compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        string label = choice.Label;
        if (compare.IsPrefix(label, key, options)) return 0;
        if (WordStarts(label, key)) return 1;
        if (compare.IndexOf(label, key, options) >= 0) return 2;
        if (WordStarts(GroupName(choice.Field), key) || compare.IsPrefix(MediaFilterTerm.KeyOf(choice.Field), key, options)) return 3;
        return -1;

        bool WordStarts(string text, string start)
        {
            for (int i = 0; i < text.Length; i++)
                if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && char.IsLetterOrDigit(text[i])
                    && compare.IsPrefix(text.AsSpan(i), start, options)) return true;
            return false;
        }
    }

    /// <summary>The list as shown: each group's heading (the full picker only) followed by its entries.</summary>
    internal static IReadOnlyList<object> Arrange(IReadOnlyList<FilterTermChoice> choices, bool headings)
    {
        if (!headings) return choices.Cast<object>().ToArray();
        List<object> items = new(choices.Count + 8);
        int? group = null;
        foreach (FilterTermChoice choice in choices)
        {
            int order = GroupOrder(choice.Field);
            if (order != group) { items.Add(new FilterTermGroup(order, GroupName(choice.Field))); group = order; }
            items.Add(choice);
        }
        return items;
    }

    /// <summary>
    /// The entries for the typed text: the own tags (ranked as in the tag fields), then the page's other entries (<see cref="Match"/>).
    /// While the popover is opened for one field only that field's group is listed; while the entry would exclude, fields that cannot exclude
    /// are left out.
    /// </summary>
    private IReadOnlyList<FilterTermChoice> Compose(string query, bool exclude)
    {
        List<FilterTermChoice> choices = new();
        if (_onlyField is null or MediaFilterField.Tag)
            choices.AddRange(Choices(Snapshot(), Used(), query).Where(item => item.Id != null)
                .Select(item => new FilterTermChoice(MediaFilterField.Tag, item.Name.Display, MediaFilterTerm.Tag(item.Id!.Value.Value), item)));
        if (!_tagsOnly)
            choices.AddRange(Match(Others().Where(choice => (_onlyField is not { } only || GroupOrder(choice.Field) == GroupOrder(only))
                && (!exclude || MediaFilterTerm.CanExclude(choice.Field))), query));
        return choices;
    }

    /// <summary>Enabled only while an entry is left to add; says why otherwise. Call when the tags or the filter change.</summary>
    internal void Refresh()
    {
        PrivateTagStoreSnapshot? snapshot = Snapshot();
        string name;
        string? reason;
        if (_tagsOnly)
        {
            name = Texts.T("tags.filter.add");
            reason = snapshot == null ? Texts.T("tags.filter.unavailable")
                : snapshot.Tags.Count == 0 ? Texts.T("tags.filter.none")
                : Choices(snapshot, Used(), null).Count == 0 ? Texts.T("tags.filter.add.allUsed") : null;
            _label.Text = name;
        }
        else
        {
            name = Texts.T("media.filter.add");
            reason = Choices(snapshot, Used(), null).Count == 0 && Others().Count == 0 ? Texts.T("media.filter.add.allUsed") : null;
            _label.Text = Texts.T("media.filter.addChip");
        }
        IsEnabled = reason == null;
        ToolTip.SetTip(this, reason == null ? name : name + " · " + reason);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetHelpText(this, reason ?? (_tagsOnly ? string.Empty : Texts.T("media.filter.add.help")));
    }

    internal void ApplyTexts()
    {
        string search = Texts.T(_tagsOnly ? "tags.filter.add.search" : "media.filter.add.search");
        _search.PlaceholderText = search;
        AutomationProperties.SetName(_list, search);
        _asInclude.Content = Texts.T("tags.filter.include");
        _asExclude.Content = Texts.T("tags.filter.exclude");
        AutomationProperties.SetName(_asInclude, Texts.T("tags.filter.add.mode") + ": " + Texts.T("tags.filter.include"));
        AutomationProperties.SetName(_asExclude, Texts.T("tags.filter.add.mode") + ": " + Texts.T("tags.filter.exclude"));
        ToolTip.SetTip(_asInclude, IncludeHint); ToolTip.SetTip(_asExclude, ExcludeHint);
        AutomationProperties.SetHelpText(_asInclude, IncludeHint ?? string.Empty); AutomationProperties.SetHelpText(_asExclude, ExcludeHint ?? string.Empty);
        _hint.Text = Texts.T(_tagsOnly ? "tags.filter.add.hint" : "media.filter.add.hint");
        Refresh();
    }

    /// <summary>Opens the popover (the button's click does the same).</summary>
    internal void Open() { if (IsEnabled) _flyout.ShowAt(this); }

    /// <summary>Opens the popover on one field's entries ("Edit…" on a chip); the next opening shows every group again.</summary>
    internal void OpenFor(MediaFilterField field)
    {
        if (!IsEnabled && !IsEffectivelyVisible) return;
        _onlyField = field;
        if (_flyout.IsOpen) Refill(); else _flyout.ShowAt(this);
    }

    internal void Close() => _flyout.Hide();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_geometryApplied) return;
        _geometryApplied = true;
        _plus.Data = Plus.Value;
    }

    /// <summary>
    /// What a pick does with the entry. The full picker hands the term to <see cref="PickedTerm"/>; the tags-only form overrides this
    /// with its tag callback. Null when added, else the localized reason it was refused.
    /// </summary>
    protected virtual string? OnPicked(FilterTermChoice choice, bool exclude) => PickedTerm(choice.Term with { Exclude = exclude });

    private bool ExcludeMode => ParseQuery(_search.Text, _asExclude.IsChecked == true).Exclude;

    private void Refill()
    {
        if (_refilling) return;
        _refilling = true;
        try
        {
            (string query, bool exclude) = ParseQuery(_search.Text, _asExclude.IsChecked == true);
            PrivateTagStoreSnapshot? snapshot = Snapshot();
            IReadOnlyList<FilterTermChoice> choices = Compose(query, exclude);
            IReadOnlyList<object> items = Arrange(choices, headings: !_tagsOnly);
            _list.ItemsSource = items;
            _list.IsVisible = choices.Count > 0;
            _list.SelectedIndex = choices.Count > 0 ? NextChoice(items, -1, 1) : -1;
            _empty.IsVisible = choices.Count == 0;
            bool typed = TagSuggestionIndex.QueryKey(query) != null;
            _empty.Text = _tagsOnly
                ? snapshot == null ? Texts.T("tags.filter.unavailable")
                    : snapshot.Tags.Count == 0 ? Texts.T("tags.filter.none")
                    : !typed ? Texts.T("tags.filter.add.allUsed")
                    : Texts.T("tags.filter.add.noMatch", query.Trim())
                : typed ? Texts.T("media.filter.add.noMatch", query.Trim()) : Texts.T("media.filter.add.allUsed");
        }
        finally { _refilling = false; }
    }

    // The index of the next entry (not a heading) from `from` in `step` direction, or `from` when there is none.
    private static int NextChoice(IReadOnlyList<object> items, int from, int step)
    {
        for (int index = from + step; index >= 0 && index < items.Count; index += step)
            if (items[index] is FilterTermChoice) return index;
        return from;
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // Containers are reused: a heading row is shown but neither selectable nor focusable, an entry row is both again.
        object? item = _list.ItemsSource is IReadOnlyList<object> items && e.Index >= 0 && e.Index < items.Count ? items[e.Index] : null;
        bool heading = item is FilterTermGroup;
        e.Container.IsEnabled = !heading;
        e.Container.Focusable = !heading;
        AutomationProperties.SetName(e.Container, item switch
        {
            FilterTermChoice choice when !_tagsOnly => GroupName(choice.Field) + ": " + choice.Label,
            FilterTermChoice choice => choice.Label,
            FilterTermGroup group => group.Name,
            _ => string.Empty
        });
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        IReadOnlyList<object> items = _list.ItemsSource as IReadOnlyList<object> ?? Array.Empty<object>();
        switch (e.Key)
        {
            case Key.Down or Key.Up when e.KeyModifiers == KeyModifiers.None && ShownChoices.Count > 0:
                _list.SelectedIndex = NextChoice(items, _list.SelectedIndex, e.Key == Key.Down ? 1 : -1);
                _list.ScrollIntoView(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift:
                e.Handled = true;
                PickSelected(e.KeyModifiers == KeyModifiers.Shift, keepOpen: false);
                break;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None && !_search.HasText:
                e.Handled = true;
                _flyout.Hide();
                Focus(NavigationMethod.Tab);
                break;
        }
    }

    // A comma adds the highlighted entry and keeps the popover open for the next one (the full picker only). It is typed as text
    // instead when an own tag's name continues with it ("a,b"), so such names stay reachable.
    private void OnSearchTextInput(object? sender, TextInputEventArgs e)
    {
        if (_tagsOnly || e.Text != ",") return;
        string query = ParseQuery(_search.Text, false).Query;
        if (CommaContinuesATagName(Snapshot(), query)) return;
        e.Handled = true;
        PickSelected(shift: false, keepOpen: true);
    }

    /// <summary>Whether an own tag's name starts with the typed text followed by a comma (then the comma is part of the name).</summary>
    internal static bool CommaContinuesATagName(PrivateTagStoreSnapshot? snapshot, string? query)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(query)) return false;
        string start = query.Trim() + ",";
        return snapshot.Tags.Any(tag => tag.Name.Display.StartsWith(start, StringComparison.CurrentCultureIgnoreCase));
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source || source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault() is not { DataContext: FilterTermChoice choice }) return;
        e.Handled = true;
        Pick(choice, e.KeyModifiers.HasFlag(KeyModifiers.Shift), keepOpen: false);
    }

    private void PickSelected(bool shift, bool keepOpen)
    {
        if (_list.SelectedItem is FilterTermChoice choice) Pick(choice, shift, keepOpen);
    }

    private void Pick(FilterTermChoice choice, bool shift, bool keepOpen)
    {
        bool exclude = shift || ExcludeMode;
        string? refusal = exclude && !MediaFilterTerm.CanExclude(choice.Field)
            ? Texts.T("media.filter.add.cannotExclude", choice.Label)
            : OnPicked(choice, exclude);
        if (refusal != null)
        {
            _error.Text = refusal;
            _error.IsVisible = true;
            return;
        }
        _error.IsVisible = false;
        if (keepOpen && _flyout.IsOpen)
        {
            // Next entry: the field starts empty (the dash, if any, was for this one) and the list offers what is left.
            _search.SetTextSilently(string.Empty);
            _search.CancelPendingQuery();
            Refill();
            PendingChanged?.Invoke(this, EventArgs.Empty);
            _search.Focus();
            return;
        }
        if (KeepQueryOnDismiss) ClearPending();
        _flyout.Hide();
        Focus(NavigationMethod.Tab);
    }
}
