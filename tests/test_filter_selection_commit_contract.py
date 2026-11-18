"""Static filter-commit contracts; no Avalonia host, profile, decoder or DB.

These pin the current event wiring rather than simulate native input. Actual
mouse/Enter equivalence still needs the existing filter-pointer-selection and
media-page desktop smokes on the integrated candidate.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def source(relative):
    return (ROOT / relative).read_text(encoding="utf-8")


def section(text, start, end):
    begin = text.index(start)
    return text[begin:text.index(end, begin + len(start))]


class FilterSelectionCommitContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.media = source("Desktop/Views/Media/MediaPage.Filters.cs")
        cls.editors = source("Desktop/Views/Archive/ArchivePage.SearchEditors.cs")
        cls.navigation = source("Desktop/Views/Archive/ArchivePage.Navigation.cs")
        cls.terms = source("Desktop/MediaFilterTerm.cs")

    def assert_ordered(self, text, *fragments):
        position = 0
        for fragment in fragments:
            found = text.find(fragment, position)
            self.assertGreaterEqual(found, 0, f"Missing or out-of-order fragment: {fragment}")
            position = found + len(fragment)

    def test_media_choices_notify_from_selection_not_only_enter(self):
        row = section(self.media, "private Control BuildFilterRow()", "private void FilterEdited()")
        # Tag mode is now a TwoWayToggle; the other choices update the canonical term set.
        self.assertIn("_tagMode.SelectionChanged += (_, _) => FilterEdited();", row)
        for control in ("_kindFilter", "_soundFilter", "_sourceFilter", "_boardFilter", "_minRating"):
            with self.subTest(control=control):
                handler = section(row, control + ".SelectionChanged +=", "};")
                self.assert_ordered(handler, "if (_localizing ||", "return;", "EditTerms(terms =>")
        edit = section(self.media, "private void EditTerms(", "private bool IsValidFilter(")
        self.assert_ordered(edit, "next = change(_terms.ToArray());", "_terms.Clear(); _terms.AddRange(next);",
                            "SyncFilterControls();", "RenderTagOptions();", "FilterEdited();")
        edited = section(self.media, "private void FilterEdited()", "private void PresentEditedFilters()")
        self.assert_ordered(edited, "if (_localizing) return;", "PresentEditedFilters();")
        present = section(self.media, "private void PresentEditedFilters()", "private void InstallFilterHandoffInterruption()")
        self.assert_ordered(present, "_nameFilter.CancelPendingQuery();", "RenderFilterChips();", "Present();")
        self.assertNotIn("Key.Enter", row + edit + edited + present)

    def test_media_selection_writes_values_and_all_choices_clear_their_field(self):
        row = section(self.media, "private Control BuildFilterRow()", "private void FilterEdited()")
        writes = {
            "_kindFilter": (
                "int index = _kindFilter.SelectedIndex;",
                "index is < 0 or > (int)MediaLibraryKindFilter.Other",
                "index == 0 ? MediaFilterTermSet.WithTypes(terms) : MediaFilterTermSet.WithTypes(terms, (MediaLibraryKindFilter)index)"),
            "_soundFilter": (
                "_soundFilter.SelectedIndex < 0",
                "_soundFilter.SelectedIndex == 0 ? MediaFilterTermSet.WithoutField(terms, MediaFilterField.Sound)",
                "MediaFilterTermSet.With(terms, MediaFilterTerm.Sound((MediaLibrarySoundFilter)_soundFilter.SelectedIndex))"),
            "_sourceFilter": (
                "_sourceFilter.SelectedItem is not Choice { Summary: false } choice",
                "choice.Value == null ? MediaFilterTermSet.WithoutField(terms, MediaFilterField.Source, exclude: false)",
                "MediaFilterTermSet.With(terms, MediaFilterTerm.Source(choice.Value))"),
            "_boardFilter": (
                "_boardFilter.SelectedItem is not BoardChoice { Summary: false } choice",
                "choice is { Board: { } board, Source: { } source } ? MediaFilterTermSet.With(terms, MediaFilterTerm.Board(source, board))",
                "MediaFilterTermSet.WithoutField(terms, MediaFilterField.Board, exclude: false)"),
            "_minRating": (
                "_minRating.SelectedIndex < 0",
                "_minRating.SelectedIndex == 0 ? MediaFilterTermSet.WithoutField(terms, MediaFilterField.Rating)",
                "MediaFilterTermSet.With(terms, MediaFilterTerm.Rating(_minRating.SelectedIndex))"),
        }
        for control, fragments in writes.items():
            handler = section(row, control + ".SelectionChanged +=", "};")
            for fragment in fragments:
                with self.subTest(control=control, fragment=fragment):
                    self.assertIn(fragment, handler)
        for control, field in (("_favoritesOnly", "Favorite"), ("_withoutMediaTags", "Untagged"), ("_missing", "Missing")):
            with self.subTest(control=control):
                self.assertIn(f"WireFlag({control}, MediaFilterField.{field});", row)
        flags = section(self.media, "private void WireFlag(", "internal MediaLibraryFilter CurrentFilter()")
        self.assert_ordered(flags, "box.IsCheckedChanged +=", "if (_localizing) return;",
                            "EditTerms(terms => box.IsChecked == true ? MediaFilterTermSet.With(terms, MediaFilterTerm.Flag(field))",
                            "MediaFilterTermSet.WithoutField(terms, field)")

    def test_media_current_filter_converts_terms_with_numeric_rating_and_qualified_board(self):
        # Follow the selection -> EditTerms -> AllTerms -> ToFilter path, not the now display-only combos.
        read = section(self.media, "internal MediaLibraryFilter CurrentFilter()", "private void SyncFilterControls()")
        self.assertIn("MediaFilterTerms.ToFilter(AllTerms(), _tagMode.SelectedIndex == 1 ? ArchiveTagMatch.Any : ArchiveTagMatch.All)", read)
        for control in ("_kindFilter", "_soundFilter", "_sourceFilter", "_boardFilter", "_minRating"):
            self.assertNotIn(control, read)
        all_terms = section(self.media, "private IEnumerable<MediaFilterTerm> AllTerms()", "private bool HasField(")
        self.assert_ordered(all_terms, "foreach (MediaFilterTerm term in _terms) yield return term;",
                            "if (!MediaLibraryFilter.HasText(_nameFilter.Text)) yield break;",
                            "yield return MediaFilterTerm.Name(_nameFilter.Text!);")
        rating = section(self.terms, "case MediaFilterField.Rating:", "case MediaFilterField.Name:")
        self.assertIn("int.TryParse(term.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int least)", rating)
        self.assertIn("least is < 1 or > 5", rating)
        self.assertIn("throw new ArgumentException", rating)
        self.assertIn("rating = Math.Max(rating ?? 0, least);", rating)
        board = section(self.terms, "case MediaFilterField.Board:", "case MediaFilterField.Rating:")
        self.assertIn("else { board = term.Value; boardSource = term.Scope; }", board)
        translation = section(self.terms, "if (board != null)\n", "/// <summary>Whether the terms ask to show missing files")
        self.assert_ordered(translation, "if (source != null && source != boardSource) throw new ArgumentException",
                            "source = boardSource;", "return new MediaLibraryFilter(", "favorites, rating,", "source, board,")

    def test_media_control_synchronization_cannot_commit_summary_or_localization(self):
        # Re-rendering selections (including multi-value/excluded summaries) must not re-enter EditTerms and erase terms.
        for start, end in (
            ("private void SyncFilterControls()", "private void RenderKindOptions()"),
            ("private void RenderKindOptions()", "private Control BuildStatusRow("),
            ("private void RenderSourceOptions()", "private static string SourceLabel("),
            ("private void RenderBoardOptions()", "private void LocalizeFilters()"),
        ):
            with self.subTest(method=start):
                sync = section(self.media, start, end)
                self.assert_ordered(sync, "bool localizing = _localizing;", "_localizing = true;", "try",
                                    "finally { _localizing = localizing; }")
                self.assertNotIn("EditTerms(", sync)
        # The selector handlers above reject these synthetic summaries rather than treating them as 'All'.
        self.assertIn("Summary: true", section(self.media, "private void RenderSourceOptions()", "private static string SourceLabel("))
        self.assertIn("Summary: true", section(self.media, "private void RenderBoardOptions()", "private void LocalizeFilters()"))

    def test_archive_state_commits_after_selection_or_popup_close(self):
        wire = section(self.editors, "private void InitializeStateEditor()", "private void RenderDraftSets()")
        for event in ("SelectionChanged", "DropDownClosed"):
            with self.subTest(event=event):
                self.assertIn(f"ArchiveStateFilter.{event} += (_, _) => Dispatcher.UIThread.Post(AddChosenState);", wire)
        # Merely navigating from the closed combo opens it, not a choice/chip.
        self.assertIn("e.Key is not (Key.Up or Key.Down)", wire)
        self.assertIn("ArchiveStateFilter.IsDropDownOpen = true; e.Handled = true;", wire)
        commit = section(self.editors, "private void AddChosenState()", "private void InitializeStateEditor()")
        self.assert_ordered(commit,
                            "if (_localizingSearchOptions || _writingDraft || ArchiveStateFilter.IsDropDownOpen) return;",
                            "ArchiveStateFilter.SelectedIndex switch", "_ => null", "if (state == null) return;",
                            "if (!_draftStates.Contains(state.Value)) _draftStates.Add(state.Value);",
                            'ArchiveStateFilter.SelectedIndex = 0; DraftEdited("state"); RenderDraftSets();')
        self.assertNotIn("ApplySearchAsync", commit + wire)

    def test_archive_regular_choices_remain_draft_until_search_reads_them(self):
        wire = section(self.editors, "private void InitializeSearchEditors()", "private void DateEdited()")
        for control, field in (("ArchiveCaptureFilter", "capture"), ("ArchivePostsFilter", "posts"),
                               ("ArchiveWorkFilter", "work"), ("ArchiveSort", "sort")):
            with self.subTest(control=control):
                self.assertIn(f'("{control}", "{field}")', wire)
        self.assertIn('SelectionChanged += (_, _) => { if (!_localizingSearchOptions) DraftEdited(entry.Item2); };', wire)
        read = section(self.editors, "private ArchiveSearchSpec ReadSearchDraft(", "private void WriteSearchDraft(")
        self.assertIn('Control<ComboBox>(name).SelectedIndex switch { 1 => true, 2 => false, _ => null }', read)
        self.assertIn('Choice("ArchiveCaptureFilter"), Choice("ArchivePostsFilter")', read)
        self.assertIn("WorkChoice(ArchiveWorkFilter.SelectedIndex)", read)
        self.assertIn("ArchiveSort.SelectedIndex", read)

    def test_identity_click_enter_and_search_share_the_validated_commit(self):
        suggestions = source("Desktop/Views/Archive/ArchivePage.Suggestions.cs")
        wire = section(suggestions, "private void InitializeSuggestions()", "private ArchiveSuggestionCache CreateSuggestionCache()")
        self.assert_ordered(wire, "editor.CandidateAccepted +=", "Dispatcher.UIThread.Post(",
                            "IdentitySuggestions.CurrentSelection(editor.Text, editor.AcceptedCandidate)",
                            "AddIdentityAsync(isBoard, item)")
        self.assertNotIn("editor.SelectionChanged +=", wire)  # highlight is not acceptance
        enter = section(self.navigation, "private async void OnSearchKeyDown(", "internal async Task ApplySearchAsync()")
        self.assert_ordered(enter, "e.Key != Key.Enter", "!string.IsNullOrWhiteSpace(editor.Text)",
                            "await AddIdentityAsync(ReferenceEquals(editor, ArchiveBoardFilter)); return;",
                            "await ApplySearchAsync();")
        apply = section(self.navigation, "internal async Task ApplySearchAsync()", "private void ShowSearchError(")
        self.assert_ordered(apply, "AddChosenState();", "await AddIdentityAsync(",
                            "_invalidSearchEditor = editor;", "return;", "CreateSearchRequest(ReadSearchDraft())")

    def test_invalid_or_superseded_identity_does_not_mutate_the_draft(self):
        commit = section(self.editors, "private async Task<bool> AddIdentityAsync(", "private void AddChosenState()")
        self.assert_ordered(commit, "accepted ??= TryReadSelectedIdentity(name);", "await ReadSuggestionsAsync(",
                            "if (_ended || _editorRevisions.GetValueOrDefault(field) != revision) return false;",
                            "IdentitySuggestions.Resolve(raw, null, candidates)",
                            "ArchiveSearchSpec.FromExactIdentities(",
                            "catch (Exception exception) { DataValidationErrors.SetErrors(editor, new object[] { exception.Message }); return false; }",
                            "if (!duplicate)", 'SetIdentityText(name, ""); editor.Dismiss();')
        self.assertNotIn("ApplySearchAsync", commit)

    def test_identity_escape_dismisses_without_accepting(self):
        # TP1 shared control is read-only here; keep its existing explicit-accept boundary.
        box = source("Desktop/IdentitySuggestionBox.cs")
        keys = section(box, "private void OnInputKey(", "private IdentitySuggestion[] ShownCandidates()")
        escape = section(keys, "if (e.Key == Key.Escape)", "if (e.Key == Key.Tab)")
        self.assert_ordered(escape, "bool listWasOpen = IsDropDownOpen;", "Dismiss();",
                            "if (listWasOpen || !ReleaseEscape) e.Handled = true;", "return;")
        self.assertNotIn("AcceptCandidate", escape)
        self.assertNotIn("Complete(", escape)
        self.assertNotIn("SetCurrentValue", escape)
        self.assertIn("internal bool ReleaseEscape { get; set; }", box)  # opt-in; default still consumes Escape
        dismiss = section(box, "internal void Dismiss()", "internal void InvalidateIdentityContext()")
        self.assert_ordered(dismiss, "++_revision; _suppressed = true;", "CancelPointerGesture();",
                            "_request?.Cancel();", "IsDropDownOpen = false;")
        self.assertNotIn("AcceptCandidate", dismiss)
        self.assertNotIn("SetCurrentValue", dismiss)
        self.assert_ordered(keys, "if (e.Key != Key.Enter || !IsDropDownOpen) return;", "AcceptCandidate();", "e.Handled = true;")
        selected = section(source("Desktop/Views/Archive/ArchivePage.Suggestions.cs"),
                           "internal IdentitySuggestion? TryReadSelectedIdentity(", "internal void InvalidateBoardSuggestions()")
        self.assertIn("IdentitySuggestions.CurrentSelection(editor.Text, editor.AcceptedCandidate)", selected)
        self.assertNotIn("editor.SelectedItem", selected)


if __name__ == "__main__":
    unittest.main()
