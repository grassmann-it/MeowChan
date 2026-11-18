"""Execute the production inline MSBuild task against private synthetic inputs.

No SDK project, restore, native helper, or provider access is involved. These
checks belong in the central heavy.lock lane because MSBuild compiles the actual
C# task; the lightweight StageBoundaryContract tests are not a substitute.
"""
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "packaging" / "PluginDelivery.targets"
DOTNET = shutil.which("dotnet")


class StageFixture:
    def __init__(self, base, stage="build", intermediate="obj/Debug/net10.0"):
        self.base = base
        self.project = base / "project"
        self.project.mkdir(parents=True)
        self.intermediate = intermediate
        self.kind = stage
        intermediate_path = self.project / intermediate
        self.destination = intermediate_path / "plugin-delivery" / stage
        if stage == "publish":
            self.destination /= "payload"
        helper_root = self.destination.parent if stage == "publish" else self.project / "inputs"
        self.worker = helper_root / "worker"
        self.compiler = helper_root / "compiler"
        for path in (self.worker, self.compiler, self.destination):
            path.mkdir(parents=True, exist_ok=True)
        self.worker_assembly = self.worker / "MeowChan.PluginWorker.dll"
        self.compiler_assembly = self.compiler / "MeowChan.PluginCompiler.dll"
        self.worker_assembly.write_bytes(b"fixture-worker")
        self.compiler_assembly.write_bytes(b"fixture-compiler")
        sdk = b"fixture-sdk"
        (self.worker / "MeowChan.PluginSdk.dll").write_bytes(sdk)
        self.sdk_hash = hashlib.sha256(sdk).hexdigest()
        (self.compiler / "toolchain.json").write_text(json.dumps({
            "SdkFingerprint": self.sdk_hash,
            "CompilerFingerprint": "c" * 64,
        }), encoding="utf-8")
        self.plugin = self.project / "inputs" / "plugins" / "Fixture.Plugin.dll"
        self.source = self.project / "inputs" / "sources" / "Fixture.cs"
        for path, data in ((self.plugin, b"fixture-plugin"), (self.source, b"// fixture-source")):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        self.sentinel = self.destination / "old-stage-sentinel.txt"
        self.sentinel.write_bytes(b"old-stage-intact")
        self.foreign = base / "foreign"
        self.foreign.mkdir()
        self.foreign_sentinel = self.foreign / "do-not-touch.txt"
        self.foreign_sentinel.write_bytes(b"foreign-intact")
        self.environment = os.environ.copy()
        for key, relative in (("HOME", "home"), ("DOTNET_CLI_HOME", "home"),
                              ("TMPDIR", "tmp"), ("XDG_CONFIG_HOME", "config"),
                              ("XDG_CACHE_HOME", "cache"), ("XDG_DATA_HOME", "data")):
            directory = base / relative
            directory.mkdir(exist_ok=True)
            self.environment[key] = str(directory)
        self.environment.update(DOTNET_CLI_TELEMETRY_OPTOUT="1",
                                DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
                                DOTNET_NOLOGO="1", MSBUILDDISABLENODEREUSE="1")

    def run(self, **overrides):
        project = ET.Element("Project")
        task = ET.parse(TARGET).getroot().find("UsingTask[@TaskName='StageMeowChanPlugins']")
        if task is None:
            raise AssertionError("production StageMeowChanPlugins task is missing")
        project.append(copy.deepcopy(task))
        target = ET.SubElement(project, "Target", Name="Probe")
        arguments = dict(ProjectDirectory=str(self.project), IntermediateOutputPath=self.intermediate,
                         StageKind=self.kind, ValidateOnly="false", Destination=str(self.destination),
                         WorkerAssembly=str(self.worker_assembly), CompilerAssembly=str(self.compiler_assembly),
                         Plugins=str(self.plugin), Sources=str(self.source),
                         ExpectedPluginAssemblies="Fixture.Plugin", ExpectedSourceFiles="Fixture.cs")
        arguments.update({key: str(value) for key, value in overrides.items()})
        ET.SubElement(target, "StageMeowChanPlugins", arguments)
        probe = self.project / "stage-probe.proj"
        ET.ElementTree(project).write(probe, encoding="utf-8", xml_declaration=True)
        result = subprocess.run([DOTNET, "msbuild", str(probe), "-target:Probe", "-nologo",
                                 "-maxcpucount:1", "-nodeReuse:false", "-verbosity:minimal"],
                                cwd=self.project, env=self.environment, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60, check=False)
        log_directory = os.environ.get("MEOWCHAN_PLUGIN_STAGE_TEST_LOG_DIR")
        if log_directory:
            logs = Path(log_directory)
            logs.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(mode="w", prefix="msbuild-", suffix=".log", dir=logs,
                                             encoding="utf-8", delete=False) as log:
                log.write(json.dumps(arguments, indent=2) + "\n" + result.stdout)
        return result


@unittest.skipUnless(DOTNET, "dotnet/MSBuild is required for production task execution")
class PluginDeliveryStageMsbuildTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="plugin-stage-msbuild-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.counter = 0

    def fixture(self, **kwargs):
        self.counter += 1
        return StageFixture(self.root / str(self.counter), **kwargs)

    def assertRejected(self, fixture, expected, **overrides):
        result = fixture.run(**overrides)
        self.assertNotEqual(0, result.returncode, result.stdout)
        # Do not let task-compilation errors masquerade as a guard being tested.
        self.assertIn(expected, result.stdout)
        self.assertNotIn("error CS", result.stdout)
        self.assertEqual(b"old-stage-intact", fixture.sentinel.read_bytes())
        self.assertEqual(b"foreign-intact", fixture.foreign_sentinel.read_bytes())

    def assertPayload(self, fixture):
        result = fixture.run()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertFalse(fixture.sentinel.exists())
        self.assertEqual(b"fixture-worker", (fixture.destination / "plugin-worker" / fixture.worker_assembly.name).read_bytes())
        self.assertEqual(b"fixture-compiler", (fixture.destination / "plugin-compiler" / fixture.compiler_assembly.name).read_bytes())
        plugins = fixture.destination / "plugins"
        self.assertEqual(fixture.plugin.read_bytes(), (plugins / "assemblies" / fixture.plugin.name).read_bytes())
        self.assertEqual(fixture.source.read_bytes(), (plugins / "sources" / fixture.source.name).read_bytes())
        manifest = json.loads((plugins / "Fixture.Plugin.plugin.json").read_text())
        self.assertEqual(fixture.sdk_hash, manifest["SdkFingerprint"])
        self.assertEqual(hashlib.sha256(fixture.plugin.read_bytes()).hexdigest(), manifest["AssemblySha256"])
        self.assertEqual(b"foreign-intact", fixture.foreign_sentinel.read_bytes())

    def test_real_build_copies_payload_and_manifest(self):
        self.assertPayload(self.fixture())

    def test_real_publish_accepts_worker_and_compiler_siblings_of_payload(self):
        self.assertPayload(self.fixture(stage="publish"))

    def test_real_custom_intermediate_inside_project_remains_supported(self):
        self.assertPayload(self.fixture(intermediate="artifacts/custom-intermediate"))

    def test_destination_overrides_do_not_mutate_any_existing_stage_or_foreign_data(self):
        fixture = self.fixture()
        for destination in (fixture.project, fixture.project / "obj", fixture.base / "home",
                            fixture.foreign, fixture.worker):
            with self.subTest(destination=destination):
                self.assertRejected(fixture, "not the trusted project staging path", Destination=destination)

    def test_parent_traversal_is_rejected_even_when_it_normalizes_to_the_stage(self):
        fixture = self.fixture()
        self.assertRejected(fixture, "absolute path without parent traversal",
                            Destination=str(fixture.destination) + "/../build")
        self.assertRejected(fixture, "intermediate path contains parent traversal",
                            IntermediateOutputPath="obj/../obj/Debug/net10.0")

    def test_relative_destination_and_external_intermediate_are_rejected(self):
        fixture = self.fixture()
        self.assertRejected(fixture, "absolute path without parent traversal", Destination="obj/stage")
        self.assertRejected(fixture, "intermediate path is outside the project",
                            IntermediateOutputPath=fixture.foreign)

    def test_validate_only_does_not_mutate_the_existing_stage(self):
        fixture = self.fixture(stage="publish")
        result = fixture.run(ValidateOnly="true", WorkerAssembly="", CompilerAssembly="", Plugins="", Sources="")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual(b"old-stage-intact", fixture.sentinel.read_bytes())
        self.assertEqual(b"foreign-intact", fixture.foreign_sentinel.read_bytes())
        self.assertFalse((fixture.destination / "plugins").exists())

    def test_bad_inventory_and_input_overlap_are_rejected_before_stage_delete(self):
        fixture = self.fixture()
        self.assertRejected(fixture, "outside the public plugin inventory", ExpectedPluginAssemblies="Other.Plugin")
        overlap = fixture.destination / "Fixture.Plugin.dll"
        overlap.write_bytes(b"input-in-stage")
        self.assertRejected(fixture, "overlaps an input directory", Plugins=overlap)
        self.assertEqual(b"input-in-stage", overlap.read_bytes())

    @unittest.skipIf(os.name == "nt", "requires POSIX symlink capability; Windows junction gate is separate")
    def test_destination_link_or_dangling_link_is_rejected_without_touching_the_target(self):
        for dangling in (False, True):
            with self.subTest(dangling=dangling):
                fixture = self.fixture()
                fixture.sentinel.unlink()
                fixture.destination.rmdir()
                target = fixture.foreign / "absent" if dangling else fixture.foreign
                fixture.destination.symlink_to(target, target_is_directory=True)
                result = fixture.run()
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertIn("symlink/reparse alias", result.stdout)
                self.assertTrue(fixture.destination.is_symlink())
                self.assertEqual(b"foreign-intact", fixture.foreign_sentinel.read_bytes())
                if dangling:
                    self.assertFalse(target.exists())

    @unittest.skipIf(os.name == "nt", "requires POSIX symlink capability; Windows junction gate is separate")
    def test_nested_stage_link_is_rejected_before_delete(self):
        fixture = self.fixture()
        (fixture.destination / "nested").mkdir()
        (fixture.destination / "nested" / "foreign-link").symlink_to(fixture.foreign, target_is_directory=True)
        self.assertRejected(fixture, "symlink/reparse alias")

    @unittest.skipIf(os.name == "nt", "requires POSIX symlink capability; Windows junction gate is separate")
    def test_ancestor_alias_is_rejected_before_stage_creation(self):
        fixture = self.fixture()
        parent = fixture.destination.parent
        moved = fixture.project / "existing-stage-tree"
        parent.rename(moved)
        parent.symlink_to(moved, target_is_directory=True)
        self.assertRejected(fixture, "symlink/reparse alias")

    @unittest.skipIf(os.name == "nt", "requires POSIX symlink capability; Windows junction gate is separate")
    def test_publish_preflight_rejects_helper_root_and_nested_aliases_before_helper_publish(self):
        for helper_name in ("worker", "compiler"):
            for nested in (False, True):
                with self.subTest(helper=helper_name, nested=nested):
                    fixture = self.fixture(stage="publish")
                    helper = getattr(fixture, helper_name)
                    if nested:
                        (helper / "nested-link").symlink_to(fixture.foreign, target_is_directory=True)
                    else:
                        shutil.rmtree(helper)
                        helper.symlink_to(fixture.foreign, target_is_directory=True)
                    self.assertRejected(fixture, "symlink/reparse alias", ValidateOnly="true")


if __name__ == "__main__":
    unittest.main()
