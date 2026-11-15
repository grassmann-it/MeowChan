using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Adds or removes own tags on the occurrences of the media page selection. Search first, like the single-file dialog: the
/// tag field filters one virtualized list of tri-state rows (on all, some or none of the ticked occurrences); a toggle
/// gives the tag to every ticked occurrence or takes it from all of them, in one write. Every occurrence of a tile is
/// listed below the tags (collapsed) and ticked by default; an unticked one is left alone. Tags stay with each
/// occurrence, threads are not tagged. The last change can be undone once.
/// </summary>
internal sealed class MediaSelectionTagDialog : Window
{
    private readonly MediaSelectionTagSession _session;
    private readonly int _tiles;
    // The selection this dialog tags ("Tags for {0} selected files"): the subtitle under the title bar, not a repeat of the title.
    private readonly TextBlock _subject = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Expander _occurrenceFrame = new() { Name = "MediaSelectionOccurrencesExpander", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _all = new() { Name = "MediaSelectionTagAll" };
    private readonly Button _none = new() { Name = "MediaSelectionTagNone" };
    private readonly StackPanel _occurrences = new() { Name = "MediaSelectionOccurrences", Spacing = 4 };
    private readonly List<(CheckBox Box, TextBlock Text)> _rows = new();
    private readonly IdentitySuggestionBox _input = new() { Name = "MediaSelectionTagInput", ReleaseEscape = true };
    private readonly Button _add = new() { Name = "MediaSelectionTagAdd" };
    private readonly TextBlock _status = new() { Name = "MediaSelectionTagStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _close = new() { Name = "MediaSelectionTagClose" };
    private readonly TagToggleSurface _surface;
    private bool _closed;
    private readonly SourceTagSuggestions _sources = new();
    private bool _syncing;

    internal MediaSelectionTagSession Session => _session;
    internal IReadOnlyList<CheckBox> OccurrenceBoxes => _rows.Select(row => row.Box).ToArray();
    internal IdentitySuggestionBox Input => _input;
    internal Button AddButton => _add;
    internal TagToggleSurface Surface => _surface;
    internal TagToggleListBox TagList => _surface.List;
    internal Button UndoButton => _surface.Undo;
    /// <summary>Danbooru source tags offered below the own tags, read once when the dialog opens.</summary>
    internal SourceTagSuggestions SourceTags => _sources;
    internal TextBlock Status => _status;

    internal MediaSelectionTagDialog(MediaSelectionTagSession session, int tiles, Func<MeowChan.Archive.BooruCatalogSnapshot?>? readSourceTags = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sources.Read = readSourceTags;
        _tiles = tiles;
        Name = "MediaSelectionTagDialog";
        // Modals plan MD2: a modal form-step dialog (fitted to the screen); the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.M, modal: true);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        _surface = new TagToggleSurface(true, "MediaSelectionTag", _input, () => _session.Snapshot, _session.Coverage,
            () => _session.LastChange, () => _session.Undone);

        for (int index = 0; index < session.Candidates.Count; index++)
        {
            int at = index;
            TextBlock text = new() { TextWrapping = TextWrapping.Wrap };
            CheckBox box = new() { Name = "MediaSelectionOccurrence:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), Content = text };
            box.IsCheckedChanged += (_, _) => { if (!_syncing) _session.Choose(at, box.IsChecked == true); };
            _rows.Add((box, text));
            _occurrences.Children.Add(box);
        }
        ScrollViewer list = new() { Content = _occurrences, MaxHeight = 160, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        WrapPanel choose = new() { ItemSpacing = 8, LineSpacing = 8, Children = { _all, _none } };
        _occurrenceFrame.Content = new StackPanel { Spacing = 8, Children = { choose, list } };
        Grid input = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        input.Children.Add(_input); input.Children.Add(_add); Grid.SetColumn(_add, 1);
        // UX103 P7 frame as in the single-file dialog: the tags first, the occurrences as one collapsed line below them.
        StackPanel header = new() { Spacing = 8 };
        header.Children.Add(_subject); header.Children.Add(_scope);
        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(16, 0, 16, 8) };
        body.Children.Add(input);
        body.Children.Add(_surface.SummaryRow); Grid.SetRow(_surface.SummaryRow, 1);
        body.Children.Add(_surface.ListArea); Grid.SetRow(_surface.ListArea, 2);
        body.Children.Add(_occurrenceFrame); Grid.SetRow(_occurrenceFrame, 3);
        // The last change's notice on the left, then Undo, and Close right-most.
        Border footer = DialogFooter.Build(_close, new Control[] { _surface.Undo }, _surface.Notice);
        Grid frame = DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_status), fillBody: true);
        DialogWindow.Frame(this, frame, modal: true, captionName: "MediaSelectionTagChrome");

        _all.Click += (_, _) => _session.ChooseAll(true);
        _none.Click += (_, _) => _session.ChooseAll(false);
        _add.Click += OnAdd; _close.Click += OnClose; _surface.Undo.Click += OnUndo;
        _surface.List.Toggled += OnToggled;
        // Own tags as suggestions while typing; a tag every chosen file already carries is not offered again.
        TagSuggestionInput.Attach(_input, () => _session.Busy ? null : _session.Snapshot, CarriedByAllChosen, () => OnAdd(null, new RoutedEventArgs()), _sources);
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DialogEscape.Attach(this);
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Localize();
        Refresh();
    }

    private void OnOpened(object? sender, EventArgs e) { _sources.Load(); _input.Focus(); }
    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_closed) { Localize(); Refresh(); } });

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        try { await AddTypedAsync(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.selection.tags.add")) { /* Logged and shown. */ }
    }

    private async System.Threading.Tasks.Task AddTypedAsync()
    {
        string name = _input.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return;
        // Several tags in one entry ("piss pee cute anal", quotes, commas), each through the alias table.
        TagEntry entry = TagEntryParser.Parse(name, _session.Snapshot);
        if (TagEntryParser.IsPlain(entry, name)) await _session.AddAsync(name);
        else if (entry.Invalid.Count > 0) await _session.AddAsync(entry.Invalid[0]);
        else await TagEntryParser.AddEachAsync(entry, text => _session.AddAsync(text), id => _session.AddExistingAsync(id), () => _session.MessageKey != null);
        // Keep a rejected draft for correction; clear it once it became a tag on the chosen files.
        if (_session.MessageKey == null && !_closed) _input.Text = string.Empty;
    }

    // On all ticked occurrences: take it from all of them; on some or none: give it to every ticked one (one write).
    private async void OnToggled(TagToggleRow row)
    {
        if (row.Tag is not { } tag) return;
        try
        {
            if (row.State == TagToggleState.All) await _session.RemoveAsync(tag.Id);
            else await _session.AddExistingAsync(tag.Id);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.selection.tags.toggle")) { /* Logged and shown. */ }
    }

    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (_session.LastChange is not { } change) return;
        try
        {
            await _session.UndoAsync();
            if (!_closed) _surface.FocusAfterUndo(change.Tag);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.selection.tags.undo")) { /* Logged and shown. */ }
    }

    // A tag every chosen file already carries is not suggested again; computed from the session's cached coverage.
    private IReadOnlySet<TagId> CarriedByAllChosen()
    {
        TagCoverage coverage = _session.Coverage();
        return coverage.Carriers.Where(item => coverage.Total > 0 && item.Value >= coverage.Total).Select(item => item.Key).ToHashSet();
    }

    // Runs after the field's own handler: an Enter that took a highlighted suggestion is already handled there.
    private async void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        _input.Dismiss();
        try { await AddTypedAsync(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.selection.tags.add")) { /* Logged and shown. */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Every change is saved as it is made: Enter on a list row only closes.
        if (_surface.ClosesOnEnter(e)) { e.Handled = true; Close(); return; }
        // The first Escape closes an open suggestion list; the next one reaches the dialog policy (DialogEscape) and closes it.
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        if (TagSuggestionInput.Route(_input, e) != TagFieldKey.CloseList) return;
        e.Handled = true; _input.Dismiss();
    }

    internal void CloseForLifetime() { if (!_closed) Close(); }

    private string Describe(MediaTagCandidate candidate)
    {
        MediaLibraryEntry entry = candidate.Entry;
        string where = Texts.T("media.filter.boardOfSource", entry.Thread.Board, entry.Thread.Source) + " · "
            + (string.IsNullOrWhiteSpace(entry.ThreadTitle) ? "#" + entry.Thread.Thread : entry.ThreadTitle);
        string text = (entry.Name ?? entry.MediaId ?? string.Empty) + " · " + where;
        return candidate.Block switch
        {
            MediaTagBlock.NotSaved => text + " — " + Texts.T("media.tile.missing"),
            MediaTagBlock.NoPostVersion => text + " — " + Texts.T("tags.media.boundElsewhere"),
            MediaTagBlock.ProviderMedium => text + " — " + Texts.T("booru.media.tags.unavailable"),
            _ => text
        };
    }

    private void Localize()
    {
        Title = Texts.T("media.selection.tag.title");
        _subject.Text = Texts.T("media.selection.tag.heading", _tiles, _session.Candidates.Count);
        _scope.Text = Texts.T("media.selection.tag.scope");
        TagSuggestionInput.Localize(_input);
        _add.Content = Texts.T("tags.add"); AutomationProperties.SetName(_add, Texts.T("tags.add"));
        _all.Content = Texts.T("media.selection.tag.all"); _none.Content = Texts.T("media.selection.tag.none");
        _close.Content = Texts.T("action.close"); AutomationProperties.SetName(_close, Texts.T("action.close"));
        for (int index = 0; index < _rows.Count; index++)
        {
            (CheckBox box, TextBlock text) = _rows[index];
            text.Text = Describe(_session.Candidates[index]);
            AutomationProperties.SetName(box, text.Text);
        }
        _surface.Localize();
    }

    // Only states change here: rows that differ repaint, and the list is laid out again only for a new vocabulary.
    private void Refresh()
    {
        if (_closed) return;
        string occurrences = Texts.T("media.selection.tag.occurrences", _session.Chosen.Count, _session.Candidates.Count);
        _occurrenceFrame.Header = occurrences;
        AutomationProperties.SetName(_occurrenceFrame, occurrences);
        AutomationProperties.SetName(_occurrences, occurrences);

        bool ready = _session.Snapshot != null && !_session.Busy;
        bool chosen = _session.Chosen.Count > 0;
        _syncing = true;
        try
        {
            for (int index = 0; index < _rows.Count; index++)
            {
                CheckBox box = _rows[index].Box;
                box.IsChecked = _session.IsChosen(index);
                box.IsEnabled = _session.Candidates[index].Subject != null && !_session.Busy;
            }
        }
        finally { _syncing = false; }
        _all.IsEnabled = _none.IsEnabled = !_session.Busy;
        _surface.Sync(ready);

        _input.IsEnabled = _add.IsEnabled = ready && chosen;
        if (!_input.IsEnabled) _input.Dismiss();
        _status.Text = _session.Snapshot == null ? Texts.T("tags.unavailable")
            : _session.MessageKey != null ? Texts.T(_session.MessageKey) : string.Empty;
        _status.IsVisible = _status.Text.Length > 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _session.Updated -= OnSessionUpdated; Texts.Changed -= OnLanguageChanged;
        _input.EndSuggestions(); _sources.End(); _surface.Dispose();
        _add.Click -= OnAdd; _close.Click -= OnClose; _surface.Undo.Click -= OnUndo;
        _surface.List.Toggled -= OnToggled;
        _input.RemoveHandler(KeyDownEvent, OnInputKey); RemoveHandler(KeyDownEvent, OnKey);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
