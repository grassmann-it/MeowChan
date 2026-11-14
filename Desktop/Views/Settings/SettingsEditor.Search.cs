using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

/// <summary>
/// The search of the settings page: a field in the page header and, below it, the ranked hits of every group. The editors,
/// their groups and their text stay where they are; the search reads them once per language (labels, hints, keys), lists
/// what matches, and jumps to the chosen setting: it opens its group, unfolds collapsed sections and marks the row.
/// Matching is in <see cref="SettingsSearchMatcher"/>.
/// </summary>
public partial class SettingsEditor
{
    private sealed record SearchTarget(string CategoryId, Control Element, IReadOnlyList<Expander> Expanders);

    private sealed class SearchEntryBuilder
    {
        public required Control Element;
        public required string CategoryId;
        public required string Group;
        public IReadOnlyList<Expander> Expanders = Array.Empty<Expander>();
        public string Label = string.Empty;
        public readonly List<string> Hints = new();
        public string? Key;
    }

    private SearchBox? _searchBox;
    private Border? _searchPanel;
    private ListBox? _searchList;
    private TextBlock? _searchNone, _searchKeys, _searchCurrent;
    private SettingsSearchIndex? _searchIndex;
    private Dictionary<string, SearchTarget> _searchTargets = new(StringComparer.Ordinal);
    private bool _searchIndexDirty = true;
    private bool _searchSuppressSelection;
    private IReadOnlyList<SettingsSearchHit> _searchHits = Array.Empty<SettingsSearchHit>();
    private Control? _searchMarked;

    private static readonly string[] HitClasses = { "settings-search-hit" };

    private void InitializeSearch()
    {
        _searchBox = new SearchBox { Name = "SettingsSearchBox", MaxLength = 100, Width = 460, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        _searchBox.QueryChanged += (_, _) => OnSearchQueryChanged();
        _searchBox.ExitRequested += (_, e) => { FocusSettingsContent(); e.Handled = true; };
        _searchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchBox.GotFocus += (_, _) => { if (_searchHits.Count > 0 && _searchBox.HasText) ShowSearchPanel(); };
        _searchBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed || _searchBox == null || _searchBox.IsKeyboardFocusWithin || _searchPanel?.IsPointerOver == true) return;
            if (_searchPanel != null) _searchPanel.IsVisible = false;
        }, DispatcherPriority.Input);

        _searchList = new ListBox
        {
            Name = "SettingsSearchResults",
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            MaxHeight = 232,
            Focusable = false,
            ItemTemplate = new FuncDataTemplate<SettingsSearchHit>((hit, _) => hit == null ? new Control() : BuildSearchRow(hit), supportsRecycling: false)
        };
        _searchList.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(Avalonia.Input.InputElement.FocusableProperty, false), new Setter(Layoutable.MinHeightProperty, 0d) }
        });
        _searchList.SelectionChanged += OnSearchSelectionChanged;
        _searchList.Tapped += OnSearchListTapped;
        AutomationProperties.SetLiveSetting(_searchList, AutomationLiveSetting.Polite);
        _searchList.SelectionChanged += (_, _) => AnnounceSearchSelection();

        _searchNone = new TextBlock { Name = "SettingsSearchNone", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Space(8), Space(4)), IsVisible = false };
        // Avalonia has no active-descendant link from the field to the list: the hit the arrow keys have reached is spoken by this
        // polite line (and mirrored in the field's help text), "3 of 12: Download folder, Downloads".
        _searchCurrent = new TextBlock { Name = "SettingsSearchCurrent", TextWrapping = TextWrapping.Wrap, IsVisible = false,
            Margin = new Thickness(Space(8), Space(2), Space(8), 0) };
        _searchCurrent[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Meta");
        _searchCurrent[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
        AutomationProperties.SetLiveSetting(_searchCurrent, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_searchNone, AutomationLiveSetting.Polite);
        _searchKeys = new TextBlock { Name = "SettingsSearchKeys", Margin = new Thickness(Space(8), Space(4), Space(8), Space(2)), TextWrapping = TextWrapping.Wrap };
        _searchKeys[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Meta");
        _searchKeys[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");

        var content = new StackPanel { Spacing = Space(2) };
        content.Children.Add(_searchList);
        content.Children.Add(_searchNone);
        content.Children.Add(_searchCurrent);
        content.Children.Add(_searchKeys);
        // The one in-window overlay look (surface, edge, radius); its width is the search field's.
        _searchPanel = new Border
        {
            Name = "SettingsSearchPanel",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Thickness(Space(2)),
            IsVisible = false,
            Child = content
        };
        _searchPanel.Classes.Add("workspace-overlay");
        _searchPanel.Bind(Layoutable.WidthProperty, _searchBox.GetObservable(Layoutable.WidthProperty));

        WorkViewElement.SetId(_searchBox, "settings.search");
        SettingsHeader.Children.Add(_searchBox);
        SettingsHeader.Children.Add(_searchPanel);
        ApplySearchTexts();
    }

    /// <summary>Primary+F: puts the caret in the search field, with its text selected.</summary>
    public void FocusSearch()
    {
        if (_searchBox == null || _isClosed) return;
        _searchBox.Focus(NavigationMethod.Tab);
        _searchBox.SelectAll();
    }

    /// <summary>
    /// Runs <paramref name="query"/> and commits its first hit as Enter does (the work-views smoke: the search finds every setting,
    /// also in a collapsed group, and opens what it jumps to). Null when nothing matches.
    /// </summary>
    internal (string Label, string CategoryId, int Groups, bool AllOpen)? ProbeSearchJump(string query)
    {
        if (_searchBox == null || _isClosed) return null;
        _searchBox.Text = query;
        OnSearchQueryChanged();
        if (_searchHits.Count == 0 || !_searchTargets.TryGetValue(_searchHits[0].Document.Id, out SearchTarget? target)) return null;
        CommitSearchHit(_searchHits[0]);
        return (_searchHits[0].Document.Label, target.CategoryId, target.Expanders.Count, target.Expanders.All(expander => expander.IsExpanded));
    }

    /// <summary>A language change or a rebuilt navigation: texts and the read index are stale.</summary>
    private void SearchLanguageRefreshed()
    {
        _searchIndexDirty = true;
        RefreshGroupHeaders();
        if (_searchBox == null) return;
        ApplySearchTexts();
        if (_searchBox.HasText) OnSearchQueryChanged();
    }

    private void ApplySearchTexts()
    {
        if (_searchBox == null) return;
        _searchBox.PlaceholderText = Texts.T("settings.search.placeholder");
        ToolTip.SetTip(_searchBox, CommandBindings.Tooltip("input.focus-settings-search"));
        if (_searchList != null) AutomationProperties.SetName(_searchList, Texts.T("settings.search.results"));
        if (_searchKeys != null) _searchKeys.Text = Texts.T("settings.search.keys");
    }

    private void OnSearchQueryChanged()
    {
        if (_searchBox == null || _searchList == null || _searchPanel == null || _searchNone == null || _isClosed) return;
        string query = _searchBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchHits = Array.Empty<SettingsSearchHit>();
            SetSearchItems(_searchHits);
            _searchPanel.IsVisible = false;
            _searchBox.ResultText = null;
            ShowSearchAnnouncement(null);
            MarkSearchTarget(null);
            return;
        }
        EnsureSearchIndex();
        _searchHits = SettingsSearchMatcher.Search(_searchIndex!, query);
        _searchNone.IsVisible = _searchHits.Count == 0;
        _searchNone.Text = Texts.T("settings.search.none", query.Trim()) + " " + Texts.T("settings.search.noneHint");
        _searchList.IsVisible = _searchHits.Count > 0;
        _searchKeys!.IsVisible = _searchHits.Count > 0;
        _searchBox.ResultText = _searchHits.Count > 0 ? Texts.Count("search.count", _searchHits.Count) : null;
        SetSearchItems(_searchHits);
        ShowSearchPanel();
        if (_searchHits.Count == 0) { ShowSearchAnnouncement(SettingsSearchAnnouncement.None(query)); MarkSearchTarget(null); return; }
        _searchList.SelectedIndex = 0;
        AnnounceSearchSelection();
    }

    // The current hit as the sentence a screen reader hears; the empty result is announced by its own text.
    private void AnnounceSearchSelection()
    {
        if (_searchSuppressSelection || _searchList == null || _searchHits.Count == 0) return;
        if (_searchList.SelectedItem is not SettingsSearchHit hit) return;
        int position = _searchList.SelectedIndex + 1;
        ShowSearchAnnouncement(SettingsSearchAnnouncement.Current(position, _searchHits.Count, hit.Document.Label, hit.Document.Group));
    }

    private void ShowSearchAnnouncement(string? text)
    {
        if (_searchCurrent == null || _searchBox == null) return;
        string value = text ?? string.Empty;
        if (!string.Equals(_searchCurrent.Text, value, StringComparison.Ordinal)) _searchCurrent.Text = value;
        _searchCurrent.IsVisible = _searchHits.Count > 0 && value.Length > 0;
        AutomationProperties.SetHelpText(_searchBox, value.Length > 0 ? value : null);
        if (_searchList != null) AutomationProperties.SetHelpText(_searchList, _searchHits.Count > 0 && value.Length > 0 ? value : null);
    }

    private void SetSearchItems(IReadOnlyList<SettingsSearchHit> hits)
    {
        _searchSuppressSelection = true;
        try { _searchList!.ItemsSource = hits; }
        finally { _searchSuppressSelection = false; }
    }

    private void ShowSearchPanel()
    {
        if (_searchPanel == null || _searchBox == null || !_searchBox.HasText) return;
        // The list never takes more than about a third of the page, so the settings below it stay in view.
        double room = Bounds.Height > 0 ? Bounds.Height * 0.38 : 232;
        if (_searchList != null) _searchList.MaxHeight = Math.Clamp(room, 112, 300);
        _searchPanel.IsVisible = true;
    }

    private void OnSearchSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_searchSuppressSelection || _searchList?.SelectedItem is not SettingsSearchHit hit) return;
        if (_searchTargets.TryGetValue(hit.Document.Id, out SearchTarget? target)) JumpToSearchTarget(target, focus: false);
    }

    private void OnSearchListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual visual) return;
        ListBoxItem? item = visual.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (item?.DataContext is SettingsSearchHit hit) CommitSearchHit(hit);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_searchList == null || e.KeyModifiers != KeyModifiers.None) return;
        int count = _searchHits.Count;
        switch (e.Key)
        {
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when count > 0:
                e.Handled = true;
                if (_searchPanel is { IsVisible: false }) { ShowSearchPanel(); return; }
                int step = e.Key is Key.PageDown or Key.PageUp ? 5 : 1;
                int direction = e.Key is Key.Down or Key.PageDown ? 1 : -1;
                int next = Math.Clamp(Math.Max(_searchList.SelectedIndex, 0) + direction * step, 0, count - 1);
                _searchList.SelectedIndex = next;
                _searchList.ScrollIntoView(next);
                break;
            case Key.Enter when count > 0:
                e.Handled = true;
                CommitSearchHit(_searchList.SelectedItem as SettingsSearchHit ?? _searchHits[0]);
                break;
        }
    }

    private void CommitSearchHit(SettingsSearchHit hit)
    {
        if (!_searchTargets.TryGetValue(hit.Document.Id, out SearchTarget? target)) return;
        JumpToSearchTarget(target, focus: true);
        if (_searchPanel != null) _searchPanel.IsVisible = false;
    }

    // The group opens, collapsed sections unfold, the row is marked and scrolled into view; a commit also moves focus into it.
    private void JumpToSearchTarget(SearchTarget target, bool focus)
    {
        if (_isClosed) return;
        SelectCategory(target.CategoryId);
        foreach (Expander expander in target.Expanders) expander.IsExpanded = true;
        MarkSearchTarget(target.Element);
        void Reveal()
        {
            if (_isClosed) return;
            target.Element.BringIntoView();
            if (focus) FocusSearchTarget(target.Element);
        }
        // The group may have just become visible: scrolling and focus need its layout.
        Dispatcher.UIThread.Post(() => { Reveal(); Dispatcher.UIThread.Post(() => { if (!_isClosed) target.Element.BringIntoView(); }, DispatcherPriority.Background); }, DispatcherPriority.Loaded);
    }

    // Spacing tokens (Workspace.Space.N), read once; the number itself is the fallback when the resource is missing.
    private static double Space(int token) =>
        Application.Current?.TryGetResource("Workspace.Space." + token, null, out object? value) == true && value is double size ? size : token;

    /// <summary>The marked row and the open list belong to a visit of the page: leaving it clears them (the typed text stays).</summary>
    internal void EndSearchVisit()
    {
        MarkSearchTarget(null);
        if (_searchPanel != null) _searchPanel.IsVisible = false;
    }

    private void MarkSearchTarget(Control? element)
    {
        if (ReferenceEquals(_searchMarked, element)) return;
        _searchMarked?.Classes.Remove(HitClasses[0]);
        _searchMarked = element;
        element?.Classes.Add(HitClasses[0]);
    }

    private static void FocusSearchTarget(Control element)
    {
        Control? input = element.GetSelfAndLogicalDescendants().OfType<Control>()
            .FirstOrDefault(control => IsSettingInput(control) && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
        (input ?? element).Focus(NavigationMethod.Tab);
    }

    // Leaving the field with Escape (it is already empty): the first editor of the open group takes the focus.
    private void FocusSettingsContent()
    {
        Control? page = CategoryPages().FirstOrDefault(item => item.Id == _selectedCategoryId).Page;
        Control? input = page?.GetSelfAndLogicalDescendants().OfType<Control>()
            .FirstOrDefault(control => IsSettingInput(control) && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
        if (input != null) input.Focus(NavigationMethod.Tab);
        else OpenSectionNavigation();
    }

    private Control BuildSearchRow(SettingsSearchHit hit)
    {
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        AppendHighlighted(label, hit.Document.Label, hit.LabelRanges);
        var group = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Text = hit.Document.Group,
            Margin = new Thickness(Space(12), 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, MaxWidth = 190 };
        group[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Meta");
        group[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(Space(2), Space(2) / 2) };
        grid.Children.Add(label);
        Grid.SetColumn(group, 1);
        grid.Children.Add(group);
        if (!string.IsNullOrWhiteSpace(hit.Document.Hint))
        {
            var hint = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            hint[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Meta");
            hint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
            AppendHighlighted(hint, hit.Document.Hint, hit.HintRanges);
            Grid.SetRow(hint, 1);
            Grid.SetColumnSpan(hint, 2);
            grid.Children.Add(hint);
        }
        AutomationProperties.SetName(grid, hit.Document.Label + ", " + hit.Document.Group);
        return grid;
    }

    private void AppendHighlighted(TextBlock block, string text, IReadOnlyList<SettingsTextRange> ranges)
    {
        IBrush? accent = this.TryFindResource("Workspace.Accent", ActualThemeVariant, out object? found) ? found as IBrush : null;
        InlineCollection inlines = block.Inlines ??= new InlineCollection();
        int at = 0;
        foreach (SettingsTextRange range in ranges)
        {
            if (range.Start < at || range.Start + range.Length > text.Length) continue;
            if (range.Start > at) inlines.Add(new Run(text[at..range.Start]));
            var run = new Run(text.Substring(range.Start, range.Length)) { FontWeight = FontWeight.SemiBold };
            if (accent != null) run.Foreground = accent;
            inlines.Add(run);
            at = range.Start + range.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    // ---- reading the page into the index ----

    private void EnsureSearchIndex()
    {
        if (_searchIndex != null && !_searchIndexDirty) return;
        var documents = new List<SettingsSearchDocument>();
        var targets = new Dictionary<string, SearchTarget>(StringComparer.Ordinal);
        foreach (SearchEntryBuilder entry in ScanSearchEntries())
        {
            string id = "s" + documents.Count;
            string hint = string.Join(" · ", entry.Hints.Distinct(StringComparer.Ordinal));
            var english = new List<string>();
            if (!string.Equals(Texts.Language, "en", StringComparison.OrdinalIgnoreCase))
            {
                english.Add(string.Join(' ', Texts.EnglishEquivalents(entry.Label)));
                foreach (string text in entry.Hints) english.AddRange(Texts.EnglishEquivalents(text));
            }
            documents.Add(new SettingsSearchDocument(id, entry.Label, hint, entry.Group, entry.Key,
                SettingsSearchAliases.For(entry.Key, entry.CategoryId), english));
            targets[id] = new SearchTarget(entry.CategoryId, entry.Element, entry.Expanders);
        }
        _searchTargets = targets;
        _searchIndex = SettingsSearchMatcher.Build(documents);
        _searchIndexDirty = false;
    }

    private List<SearchEntryBuilder> ScanSearchEntries()
    {
        var result = new List<SearchEntryBuilder>();
        foreach ((string id, Control page) in CategoryPages())
        {
            if (page is ShortcutEditor)
            {
                string title = Texts.T("shortcuts.title");
                result.Add(new SearchEntryBuilder { Element = page, CategoryId = id, Group = SettingsTitle, Label = title, Key = "shortcuts",
                    Expanders = ((ShortcutEditor)page).MoreGroup is { } shortcutGroup ? [shortcutGroup] : Array.Empty<Expander>() });
                continue;
            }
            if (page is not Panel panel) continue;
            string pageTitle = panel.Children.OfType<TextBlock>().FirstOrDefault(IsSearchHeading)?.Text ?? SettingsTitle;
            ScanFlow(panel, id, pageTitle, pageTitle, Array.Empty<Expander>(), result, pageRoot: true);
        }
        return result;
    }

    private void ScanFlow(Panel panel, string categoryId, string pageTitle, string group, IReadOnlyList<Expander> expanders,
        List<SearchEntryBuilder> output, bool pageRoot)
    {
        SearchEntryBuilder? last = null;
        bool firstHeading = pageRoot;
        foreach (Control child in panel.Children.ToArray())
        {
            if (child is TextBlock heading && IsSearchHeading(heading))
            {
                string text = heading.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) continue;
                bool pageHeading = firstHeading;
                firstHeading = false;
                if (!pageHeading) group = pageTitle + " › " + text;
                last = new SearchEntryBuilder { Element = heading, CategoryId = categoryId, Expanders = expanders, Label = text,
                    Group = pageHeading ? SettingsTitle : pageTitle };
                output.Add(last);
                continue;
            }
            if (child is Expander expander && expander.Content is Panel inner && IsComposite(inner))
            {
                string header = expander.Header as string ?? string.Empty;
                // The "More settings" groups of the work views are containers, not settings: their content is listed, they are not.
                if (!string.IsNullOrWhiteSpace(header) && WorkViewElement.GetId(expander) == null)
                    output.Add(new SearchEntryBuilder { Element = expander, CategoryId = categoryId, Group = group, Label = header,
                        Expanders = expanders });
                ScanFlow(inner, categoryId, pageTitle, group, expanders.Append(expander).ToArray(), output, pageRoot: false);
                last = null;
                continue;
            }
            if (child is StackPanel nested && IsComposite(nested))
            {
                ScanFlow(nested, categoryId, pageTitle, group, expanders, output, pageRoot: false);
                last = null;
                continue;
            }
            if (child is TextBlock hint)
            {
                // Text under a row is that row's hint; under a heading it is the group's introduction.
                if (last != null && !string.IsNullOrWhiteSpace(hint.Text) && !IsStatusText(hint)) last.Hints.Add(hint.Text!);
                continue;
            }
            SearchEntryBuilder? leaf = BuildSearchLeaf(child, categoryId, group, expanders);
            if (leaf != null) { output.Add(leaf); last = leaf; }
        }
    }

    private static SearchEntryBuilder? BuildSearchLeaf(Control unit, string categoryId, string group, IReadOnlyList<Expander> expanders)
    {
        Control[] parts = unit.GetSelfAndLogicalDescendants().OfType<Control>().ToArray();
        Control[] inputs = parts.Where(IsSettingInput).ToArray();
        string? label = inputs.Select(AutomationProperties.GetName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        TextBlock[] texts = parts.OfType<TextBlock>().Where(block => !IsStatusText(block) && !string.IsNullOrWhiteSpace(block.Text)).ToArray();
        label ??= texts.FirstOrDefault(block => block.Classes.Contains("workspace-field-label"))?.Text;
        label ??= parts.OfType<ContentControl>().Select(control => control.Content as string).FirstOrDefault(content => !string.IsNullOrWhiteSpace(content));
        label ??= (unit as Expander)?.Header as string;
        label ??= texts.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(label)) return null;
        var entry = new SearchEntryBuilder { Element = unit, CategoryId = categoryId, Group = group, Label = label, Expanders = expanders };
        if (unit is Expander own) entry.Expanders = expanders.Append(own).ToArray();
        foreach (TextBlock block in texts)
            if (!string.Equals(block.Text, label, StringComparison.Ordinal)) entry.Hints.Add(block.Text!);
        foreach (Control input in inputs)
            if (AutomationProperties.GetHelpText(input) is { Length: > 0 } help && !string.Equals(help, label, StringComparison.Ordinal)) entry.Hints.Add(help);
        string? name = inputs.Select(control => control.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? unit.Name;
        entry.Key = name != null && name.EndsWith("Input", StringComparison.Ordinal) && name.Length > 5 ? name[..^5] : name;
        return entry;
    }

    private static bool IsSettingInput(Control control) =>
        control is TextBox or ComboBox or CheckBox or NumericUpDown or ToggleSwitch or Slider or RadioButton;

    private static bool IsSearchHeading(TextBlock block) =>
        block.Classes.Contains("section-title") || block.Name?.EndsWith("Heading", StringComparison.Ordinal) == true
        || block.Classes.Contains("workspace-field-label") && block.FontWeight >= FontWeight.SemiBold;

    private static bool IsStatusText(TextBlock block) =>
        block.Classes.Contains("workspace-error") || block.Name is { } name
        && (name.EndsWith("Error", StringComparison.Ordinal) || name.EndsWith("Status", StringComparison.Ordinal) || name.EndsWith("Warning", StringComparison.Ordinal));

    // A panel that holds several settings of its own (a sub-section, the content of a collapsible group) is read item by item;
    // one that holds a single setting with its helper controls is one entry.
    private static bool IsComposite(Panel panel) =>
        panel is StackPanel { Orientation: Avalonia.Layout.Orientation.Vertical }
        && panel.Children.Count(child => child.GetSelfAndLogicalDescendants().OfType<Control>().Any(IsSettingInput)) >= 2;
}
