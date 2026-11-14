"""Offline safety tests for the SDK package-only consumer gate.

These tests deliberately never invoke dotnet, NuGet, a provider, a GUI, or a
network.  The real restore/build/run lane belongs to the parent heavy.lock job.
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "verify-sdk-package-consumer.py"
SPEC = importlib.util.spec_from_file_location("verify_sdk_package_consumer", SCRIPT)
RUNNER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(RUNNER)

SOURCE_COMMIT = "a" * 40
PRODUCT_VERSION = "1.1.0"


def make_package(path: Path, *, package_id: str = RUNNER.PACKAGE_ID, version: str = PRODUCT_VERSION,
                 commit: str = SOURCE_COMMIT, traversal: bool = False) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    names = {
        "_rels/.rels": b"placeholder",
        "lib/net10.0/MeowChan.PluginSdk.dll": b"sdk assembly",
        "meowchan.pluginsdk.nuspec": (
            f"""<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata>
            <id>{package_id}</id><version>{version}</version><authors>test</authors>
            <repository type=\"git\" url=\"https://example.test/repo\" commit=\"{commit}\" />
            </metadata></package>"""
        ).encode(),
    }
    if traversal:
        names["../escaped.txt"] = b"must not be accepted"
    with zipfile.ZipFile(path, "w") as archive:
        for name, value in names.items():
            archive.writestr(name, value)


def valid_candidate(path: Path) -> dict:
    return RUNNER.inspect_package(path, PRODUCT_VERSION, SOURCE_COMMIT)


class SdkPackageConsumerSafetyTests(unittest.TestCase):
    def test_package_pin_version_and_id_are_exact(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            wrong_version = root / "wrong-version.nupkg"
            make_package(wrong_version, version="1.0.0")
            with self.assertRaisesRegex(RUNNER.GateError, "version"):
                RUNNER.inspect_package(wrong_version, PRODUCT_VERSION, SOURCE_COMMIT)

            wrong_id = root / "wrong-id.nupkg"
            make_package(wrong_id, package_id="Not.MeowChan.PluginSdk")
            with self.assertRaisesRegex(RUNNER.GateError, "ID"):
                RUNNER.inspect_package(wrong_id, PRODUCT_VERSION, SOURCE_COMMIT)

            wrong_commit = root / "wrong-commit.nupkg"
            make_package(wrong_commit, commit="b" * 40)
            with self.assertRaisesRegex(RUNNER.GateError, "repository commit"):
                RUNNER.inspect_package(wrong_commit, PRODUCT_VERSION, SOURCE_COMMIT)

    def test_archive_path_traversal_is_rejected_before_metadata_is_used(self):
        with tempfile.TemporaryDirectory() as temporary:
            package = Path(temporary) / "traversal.nupkg"
            make_package(package, traversal=True)
            with self.assertRaisesRegex(RUNNER.GateError, "traversal"):
                RUNNER.inspect_package(package, PRODUCT_VERSION, SOURCE_COMMIT)

    def test_consumer_project_rejects_project_and_direct_dll_references(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for tag in ("ProjectReference", "Reference"):
                project = root / f"{tag}.csproj"
                project.write_text(
                    f"""<Project><ItemGroup><{tag} Include=\"../MeowChan.PluginSdk/MeowChan.PluginSdk.csproj\" /></ItemGroup></Project>""",
                    encoding="utf-8",
                )
                with self.assertRaisesRegex(RUNNER.GateError, "forbidden"):
                    RUNNER.inspect_consumer_project(project)

    def test_nuget_config_has_only_local_candidate_source_and_mapping(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            feed = root / "empty-feed"
            feed.mkdir()
            config = root / "NuGet.Config"
            RUNNER.write_nuget_config(config, feed)
            text = config.read_text(encoding="utf-8")
            self.assertIn('key="candidate"', text)
            self.assertIn('pattern="MeowChan.PluginSdk"', text)
            self.assertNotIn("nuget.org", text)
            self.assertNotIn("http://", text)
            self.assertNotIn("https://", text)

    def test_assets_reject_project_reference_and_accept_exact_package_identity(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root / "MeowChan.PluginSdk.1.1.0.nupkg"
            make_package(package)
            candidate = valid_candidate(package)
            cache = root / "packages"
            restored = cache / "meowchan.pluginsdk" / "1.1.0"
            restored.mkdir(parents=True)
            (restored / package.name).write_bytes(package.read_bytes())
            (restored / "lib" / "net10.0").mkdir(parents=True)
            (restored / "lib" / "net10.0" / "MeowChan.PluginSdk.dll").write_bytes(b"sdk assembly")
            assets = {
                "packageFolders": {str(cache) + "/": {}},
                "libraries": {
                    "MeowChan.PluginSdk/1.1.0": {
                        "type": "package", "path": "meowchan.pluginsdk/1.1.0",
                    },
                },
                "targets": {
                    "net10.0": {"MeowChan.PluginSdk/1.1.0": {"type": "package"}},
                },
            }
            evidence = RUNNER.validate_assets(assets, cache, candidate)
            self.assertEqual(candidate["sha256"], evidence["package"]["packageSha256"])

            project_assets = json.loads(json.dumps(assets))
            project_assets["libraries"]["MeowChan.PluginSdk/1.1.0"]["type"] = "project"
            with self.assertRaisesRegex(RUNNER.GateError, "non-package|project"):
                RUNNER.validate_assets(project_assets, cache, candidate)

    def test_source_pin_requires_full_exact_clean_head(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)
            with mock.patch.object(RUNNER.subprocess, "run", side_effect=[
                mock.Mock(returncode=0, stdout=str(source) + "\n", stderr=""),
                mock.Mock(returncode=0, stdout=SOURCE_COMMIT + "\n", stderr=""),
                mock.Mock(returncode=0, stdout="", stderr=""),
            ]) as run:
                result = RUNNER.verify_source_pin(source, SOURCE_COMMIT)
            self.assertEqual({"commit": SOURCE_COMMIT, "headExact": True, "clean": True}, result)
            self.assertEqual(3, run.call_count)
            with self.assertRaisesRegex(RUNNER.GateError, "full lowercase"):
                RUNNER.verify_source_pin(source, SOURCE_COMMIT.upper())

    def test_run_command_reports_nonzero_exit_without_shell(self):
        class Completed:
            pid = 123
            returncode = 7

            def communicate(self, timeout):
                self.timeout = timeout
                return "not JSON\n", None

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            commands: list[dict] = []
            with mock.patch.object(RUNNER.subprocess, "Popen", return_value=Completed()) as popen:
                with self.assertRaisesRegex(RUNNER.CommandFailed, "exit code 7"):
                    RUNNER.run_command(
                        ["dotnet", "restore", "consumer.csproj"], root, {"HOME": str(root / "home")},
                        root / "restore.log", RUNNER.Deadline(5), "restore", commands,
                    )
            self.assertFalse(popen.call_args.kwargs["shell"])
            self.assertEqual("not JSON\n", (root / "restore.log").read_text(encoding="utf-8"))
            self.assertEqual(7, commands[0]["exitCode"])

    def test_load_assets_rejects_non_json(self):
        with tempfile.TemporaryDirectory() as temporary:
            assets = Path(temporary) / "project.assets.json"
            assets.write_text("restore failed: not JSON\n", encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.GateError, "valid JSON"):
                RUNNER._load_assets(assets)

    def test_atomic_report_and_stage_schema(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            report_path = root / RUNNER.RESULT_NAME
            report = RUNNER._new_report(SOURCE_COMMIT, root / "package.nupkg", root, 30)
            with RUNNER.report_stage(report, "preflight"):
                pass
            RUNNER.atomic_json(report_path, report)
            value = json.loads(report_path.read_text(encoding="utf-8"))
            self.assertEqual(1, value["schemaVersion"])
            self.assertFalse(value["passed"])
            self.assertEqual("passed", value["stages"][0]["status"])
            self.assertEqual("pending", value["stages"][1]["status"])
            self.assertEqual([], list(root.glob("*.tmp")))

    def test_missing_package_writes_completed_failed_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "evidence"
            missing = root / "missing.nupkg"
            with mock.patch.object(RUNNER, "verify_source_pin", return_value={
                "commit": SOURCE_COMMIT, "headExact": True, "clean": True,
            }):
                with self.assertRaises(RUNNER.GateError):
                    RUNNER.run_gate(missing, SOURCE_COMMIT, output, timeout_seconds=5)
            report = json.loads((output / RUNNER.RESULT_NAME).read_text(encoding="utf-8"))
            self.assertFalse(report["passed"])
            self.assertTrue(report["completed"])
            self.assertEqual("failed", report["stages"][0]["status"])
            self.assertTrue(all(stage["status"] == "skipped" for stage in report["stages"][1:]))

    def test_output_must_be_absolute_new_external_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaisesRegex(RUNNER.GateError, "absolute"):
                RUNNER._prepare_output(Path("relative"), ROOT)
            existing = root / "existing"
            existing.mkdir()
            with self.assertRaisesRegex(RUNNER.GateError, "existing collision"):
                RUNNER._prepare_output(existing, ROOT)


if __name__ == "__main__":
    unittest.main()
