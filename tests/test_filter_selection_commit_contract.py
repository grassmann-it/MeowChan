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

    def assert_ordered(self, text, *fragments):
        position = 0
        for fragment in fragments:
            found = text.find(fragment, position)
            self.assertGreaterEqual(found, 0, f"Missing or out-of-order fragment: {fragment}")
            position = found + len(fragment)

    def test_media_choices_notify_from_selection_not_only_enter(self):
        row = section(self.media, "private Control BuildFilterRow()", "private void FilterEdited()")
        self.assertRegex(row, r"foreach \(ComboBox combo in new\[\] \{ _minRating, _tagMode \}\)\s*"
                              r"combo.SelectionChanged \+= \(_, _\) => FilterEdited\(\);")
        for control in ("_kindFilter", "_soundFilter", "_sourceFilter", "_boardFilter"):
            with self.subTest(control=control):
                handler = section(row, control + ".SelectionChanged +=", "};")
                self.assertIn("FilterEdited();", handler)
        edited = section(self.media, "private void FilterEdited()", "private void OnOwnerActivated(")
        self.assert_ordered(edited, "if (_localizing) return;", "RenderFilterChips();", "Present();")
        self.assertNotIn("Key.Enter", row + edited)

    def test_media_filter_reads_selected_values_including_numeric_rating(self):
        read = section(self.media, "internal MediaLibraryFilter CurrentFilter()", "private Control BuildStatusRow(")
        for fragment in (
            "_boardFilter.SelectedItem as BoardChoice",
            "(_sourceFilter.SelectedItem as Choice)?.Value ?? board?.Source",
            "_kindFilter.SelectedIndex", "_soundFilter.SelectedIndex", "_tagMode.SelectedIndex",
            "_minRating.SelectedIndex > 0 ? _minRating.SelectedIndex : null",
            "source, board?.Board",
        ):
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, read)

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
        self.assertIn("if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }", keys)
        self.assert_ordered(keys, "if (e.Key != Key.Enter || !IsDropDownOpen) return;", "AcceptCandidate();", "e.Handled = true;")
        selected = section(source("Desktop/Views/Archive/ArchivePage.Suggestions.cs"),
                           "internal IdentitySuggestion? TryReadSelectedIdentity(", "internal void InvalidateBoardSuggestions()")
        self.assertIn("IdentitySuggestions.CurrentSelection(editor.Text, editor.AcceptedCandidate)", selected)
        self.assertNotIn("editor.SelectedItem", selected)


if __name__ == "__main__":
    unittest.main()
