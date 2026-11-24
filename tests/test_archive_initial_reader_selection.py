"""No-build wiring contracts; headless behavior lives in ArchiveInitialReaderSelectionTests."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
ARCHIVE = ROOT / "Desktop/Views/Archive"


class InitialReaderSelectionContracts(unittest.TestCase):
    def test_selection_uses_complete_filtered_order_and_retains_current_post(self):
        source = (ARCHIVE / "ArchivePage.axaml.cs").read_text()
        render = source.split("private void RenderThread(", 1)[1].split("private readonly record struct PostRowContext", 1)[0]
        self.assertIn("new ArchivePostRow[projection?.Count ?? 0]", render)
        self.assertIn("selected = projection.PositionOf(retained)", render)
        self.assertLess(render.index("postList.ItemsSource = posts;"), render.index("postList.SelectedIndex = selected;"))
        self.assertIn("_readerPostId = selected >= 0 ? posts[selected].Id : null;", render)
        self.assertNotIn(".Focus(", render)
        self.assertNotIn("FocusPostAsync(", render)
        self.assertNotIn("ReaderPositionChanged(", render)
        self.assertIn("ArchivePosts.AutoScrollToSelectedItem = false;", source)

    def test_post_identity_does_not_leak_across_threads(self):
        source = (ARCHIVE / "ArchivePage.axaml.cs").read_text()
        load = source.split("private async Task LoadThreadAsync(", 1)[1].split("private void RenderThread(", 1)[0]
        self.assertLess(load.index("_readerPostId = null;"), load.index("_selectedThread = id;"))

    def test_selection_drives_both_media_presentations(self):
        media = (ARCHIVE / "ArchivePage.ReaderMedia.cs").read_text()
        sidebar = (ARCHIVE / "ArchivePage.MediaSidebar.cs").read_text()
        self.assertIn("ArchivePosts.SelectionChanged += (_, _) => AnchorFocusedCard();", media)
        self.assertIn("OnMediaSidebarSelectionChanged()", sidebar)
        self.assertIn("ArchivePostRow? post = ArchivePosts.SelectedItem as ArchivePostRow;", sidebar)
        self.assertIn("_sidebarIndex = _sidebarPendingIndex ?? 0;", sidebar)

    def test_restored_and_explicit_selection_remain_authoritative(self):
        reader = (ARCHIVE / "ArchivePage.Reader.cs").read_text()
        navigation = (ARCHIVE / "ArchivePage.Navigation.cs").read_text()
        self.assertIn("list.SelectedItem = selected;", reader)
        self.assertIn("_readerPostId = selected.Id;", reader)
        self.assertIn("_projectionSelectionPost = postId;", navigation)


if __name__ == "__main__":
    unittest.main()
