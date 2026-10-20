"""Source offering checks: reproducibility and rejection of stale binary notices."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import plistlib
import subprocess
import tarfile
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("macos_sources", ROOT / "scripts/package-macos-sources.py")
SOURCES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SOURCES)


class MacSourceOverlayTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repository = self.root / "repo"
        self.repository.mkdir()
        self.base = self.root / "immutable-base.tar.gz"
        self.base.write_bytes(b"test source base, not the production source base")
        self.data = b"unchanged native data"
        self.native_names = ["lib/libvlc.dylib", "lib/libvlccore.dylib",
                             "lib/vlc/plugins/liblua_plugin.dylib", "lib/vlc/share/data"]
        original = [{"path": name, "official_app_path": name, "bytes": len(self.data),
                     "sha256": hashlib.sha256(self.data).hexdigest()} for name in self.native_names]
        inputs = {"source_base": SOURCES.file_record(self.base),
                  "architectures": {"osx-arm64": {"original_files": original}},
                  "coverage": {"finding": "fixture"}, "rebuild_provenance_uncertainties": ["fixture"]}
        self.files = {name: b"fixture committed source\n" for name in SOURCES.REQUIRED}
        self.files[SOURCES.INPUT_MANIFEST] = SOURCES.json_bytes(inputs)
        for name, data in self.files.items():
            path = self.repository / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        # This disposable repository tests Git snapshot behavior; the workspace is untouched.
        self.git("init", "--quiet")
        self.git("add", ".")
        self.git("-c", "user.name=Source fixture", "-c", "user.email=fixture@example.invalid",
                 "commit", "--quiet", "-m", "source fixture")
        self.commit = self.git("rev-parse", "HEAD").strip()
        self.binary = self.root / "MeowChan-0.9.0-osx-arm64.zip"
        self.write_zip()

    def git(self, *arguments):
        return subprocess.check_output(["git", "-C", str(self.repository), *arguments], text=True)

    def write_zip(self, omit=None, stale_notice=False):
        with zipfile.ZipFile(self.binary, "w") as archive:
            archive.writestr("MeowChan.app/Contents/Info.plist",
                             plistlib.dumps({"CFBundleShortVersionString": "0.9.0"}))
            for name in self.native_names:
                if name != omit:
                    data = b"new signed native bytes" if name.endswith(".dylib") else self.data
                    archive.writestr("MeowChan.app/Contents/Resources/application/libvlc/osx-arm64/" + name, data)
            for name in (SOURCES.INPUT_MANIFEST, "docs/native-video-macos-sources.md",
                         "docs/native-video-macos-COPYING", "docs/native-video-licenses.txt", "THIRD-PARTY-NOTICES.md"):
                data = b"stale notice" if stale_notice and name.endswith("-sources.md") else self.files[name]
                archive.writestr("MeowChan.app/Contents/Resources/application/" + name, data)

    def generate(self, name):
        output = self.root / name
        result = SOURCES.generate(self.repository, self.commit, self.base,
                                  {"osx-arm64": self.binary}, {}, "0.9.0", output)
        return output, result

    def test_identical_inputs_give_identical_archive_and_complete_mapping(self):
        # Uncommitted changes must never enter the source offering.
        (self.repository / "LICENSE").write_text("private uncommitted change")
        first, record = self.generate("first.tar.gz")
        second, other = self.generate("second.tar.gz")
        self.assertEqual(first.read_bytes(), second.read_bytes())
        self.assertEqual(record["sha256"], other["sha256"])
        with tarfile.open(first) as archive:
            manifest = json.load(archive.extractfile("manifest.json"))
            self.assertEqual(self.commit, manifest["application_source_commit"])
            inventory = json.load(archive.extractfile("binary-inventories/osx-arm64.json"))
            self.assertEqual(SOURCES.file_record(self.binary), inventory["signed_zip"])
            self.assertEqual(4, len(inventory["native_files"]))
            self.assertEqual(3, sum(row["bytes_changed"] for row in inventory["native_files"]))
            source = archive.extractfile("application-source.tar").read()
            with tarfile.open(fileobj=io.BytesIO(source)) as application:
                self.assertEqual(self.files["LICENSE"], application.extractfile("LICENSE").read())
            for line in archive.extractfile("SHA256SUMS").read().decode().splitlines():
                expected, name = line.split("  ", 1)
                self.assertEqual(expected, SOURCES.sha256(archive.extractfile(name).read()))

    def test_removed_plugin_is_rejected(self):
        self.write_zip(omit="lib/vlc/plugins/liblua_plugin.dylib")
        with self.assertRaisesRegex(ValueError, "native path set differs"):
            self.generate("rejected.tar.gz")
        self.assertFalse((self.root / "rejected.tar.gz").exists())

    def test_stale_notices_are_rejected(self):
        self.write_zip(stale_notice=True)
        with self.assertRaisesRegex(ValueError, "notice differs"):
            self.generate("rejected.tar.gz")

    def test_changed_base_and_non_exact_source_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "40-character"):
            SOURCES.committed_sources(self.repository, "HEAD")
        self.base.write_bytes(b"changed immutable source base")
        with self.assertRaisesRegex(ValueError, "base size/hash mismatch"):
            self.generate("rejected.tar.gz")

    def test_native_data_changes_and_existing_output_are_rejected(self):
        self.generate("existing.tar.gz")
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.generate("existing.tar.gz")
        self.data = b"unexpected changed native data"
        self.write_zip()
        with self.assertRaisesRegex(ValueError, "modified native data"):
            self.generate("rejected.tar.gz")


class MacSourceEvidenceTests(unittest.TestCase):
    def test_durable_input_mapping_covers_both_architectures_and_retained_gpl(self):
        inputs = json.loads((ROOT / SOURCES.INPUT_MANIFEST).read_text())
        for rid, count in (("osx-arm64", 397), ("osx-x64", 403)):
            rows = inputs["architectures"][rid]["original_files"]
            self.assertEqual(count, len(rows))
            self.assertEqual(count, len({row["path"] for row in rows}))
            for row in inputs["retained_gpl_modules"]:
                self.assertIn("lib/vlc/plugins/" + row["binary"], {item["path"] for item in rows})
        comparison = inputs["contrib_comparison"]
        self.assertEqual(86, len(comparison["rows"]))
        self.assertEqual([], comparison["summary"]["identified_missing_source_inputs"])
        self.assertEqual([], comparison["summary"]["identified_version_mismatches"])
        for row in comparison["rows"]:
            self.assertTrue(row["base_inputs"])
            self.assertTrue(all(item["present"] for item in row["base_inputs"]))
        helper = ROOT / "scripts/native-video/package-nuget-mac.sh"
        self.assertEqual(inputs["upstream_packaging"]["script_sha256"], SOURCES.sha256(helper.read_bytes()))


if __name__ == "__main__":
    unittest.main()
