using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using Ursa.Controls;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly List<string> _draftSources = new();
    private readonly List<ArchiveBoardConstraint> _draftBoards = new();
    private readonly List<ThreadSourceState> _draftStates = new();
    private readonly Dictionary<string, long> _editorRevisions = new(StringComparer.Ordinal);
    private long _draftRevision;
    private long _searchValidationGeneration;
    private Control? _invalidSearchEditor;
    private string? _draftTimeZone;
    private bool _writingDraft;
    private bool _localizingSearchOptions;
    private bool _updatingBoardScopes;
    private bool _boardScopeChosen;
    private sealed record BoardScope(string? Source, string Label) { public override string ToString() => Label; }
    internal event Action? SearchStateChanged;
    internal IReadOnlyList<string> DraftSources => _draftSources.AsReadOnly();
    internal IReadOnlyList<ArchiveBoardConstraint> DraftBoards => _draftBoards.AsReadOnly();
    private string IdentityText(string name) => Control<Control>(name) switch
    { TextBox text => text.Text ?? "", Ursa.Controls.AutoCompleteBox text => text.Text ?? "", _ => "" };
    private void SetIdentityText(string name, string value)
    {
        switch (Control<Control>(name))
        { case TextBox text: text.Text = value; break; case Ursa.Controls.AutoCompleteBox text: text.Text = value; break; }
    }
    private void DraftEdited(string field)
    {
        ++_draftRevision;
        _editorRevisions[field] = _editorRevisions.GetValueOrDefault(field) + 1;
        if (!_writingDraft) SearchStateChanged?.Invoke();
    }
    private void InitializeSearchEditors()
    {
        GetBoardSuggestionSource = () => (ArchiveBoardScope.SelectedItem as BoardScope)?.Source;
        ArchiveSearchText.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) DraftEdited("text"); };
        // Escape empties the typed draft (the applied search stays until "Search"); a second Escape moves on to the results.
        ArchiveSearchText.ExitRequested += (_, e) =>
        {
            if (!ArchiveThreads.IsEffectivelyVisible || ArchiveThreads.ItemCount == 0) return;
            ArchiveThreads.Focus();
            e.Handled = true;
        };
        foreach (var entry in new[] { ("ArchiveSourceFilter", "source"), ("ArchiveBoardFilter", "board") })
            Control<Control>(entry.Item1).PropertyChanged += (_, e) => { if (e.Property.Name == "Text") DraftEdited(entry.Item2); };
        foreach (var entry in new[] { ("ArchiveStateFilter", "state"), ("ArchiveCaptureFilter", "capture"),
            ("ArchivePostsFilter", "posts"), ("ArchiveWorkFilter", "work"), ("ArchiveSort", "sort"), ("ArchiveTextScope", "scope") })
            Control<ComboBox>(entry.Item1).SelectionChanged += (_, _) => { if (!_localizingSearchOptions) DraftEdited(entry.Item2); };
        InitializeRelevanceSort();
        ArchiveBoardScope.SelectionChanged += (_, _) => { if (_updatingBoardScopes) return; _boardScopeChosen = true; DraftEdited("board"); InvalidateBoardSuggestions(); };
        InitializeTagFilterEditor(); InitializeStateEditor();
        ArchiveStartDate.Edited += () => DateEdited();
        ArchiveEndDate.Edited += () => DateEdited();
    }
    private void DateEdited()
    {
        if (!_writingDraft && _draftTimeZone == null)
        {
            try { _draftTimeZone = DeviceTimeZone(); }
            catch (Exception exception) { SearchEditorError(exception.Message); }
        }
        DraftEdited("date"); UpdateTimeZoneLabel();
    }
    private static string DeviceTimeZone() => ArchiveDateResolver.GetLocalTimeZoneId();
    private void OnUseDeviceZone(object? sender, RoutedEventArgs e)
    {
        try { _draftTimeZone = DeviceTimeZone(); DraftEdited("date"); UpdateTimeZoneLabel(); SearchEditorError(""); }
        catch (Exception exception) { SearchEditorError(exception.Message); }
    }
    private void UpdateTimeZoneLabel() => ArchiveDateZone.Text =
        Texts.T("archive.dateZone", _draftTimeZone ?? Texts.T("archive.zoneOnUse"));
    private void SearchEditorError(string message)
    {
        ArchiveSearchError.Text = message;
        ArchiveSearchError.IsVisible = message.Length > 0;
        SyncSearchPanelGaps();
    }
    private Control ValidationFocusTarget(Control editor) => editor is ArchiveDateEditor
        ? editor.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(text => !text.IsReadOnly) ?? editor
        : ReferenceEquals(editor, ArchiveTagChoiceHost) ? ArchiveTagChoice : editor;
    private void RevealSearchValidationError(long generation)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        Control editor = _invalidSearchEditor ?? ArchiveSearchError;
        long revision = _draftRevision;
        ArchiveSearchRequest? applied = _appliedSearch;
        var origin = owner.FocusManager?.GetFocusedElement();
        if (_invalidSearchEditor != null) { ArchiveFilters.IsChecked = true; EnsureFilterEditorShown(_invalidSearchEditor); }
        else editor.Focusable = true;
        bool Current() => !_ended && !_lifetime.IsCancellationRequested && CanRead && !_busy && IsEffectivelyVisible && owner.IsActive
            && generation == _searchValidationGeneration && revision == _draftRevision && ReferenceEquals(applied, _appliedSearch)
            && ReferenceEquals(TopLevel.GetTopLevel(this), owner);
        // Expanding the nested filter region must finish layout before scrolling.
        // A later edit/search/navigation, focus move, detach or close cancels this reveal.
        Dispatcher.UIThread.Post(() =>
        {
            if (!Current() || !ReferenceEquals(owner.FocusManager?.GetFocusedElement(), origin)) return;
            owner.UpdateLayout();
            Control target = ValidationFocusTarget(editor);
            if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) return;
            target.Focus(); target.BringIntoView();
            Dispatcher.UIThread.Post(() =>
            {
                if (!Current() || !ReferenceEquals(owner.FocusManager?.GetFocusedElement(), target)) return;
                target.BringIntoView(); // Also reach the outer bounded search viewport after its remeasure.
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
    private async void OnAddSource(object? sender, RoutedEventArgs e) => await AddIdentityAsync(board: false);
    private async void OnAddBoard(object? sender, RoutedEventArgs e) => await AddIdentityAsync(board: true);
    // The one way a source or board becomes a chip: an accepted suggestion, Enter, Add and Search all use it.
    // Without an accepted suggestion the text resolves like Enter (IdentitySuggestions.Resolve) against
    // candidates read for exactly this text and scope. False: invalid (error under the field) or superseded.
    private async Task<bool> AddIdentityAsync(bool board, IdentitySuggestion? accepted = null)
    {
        string name = board ? "ArchiveBoardFilter" : "ArchiveSourceFilter", field = board ? "board" : "source";
        IdentitySuggestionBox editor = board ? ArchiveBoardFilter : ArchiveSourceFilter;
        string raw = IdentityText(name);
        accepted ??= TryReadSelectedIdentity(name);
        if (accepted == null && string.IsNullOrWhiteSpace(raw)) { DataValidationErrors.ClearErrors(editor); return true; }
        if (accepted == null)
        {
            long revision = _editorRevisions.GetValueOrDefault(field);
            IdentitySuggestion[] candidates;
            try { candidates = await ReadSuggestionsAsync(board, raw, _lifetime.Token); }
            catch (Exception) { candidates = Array.Empty<IdentitySuggestion>(); } // Unavailable suggestions leave free input.
            // A later edit of this text or scope wins; never resolve against candidates for an earlier text.
            if (_ended || _editorRevisions.GetValueOrDefault(field) != revision) return false;
            accepted = IdentitySuggestions.Resolve(raw, null, candidates);
        }
        string value = accepted?.Value ?? raw.Trim();
        // An accepted board suggestion carries its actual source; free text takes the chosen board scope.
        string? source = accepted?.Source ?? (ArchiveBoardScope.SelectedItem as BoardScope)?.Source;
        ArchiveBoardConstraint pair = new(source, value);
        bool duplicate = board ? _draftBoards.Contains(pair) : _draftSources.Contains(value, StringComparer.Ordinal);
        try
        {
            _ = board ? ArchiveSearchSpec.FromExactIdentities(boards: duplicate ? _draftBoards : _draftBoards.Append(pair))
                : ArchiveSearchSpec.FromExactIdentities(sources: duplicate ? _draftSources : _draftSources.Append(value));
        }
        catch (Exception exception) { DataValidationErrors.SetErrors(editor, new object[] { exception.Message }); return false; }
        DataValidationErrors.ClearErrors(editor);
        if (!duplicate) { if (board) _draftBoards.Add(pair); else _draftSources.Add(value); }
        // Focus stays in the field; the list stays closed until the next typing or Down.
        SetIdentityText(name, ""); editor.Dismiss(); DraftEdited(field); RenderDraftSets();
        return true;
    }
    // Choosing a state is the decision: it becomes a chip and the list returns to "All source states".
    private void AddChosenState()
    {
        if (_localizingSearchOptions || _writingDraft || ArchiveStateFilter.IsDropDownOpen) return;
        ThreadSourceState? state = ArchiveStateFilter.SelectedIndex switch
        { 1 => ThreadSourceState.Active, 2 => ThreadSourceState.Archived, 3 => ThreadSourceState.NotFound, _ => null };
        if (state == null) return;
        if (!_draftStates.Contains(state.Value)) _draftStates.Add(state.Value);
        ArchiveStateFilter.SelectedIndex = 0; DraftEdited("state"); RenderDraftSets();
    }
    private void InitializeStateEditor()
    {
        // Applied after the selection update has finished; an open list commits when it closes.
        ArchiveStateFilter.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(AddChosenState);
        ArchiveStateFilter.DropDownClosed += (_, _) => Dispatcher.UIThread.Post(AddChosenState);
        // Arrows on the closed list would add the next state at once; they open the list instead.
        ArchiveStateFilter.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (ArchiveStateFilter.IsDropDownOpen || e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None) return;
            ArchiveStateFilter.IsDropDownOpen = true; e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
    private void RenderDraftSets()
    {
        RenderDraftTagFilter();
        RenderDraftTags("ArchiveDraftSources", "ArchiveSourceFilter", _draftSources.Select(source => (source, (Action)(() =>
        { _draftSources.Remove(source); DraftEdited("source"); RenderDraftSets(); }))));
        RenderDraftTags("ArchiveDraftBoards", "ArchiveBoardFilter", _draftBoards.Select(board => (BoardLabel(board) + (board.Source != null && _draftSources.Count > 0 && !_draftSources.Contains(board.Source, StringComparer.Ordinal) ? " · " + Texts.T("archive.boardOutsideSources") : ""), (Action)(() =>
        { _draftBoards.Remove(board); DraftEdited("board"); RenderDraftSets(); }))));
        RenderDraftTags("ArchiveDraftStates", "ArchiveStateFilter", _draftStates.Select(state => (StateLabel(state), (Action)(() =>
        { _draftStates.Remove(state); DraftEdited("state"); RenderDraftSets(); }))));
        ComboBox scopes = ArchiveBoardScope;
        BoardScope? previousScope = scopes.SelectedItem as BoardScope;
        string? previous = previousScope?.Source;
        BoardScope[] options = new[] { new BoardScope(null, Texts.T("archive.allSourceBoards")) }
            .Concat(_draftSources.Concat(_draftBoards.Select(board => board.Source).OfType<string>()).Concat(previous == null ? Array.Empty<string>() : new[] { previous })
                .Distinct(StringComparer.Ordinal).Select(source => new BoardScope(source, source))).ToArray();
        _updatingBoardScopes = true;
        try
        {
            scopes.ItemsSource = options;
            scopes.SelectedItem = !_boardScopeChosen && options.Length == 2 ? options[1]
                : options.FirstOrDefault(option => option.Source == previous) ?? options[0];
        }
        finally { _updatingBoardScopes = false; }
        InvalidateBoardSuggestions();
    }
    private void RenderDraftTags(string name, string editorName, IEnumerable<(string Label, Action Remove)> entries,
        Func<int, string, Action, ClosableTag>? createChip = null)
    {
        WrapPanel panel = Control<WrapPanel>(name); panel.Children.Clear();
        int index = 0;
        foreach (var entry in entries.ToArray())
        {
            int position = index++;
            ClosableTag? tag = null;
            // Only a removal made from the tag itself (keyboard focus on it) moves focus; pointer removals
            // and other re-renders leave focus alone. No search is applied.
            Action remove = () =>
            {
                bool focused = tag!.IsKeyboardFocusWithin;
                entry.Remove();
                if (focused) FocusAfterDraftTagRemoval(name, editorName, position, tag);
            };
            tag = createChip?.Invoke(position, entry.Label, remove) ?? CreateFilterTag(entry.Label, remove);
            if (createChip == null) AttachFilterChipMenu(tag, remove);
            panel.Children.Add(tag);
        }
        // The chips of a category are a polite live region named by their labels, so a new chip is announced.
        AutomationProperties.SetName(panel, string.Join(", ", entries.Select(entry => entry.Label)));
    }
    private void FocusAfterDraftTagRemoval(string panelName, string editorName, int position, Control removed)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // A newer deliberate focus target (or a closed page) wins over this continuation.
            IInputElement? current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (current != null && !ReferenceEquals(current, removed) && current is Visual v && v.IsAttachedToVisualTree()) return;
            WrapPanel panel = Control<WrapPanel>(panelName);
            Control target = panel.Children.Count > 0 ? panel.Children[Math.Min(position, panel.Children.Count - 1)] : Control<Control>(editorName);
            target.Focus(NavigationMethod.Tab);
        }, Avalonia.Threading.DispatcherPriority.Input);
    }
    private static string StateLabel(ThreadSourceState state) => Texts.T(state switch
    { ThreadSourceState.Active => "archive.sourceActive", ThreadSourceState.Archived => "archive.sourceArchived", _ => "archive.sourceMissing" });
    private static string BoardLabel(ArchiveBoardConstraint board) =>
        (board.Source ?? Texts.T("archive.allSourceBoards")) + " /" + board.Board + "/";

    private ArchiveSearchSpec ReadSearchDraft(bool validateFields = true)
    {
        if (validateFields) _invalidSearchEditor = null;
        // ApplySearchAsync adds leftover source, board and state input first; only a chosen tag still needs
        // Include or Exclude. For the unapplied-draft marker, leftover source or board text is unapplied input.
        bool pending = HasPendingTagChoice;
        if (validateFields) DataValidationErrors.SetErrors(ArchiveTagChoiceHost, pending ? new object[] { Texts.T("tags.filter.pending") } : null);
        if (validateFields && pending) _invalidSearchEditor = ArchiveTagChoiceHost;
        if (pending) throw new InvalidOperationException(Texts.T("tags.filter.pending"));
        if (!validateFields && (!string.IsNullOrWhiteSpace(IdentityText("ArchiveSourceFilter")) || !string.IsNullOrWhiteSpace(IdentityText("ArchiveBoardFilter"))))
            throw new InvalidOperationException("Unadded source or board text.");
        bool? Choice(string name) => Control<ComboBox>(name).SelectedIndex switch { 1 => true, 2 => false, _ => null };
        DateOnly? ParseDate(string name)
        {
            ArchiveDateEditor editor = Control<ArchiveDateEditor>(name);
            try { return editor.ParseRaw(validateFields); }
            catch (FormatException) { if (validateFields) _invalidSearchEditor = editor; throw; }
        }
        DateOnly? start = ParseDate("ArchiveStartDate");
        DateOnly? end = ParseDate("ArchiveEndDate");
        if (start.HasValue && end.HasValue && start.Value > end.Value)
        {
            string error = Texts.T("archive.invertedDates");
            if (validateFields)
            {
                _invalidSearchEditor = ArchiveEndDate;
                DataValidationErrors.SetErrors(ValidationFocusTarget(_invalidSearchEditor), new object[] { error });
            }
            throw new FormatException(error);
        }
        if (validateFields)
        {
            DataValidationErrors.ClearErrors(ArchiveEndDate);
            DataValidationErrors.ClearErrors(ValidationFocusTarget(ArchiveEndDate));
        }
        ArchiveDateRange? date = start == null && end == null ? null : new ArchiveDateRange(start, end,
            _draftTimeZone ?? throw new InvalidOperationException(Texts.T("archive.zoneUnavailable", TimeZoneInfo.Local.Id)));
        return ArchiveSearchSpec.FromExactIdentities(ArchiveSearchText.Text ?? "", _draftSources, _draftBoards, _draftStates,
            Choice("ArchiveCaptureFilter"), Choice("ArchivePostsFilter"),
            (ArchiveThreadSort)Math.Max(0, ArchiveSort.SelectedIndex),
            WorkChoice(ArchiveWorkFilter.SelectedIndex), date).WithTags(ReadTagFilterDraft()).WithScope(DraftTextScope);
    }
    private void WriteSearchDraft(ArchiveSearchSpec spec)
    {
        _writingDraft = true;
        try
        {
            ArchiveSearchText.Text = spec.Text;
            _draftSources.Clear(); _draftSources.AddRange(spec.Sources); SetIdentityText("ArchiveSourceFilter", "");
            _draftBoards.Clear(); _draftBoards.AddRange(spec.Boards); SetIdentityText("ArchiveBoardFilter", "");
            _draftStates.Clear(); _draftStates.AddRange(spec.SourceStates);
            ArchiveStateFilter.SelectedIndex = 0;
            ArchiveCaptureFilter.SelectedIndex = spec.CaptureEnabled switch { true => 1, false => 2, _ => 0 };
            ArchivePostsFilter.SelectedIndex = spec.HasPosts switch { true => 1, false => 2, _ => 0 };
            ArchiveWorkFilter.SelectedIndex = WorkIndex(spec.WorkFilter);
            ArchiveSort.SelectedIndex = (int)spec.Sort;
            ArchiveTextScope.SelectedIndex = TextScopeToIndex(spec.Scope);
            _draftTimeZone = spec.DateRange?.TimeZoneId;
            ArchiveStartDate.RawText = spec.DateRange?.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            ArchiveEndDate.RawText = spec.DateRange?.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            WriteTagFilterDraft(spec.Tags);
            foreach (string field in new[] { "text", "source", "board", "state", "capture", "posts", "sort", "work", "date", "tags", "scope" }) DraftEdited(field);
            RenderDraftSets(); UpdateTimeZoneLabel(); SearchEditorError("");
        }
        finally { _writingDraft = false; }
        SearchStateChanged?.Invoke();
    }
    private Action CaptureSearchDraft()
    {
        string text = ArchiveSearchText.Text ?? "";
        string source = IdentityText("ArchiveSourceFilter"), board = IdentityText("ArchiveBoardFilter");
        string[] sources = _draftSources.ToArray(); ArchiveBoardConstraint[] boards = _draftBoards.ToArray();
        ThreadSourceState[] states = _draftStates.ToArray();
        string? zone = _draftTimeZone;
        string start = ArchiveStartDate.RawText, end = ArchiveEndDate.RawText;
        string[] names = { "ArchiveStateFilter", "ArchiveCaptureFilter", "ArchivePostsFilter", "ArchiveWorkFilter", "ArchiveSort", "ArchiveTextScope" };
        int[] indices = names.Select(name => Control<ComboBox>(name).SelectedIndex).ToArray();
        BoardScope? scope = ArchiveBoardScope.SelectedItem as BoardScope;
        bool scopeChosen = _boardScopeChosen;
        Action restoreTags = CaptureTagFilterDraft();
        return () =>
        {
            _writingDraft = true;
            try
            {
                ArchiveSearchText.Text = text;
                _draftSources.Clear(); _draftSources.AddRange(sources); _draftBoards.Clear(); _draftBoards.AddRange(boards);
                _draftStates.Clear(); _draftStates.AddRange(states); SetIdentityText("ArchiveSourceFilter", source); SetIdentityText("ArchiveBoardFilter", board);
                for (int i = 0; i < names.Length; ++i) Control<ComboBox>(names[i]).SelectedIndex = indices[i];
                _draftTimeZone = zone; _boardScopeChosen = scopeChosen;
                ArchiveStartDate.RawText = start; ArchiveEndDate.RawText = end;
                RenderDraftSets();
                ComboBox combo = ArchiveBoardScope;
                if (combo.ItemsSource is IEnumerable<BoardScope> options) combo.SelectedItem = options.FirstOrDefault(item => item.Source == scope?.Source);
                UpdateTimeZoneLabel();
                restoreTags();
                foreach (string field in new[] { "text", "source", "board", "state", "capture", "posts", "work", "sort", "date", "tags", "scope" }) DraftEdited(field);
            }
            finally { _writingDraft = false; }
            SearchStateChanged?.Invoke();
        };
    }

    internal bool HasUnappliedSearchDraft
    {
        get
        {
            try { return AppliedSearchSpec == null || ArchiveSearchSpecJson.Serialize(ReadSearchDraft(validateFields: false)) != ArchiveSearchSpecJson.Serialize(AppliedSearchSpec); }
            catch { return true; }
        }
    }
    private void LocalizeSearchEditors()
    {
        foreach (string name in new[] { "ArchiveAddSource", "ArchiveAddBoard" }) Control<Button>(name).Content = Texts.T("archive.addFilter");
        AutomationProperties.SetName(ArchiveBoardScope, Texts.T("archive.boardSourceScope"));
        ArchiveDateTitle.Text = Texts.T("archive.dateBasis");
        ArchiveDateHint.Text = Texts.T("archive.dateHint");
        ArchiveStartDate.SetLabel(Texts.T("archive.dateFrom"));
        ArchiveEndDate.SetLabel(Texts.T("archive.dateTo"));
        ArchiveDateFromLabel.Text = Texts.T("archive.dateFrom");
        ArchiveDateToLabel.Text = Texts.T("archive.dateTo");
        ArchiveUseDeviceZone.Content = Texts.T("archive.useDeviceZone");
        ArchiveRetrySearch.Content = Texts.T("action.retry");
        LocalizeTagFilterEditor();
        RenderDraftSets(); UpdateTimeZoneLabel();
    }
}
