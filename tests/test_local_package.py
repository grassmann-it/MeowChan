"""Package metadata checks without running dotnet or installing a candidate."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "test-local-package.py"
SPEC = importlib.util.spec_from_file_location("meowchan_local_package", SCRIPT)
verifier = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verifier)


class LocalPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="meowchan package fixtures ")
        self.addCleanup(self.temporary.cleanup)
        self.package = Path(self.temporary.name) / "libmeowchan.0.9.0.nupkg"

    def write_package(self, package_id="libmeowchan", assembly="lib/net10.0/libmeowchan.dll", dependency=""):
        dependency_xml = f'<dependency id="{dependency}" version="1.0.0" />' if dependency else ""
        nuspec = (f'<package><metadata><id>{package_id}</id><version>0.9.0</version>'
                  f'<readme>README.md</readme><dependencies><group targetFramework="net10.0">'
                  f'{dependency_xml}</group></dependencies></metadata></package>')
        with zipfile.ZipFile(self.package, "w") as archive:
            archive.writestr(f"{package_id}.nuspec", nuspec)
            archive.writestr(assembly, b"managed fixture")
            archive.writestr("README.md", b"MeowChan")
            archive.writestr("LICENSE", b"fixture license")

    def test_accepts_renamed_library_contract(self):
        self.write_package()
        self.assertEqual(("libmeowchan", "0.9.0"), verifier.inspect_package(self.package))

    def test_rejects_old_identity_or_assembly(self):
        self.write_package(package_id="libochan")
        with self.assertRaisesRegex(ValueError, "package ID"):
            verifier.inspect_package(self.package)
        self.write_package(assembly="lib/net10.0/libochan.dll")
        with self.assertRaisesRegex(ValueError, "assemblies"):
            verifier.inspect_package(self.package)

    def test_rejects_desktop_dependency(self):
        for dependency in ("MeowChan", "Avalonia.Desktop"):
            with self.subTest(dependency=dependency):
                self.write_package(dependency=dependency)
                with self.assertRaisesRegex(ValueError, "Desktop dependency"):
                    verifier.inspect_package(self.package)


if __name__ == "__main__":
    unittest.main()
