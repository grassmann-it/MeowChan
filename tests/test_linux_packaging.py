"""Small offline packaging/provenance fixtures; not native distribution proof."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


pack = module("linux_pack_test", "package-linux.py")
sources = module("linux_sources_test", "package-linux-sources.py")
checker = module("linux_native_check_test", "check-video-native.py")


def elf(machine=62):
    header = bytearray(64)
    header[:6] = b"\x7fELF\x02\x01"
    struct.pack_into("<H", header, 18, machine)
    return bytes(header)


class LinuxPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="linux package fixture ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_ubuntu_maintained_vlc_3020_supported_without_allowing_older_or_other_abi(self):
        for version in ("3.0.20 Vetinari", "3.0.21 Vetinari", "3.0.22", "3.1.0"):
            pack.check_vlc_version(version)
        for version in ("3.0.19 Vetinari", "2.2.8", "4.0.0", "not a version"):
            with self.assertRaises(ValueError):
                pack.check_vlc_version(version)

    def test_architecture_checked_from_real_header_not_filename(self):
        path = self.root / "misleading-aarch64.so"
        path.write_bytes(elf())
        pack.check_elf(path, "linux-x64")
        with self.assertRaises(ValueError):
            pack.check_elf(path, "linux-arm64")
        path.write_bytes(b"not an ELF")
        with self.assertRaises(ValueError):
            pack.elf_machine(path)

    def test_every_selected_plugin_contributes_recursive_dependency_closure(self):
        a, b, c, d = [self.root / name for name in ("core", "plugin", "codec", "common")]
        for path in (a, b, c, d):
            path.write_bytes(elf())
        graph = {a: [], b: ["libcodec.so.1"], c: ["libcommon.so.0", "libc.so.6"], d: []}
        resolved = {a: {}, b: {"libcodec.so.1": c}, c: {"libcommon.so.0": d}, d: {}}
        with mock.patch.object(pack, "needed", side_effect=lambda p: graph[p]), mock.patch.object(pack, "dependency_paths", side_effect=lambda p: resolved[p]):
            files, edges = pack.collect_closure([(a, Path("lib/core.so")), (b, Path("lib/vlc/plugins/codec/plugin.so"))], "linux-x64")
        self.assertEqual(set(files), {"lib/core.so", "lib/vlc/plugins/codec/plugin.so", "lib/libcodec.so.1", "lib/libcommon.so.0"})
        self.assertTrue(any(e["needed"] == "libc.so.6" and e["host"] for e in edges))

    def test_missing_and_absolute_dependency_fail_before_packaging(self):
        path = self.root / "plugin"
        path.write_bytes(elf())
        for dependency in ("missing.so", "/build/private.so"):
            with mock.patch.object(pack, "needed", return_value=[dependency]), mock.patch.object(pack, "dependency_paths", return_value={}):
                with self.assertRaises(ValueError):
                    pack.collect_closure([(path, Path("lib/plugin.so"))], "linux-x64")

    def test_version_definitions_do_not_inflate_host_requirements(self):
        text = "Version definition section\nName: GLIBCXX_3.4.99\nVersion needs section\nName: GLIBC_2.27\nName: GLIBCXX_3.4.22\n"
        with mock.patch.object(pack, "run", return_value=text):
            self.assertEqual(pack.symbols(self.root / "lib"), {"GLIBC": "2.27", "GLIBCXX": "3.4.22"})

    def test_exact_deb_verification_checks_archive_bytes_usrmerge_members_and_completeness(self):
        content = b"canonical distro ELF fixture"
        archive_bytes = io.BytesIO()
        with tarfile.open(fileobj=archive_bytes, mode="w") as archive:
            entry = tarfile.TarInfo("./lib/example.so.1")
            entry.size = len(content)
            archive.addfile(entry, io.BytesIO(content))
        deb_bytes = b"checksum-bound deb fixture"
        package = {"package": "fixture:amd64", "version": "1.0-1", "deb_sha256": hashlib.sha256(deb_bytes).hexdigest()}
        mappings = [{"package": package["package"], "original_path": "/usr/lib/example.so.1", "original_sha256": hashlib.sha256(content).hexdigest()}]
        def download(*args, cwd=None):
            self.assertEqual(args, ("apt-get", "download", "fixture:amd64=1.0-1"))
            (cwd / "fixture.deb").write_bytes(deb_bytes)
        def inspect(*args, **kwargs):
            return mock.Mock(stdout=io.BytesIO(archive_bytes.getvalue()), wait=mock.Mock(return_value=0), poll=mock.Mock(return_value=0))
        with mock.patch.object(pack, "run", side_effect=download), mock.patch.object(pack.subprocess, "Popen", side_effect=inspect), mock.patch.object(pack, "reserve"):
            pack.verify_distro_binary(package, mappings, self.root)
            self.assertEqual(package["verified_original_files"], 1)
            mappings[0]["original_sha256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "differs from its exact .deb"):
                pack.verify_distro_binary(package, mappings, self.root)
            mappings[0]["original_path"] = "/usr/lib/missing.so"
            with self.assertRaisesRegex(ValueError, "not mapped"):
                pack.verify_distro_binary(package, mappings, self.root)
            package["deb_sha256"] = "0" * 64
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                pack.verify_distro_binary(package, mappings, self.root)
        self.assertFalse(list(self.root.glob("deb-*")))

    def test_runtime_profile_includes_all_gallery_container_demuxers(self):
        self.assertTrue({"mkv", "mp4", "avi"} <= set(pack.PLUGIN_FILES["demux"]))
        self.assertIn("avcodec", pack.REQUIRED_PLUGINS)
        root = self.root / "plugins"
        for name in pack.REQUIRED_PLUGINS:
            category = next(category for category, names in pack.PLUGIN_FILES.items() if name in names)
            path = root / category / ("lib" + name + "_plugin.so")
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(elf())
        self.assertEqual(len(pack.selected_plugins(root)), len(pack.REQUIRED_PLUGINS))
        (root / "demux/libmp4_plugin.so").unlink()
        with self.assertRaises(ValueError):
            pack.selected_plugins(root)

    def fixture(self):
        publish = self.root / "publish"
        (publish / "Assets").mkdir(parents=True)
        (publish / "MeowChan").write_bytes(elf())
        (publish / "MeowChan.dll").write_bytes(b"managed fixture")
        (publish / "Assets/meowchan.png").write_bytes(b"fixture PNG")
        directory = self.root / "native"
        runtime = directory / "runtime"
        (runtime / "lib/vlc/plugins/video_output").mkdir(parents=True)
        (runtime / "lib/vlc/share").mkdir()
        for relative in ("lib/libvlc.so.5", "lib/libvlccore.so.9", "lib/vlc/plugins/video_output/libvmem_plugin.so"):
            (runtime / relative).write_bytes(elf())
        (runtime / "lib/vlc/share/data").write_bytes(b"VLC data")
        licenses = directory / "licenses"
        licenses.mkdir()
        (licenses / "VLC.copyright").write_text("fixture LGPL/GPL notices")
        inventory = {"schema": "meowchan.linux-runtime.v1", "runtime": "linux-x64", "files": pack.file_hashes(runtime), "licenses": pack.file_hashes(licenses), "packages": []}
        manifest = directory / "runtime-manifest.json"
        pack.write_json(manifest, inventory)
        return publish, runtime, manifest

    def test_three_artifacts_bind_apphost_markers_licenses_and_complete_payload(self):
        publish, runtime, manifest = self.fixture()
        output = self.root / "output"
        appdir_files = {}
        def appimage(appdir, destination, rid, tools):
            appdir_files.update(pack.file_hashes(appdir))
            self.assertFalse((appdir / ".DirIcon").is_symlink())
            self.assertIn('exec "$HERE/usr/lib/meowchan/MeowChan" "$@"', (appdir / "AppRun").read_text())
            destination.write_bytes(elf() + b"fixture image")
        with mock.patch.object(pack, "build_appimage", side_effect=appimage), mock.patch.object(pack, "symbols", return_value={}), mock.patch.object(pack, "reserve"):
            result = pack.package(publish, "linux-x64", "0.9.0", "a" * 40, runtime, manifest, self.root / "tools", output)
        self.assertEqual(len(result["artifacts"]), 3)
        self.assertEqual(result["source_commit"], "a" * 40)
        self.assertEqual(result["apphost_sha256"], pack.digest(publish / "MeowChan"))
        for artifact in result["artifacts"]:
            self.assertEqual(artifact["sha256"], pack.digest(output / artifact["file"]))
            if artifact["flavor"] == "appimage":
                self.assertEqual(artifact["payload_files"], appdir_files)
                continue
            extracted = self.root / artifact["flavor"]
            with tarfile.open(output / artifact["file"]) as archive:
                self.assertTrue(all(m.isfile() or m.isdir() for m in archive))
                archive.extractall(extracted, filter="data")
            self.assertEqual(artifact["payload_files"], pack.file_hashes(extracted))
            self.assertTrue((extracted / "MeowChan").stat().st_mode & 0o111)
            marker = json.loads((extracted / "linux-video-runtime.json").read_text())
            self.assertEqual(marker["Mode"], "bundled" if artifact["bundled"] else "system")
            checked = checker.validate(extracted, "linux-x64")
            self.assertIn("backend", checked)
            if artifact["bundled"]:
                legal = json.loads((extracted / "linux-video-runtime-inventory.json").read_text())
                self.assertEqual(legal["license_files"], pack.file_hashes(extracted / "linux-video-licenses"))
                self.assertEqual(marker["Files"], pack.file_hashes(extracted / "libvlc/linux-x64"))
                (extracted / "libvlc/linux-x64/lib/libvlc.so.5").write_bytes(b"corruption")
                with self.assertRaises(ValueError):
                    checker.validate(extracted, "linux-x64")

    def test_changed_private_runtime_rejected_before_emitting_artifacts(self):
        publish, runtime, manifest = self.fixture()
        (runtime / "lib/libvlc.so.5").write_bytes(b"changed")
        with mock.patch.object(pack, "symbols", return_value={}):
            with self.assertRaises(ValueError):
                pack.package(publish, "linux-x64", "0.9.0", "a" * 40, runtime, manifest, self.root, self.root / "out")
        self.assertFalse((self.root / "out").exists())

    def test_symlinks_and_unlisted_files_are_not_silently_accepted(self):
        root = self.root / "payload"
        root.mkdir()
        (root / "file").write_bytes(b"original")
        known = pack.file_hashes(root)
        (root / "unlisted").write_bytes(b"extra")
        with self.assertRaises(ValueError):
            pack.verify_files(root, known)
        (root / "link").symlink_to("file")
        with self.assertRaises(ValueError):
            pack.file_hashes(root)

    def test_pinned_tools_reject_wrong_bytes_without_executing_them(self):
        tools = self.root / "tools"
        tools.mkdir()
        for item in pack.tool_assets("linux-x64").values():
            (tools / item["file"]).write_bytes(elf())
        with mock.patch.object(pack.subprocess, "run") as execute:
            with self.assertRaises(ValueError):
                pack.build_appimage(self.root, self.root / "out.AppImage", "linux-x64", tools)
            execute.assert_not_called()

    @unittest.skipUnless(shutil.which("cc") and shutil.which("patchelf") and shutil.which("readelf"), "Small native ELF tools unavailable")
    def test_real_small_elf_plugin_dependency_can_be_relocated_without_loader_environment(self):
        dep = self.root / "dependency.c"
        dep.write_text("int dependency(void) { return 42; }\n")
        plugin = self.root / "plugin.c"
        plugin.write_text("extern int dependency(void); int plugin(void) { return dependency(); }\n")
        subprocess.run(["cc", "-shared", "-fPIC", str(dep), "-Wl,-soname,libfixture.so.1", "-o", str(self.root / "libfixture.so.1")], check=True, capture_output=True)
        subprocess.run(["cc", "-shared", "-fPIC", str(plugin), "-L" + str(self.root), "-l:libfixture.so.1", "-Wl,-rpath," + str(self.root), "-o", str(self.root / "plugin.so")], check=True, capture_output=True)
        self.assertIn("libfixture.so.1", pack.needed(self.root / "plugin.so"))
        target = self.root / "moved/lib/vlc/plugins/codec"
        target.mkdir(parents=True)
        shutil.copyfile(self.root / "plugin.so", target / "plugin.so")
        shutil.copyfile(self.root / "libfixture.so.1", self.root / "moved/lib/libfixture.so.1")
        pack.run("patchelf", "--set-rpath", "$ORIGIN/../../..", str(target / "plugin.so"))
        resolved = pack.dependency_paths(target / "plugin.so")
        self.assertEqual(resolved["libfixture.so.1"].resolve(), (self.root / "moved/lib/libfixture.so.1").resolve())


class LinuxSourceTests(unittest.TestCase):
    def test_dsc_exact_version_archives_and_tamper_check(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            data = root / "vlc.orig.tar.xz"
            data.write_bytes(b"source archive fixture")
            dsc = root / "vlc.dsc"
            dsc.write_text(f"Source: vlc\nVersion: 3.0.21-1ubuntu1\nChecksums-Sha256:\n {pack.digest(data)} {data.stat().st_size} {data.name}\n")
            result = sources.dsc_files(dsc, "vlc", "3.0.21-1ubuntu1")
            self.assertEqual(set(result), {data.name, dsc.name})
            with self.assertRaises(ValueError):
                sources.dsc_files(dsc, "vlc", "3.0.20-1")
            data.write_bytes(b"tampered")
            with self.assertRaises(ValueError):
                sources.dsc_files(dsc, "vlc", "3.0.21-1ubuntu1")

    def test_source_assignments_are_unique_and_confined_to_supported_rids(self):
        self.assertEqual(sources.assignments(["linux-x64=a=b.json"]), {"linux-x64": Path("a=b.json")})
        for values in (["linux-x64=a", "linux-x64=b"], ["osx-x64=a"], ["linux-x64="]):
            with self.assertRaises(ValueError):
                sources.assignments(values)

    def test_exact_static_lgpl_input_is_pinned(self):
        fuse = next(item for item in sources.RUNTIME_INPUTS if item["kind"] == "static-LGPL-library")
        self.assertEqual(fuse["file"], "fuse-3.15.0.tar.xz")
        self.assertEqual(fuse["sha256"], "70589cfd5e1cff7ccd6ac91c86c01be340b227285c5e200baa284e401eea2ca0")

    def test_source_companion_streams_exact_distro_and_runtime_inputs_with_commit_binding(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifests = {}
            for rid in pack.RIDS:
                directory = root / rid
                (directory / "licenses").mkdir(parents=True)
                (directory / "licenses/copyright").write_text("license fixture")
                manifest = directory / "runtime-manifest.json"
                pack.write_json(manifest, {"schema": "meowchan.linux-runtime.v1", "runtime": rid, "files": {}, "packages": [{"source": "vlc", "source_version": "3.0.21-1"}]})
                manifests[rid] = manifest
            calls = []
            def source_download(*command, cwd=None):
                calls.append(command)
                if "--print-uris" in command:
                    return "'https://fixture.invalid/source.tar.xz' source.tar.xz 100 SHA256:" + "a" * 64 + "\n"
                payload = cwd / "vlc.orig.tar.xz"
                payload.write_bytes(b"source")
                (cwd / "vlc.dsc").write_text(f"Source: vlc\nVersion: 3.0.21-1\nChecksums-Sha256:\n {pack.digest(payload)} {payload.stat().st_size} {payload.name}\n")
                return "Verified exact source fixture\n"
            def runtime_download(url, destination, expected=None):
                if destination.name.startswith("type2-runtime-"):
                    with tarfile.open(destination, "w:gz") as archive:
                        data = b"pinned runtime recipe fixture"
                        member = tarfile.TarInfo("type2-runtime-" + pack.RUNTIME_COMMIT + "/BUILD.md")
                        member.size = len(data)
                        archive.addfile(member, io.BytesIO(data))
                else:
                    self.assertIsNotNone(expected)
                    destination.write_bytes(b"source input fixture")
            output = root / "sources.tar.gz"
            with mock.patch.object(sources.common, "run", side_effect=source_download), mock.patch.object(sources.common, "download", side_effect=runtime_download), mock.patch.object(sources.common, "reserve"):
                result = sources.collect(manifests, output, root / "cache", "b" * 40)
            self.assertEqual(len(calls), 2)  # one size query/download, shared by both CPUs
            self.assertEqual(result["source_commit"], "b" * 40)
            self.assertEqual(len(result["appimage_runtime"]), 3)
            self.assertEqual(result["managed_binding"]["file"], "libvlcsharp-3.10.2.tar.gz")
            with tarfile.open(output) as archive:
                names = archive.getnames()
                self.assertIn("manifest.json", names)
                self.assertIn("packaging/scripts/package-linux.py", names)
                self.assertTrue(any(name.endswith("/vlc.dsc") for name in names))
                stored = json.load(archive.extractfile("manifest.json"))
                self.assertEqual(stored["source_commit"], "b" * 40)
            record = json.loads(output.with_name(output.name + ".json").read_text())
            self.assertEqual(record["sha256"], pack.digest(output))


if __name__ == "__main__":
    unittest.main()
