"""Small source-only WV11 checks. These do NOT replace C# or native layout/a11y tests."""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
MODEL = ROOT / "Desktop/WorkViews/WorkViewOverview.cs"
CONTROL = ROOT / "Desktop/Views/Settings/WorkViewOverviewControl.cs"
CATALOG = ROOT / "Desktop/Localization/WorkViewOverviewCatalog.cs"


def method(source, signature):
    """Extract these block-bodied methods (strings/comments here contain no unmatched braces)."""
    start = source.index("{", source.index(signature))
    depth = 1
    end = start + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


class WorkViewOverviewSourceContracts(unittest.TestCase):
    def test_localization_literals_are_supplied_in_the_new_catalog(self):
        catalog = CATALOG.read_text()
        entries = re.findall(r'\("([^"]+)", "([^"]*)", "([^"]*)"\)', catalog)
        self.assertGreater(len(entries), 0)
        keys = {key for key, _, _ in entries}
        self.assertEqual(len(entries), len(keys))
        referenced = set(re.findall(r'"((?:workview\.(?:overview|presence|column)\.|settings\.section\.workViews)[^"]*)"',
                                    MODEL.read_text() + CONTROL.read_text()))
        # The page key is deliberately computed for the enum; new pages get a humanized fallback.
        referenced.discard("workview.overview.page.")
        self.assertFalse(referenced - keys, referenced - keys)
        for key, english, german in entries:
            self.assertTrue(english.strip(), key)
            self.assertTrue(german.strip(), key)
            self.assertEqual(re.findall(r"\{\d+\}", english), re.findall(r"\{\d+\}", german), key)
            for glyph in ("🔒", "→", "●", "🔍", "⋯"):
                self.assertNotIn(glyph, english + german, key)

    def test_projection_has_no_ui_registry_or_persistence_dependency(self):
        source = MODEL.read_text()
        self.assertNotRegex(source, r"using Avalonia|WorkViewElement\.|Dispatcher\.|new Config\b|\.Take\(")
        self.assertIn("WorkViewPolicy.Entries", source)
        self.assertIn("CommandCatalog.Label(command)", source)
        self.assertIn("registeredName?.Invoke(entry.Id)", source)
        self.assertLess(source.index("CommandCatalog.Label(command)"), source.index("registeredName?.Invoke(entry.Id)"))

    def test_construction_and_filtering_remain_lazy_and_read_only(self):
        source = CONTROL.read_text()
        constructor = method(source, "internal WorkViewOverviewControl(")
        self.assertNotRegex(constructor, r"new DataGrid|WorkViewOverview.Build|EnsureLoaded\(")
        for signature in ("internal void SetActiveView(", "internal void SetPageFilter(", "internal void SetTextFilter("):
            self.assertNotIn("EnsureLoaded()", method(source, signature))
        filtered = method(source, "private void RefreshFilter()")
        self.assertIn("_view?.Refresh()", filtered)
        self.assertNotRegex(filtered, r"new DataGrid|WorkViewOverview.Build|RefreshTextAndRows\(")
        self.assertNotRegex(source, r"Texts\.Changed\s*\+=|new Config\b|Config\.Save|new .*Page\(")
        self.assertIn("if (IsOverviewLoaded) RefreshTextAndRows();", method(source, "internal void LocaleRefresh()"))

    def test_ui_registry_snapshot_is_confined_and_lifecycle_hooks_exist(self):
        source = CONTROL.read_text()
        capture = method(source, "private void RefreshTextAndRows()")
        self.assertLess(capture.index("Dispatcher.UIThread.VerifyAccess()"), capture.index("WorkViewElement.All()"))
        self.assertEqual(1, source.count("foreach (Control control in WorkViewElement.All())"))
        self.assertIn("ObjectDisposedException.ThrowIf", source)
        disposed = method(source, "public void Dispose()")
        for cleanup in ("AttachedToVisualTree -=", "KeyDown -=", "SelectionChanged -=", "TextChanged -=", "_cells.Clear()", "Content = null"):
            self.assertIn(cleanup, disposed)

    def test_table_source_declares_wrapping_star_widths_and_semantic_names(self):
        # Source contracts only. They do not claim a 640x480 screenshot, virtualization timing or SR acceptance.
        source = CONTROL.read_text()
        self.assertIn("RowHeight = double.NaN", source)
        self.assertIn("MinColumnWidth = 0", source)
        self.assertIn("HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled", source)
        self.assertEqual(2, source.count("DataGridLengthUnitType.Star"))
        self.assertIn("TextWrapping = TextWrapping.Wrap", source)
        self.assertIn("new Binding(nameof(WorkViewOverview.Row.AccessibleName))", source)
        self.assertIn("AutomationProperties.SetName(cell, name)", source)
        self.assertIn("supportsRecycling: false", source)
        self.assertIn("KeyModifiers.Meta : KeyModifiers.Control", source)
        self.assertNotRegex(source, r"RowHeight\s*=\s*\d|Width\s*=\s*new DataGridLength\(\d+\)")


if __name__ == "__main__":
    unittest.main()
