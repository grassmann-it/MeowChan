"""Release policy failures; no .NET process or inventory promotion."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("public_api_policy", Path(__file__).resolve().parents[1] / "scripts/check-public-api.py")
policy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(policy)


class PublicApiPolicyTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        for project in policy.PROJECTS:
            folder = self.root / project
            folder.mkdir()
            (folder / "PublicAPI.Shipped.txt").write_text("#nullable enable\nExisting.Contract\n")
            (folder / "PublicAPI.Unshipped.txt").write_text("#nullable enable\n\n")

    def test_empty_inventory_allows_release_without_changing_bytes(self):
        before = {path: path.read_bytes() for path in self.root.rglob("*.txt")}
        report = policy.audit(self.root)
        policy.require_release(report)
        self.assertTrue(report["releaseReady"])
        self.assertEqual(before, {path: path.read_bytes() for path in before})

    def test_addition_and_removal_both_require_explicit_release_review(self):
        for api in ("New.Contract", "*REMOVED*Existing.Contract"):
            with self.subTest(api=api):
                path = self.root / "libmeowchan/PublicAPI.Unshipped.txt"
                path.write_text("#nullable enable\n" + api + "\n")
                report = policy.audit(self.root)
                self.assertFalse(report["releaseReady"])
                self.assertEqual(api, report["projects"]["libmeowchan"]["unshipped"]["entries"][0]["api"])
                with self.assertRaisesRegex(ValueError, "libmeowchan=1"):
                    policy.require_release(report)

    def test_missing_inventory_fails_closed(self):
        (self.root / "MeowChan.PluginSdk/PublicAPI.Unshipped.txt").unlink()
        with self.assertRaises(FileNotFoundError):
            policy.audit(self.root)

    def test_comments_in_either_inventory_fail_even_without_release_check(self):
        for kind in ("Shipped", "Unshipped"):
            with self.subTest(kind=kind):
                path = self.root / "MeowChan.PluginSdk" / ("PublicAPI." + kind + ".txt")
                before = path.read_bytes()
                path.write_text("#nullable enable\n// Reviewed URSA additions (2026-10-24)\n")
                with self.assertRaisesRegex(ValueError, r"MeowChan.PluginSdk/PublicAPI\..*comments are not supported"):
                    policy.audit(self.root)
                path.write_bytes(before)

    def test_only_exact_nullable_enable_at_first_nonblank_position_is_a_marker(self):
        for kind in ("Shipped", "Unshipped"):
            for text in ("#nullable disable\n", "#unknown Contract\n", " #nullable enable\n",
                         "#nullable enable \n", "Existing.Contract\n#nullable enable\n",
                         "#nullable enable\n#nullable enable\n"):
                with self.subTest(kind=kind, text=text):
                    path = self.root / "MeowChan.PluginSdk" / ("PublicAPI." + kind + ".txt")
                    before = path.read_bytes()
                    path.write_text(text)
                    with self.assertRaises(ValueError):
                        policy.audit(self.root)
                    path.write_bytes(before)
        self.assertEqual([], policy.entries(b"\xef\xbb\xbf\n \n#nullable enable\n\n"))
        self.assertEqual([], policy.entries(b"\n\n"))



if __name__ == "__main__":
    unittest.main()
