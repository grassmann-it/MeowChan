"""Package-consumer integrity failures; no dotnet, restore or real package needed."""
import copy
import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "test-plugin-packages.py"
SPEC = importlib.util.spec_from_file_location("meowchan_plugin_packages", SCRIPT)
verifier = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verifier)


class PluginPackageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="meowchan package integrity ")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.feed = self.root / "feed"
        self.feed.mkdir()
        self.cache = self.root / "fresh cache"
        self.cache.mkdir()

    def package(self, package_id="MeowChan.PluginSdk", version="1.0.0", dependencies=None,
                extra_dll=None, readme=True, license=True, assembly=None, filename=None):
        if dependencies is None:
            dependencies = ["MeowChan.PluginSdk", "libmeowchan"] if package_id == "MeowChan.PluginHost" else []
        expected = verifier.PACKAGES.get(package_id.lower(), (package_id, package_id + ".dll"))[1]
        dependency_xml = "".join(f'<dependency id="{item}" version="1.0.0" />' for item in dependencies)
        nuspec = (f'<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>'
                  f'<id>{package_id}</id><version>{version}</version><readme>README.md</readme>'
                  f'<license type="file">LICENSE</license><dependencies><group targetFramework="net10.0">'
                  f'{dependency_xml}</group></dependencies></metadata></package>')
        path = self.feed / (filename or f"{package_id}.{version}.nupkg")
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(package_id + ".nuspec", nuspec)
            archive.writestr(assembly or "lib/net10.0/" + expected, package_id.encode() + b" fixture DLL")
            if extra_dll:
                archive.writestr(extra_dll, b"unexpected binary")
            if readme:
                archive.writestr("README.md", b"Package reference documentation")
            if license:
                archive.writestr("LICENSE", b"Fixture license")
        return path

    def restored(self, package_ids=("MeowChan.PluginSdk", "MeowChan.PluginHost", "libmeowchan")):
        candidates = {}
        assets = {"packageFolders": {str(self.cache): {}}, "libraries": {}}
        for package_id in package_ids:
            path = self.package(package_id)
            candidate = verifier.inspect_package(path)
            key = package_id.lower()
            candidates[key] = candidate
            relative = Path(key) / candidate["version"]
            directory = self.cache / relative
            directory.mkdir(parents=True)
            shutil.copyfile(path, directory / path.name)
            with zipfile.ZipFile(path) as archive:
                archive.extractall(directory)
            assets["libraries"][package_id + "/" + candidate["version"]] = {"type": "package", "path": relative.as_posix()}
        return candidates, assets

    def verify(self, candidates, assets, sdk_only=False):
        return verifier.verify_assets(assets, self.cache, candidates, set(candidates), sdk_only=sdk_only)

    def test_accepts_all_three_packages_and_records_exact_bytes(self):
        candidates, assets = self.restored()
        checked = self.verify(candidates, assets)
        self.assertEqual(set(candidates), set(checked["packages"]))
        for key, candidate in candidates.items():
            self.assertEqual(candidate["sha256"], checked["packages"][key]["packageSha256"])
            self.assertEqual(candidate["assemblySha256"], checked["packages"][key]["assemblySha256"])

    def test_rejects_wrong_identity_and_extra_or_renamed_assemblies(self):
        for options in ({"package_id": "libochan"}, {"extra_dll": "tools/helper.dll"},
                        {"assembly": "lib/net10.0/old-sdk.dll"}):
            with self.subTest(options=options), self.assertRaisesRegex(ValueError, "package ID|assemblies"):
                verifier.inspect_package(self.package(**options))

    def test_rejects_desktop_roslyn_and_helper_dependencies(self):
        for dependency in ("Avalonia.Desktop", "Semi.Avalonia", "Irihi.Ursa", "Microsoft.CodeAnalysis.CSharp",
                           "MeowChan", "MeowChan.PluginWorker", "MeowChan.PluginCompiler"):
            with self.subTest(dependency=dependency), self.assertRaisesRegex(ValueError, "dependency leaked"):
                verifier.inspect_package(self.package("MeowChan.PluginHost", dependencies=["MeowChan.PluginSdk", "libmeowchan", dependency]))

    def test_sdk_has_no_dependencies_and_host_declares_both_contract_packages(self):
        with self.assertRaisesRegex(ValueError, "SDK must be independent"):
            verifier.inspect_package(self.package(dependencies=["Serilog"]))
        for dependencies in ([], ["MeowChan.PluginSdk"], ["libmeowchan"]):
            with self.subTest(dependencies=dependencies), self.assertRaisesRegex(ValueError, "Host package must declare"):
                verifier.inspect_package(self.package("MeowChan.PluginHost", dependencies=dependencies))

    def test_rejects_missing_readme_or_license_payload(self):
        for options in ({"readme": False}, {"license": False}):
            with self.subTest(options=options), self.assertRaisesRegex(ValueError, "README|license"):
                verifier.inspect_package(self.package(**options))

    def test_rejects_duplicate_zip_entries(self):
        path = self.package()
        with zipfile.ZipFile(path, "a") as archive:
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                archive.writestr("README.md", b"ambiguous README")
        with self.assertRaisesRegex(ValueError, "Duplicate ZIP"):
            verifier.inspect_package(path)

    def test_feed_requires_all_candidates_and_rejects_ambiguous_versions(self):
        self.package()
        with self.assertRaisesRegex(ValueError, "missing"):
            verifier.candidates_from_feed(self.feed)
        self.package("MeowChan.PluginHost")
        self.package("libmeowchan")
        self.package("Serilog")
        self.assertEqual(set(verifier.PACKAGES), set(verifier.candidates_from_feed(self.feed)))
        self.package(version="1.0.1")
        with self.assertRaisesRegex(ValueError, "Ambiguous feed"):
            verifier.candidates_from_feed(self.feed)

    def test_source_mapping_prevents_candidate_fallback_to_public_source(self):
        config = self.root / "NuGet.Config"
        verifier.write_config(config, self.feed, ["https://example.invalid/nuget/index.json", str(self.root / "offline")])
        root = ET.parse(config).getroot()
        self.assertIsNotNone(root.find("packageSources/clear"))
        mappings = {item.attrib["key"]: {package.attrib["pattern"] for package in item}
                    for item in root.findall("packageSourceMapping/packageSource")}
        self.assertEqual({item[0] for item in verifier.PACKAGES.values()}, mappings["candidate"])
        self.assertEqual({"*"}, mappings["dependencies-0"])
        self.assertEqual({"*"}, mappings["dependencies-1"])

    def test_project_reference_is_rejected_before_copying(self):
        project = self.root / "source/Consumer.csproj"
        project.parent.mkdir()
        project.write_text('<Project><ItemGroup><ProjectReference Include="../../source.csproj" /></ItemGroup></Project>')
        target = self.root / "isolated"
        with self.assertRaisesRegex(ValueError, "ProjectReference"):
            verifier.copy_consumer(project, target)
        self.assertFalse(target.exists())

    def test_copy_does_not_reuse_existing_build_outputs_or_lock(self):
        project = self.root / "source/Consumer.csproj"
        project.parent.mkdir()
        project.write_text('<Project><ItemGroup><PackageReference Include="MeowChan.PluginSdk" Version="[1.0.0]" /></ItemGroup></Project>')
        for name in ("bin/reused.dll", "obj/project.assets.json", "packages.lock.json"):
            path = project.parent / name
            path.parent.mkdir(exist_ok=True)
            path.write_text("stale")
        copied = verifier.copy_consumer(project, self.root / "isolated")
        self.assertTrue(copied.exists())
        self.assertEqual([copied], list(copied.parent.iterdir()))

    def test_rejects_restored_package_or_dll_tampering(self):
        candidates, assets = self.restored(("MeowChan.PluginSdk",))
        directory = self.cache / assets["libraries"]["MeowChan.PluginSdk/1.0.0"]["path"]
        for path, error in ((next(directory.glob("*.nupkg")), "package bytes"),
                            (directory / candidates["meowchan.pluginsdk"]["assembly"], "assembly bytes")):
            original = path.read_bytes()
            with self.subTest(path=path):
                path.write_bytes(original + b"tamper")
                with self.assertRaisesRegex(ValueError, error):
                    self.verify(candidates, assets)
                path.write_bytes(original)

    def test_rejects_second_cache_and_candidate_version_substitution(self):
        candidates, assets = self.restored(("MeowChan.PluginSdk",))
        changed = copy.deepcopy(assets)
        changed["packageFolders"][str(self.root / "personal cache")] = {}
        with self.assertRaisesRegex(ValueError, "only the isolated"):
            self.verify(candidates, changed)
        changed = copy.deepcopy(assets)
        changed["libraries"]["MeowChan.PluginSdk/9.9.9"] = changed["libraries"].pop("MeowChan.PluginSdk/1.0.0")
        with self.assertRaisesRegex(ValueError, "different candidate version"):
            self.verify(candidates, changed)

    def test_rejects_cache_escape_for_candidate_and_external_dependency(self):
        candidates, assets = self.restored(("MeowChan.PluginSdk",))
        outside = self.root / "external package"
        outside.mkdir()
        for name in ("MeowChan.PluginSdk/1.0.0", "Serilog/4.0.2"):
            changed = copy.deepcopy(assets)
            changed["libraries"][name] = {"type": "package", "path": str(outside)}
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "outside the isolated"):
                self.verify(candidates, changed)

    def test_rejects_project_and_forbidden_transitive_dependencies(self):
        candidates, assets = self.restored(("MeowChan.PluginSdk",))
        for name, kind in (("Unrelated/1.0.0", "project"), ("Avalonia/11.0.0", "package"),
                           ("Microsoft.CodeAnalysis.Common/5.0.0", "package")):
            changed = copy.deepcopy(assets)
            changed["libraries"][name] = {"type": kind, "path": "unused"}
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "non-package|dependency leaked"):
                self.verify(candidates, changed)

    def test_sdk_consumer_rejects_host_and_core_even_if_graph_is_valid(self):
        candidates, assets = self.restored()
        with self.assertRaisesRegex(ValueError, "SDK-only consumer acquired"):
            self.verify(candidates, assets, sdk_only=True)

    def test_missing_required_package_does_not_pass(self):
        candidates, assets = self.restored()
        assets["libraries"].pop("MeowChan.PluginHost/1.0.0")
        with self.assertRaisesRegex(ValueError, "required packages"):
            self.verify(candidates, assets)

    def test_executed_output_must_match_package_dll_bytes(self):
        candidates, assets = self.restored()
        resolved = self.verify(candidates, assets)
        output = self.root / "consumer output"
        output.mkdir()
        for key, candidate in candidates.items():
            restored = Path(resolved["packages"][key]["restoredPath"])
            shutil.copyfile(restored / candidate["assembly"], output / verifier.PACKAGES[key][1])
        self.assertEqual(3, len(verifier.verify_output(output, resolved, candidates)))
        (output / "MeowChan.PluginHost.dll").write_bytes(b"different build")
        with self.assertRaisesRegex(ValueError, "output DLL differs"):
            verifier.verify_output(output, resolved, candidates)


if __name__ == "__main__":
    unittest.main()
