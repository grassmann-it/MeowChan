"""Focused regressions for the manifest-only contract; no .NET or target code runs."""
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


PROBE_PATH = Path(__file__).with_name("cross_publish_probe.py")
SPEC = importlib.util.spec_from_file_location("cross_publish_probe", PROBE_PATH)
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class ManifestContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="compiler-probe-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "package"
        self.root.mkdir()
        entries = []
        for path, role in (
            ("MeowChan.PluginCompiler.dll", "compiler"),
            ("Microsoft.CodeAnalysis.dll", "compiler"),
            ("Microsoft.CodeAnalysis.CSharp.dll", "compiler"),
            ("MeowChan.PluginSdk.dll", "sdk"),
            ("references/System.Runtime.dll", "framework"),
        ):
            target = self.root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            data = ("fixture bytes for " + path).encode()
            target.write_bytes(data)
            entries.append({"Path": path, "Sha256": hashlib.sha256(data).hexdigest(), "Role": role})
        self.manifest = {
            "SchemaVersion": 1,
            "TargetFramework": "net10.0",
            "Options": probe.EXPECTED_OPTIONS,
            "Files": entries,
            "CompilerFingerprint": probe.file_set_hash([entry for entry in entries if entry["Role"] == "compiler"]),
            "SdkFingerprint": next(entry["Sha256"] for entry in entries if entry["Role"] == "sdk"),
            "ReferenceFingerprint": probe.file_set_hash([entry for entry in entries if entry["Role"] != "compiler"]),
        }
        self.write_manifest()

    def write_manifest(self):
        (self.root / "toolchain.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def test_probe_options_match_the_product_contract(self):
        source = PROBE_PATH.parents[2] / "MeowChan.PluginHost/Compilation/CompilerWire.cs"
        match = re.search(r'internal const string Options = "([^"]+)";', source.read_text(encoding="utf-8"))
        self.assertIsNotNone(match)
        self.assertEqual(match.group(1), probe.EXPECTED_OPTIONS)

    def test_valid_manifest_is_accepted(self):
        self.assertEqual(5, probe.inspect_manifest(self.root)["fileCount"])

    def test_mutated_options_are_rejected_with_all_other_hashes_unchanged(self):
        original = dict(self.manifest)
        for value in ("stale", "", None):
            with self.subTest(options=value):
                self.manifest["Options"] = value
                self.write_manifest()
                self.assertEqual(original["Files"], self.manifest["Files"])
                self.assertEqual(original["CompilerFingerprint"], self.manifest["CompilerFingerprint"])
                with self.assertRaisesRegex(ValueError, "Unexpected compiler options"):
                    probe.inspect_manifest(self.root)
        self.manifest.pop("Options")
        self.write_manifest()
        with self.assertRaisesRegex(ValueError, "Unexpected compiler options"):
            probe.inspect_manifest(self.root)

    def test_root_link_is_rejected_without_resolving_it_away(self):
        link = self.root.parent / "linked-package"
        link.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Symbolic links/reparse points"):
            probe.inspect_manifest(link)
        result = subprocess.run([sys.executable, str(PROBE_PATH), str(link), "linux-x64"], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Symbolic links/reparse points", result.stderr)

    def test_manifest_link_inside_same_package_is_rejected(self):
        (self.root / "toolchain.json").rename(self.root / "actual-toolchain.json")
        (self.root / "toolchain.json").symlink_to("actual-toolchain.json")
        with self.assertRaisesRegex(ValueError, "Symbolic links/reparse points"):
            probe.inspect_manifest(self.root)

    def test_directory_link_inside_same_package_is_rejected(self):
        (self.root / "references").rename(self.root / "actual-references")
        (self.root / "references").symlink_to("actual-references", target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Symbolic links/reparse points"):
            probe.inspect_manifest(self.root)

    def test_file_link_inside_same_package_is_rejected(self):
        original = self.root / "references/System.Runtime.dll"
        original.rename(original.with_name("actual-System.Runtime.dll"))
        original.symlink_to("actual-System.Runtime.dll")
        with self.assertRaisesRegex(ValueError, "Symbolic links/reparse points"):
            probe.inspect_manifest(self.root)


if __name__ == "__main__":
    unittest.main(verbosity=2)
