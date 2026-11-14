"""Filesystem-level contract tests for the plugin delivery stage boundary.

These tests intentionally do not invoke dotnet or MSBuild.  The dynamic cases
exercise the fail-closed boundary with private synthetic fixtures; the source
contract checks bind that behavior to the inline task's independent trusted
inputs and to the preflight calls that run before helper MSBuild operations.
"""
import os
from pathlib import Path
import shutil
import stat
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "packaging" / "PluginDelivery.targets"


class StageRejected(ValueError):
    pass


class StageBoundaryContract:
    """Small filesystem probe matching the task's deliberately narrow contract.

    This is not production code.  Keeping it independent makes the tests able
    to prove the mutation ordering without starting an MSBuild process, which is
    required by the central test-runner policy for this repository.
    """

    def __init__(self, project: Path, intermediate: str, stage: str):
        self.project = self._canonical(project)
        self.intermediate_input = intermediate
        self.stage = stage

    @staticmethod
    def _canonical(value: Path | str) -> Path:
        return Path(os.path.normpath(os.path.abspath(os.fspath(value))))

    @staticmethod
    def _has_parent_traversal(value: str) -> bool:
        return any(part == ".." for part in value.replace("\\", "/").split("/"))

    @staticmethod
    def _same(left: Path, right: Path) -> bool:
        if os.name == "nt":
            return os.path.normcase(os.fspath(left)) == os.path.normcase(os.fspath(right))
        return left == right

    @classmethod
    def _within(cls, candidate: Path, ancestor: Path) -> bool:
        candidate = cls._canonical(candidate)
        ancestor = cls._canonical(ancestor)
        if cls._same(candidate, ancestor):
            return True
        try:
            candidate.relative_to(ancestor)
            return True
        except ValueError:
            return False

    @classmethod
    def _reject_reparse_path(cls, path: Path) -> None:
        current = cls._canonical(path)
        while True:
            try:
                if stat.S_ISLNK(os.lstat(current).st_mode):
                    raise StageRejected(f"reparse alias: {current}")
            except FileNotFoundError:
                pass
            parent = current.parent
            if parent == current:
                return
            current = parent

    @classmethod
    def _reject_reparse_tree(cls, path: Path) -> None:
        cls._reject_reparse_path(path)
        if not path.exists():
            return
        if not path.is_dir():
            raise StageRejected(f"destination is not a directory: {path}")
        for current, directories, files in os.walk(path, followlinks=False):
            for name in (*directories, *files):
                entry = Path(current) / name
                if stat.S_ISLNK(os.lstat(entry).st_mode):
                    raise StageRejected(f"reparse alias: {entry}")

    def expected(self) -> Path:
        self._reject_reparse_path(self.project)
        if not self.project.is_dir():
            raise StageRejected("project root is not a directory")
        if not self.intermediate_input or self._has_parent_traversal(self.intermediate_input):
            raise StageRejected("intermediate path has parent traversal")
        intermediate = self._canonical(
            self.intermediate_input
            if os.path.isabs(self.intermediate_input)
            else self.project / self.intermediate_input
        )
        if not self._within(intermediate, self.project) or self._same(intermediate, self.project):
            raise StageRejected("intermediate path is outside the project boundary")
        self._reject_reparse_path(intermediate)
        if self.stage == "build":
            return intermediate / "plugin-delivery" / "build"
        if self.stage == "publish":
            return intermediate / "plugin-delivery" / "publish" / "payload"
        raise StageRejected(f"unknown stage: {self.stage}")

    def guard(self, destination: Path | str) -> Path:
        raw = os.fspath(destination)
        if not os.path.isabs(raw) or self._has_parent_traversal(raw):
            raise StageRejected("destination is not absolute and traversal-free")
        expected = self._canonical(self.expected())
        root = self._canonical(raw)
        if not self._same(root, expected):
            raise StageRejected("destination is not the exact project staging path")
        self._reject_reparse_tree(root)
        return root

    def mutate(self, destination: Path | str) -> Path:
        """Perform the same observable mutation sequence only after guard()."""
        root = self.guard(destination)
        if root.exists():
            shutil.rmtree(root)
        root.mkdir(parents=True)
        (root / "payload-sentinel.txt").write_text("valid payload\n", encoding="utf-8")
        return root


class PluginDeliveryStageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.target_text = TARGET.read_text(encoding="utf-8")
        cls.delivery = ET.parse(TARGET).getroot()

    def test_task_uses_independent_trusted_boundary_and_preflights_before_helpers(self):
        task = self.target_text
        for parameter in ("ProjectDirectory", "IntermediateOutputPath", "StageKind", "ValidateOnly"):
            self.assertIn(f"<{parameter} ", task)
        self.assertIn("expectedDestination", task)
        self.assertIn("requireNoReparsePath", task)
        self.assertIn("requireNoReparseTree", task)
        self.assertIn("hasParentTraversal", task)
        self.assertIn("rejectInputOverlap", task)
        self.assertNotIn("string root = Path.GetFullPath(Destination)", task)
        self.assertLess(task.index("if (ValidateOnly) return true;"), task.index("Directory.Delete(root, true)"))

        build = task[task.index('Target Name="PrepareMeowChanPluginBuildDelivery"'):]
        self.assertLess(build.index('StageKind="build" ValidateOnly="true"'), build.index("<MSBuild "))
        publish = task[task.index('Target Name="PrepareMeowChanPluginPublishDelivery"'):]
        self.assertLess(publish.index('StageKind="publish" ValidateOnly="true"'), publish.index("<MSBuild "))
        self.assertIn("GetFullPath('$(IntermediateOutputPath)', '$(MSBuildProjectDirectory)'))/plugin-delivery/publish/worker/", publish)
        self.assertIn("GetFullPath('$(IntermediateOutputPath)', '$(MSBuildProjectDirectory)'))/plugin-delivery/publish/compiler/", publish)

    def test_invalid_destinations_are_rejected_without_mutating_foreign_sentinels(self):
        with tempfile.TemporaryDirectory(prefix="plugin-stage-boundary-") as temporary:
            fixture = Path(temporary)
            project = fixture / "project"
            project.mkdir()
            intermediate = "obj/Debug/net10.0"
            boundary = StageBoundaryContract(project, intermediate, "build")
            allowed = boundary.expected()
            foreign = fixture / "foreign-data"
            foreign.mkdir()
            sentinel = foreign / "do-not-delete.txt"
            sentinel.write_text("untouched", encoding="utf-8")
            input_directory = fixture / "helper-input"
            input_directory.mkdir()
            (input_directory / "worker.dll").write_text("input", encoding="utf-8")

            invalid = (
                project,
                project / "obj",
                fixture / "home-like-directory",
                foreign,
                fixture / "another-project" / ".." / "foreign-data",
                input_directory,
                allowed / ".." / ".." / "foreign-data",
            )
            for destination in invalid:
                with self.subTest(destination=destination):
                    before = sentinel.read_bytes()
                    with self.assertRaises(StageRejected):
                        boundary.mutate(destination)
                    self.assertEqual(before, sentinel.read_bytes())
                    self.assertTrue(sentinel.is_file())

    @unittest.skipUnless(os.name != "nt", "POSIX link fixture; Windows reparse coverage is run natively")
    def test_destination_and_existing_tree_link_aliases_are_rejected_before_mutation(self):
        with tempfile.TemporaryDirectory(prefix="plugin-stage-link-boundary-") as temporary:
            fixture = Path(temporary)
            project = fixture / "project"
            project.mkdir()
            foreign = fixture / "foreign-data"
            foreign.mkdir()
            sentinel = foreign / "do-not-delete.txt"
            sentinel.write_text("untouched", encoding="utf-8")

            # The exact stage itself is an alias to a foreign directory.
            boundary = StageBoundaryContract(project, "obj/Debug/net10.0", "build")
            aliased_stage = boundary.expected()
            aliased_stage.parent.mkdir(parents=True)
            aliased_stage.symlink_to(foreign, target_is_directory=True)
            with self.assertRaises(StageRejected):
                boundary.mutate(aliased_stage)
            self.assertEqual(b"untouched", sentinel.read_bytes())

            # An ancestor alias is just as invalid, even when the final child is absent.
            project2 = fixture / "project2"
            project2.mkdir()
            boundary2 = StageBoundaryContract(project2, "obj/Debug/net10.0", "publish")
            publish_parent = boundary2.expected().parents[0]  # .../plugin-delivery/publish
            publish_parent.parent.mkdir(parents=True)
            publish_parent.symlink_to(foreign, target_is_directory=True)
            with self.assertRaises(StageRejected):
                boundary2.mutate(boundary2.expected())
            self.assertEqual(b"untouched", sentinel.read_bytes())

            # A link below an existing destination must be detected before rmtree.
            project3 = fixture / "project3"
            project3.mkdir()
            boundary3 = StageBoundaryContract(project3, "obj/Debug/net10.0", "build")
            stage3 = boundary3.expected()
            (stage3 / "nested").mkdir(parents=True)
            (stage3 / "nested" / "outside-link").symlink_to(foreign, target_is_directory=True)
            with self.assertRaises(StageRejected):
                boundary3.mutate(stage3)
            self.assertEqual(b"untouched", sentinel.read_bytes())

    def test_valid_build_publish_and_custom_in_project_stages_preserve_payload_capability(self):
        with tempfile.TemporaryDirectory(prefix="plugin-stage-valid-") as temporary:
            fixture = Path(temporary)
            project = fixture / "project"
            project.mkdir()
            foreign = fixture / "foreign-data"
            foreign.mkdir()
            sentinel = foreign / "do-not-delete.txt"
            sentinel.write_text("untouched", encoding="utf-8")

            for stage in ("build", "publish"):
                with self.subTest(stage=stage):
                    boundary = StageBoundaryContract(project, "obj/Debug/net10.0", stage)
                    output = boundary.mutate(boundary.expected())
                    self.assertEqual("valid payload\n", (output / "payload-sentinel.txt").read_text(encoding="utf-8"))
                    self.assertEqual(b"untouched", sentinel.read_bytes())

            custom = StageBoundaryContract(project, "artifacts/custom-intermediate", "build")
            custom_output = custom.mutate(custom.expected())
            self.assertTrue((custom_output / "payload-sentinel.txt").is_file())
            self.assertEqual(b"untouched", sentinel.read_bytes())

            outside = StageBoundaryContract(project, str(fixture / "external-intermediate"), "build")
            with self.assertRaises(StageRejected):
                outside.mutate(outside.expected())
            self.assertEqual(b"untouched", sentinel.read_bytes())


if __name__ == "__main__":
    unittest.main()
