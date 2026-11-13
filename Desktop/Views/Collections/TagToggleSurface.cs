using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The shared middle of both media tag dialogs: the filter that the tag field drives, the one virtualized list with its
/// summary and empty text, and the footer line with the one-step undo. The dialogs keep their sessions, writes and
/// close semantics; this only shows rows and decides when the list is laid out again.
/// </summary>
internal sealed class TagToggleSurface : IDisposable
{
    /// <summary>Above this many tags the list waits for a pause in typing before it filters.</summary>
    internal const int ImmediateFilterLimit = 2_000;
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);
    private readonly bool _many;
    private readonly IdentitySuggestionBox _input;
    private readonly Func<PrivateTagStoreSnapshot?> _snapshot;
    private readonly Func<TagCoverage> _coverage;
    private readonly Func<TagDialogChange?> _lastChange;
    private readonly Func<bool> _undone;
    private readonly QueryDebounce _filter;
    private bool _ended;

    internal TagToggleSurface(bool many, string name, IdentitySuggestionBox input, Func<PrivateTagStoreSnapshot?> snapshot,
        Func<TagCoverage> coverage, Func<TagDialogChange?> lastChange, Func<bool> undone)
    {
        _many = many;
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        _lastChange = lastChange ?? throw new ArgumentNullException(nameof(lastChange));
        _undone = undone ?? throw new ArgumentNullException(nameof(undone));
        Model = new TagToggleList(many);
        List = new TagToggleListBox { Name = name + "List", MinHeight = 96 };
        Summary = new TextBlock { Name = name + "Summary", TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
        Empty = new TextBlock { Name = name + "Empty", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), IsVisible = false };
        Notice = new TextBlock { Name = name + "Notice", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Undo = new Button { Name = name + "Undo", IsVisible = false };
        AutomationProperties.SetLiveSetting(Summary, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(Notice, AutomationLiveSetting.Polite);
        ListArea = new Grid { Children = { List, Empty } };
        Sort = TagSortChoice.Create(name + "Sort");
        SummaryRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        SummaryRow.Children.Add(Summary);
        SummaryRow.Children.Add(Sort); Grid.SetColumn(Sort, 1);
        Summary.VerticalAlignment = VerticalAlignment.Center;
        Sort.SelectionChanged += OnSortChanged;
        _filter = new QueryDebounce(Relayout);
        _input.TextChanged += OnTextChanged;
    }

    internal TagToggleList Model { get; }
    internal TagToggleListBox List { get; }
    internal TextBlock Summary { get; }
    internal TextBlock Empty { get; }
    /// <summary>What the last change did, or that it was undone.</summary>
    internal TextBlock Notice { get; }
    internal Button Undo { get; }
    /// <summary>"A–Z" (with the most used tags on top) or "Most used" first.</summary>
    internal ComboBox Sort { get; }
    /// <summary>The summary with the sort choice beside it.</summary>
    internal Grid SummaryRow { get; }
    /// <summary>The list with the empty text over it; the body's growing row.</summary>
    internal Grid ListArea { get; }
    /// <summary>True while a typed filter waits for a pause before the list follows it.</summary>
    internal bool FilterPending => _filter.IsPending;

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_ended) return;
        // The field also reports changes that leave the filter as it is (for example no text becoming empty text when it
        // loses the focus): those must not lay 20,000 rows out again under the cursor.
        if (string.Equals(_input.Text ?? string.Empty, Model.Filter, StringComparison.Ordinal)) { _filter.Cancel(); return; }
        if (Model.TagCount > ImmediateFilterLimit) _filter.Request(FilterDelay);
        else { _filter.Cancel(); Relayout(); }
    }

    private void OnSortChanged(object? sender, SelectionChangedEventArgs e) { if (!_localizing) Relayout(); }
    private bool _localizing;

    /// <summary>Runs a waiting filter now (tests and smoke checks).</summary>
    internal void FlushFilter() => _filter.Flush();

    /// <summary>Lays the sections out again (new filter, vocabulary or language) and keeps the cursor on its tag.</summary>
    internal void Relayout()
    {
        if (_ended) return;
        TagId? keep = List.Current?.Tag?.Id;
        Model.Build(_snapshot(), _coverage(), _input.Text, TagSortChoice.Of(Sort));
        List.Show(Model.Rows, keep is { } id ? Model.RowOf(id) : null);
        UpdateTexts();
    }

    /// <summary>After a session update: only rows whose state changed repaint; a new vocabulary lays the list out again.</summary>
    internal void Sync(bool ready)
    {
        if (_ended) return;
        if (Model.Apply(_snapshot(), _coverage()) < 0) Relayout();
        else UpdateTexts();
        Undo.IsEnabled = ready;
    }

    internal void Localize()
    {
        AutomationProperties.SetName(List, Texts.T("tags.dialog.list"));
        AutomationProperties.SetHelpText(List, Texts.T(_many ? "tags.dialog.listHelpMany" : "tags.dialog.listHelp"));
        Undo.Content = Texts.T("tags.dialog.undo");
        AutomationProperties.SetName(Undo, Texts.T("tags.dialog.undoName"));
        ToolTip.SetTip(Undo, Texts.T("tags.dialog.undoName"));
        _localizing = true;
        try { TagSortChoice.Localize(Sort); } finally { _localizing = false; }
        Model.Relocalize();
        Relayout();
    }

    private void UpdateTexts()
    {
        PrivateTagStoreSnapshot? snapshot = Model.Snapshot;
        (int all, int some) = Model.Counts();
        Summary.Text = snapshot == null ? string.Empty
            : _many ? Texts.T("tags.dialog.summaryMany", all, some, Model.TagCount)
            : Texts.T("tags.dialog.summary", all, Model.TagCount);
        Summary.IsVisible = Summary.Text.Length > 0;
        string filter = (_input.Text ?? string.Empty).Trim();
        Empty.Text = snapshot == null || Model.Rows.Count > 0 ? string.Empty
            : Model.TagCount == 0 ? Texts.T("tags.dialog.empty")
            : Texts.T("tags.dialog.noMatch", filter);
        Empty.IsVisible = Empty.Text.Length > 0;
        List.IsVisible = snapshot != null && Model.Rows.Count > 0;
        TagDialogChange? change = _lastChange();
        Notice.Text = change is { } last
            ? _many ? Texts.T(last.Assigned ? "tags.dialog.addedMany" : "tags.dialog.removedMany", last.Name, last.Subjects.Count)
                : Texts.T(last.Assigned ? "tags.dialog.added" : "tags.dialog.removed", last.Name)
            : _undone() ? Texts.T("tags.dialog.undone") : string.Empty;
        Notice.IsVisible = Notice.Text.Length > 0;
        Undo.IsVisible = change != null;
    }

    /// <summary>After an undo the button is gone: the cursor goes back to the undone tag, or to the tag field.</summary>
    internal void FocusAfterUndo(TagId tag)
    {
        if (Model.RowOf(tag) is { } row && List.IsVisible) List.Focus(row);
        else _input.Focus();
    }

    /// <summary>Enter on a list row closes the dialog (every change is already saved).</summary>
    internal bool ClosesOnEnter(KeyEventArgs e) =>
        e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && List.IsKeyboardFocusWithin;

    public void Dispose()
    {
        if (_ended) return;
        _ended = true;
        _input.TextChanged -= OnTextChanged;
        Sort.SelectionChanged -= OnSortChanged;
        _filter.Dispose();
    }
}

/// <summary>
/// The sort choice of the tag lists (both tag dialogs and the tag manager): alphabetical or most used first. There is no
/// "recently used" choice because the store keeps no time of use.
/// </summary>
internal static class TagSortChoice
{
    internal static ComboBox Create(string name, TagListSort initial = TagListSort.Alphabetical)
    {
        ComboBox box = new() { Name = name, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        box.Items.Add(new ComboBoxItem { Tag = TagListSort.Alphabetical });
        box.Items.Add(new ComboBoxItem { Tag = TagListSort.Uses });
        box.SelectedIndex = initial == TagListSort.Uses ? 1 : 0;
        Localize(box);
        return box;
    }

    internal static TagListSort Of(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag is TagListSort sort ? sort : TagListSort.Alphabetical;

    internal static void Localize(ComboBox box)
    {
        foreach (ComboBoxItem item in box.Items.OfType<ComboBoxItem>())
        {
            string text = Texts.T(item.Tag is TagListSort.Uses ? "tags.dialog.sort.uses" : "tags.dialog.sort.name");
            item.Content = text;
            AutomationProperties.SetName(item, text);
        }
        AutomationProperties.SetName(box, Texts.T("tags.dialog.sort"));
        ToolTip.SetTip(box, Texts.T("tags.dialog.sort"));
    }
}
