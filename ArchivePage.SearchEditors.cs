using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private bool _updatingBoardScopes;
    private bool _boardScopeChosen;
    private sealed record BoardScope(string? Source, string Label) { public override string ToString() => Label; }
    internal event Action? SearchStateChanged;
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
        GetBoardSuggestionSource = () => (Control<ComboBox>("ArchiveBoardScope").SelectedItem as BoardScope)?.Source;
        Control<TextBox>("ArchiveSearchText").PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) DraftEdited("text"); };
        foreach (var entry in new[] { ("ArchiveSourceFilter", "source"), ("ArchiveBoardFilter", "board") })
            Control<Control>(entry.Item1).PropertyChanged += (_, e) => { if (e.Property.Name == "Text") DraftEdited(entry.Item2); };
        foreach (var entry in new[] { ("ArchiveStateFilter", "state"), ("ArchiveCaptureFilter", "capture"),
            ("ArchivePostsFilter", "posts"), ("ArchiveWorkFilter", "work"), ("ArchiveSort", "sort") })
            Control<ComboBox>(entry.Item1).SelectionChanged += (_, _) => DraftEdited(entry.Item2);
        Control<ComboBox>("ArchiveBoardScope").SelectionChanged += (_, _) => { if (_updatingBoardScopes) return; _boardScopeChosen = true; DraftEdited("board"); InvalidateBoardSuggestions(); };
        Control<ArchiveDateEditor>("ArchiveStartDate").Edited += () => DateEdited();
        Control<ArchiveDateEditor>("ArchiveEndDate").Edited += () => DateEdited();
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
    private void UpdateTimeZoneLabel() => Control<TextBlock>("ArchiveDateZone").Text =
        Texts.T("archive.dateZone", _draftTimeZone ?? Texts.T("archive.zoneOnUse"));
    private void SearchEditorError(string message)
    {
        Control<TextBlock>("ArchiveSearchError").Text = message;
        Control<TextBlock>("ArchiveSearchError").IsVisible = message.Length > 0;
    }
    private static Control ValidationFocusTarget(Control editor) => editor is ArchiveDateEditor
        ? editor.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault(text => !text.IsReadOnly) ?? editor
        : editor;
    private void RevealSearchValidationError(long generation)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        Control editor = _invalidSearchEditor ?? Control<TextBlock>("ArchiveSearchError");
        long revision = _draftRevision;
        ArchiveSearchRequest? applied = _appliedSearch;
        var origin = owner.FocusManager?.GetFocusedElement();
        if (_invalidSearchEditor != null) Control<Expander>("ArchiveFilters").IsExpanded = true;
        else editor.Focusable = true;
        bool Current() => !_ended && !_lifetime.IsCancellationRequested && _available && !_busy && IsEffectivelyVisible && owner.IsActive
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
    private void OnAddSource(object? sender, RoutedEventArgs e)
    {
        string source = IdentityText("ArchiveSourceFilter").Trim();
        if (source.Length == 0) return;
        try { _ = new ArchiveSearchSpec(sources: _draftSources.Contains(source, StringComparer.Ordinal) ? _draftSources : _draftSources.Append(source)); }
        catch (Exception exception) { DataValidationErrors.SetErrors(Control<Control>("ArchiveSourceFilter"), new object[] { exception.Message }); SearchEditorError(exception.Message); return; }
        DataValidationErrors.ClearErrors(Control<Control>("ArchiveSourceFilter"));
        if (!_draftSources.Contains(source, StringComparer.Ordinal)) _draftSources.Add(source);
        SetIdentityText("ArchiveSourceFilter", ""); DraftEdited("source"); RenderDraftSets();
    }
    private void OnAddBoard(object? sender, RoutedEventArgs e)
    {
        string board = IdentityText("ArchiveBoardFilter").Trim();
        if (board.Length == 0) return;
        string? source = (Control<ComboBox>("ArchiveBoardScope").SelectedItem as BoardScope)?.Source;
        // W04's explicit accepted candidates carry their actual source. Merely
        // highlighting a suggestion must not qualify freely typed board text.
        var candidate = TryReadSelectedIdentity("ArchiveBoardFilter");
        if (candidate?.Source != null && candidate.Value == board) source = candidate.Source;
        ArchiveBoardConstraint pair = new(source, board);
        bool duplicate = _draftBoards.Any(item => item.Source == source && item.Board == board);
        try { _ = new ArchiveSearchSpec(boards: duplicate ? _draftBoards : _draftBoards.Append(pair)); }
        catch (Exception exception) { DataValidationErrors.SetErrors(Control<Control>("ArchiveBoardFilter"), new object[] { exception.Message }); SearchEditorError(exception.Message); return; }
        DataValidationErrors.ClearErrors(Control<Control>("ArchiveBoardFilter"));
        if (!duplicate) _draftBoards.Add(pair);
        SetIdentityText("ArchiveBoardFilter", ""); DraftEdited("board"); RenderDraftSets();
    }
    private void OnAddState(object? sender, RoutedEventArgs e)
    {
        ThreadSourceState? state = Control<ComboBox>("ArchiveStateFilter").SelectedIndex switch
        { 1 => ThreadSourceState.Active, 2 => ThreadSourceState.Archived, 3 => ThreadSourceState.NotFound, _ => null };
        if (state.HasValue && !_draftStates.Contains(state.Value)) _draftStates.Add(state.Value);
        Control<ComboBox>("ArchiveStateFilter").SelectedIndex = 0; DraftEdited("state"); RenderDraftSets();
    }
    private void RenderDraftSets()
    {
        RenderDraftTags("ArchiveDraftSources", _draftSources.Select(source => (source, (Action)(() =>
        { _draftSources.Remove(source); DraftEdited("source"); RenderDraftSets(); }))));
        RenderDraftTags("ArchiveDraftBoards", _draftBoards.Select(board => (BoardLabel(board) + (board.Source != null && _draftSources.Count > 0 && !_draftSources.Contains(board.Source, StringComparer.Ordinal) ? " · " + Texts.T("archive.boardOutsideSources") : ""), (Action)(() =>
        { _draftBoards.Remove(board); DraftEdited("board"); RenderDraftSets(); }))));
        RenderDraftTags("ArchiveDraftStates", _draftStates.Select(state => (StateLabel(state), (Action)(() =>
        { _draftStates.Remove(state); DraftEdited("state"); RenderDraftSets(); }))));
        ComboBox scopes = Control<ComboBox>("ArchiveBoardScope");
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
    private void RenderDraftTags(string name, IEnumerable<(string Label, Action Remove)> entries)
    {
        WrapPanel panel = Control<WrapPanel>(name); panel.Children.Clear();
        foreach (var entry in entries.ToArray()) panel.Children.Add(CreateFilterTag(entry.Label, entry.Remove));
    }
    private static string StateLabel(ThreadSourceState state) => Texts.T(state switch
    { ThreadSourceState.Active => "archive.sourceActive", ThreadSourceState.Archived => "archive.sourceArchived", _ => "archive.sourceMissing" });
    private static string BoardLabel(ArchiveBoardConstraint board) =>
        (board.Source ?? Texts.T("archive.allSourceBoards")) + " /" + board.Board + "/";

    private ArchiveSearchSpec ReadSearchDraft(bool validateFields = true)
    {
        if (validateFields) _invalidSearchEditor = null;
        bool pending = false;
        foreach (string name in new[] { "ArchiveSourceFilter", "ArchiveBoardFilter", "ArchiveStateFilter" })
        {
            bool hasPending = name == "ArchiveStateFilter" ? Control<ComboBox>(name).SelectedIndex > 0 : !string.IsNullOrWhiteSpace(IdentityText(name));
            if (validateFields) DataValidationErrors.SetErrors(Control<Control>(name), hasPending ? new object[] { Texts.T("archive.pendingFilter") } : null);
            if (validateFields && hasPending) _invalidSearchEditor ??= Control<Control>(name);
            pending |= hasPending;
        }
        if (pending) throw new InvalidOperationException(Texts.T("archive.pendingFilter"));
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
                _invalidSearchEditor = Control<ArchiveDateEditor>("ArchiveEndDate");
                DataValidationErrors.SetErrors(ValidationFocusTarget(_invalidSearchEditor), new object[] { error });
            }
            throw new FormatException(error);
        }
        if (validateFields)
        {
            DataValidationErrors.ClearErrors(Control<ArchiveDateEditor>("ArchiveEndDate"));
            DataValidationErrors.ClearErrors(ValidationFocusTarget(Control<ArchiveDateEditor>("ArchiveEndDate")));
        }
        ArchiveDateRange? date = start == null && end == null ? null : new ArchiveDateRange(start, end,
            _draftTimeZone ?? throw new InvalidOperationException(Texts.T("archive.zoneUnavailable", TimeZoneInfo.Local.Id)));
        return new ArchiveSearchSpec(Control<TextBox>("ArchiveSearchText").Text ?? "", _draftSources, _draftBoards, _draftStates,
            Choice("ArchiveCaptureFilter"), Choice("ArchivePostsFilter"),
            (ArchiveThreadSort)Math.Max(0, Control<ComboBox>("ArchiveSort").SelectedIndex),
            WorkChoice(Control<ComboBox>("ArchiveWorkFilter").SelectedIndex), date);
    }
    private void WriteSearchDraft(ArchiveSearchSpec spec)
    {
        _writingDraft = true;
        try
        {
            Control<TextBox>("ArchiveSearchText").Text = spec.Text;
            _draftSources.Clear(); _draftSources.AddRange(spec.Sources); SetIdentityText("ArchiveSourceFilter", "");
            _draftBoards.Clear(); _draftBoards.AddRange(spec.Boards); SetIdentityText("ArchiveBoardFilter", "");
            _draftStates.Clear(); _draftStates.AddRange(spec.SourceStates);
            Control<ComboBox>("ArchiveStateFilter").SelectedIndex = 0;
            Control<ComboBox>("ArchiveCaptureFilter").SelectedIndex = spec.CaptureEnabled switch { true => 1, false => 2, _ => 0 };
            Control<ComboBox>("ArchivePostsFilter").SelectedIndex = spec.HasPosts switch { true => 1, false => 2, _ => 0 };
            Control<ComboBox>("ArchiveWorkFilter").SelectedIndex = WorkIndex(spec.WorkFilter);
            Control<ComboBox>("ArchiveSort").SelectedIndex = (int)spec.Sort;
            _draftTimeZone = spec.DateRange?.TimeZoneId;
            Control<ArchiveDateEditor>("ArchiveStartDate").RawText = spec.DateRange?.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            Control<ArchiveDateEditor>("ArchiveEndDate").RawText = spec.DateRange?.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            foreach (string field in new[] { "text", "source", "board", "state", "capture", "posts", "sort", "work", "date" }) DraftEdited(field);
            RenderDraftSets(); UpdateTimeZoneLabel(); SearchEditorError("");
        }
        finally { _writingDraft = false; }
        SearchStateChanged?.Invoke();
    }
    private Action CaptureSearchDraft()
    {
        string text = Control<TextBox>("ArchiveSearchText").Text ?? "";
        string source = IdentityText("ArchiveSourceFilter"), board = IdentityText("ArchiveBoardFilter");
        string[] sources = _draftSources.ToArray(); ArchiveBoardConstraint[] boards = _draftBoards.ToArray();
        ThreadSourceState[] states = _draftStates.ToArray();
        string? zone = _draftTimeZone;
        string start = Control<ArchiveDateEditor>("ArchiveStartDate").RawText, end = Control<ArchiveDateEditor>("ArchiveEndDate").RawText;
        string[] names = { "ArchiveStateFilter", "ArchiveCaptureFilter", "ArchivePostsFilter", "ArchiveWorkFilter", "ArchiveSort" };
        int[] indices = names.Select(name => Control<ComboBox>(name).SelectedIndex).ToArray();
        BoardScope? scope = Control<ComboBox>("ArchiveBoardScope").SelectedItem as BoardScope;
        bool scopeChosen = _boardScopeChosen;
        return () =>
        {
            _writingDraft = true;
            try
            {
                Control<TextBox>("ArchiveSearchText").Text = text;
                _draftSources.Clear(); _draftSources.AddRange(sources); _draftBoards.Clear(); _draftBoards.AddRange(boards);
                _draftStates.Clear(); _draftStates.AddRange(states); SetIdentityText("ArchiveSourceFilter", source); SetIdentityText("ArchiveBoardFilter", board);
                for (int i = 0; i < names.Length; ++i) Control<ComboBox>(names[i]).SelectedIndex = indices[i];
                _draftTimeZone = zone; _boardScopeChosen = scopeChosen;
                Control<ArchiveDateEditor>("ArchiveStartDate").RawText = start; Control<ArchiveDateEditor>("ArchiveEndDate").RawText = end;
                RenderDraftSets();
                ComboBox combo = Control<ComboBox>("ArchiveBoardScope");
                if (combo.ItemsSource is IEnumerable<BoardScope> options) combo.SelectedItem = options.FirstOrDefault(item => item.Source == scope?.Source);
                UpdateTimeZoneLabel();
                foreach (string field in new[] { "text", "source", "board", "state", "capture", "posts", "work", "sort", "date" }) DraftEdited(field);
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
        foreach (string name in new[] { "ArchiveAddSource", "ArchiveAddBoard", "ArchiveAddState" }) Control<Button>(name).Content = Texts.T("archive.addFilter");
        AutomationProperties.SetName(Control<ComboBox>("ArchiveBoardScope"), Texts.T("archive.boardSourceScope"));
        Control<TextBlock>("ArchiveDateTitle").Text = Texts.T("archive.dateBasis");
        Control<TextBlock>("ArchiveDateHint").Text = Texts.T("archive.dateHint");
        Control<ArchiveDateEditor>("ArchiveStartDate").SetLabel(Texts.T("archive.dateFrom"));
        Control<ArchiveDateEditor>("ArchiveEndDate").SetLabel(Texts.T("archive.dateTo"));
        Control<TextBlock>("ArchiveDateFromLabel").Text = Texts.T("archive.dateFrom");
        Control<TextBlock>("ArchiveDateToLabel").Text = Texts.T("archive.dateTo");
        Control<Button>("ArchiveUseDeviceZone").Content = Texts.T("archive.useDeviceZone");
        Control<Button>("ArchiveRetrySearch").Content = Texts.T("action.retry");
        RenderDraftSets(); UpdateTimeZoneLabel();
    }
}
