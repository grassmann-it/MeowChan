"""Offline safety tests for the SDK package-only consumer gate.

These tests deliberately never invoke dotnet, NuGet, a provider, a GUI, or a
network.  The real restore/build/run lane belongs to the parent heavy.lock job.
"""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
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

    def test_source_inventory_and_fresh_copy_exclude_generated_dirs_case_insensitively(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            source = base / "sample"
            source.mkdir()
            (source / "Program.cs").write_text("source", encoding="utf-8")
            for generated in ("BIN", "Obj"):
                (source / generated / "Release" / "net10.0").mkdir(parents=True)
                (source / generated / "Release" / "net10.0" / "stale.dll").write_bytes(b"old output")

            inventory = RUNNER.inventory_tree(source, "SDK consumer sample", skip_generated=True)
            self.assertEqual(["Program.cs"], [item["path"] for item in inventory["files"]])

            copied = base / "copied"
            RUNNER.copy_tree(source, copied, "SDK consumer sample", skip_generated=True)
            self.assertTrue((copied / "Program.cs").is_file())
            self.assertFalse((copied / "BIN").exists())
            self.assertFalse((copied / "Obj").exists())

    def test_mock_run_gate_accepts_fresh_generated_outputs_and_retains_consumer_dll(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            source_root = base / "source"
            source_root.mkdir()
            (source_root / "Directory.Build.props").write_text(
                "<Project><PropertyGroup><Version>1.1.0</Version></PropertyGroup></Project>",
                encoding="utf-8",
            )
            (source_root / "global.json").write_text("{}\n", encoding="utf-8")

            sample = base / "sample"
            sample.mkdir()
            project = sample / "Synthetic.Consumer.csproj"
            project.write_text(
                """<Project><PropertyGroup><MeowChanPluginSdkVersion>1.1.0</MeowChanPluginSdkVersion></PropertyGroup>
                <ItemGroup><PackageReference Include="MeowChan.PluginSdk" Version="[$(MeowChanPluginSdkVersion)]" /></ItemGroup></Project>""",
                encoding="utf-8",
            )
            (sample / "Program.cs").write_text("synthetic source", encoding="utf-8")
            for generated in ("BIN", "Obj"):
                (sample / generated / "Release" / "net10.0").mkdir(parents=True)
                (sample / generated / "Release" / "net10.0" / "old.dll").write_bytes(b"old output")

            source_inventory_before = RUNNER.inventory_tree(sample, "SDK consumer sample", skip_generated=True)
            package = base / "MeowChan.PluginSdk.1.1.0.nupkg"
            make_package(package)
            evidence = base / "evidence"
            calls = []
            mutate_package = False

            def fake_bounded(arguments, cwd, environment, log, timeout, label):
                calls.append(label)
                log.parent.mkdir(parents=True, exist_ok=True)
                log.write_text("synthetic child\n", encoding="utf-8")
                if label == "restore":
                    project_path = Path(arguments[2])
                    cache = Path(environment["NUGET_PACKAGES"])
                    restored = cache / "meowchan.pluginsdk" / PRODUCT_VERSION
                    (restored / "lib" / "net10.0").mkdir(parents=True)
                    (restored / package.name).write_bytes(package.read_bytes())
                    (restored / "lib" / "net10.0" / "MeowChan.PluginSdk.dll").write_bytes(b"sdk assembly")
                    library = f"{RUNNER.PACKAGE_ID}/{PRODUCT_VERSION}"
                    assets = {
                        "packageFolders": {str(cache) + os.sep: {}},
                        "libraries": {library: {"type": "package", "path": "meowchan.pluginsdk/1.1.0"}},
                        "targets": {"net10.0": {library: {"type": "package"}}},
                    }
                    assets_path = project_path.parent / "obj" / "project.assets.json"
                    assets_path.parent.mkdir(parents=True)
                    assets_path.write_text(json.dumps(assets), encoding="utf-8")
                elif label == "build":
                    project_path = Path(arguments[2])
                    assembly = project_path.parent / "bin" / "Release" / "net10.0" / f"{project_path.stem}.dll"
                    assembly.parent.mkdir(parents=True)
                    assembly.write_bytes(b"fresh consumer assembly")
                    if mutate_package:
                        package.write_bytes(package.read_bytes() + b"mutated during build")
                return "synthetic success\n"

            with mock.patch.object(RUNNER, "SAMPLE_ROOT", sample), \
                 mock.patch.object(RUNNER, "SAMPLE_PROJECT", project), \
                 mock.patch.object(RUNNER, "verify_source_pin", return_value={
                     "commit": SOURCE_COMMIT, "headExact": True, "clean": True,
                 }), \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=fake_bounded):
                report = RUNNER.run_gate(
                    package, SOURCE_COMMIT, evidence, timeout_seconds=30,
                    source_root=source_root, heavy_lock_path=base / "heavy.lock",
                )

            self.assertTrue(report["passed"])
            self.assertEqual(["restore", "build", "consumer"], calls)
            self.assertEqual(["Program.cs", "Synthetic.Consumer.csproj"],
                             [item["path"] for item in report["isolation"]["consumerInputBefore"]["files"]])
            self.assertEqual(source_inventory_before["treeSha256"], report["inputs"]["before"]["sampleTreeSha256"])
            self.assertEqual(source_inventory_before["treeSha256"], report["inputs"]["after"]["sampleTreeSha256"])
            self.assertTrue((sample / "BIN" / "Release" / "net10.0" / "old.dll").is_file())
            self.assertTrue((sample / "Obj" / "Release" / "net10.0" / "old.dll").is_file())
            assembly_evidence = Path(report["consumer"]["assembly"]["evidencePath"])
            self.assertTrue(assembly_evidence.is_file())
            self.assertEqual(b"fresh consumer assembly", assembly_evidence.read_bytes())

            mutate_package = True
            mutation_evidence = base / "mutation-evidence"
            with mock.patch.object(RUNNER, "SAMPLE_ROOT", sample), \
                 mock.patch.object(RUNNER, "SAMPLE_PROJECT", project), \
                 mock.patch.object(RUNNER, "verify_source_pin", return_value={
                     "commit": SOURCE_COMMIT, "headExact": True, "clean": True,
                 }), \
                 mock.patch.object(RUNNER, "run_bounded_command", side_effect=fake_bounded):
                with self.assertRaisesRegex(RUNNER.GateError, "Supplied package changed"):
                    RUNNER.run_gate(
                        package, SOURCE_COMMIT, mutation_evidence, timeout_seconds=30,
                        source_root=source_root, heavy_lock_path=base / "heavy.lock",
                    )
            mutation_report = json.loads((mutation_evidence / RUNNER.RESULT_NAME).read_text(encoding="utf-8"))
            self.assertFalse(mutation_report["passed"])
            self.assertIn("Supplied package changed", mutation_report["error"]["message"])

    def test_missing_consumer_output_cannot_be_recorded_as_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaisesRegex(RUNNER.GateError, "does not exist"):
                RUNNER.persist_file_evidence(
                    root / "missing.dll", root / "evidence" / "consumer.dll",
                    "executed consumer assembly",
                )

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

    def test_run_command_translates_nonzero_shared_helper_error_and_keeps_metadata(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            log = root / "restore.log"
            commands: list[dict] = []
            error = RUNNER.ProcessCommandFailed("restore", 7, log, "not JSON\n")
            with mock.patch.object(RUNNER, "run_bounded_command", side_effect=error) as helper:
                with self.assertRaisesRegex(RUNNER.CommandFailed, "exit code 7"):
                    RUNNER.run_command(
                        ["dotnet", "restore", "consumer.csproj"], root, {"HOME": str(root / "home")},
                        log, RUNNER.Deadline(5), "restore", commands,
                    )
            self.assertEqual(1, helper.call_count)
            self.assertGreater(helper.call_args.args[4], 0)
            self.assertEqual(7, commands[0]["exitCode"])
            self.assertTrue(commands[0]["boundedLog"])
            self.assertEqual(RUNNER.MAX_TAIL_BYTES, commands[0]["maxLogBytes"])

    def test_run_command_nonzero_python_child_keeps_bounded_tail_and_exit_code(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            log = root / "nonzero.log"
            commands: list[dict] = []
            code = "import sys; print('not JSON'); sys.exit(7)"
            with self.assertRaisesRegex(RUNNER.CommandFailed, "exit code 7"):
                RUNNER.run_command(
                    [sys.executable, "-c", code], root, {"HOME": str(root / "home")},
                    log, RUNNER.Deadline(10), "python-nonzero", commands,
                )
            self.assertIn("not JSON", log.read_text(encoding="utf-8"))
            self.assertEqual(7, commands[0]["exitCode"])
            self.assertTrue(commands[0]["boundedLog"])

    def test_run_command_translates_timeout_and_interrupt_after_helper_cleanup(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for side_effect, expected in (
                (RUNNER.ProcessCommandTimedOut("restore", 1, root / "timeout.log", "tail"), RUNNER.DeadlineExceeded),
                (KeyboardInterrupt(), RUNNER.DeadlineExceeded),
            ):
                commands: list[dict] = []
                with mock.patch.object(RUNNER, "run_bounded_command", side_effect=side_effect):
                    with self.assertRaises(expected):
                        RUNNER.run_command(
                            ["python-child"], root, {"HOME": str(root / "home")},
                            root / "command.log", RUNNER.Deadline(5), "restore", commands,
                        )
                self.assertIsNone(commands[0]["exitCode"])
                self.assertTrue(commands[0]["timedOut"])

    def test_shared_helper_keeps_noisy_python_child_log_bounded(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            log = root / "noisy.log"
            commands: list[dict] = []
            code = "import sys; sys.stdout.write('x' * %d)" % (RUNNER.MAX_TAIL_BYTES + 4096)
            RUNNER.run_command(
                [sys.executable, "-c", code], root, {"HOME": str(root / "home")},
                log, RUNNER.Deadline(10), "python-noisy", commands,
            )
            self.assertLessEqual(log.stat().st_size, RUNNER.MAX_TAIL_BYTES)
            self.assertEqual(0, commands[0]["exitCode"])
            self.assertTrue(commands[0]["boundedLog"])


    def test_load_assets_rejects_non_json(self):
        with tempfile.TemporaryDirectory() as temporary:
            assets = Path(temporary) / "project.assets.json"
            assets.write_text("restore failed: not JSON\n", encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.GateError, "valid JSON"):
                RUNNER._load_assets(assets)

    def test_assets_and_consumer_assembly_evidence_survive_disposable_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            disposable = root / "disposable"
            assets = disposable / "consumer" / "obj" / "project.assets.json"
            assembly = disposable / "consumer" / "bin" / "Release" / "net10.0" / "MeowChan.PluginSdk.Consumer.dll"
            assets.parent.mkdir(parents=True)
            assembly.parent.mkdir(parents=True)
            assets.write_bytes(b"{\"package\":\"candidate\"}\n")
            assembly.write_bytes(b"consumer assembly bytes")
            assets_evidence = RUNNER.persist_file_evidence(assets, root / "evidence" / "assets" / assets.name, "project.assets.json")
            assembly_evidence = RUNNER.persist_file_evidence(
                assembly, root / "evidence" / "consumer-evidence" / assembly.name, "executed consumer assembly",
            )
            shutil.rmtree(disposable)
            self.assertEqual(assets_evidence["sha256"], RUNNER.sha256_file(Path(assets_evidence["evidencePath"])))
            self.assertEqual(assembly_evidence["sha256"], RUNNER.sha256_file(Path(assembly_evidence["evidencePath"])))
            self.assertTrue(Path(assets_evidence["evidencePath"]).is_file())
            self.assertTrue(Path(assembly_evidence["evidencePath"]).is_file())

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
