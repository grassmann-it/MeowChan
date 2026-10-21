"""Fixture-only packaging checks; these do not launch or validate a macOS app."""

import importlib.util
import os
from pathlib import Path
import plistlib
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace
import zlib


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "package-macos.py"
SPEC = importlib.util.spec_from_file_location("meowchan_macos_packaging", SCRIPT)
packaging = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(packaging)


def png_chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def make_png(width=256, height=256):
    return (
        packaging.PNG_SIGNATURE
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress((b"\x00" + b"\x44\x55\x66\xff" * width) * height))
        + png_chunk(b"IEND", b"")
    )


def make_ico(png):
    return struct.pack("<HHHBBBBHHII", 0, 1, 1, 0, 0, 0, 0, 1, 32, len(png), 22) + png


class MacosPackagingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="meowchan packaging fixtures ")
        self.addCleanup(self.temporary.cleanup)
        # macOS may return /var while packaging resolves the same tree to /private/var.
        self.root = Path(self.temporary.name).resolve(strict=True)
        self.publish = self.root / "publish input"
        self.publish.mkdir()
        (self.publish / "MeowChan").write_bytes(b"fixture executable; not runnable")
        (self.publish / "MeowChan").chmod(0o640)
        (self.publish / "MeowChan.dll").write_bytes(b"managed fixture")
        (self.publish / "libAvaloniaNative.dylib").write_bytes(b"native fixture")
        (self.publish / ".metadata").write_text("publish metadata", encoding="utf-8")
        notices = self.publish / "licenses"
        notices.mkdir()
        (notices / "Avalonia.txt").write_text("upstream license", encoding="utf-8")
        (self.publish / "THIRD-PARTY-NOTICES.md").write_text("notices", encoding="utf-8")
        (self.publish / "LICENSE").write_text("application license", encoding="utf-8")
        (self.publish / "docs").mkdir()
        for name in ("native-video-licenses.txt", "native-video-sources.md", "native-video-linux-sources.md", "native-video-source-manifest.json",
                     "native-video-macos-sources.md", "native-video-macos-source-manifest.json", "native-video-macos-COPYING"):
            (self.publish / "docs" / name).write_text("legal/source fixture", encoding="utf-8")
        self.png = make_png()
        (self.publish / "Assets").mkdir()
        (self.publish / "Assets/meowchan.png").write_bytes(self.png)
        self.icon = self.root / "source icon.ico"
        self.icon.write_bytes(make_ico(self.png))
        self.output = self.root / "artifact output"
        self.output.mkdir()
        self.sibling = self.output / "unrelated data.txt"
        self.sibling.write_text("keep", encoding="utf-8")
        self.archive = self.output / "MeowChan osx fixture.tar.gz"

    def package(self, **kwargs):
        options = dict(publish_directory=self.publish, output_directory=self.output, version="1.0.0", icon_path=self.icon)
        options.update(kwargs)
        return packaging.package_macos(**options)

    def assert_no_bundle(self):
        self.assertFalse((self.output / "MeowChan.app").exists())
        self.assertEqual("keep", self.sibling.read_text(encoding="utf-8"))
        self.assertEqual([self.sibling], list(self.output.iterdir()))

    def test_complete_bundle_preserves_source_and_siblings(self):
        source_mode = (self.publish / "MeowChan").stat().st_mode
        bundle, archive = self.package()
        self.assertIsNone(archive)
        macos = bundle / "Contents" / "MacOS"
        for source in self.publish.rglob("*"):
            target = macos / source.relative_to(self.publish)
            if source.is_file():
                self.assertEqual(source.read_bytes(), target.read_bytes())
            else:
                self.assertTrue(target.is_dir())
        self.assertEqual("keep", self.sibling.read_text(encoding="utf-8"))
        self.assertEqual(source_mode, (self.publish / "MeowChan").stat().st_mode)
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(source_mode) | stat.S_IXUSR, stat.S_IMODE((macos / "MeowChan").stat().st_mode))
        metadata = plistlib.loads((bundle / "Contents" / "Info.plist").read_bytes())
        self.assertEqual("org.meowchan.desktop", metadata["CFBundleIdentifier"])
        self.assertEqual("1.0.0", metadata["CFBundleShortVersionString"])
        self.assertEqual("1.0.0", metadata["CFBundleVersion"])
        self.assertEqual("15.0", metadata["LSMinimumSystemVersion"])
        self.assertEqual("MeowChan", metadata["CFBundleExecutable"])
        self.assertEqual("meowchan.icns", metadata["CFBundleIconFile"])
        icon = (bundle / "Contents" / "Resources" / "meowchan.icns").read_bytes()
        self.assertEqual((b"icns", len(icon), b"ic08", len(icon) - 8), struct.unpack_from(">4sI4sI", icon))
        self.assertEqual(self.png, icon[16:])
        self.assertEqual(self.png, packaging.validate_icns(icon))

    def test_archive_round_trip_includes_only_full_bundle(self):
        bundle, archive_path = self.package(archive_path=self.archive)
        self.assertEqual(self.archive, archive_path)
        with tarfile.open(archive_path, "r:gz") as archive:
            names = set(archive.getnames())
            expected = {"MeowChan.app"} | {"MeowChan.app/" + entry.relative_to(bundle).as_posix() for entry in bundle.rglob("*")}
            self.assertEqual(expected, names)
            self.assertTrue(all(name.split("/")[0] == "MeowChan.app" and ".." not in name.split("/") for name in names))
            for name in names:
                if archive.getmember(name).isfile():
                    self.assertEqual((self.output / name).read_bytes(), archive.extractfile(name).read())
            self.assertTrue(archive.getmember("MeowChan.app/Contents/MacOS/MeowChan").mode & stat.S_IXUSR)
            self.assertNotIn("unrelated data.txt", names)

    def test_existing_bundle_is_untouched(self):
        bundle = self.output / "MeowChan.app"
        bundle.mkdir()
        marker = bundle / "existing.txt"
        marker.write_text("existing", encoding="utf-8")
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package()
        self.assertEqual("existing", marker.read_text(encoding="utf-8"))

    def test_existing_archive_is_untouched(self):
        self.archive.write_bytes(b"previous archive")
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package(archive_path=self.archive)
        self.assertEqual(b"previous archive", self.archive.read_bytes())
        self.assertFalse((self.output / "MeowChan.app").exists())

    def test_missing_input_and_executable_are_rejected(self):
        with self.assertRaises(packaging.PackagingError):
            self.package(publish_directory=self.root / "missing")
        (self.publish / "MeowChan").unlink()
        with self.assertRaisesRegex(packaging.PackagingError, "executable"):
            self.package()
        self.assert_no_bundle()

    def test_missing_license_is_rejected_before_packaging(self):
        (self.publish / "LICENSE").unlink()
        with self.assertRaisesRegex(packaging.PackagingError, "LICENSE"):
            self.package()
        self.assert_no_bundle()

    def test_stale_repository_readme_is_rejected_without_changing_input(self):
        readme = self.publish / "README.md"
        readme.write_text("keep in repository", encoding="utf-8")
        with self.assertRaisesRegex(packaging.PackagingError, "README"):
            self.package()
        self.assertEqual("keep in repository", readme.read_text(encoding="utf-8"))
        self.assert_no_bundle()

    def test_stale_showcase_screenshots_are_rejected(self):
        screenshots = self.publish / "docs/screenshots"
        screenshots.mkdir()
        (screenshots / "v0.9.0-gallery.png").write_bytes(self.png)
        with self.assertRaisesRegex(packaging.PackagingError, "screenshots"):
            self.package()
        self.assert_no_bundle()

    def test_file_input_is_rejected(self):
        with self.assertRaisesRegex(packaging.PackagingError, "directory"):
            self.package(publish_directory=self.publish / "MeowChan")
        self.assert_no_bundle()

    def test_invalid_version_is_rejected(self):
        for version in ("", "v1.0.0", "1.0", "1.0.0-beta", "01.0.0", "../../other"):
            with self.subTest(version=version), self.assertRaises(packaging.PackagingError):
                self.package(version=version)
        self.assert_no_bundle()

    def test_invalid_icons_are_rejected_before_output(self):
        wrong_dimensions = make_ico(make_png(128, 256))
        bad_offset = bytearray(make_ico(self.png))
        struct.pack_into("<I", bad_offset, 18, len(bad_offset) + 100)
        bad_crc = bytearray(make_ico(self.png))
        bad_crc[-1] ^= 1
        for payload in (b"", b"invalid icon", make_ico(self.png)[:-1], wrong_dimensions, bad_offset, bad_crc):
            with self.subTest(payload_length=len(payload)):
                self.icon.write_bytes(payload)
                with self.assertRaises(packaging.PackagingError):
                    self.package()
        self.assert_no_bundle()

    def test_missing_icon_is_rejected(self):
        self.icon.unlink()
        with self.assertRaises(OSError):
            self.package()
        self.assert_no_bundle()

    def test_output_must_not_overlap_publish_tree(self):
        with self.assertRaisesRegex(packaging.PackagingError, "overlap"):
            self.package(output_directory=self.publish)
        self.assertFalse((self.publish / "MeowChan.app").exists())
        with self.assertRaisesRegex(packaging.PackagingError, "outside"):
            self.package(archive_path=self.publish / "output.tar.gz")
        with self.assertRaisesRegex(packaging.PackagingError, "outside"):
            self.package(archive_path=self.output / "MeowChan.app" / "output.tar.gz")
        self.assert_no_bundle()

    def test_archive_extension_is_validated(self):
        with self.assertRaisesRegex(packaging.PackagingError, "tar.gz"):
            self.package(archive_path=self.output / "archive.bad")
        self.assert_no_bundle()

    def test_zip_requires_explicit_native_signing(self):
        with self.assertRaisesRegex(packaging.PackagingError, "ZIP output requires"):
            self.package(archive_path=self.output / "archive.zip")
        self.assert_no_bundle()

    def test_signing_requires_explicit_runtime(self):
        with self.assertRaisesRegex(packaging.PackagingError, "requires --runtime"):
            self.package(sign_adhoc=True)
        self.assert_no_bundle()

    def test_signed_layout_keeps_real_apphost_and_seals_resource_targets(self):
        # This is a layout-only test; Apple signature execution is covered by
        # the separate native fixture, never inferred from this mock.
        signer = mock.Mock()
        with mock.patch.object(packaging.sys, 'platform', 'darwin'), mock.patch.object(packaging, 'signing_module', return_value=signer), mock.patch.object(packaging, 'validate_bundle'):
            bundle, _ = self.package(runtime='osx-arm64', sign_adhoc=True)
        macos = bundle / 'Contents/MacOS'
        payload = bundle / 'Contents/Resources/application'
        self.assertFalse((macos / 'MeowChan').is_symlink())
        self.assertTrue((payload / 'MeowChan').is_symlink())
        for path in macos.iterdir():
            if path.name != 'MeowChan':
                self.assertTrue(path.is_symlink())
                self.assertFalse(Path(os.readlink(path)).is_absolute())
                self.assertTrue(path.resolve().is_relative_to(payload))
        for path in self.publish.rglob('*'):
            if path.is_file():
                self.assertEqual(path.read_bytes(), (macos / path.relative_to(self.publish)).read_bytes())
        signer.sign.assert_called_once()

    @unittest.skipUnless(sys.platform == "darwin", "Requires native Apple signing tools")
    def test_real_native_signed_zip_roundtrip(self):
        import platform
        runtime = "osx-arm64" if platform.machine() == "arm64" else "osx-x64"
        source = self.root / "fixture.c"
        source.write_text('''#include <dlfcn.h>
#include <CoreFoundation/CoreFoundation.h>
#include <stdio.h>
extern int core(void);
int main(int argc, char **argv) {
    if (core() != 0) return 5;
    CFBundleRef bundle = CFBundleGetMainBundle();
    CFStringRef identifier = bundle ? CFBundleGetIdentifier(bundle) : NULL;
    if (!identifier || !CFEqual(identifier, CFSTR("org.meowchan.desktop"))) {
        fprintf(stderr, "Main bundle identity missing at native apphost invocation\\n");
        if (bundle) CFShow(bundle);
        if (identifier) CFShow(identifier);
        return 4;
    }
    CFURLRef icon = CFBundleCopyResourceURL(bundle, CFSTR("meowchan"), CFSTR("icns"), NULL);
    if (!icon) {
        fprintf(stderr, "Outer bundle icon resource missing\\n");
        return 6;
    }
    CFRelease(icon);
    puts("BUNDLE IDENTITY PASS");
    if (argc < 2) return 0;
    void *p = dlopen(argv[1], RTLD_NOW);
    if (!p) return 2;
    int (*f)(void) = (int (*)(void))dlsym(p, "plugin");
    return f ? f() : 3;
}
''')
        native = self.publish / "libvlc" / runtime / "lib"
        plugins = native / "vlc/plugins"
        plugins.mkdir(parents=True)
        core_source = self.root / "core.c"
        core_source.write_text("int core(void) { return 0; }\n")
        plugin_source = self.root / "plugin.c"
        plugin_source.write_text("extern int core(void); int plugin(void) { return core(); }\n")
        core = native / "libvlccore.dylib"
        self.symlink("libvlccore.dylib", native / "libvlccore-alias.dylib")
        plugin = plugins / "libfixture_plugin.dylib"
        subprocess.run(["/usr/bin/clang", "-dynamiclib", str(core_source), "-install_name", "@rpath/libvlccore.dylib", "-o", str(core)], check=True)
        # Portable fixtures use a text placeholder here. Apple's bundle signer
        # correctly treats a .dylib sibling as code, so native fixtures need code.
        subprocess.run(["/usr/bin/clang", "-dynamiclib", str(core_source), "-install_name", "@loader_path/libAvaloniaNative.dylib", "-o", str(self.publish / "libAvaloniaNative.dylib")], check=True)
        plugin_name = f"@executable_path/libvlc/{runtime}/lib/vlc/plugins/libfixture_plugin.dylib"
        subprocess.run(["/usr/bin/clang", "-dynamiclib", str(plugin_source), str(core), "-install_name", plugin_name, "-o", str(plugin)], check=True)
        payload_dependency = self.publish / "native.dylib"
        subprocess.run(["/usr/bin/clang", "-dynamiclib", str(core_source), "-install_name", "@executable_path/native.dylib", "-o", str(payload_dependency)], check=True)
        subprocess.run(["/usr/bin/clang", str(source), str(payload_dependency), "-framework", "CoreFoundation", "-o", str(self.publish / "MeowChan")], check=True)
        bundle, archive = self.package(archive_path=self.output / "native.zip", runtime=runtime, sign_adhoc=True)
        self.assertTrue(archive.is_file())
        report = packaging.signing_module().validate(bundle, runtime)
        self.assertEqual(5, report["macho_files"])
        dependencies, _ = packaging.signing_module().load_commands(packaging.signing_module().run("/usr/bin/otool", "-l", bundle / "Contents/Resources/application/libvlc" / runtime / "lib/vlc/plugins/libfixture_plugin.dylib"))
        self.assertIn("@loader_path/../../libvlccore.dylib", dependencies)
        extracted = self.root / "extracted ZIP with spaces"
        subprocess.run(["/usr/bin/ditto", "-x", "-k", str(archive), str(extracted)], check=True)
        extracted_bundle = extracted / "MeowChan.app"
        bundle_alias = self.root / "bundle alias with spaces.app"
        self.symlink(extracted_bundle, bundle_alias)
        # A standalone executable symlink outside the .app makes CoreFoundation
        # use the external invocation directory as MainBundle (native runs
        # 39514856002, ARM and Intel). It is not a supported bundle invocation.
        # Test the shipped path and an entire .app symlink, each from a foreign
        # CWD; retain the strict identity/icon/dependency checks in both cases.
        for invocation in (extracted_bundle, bundle_alias):
            with self.subTest(bundle_invocation=invocation.name):
                result = subprocess.run([str(invocation / "Contents/MacOS/MeowChan"), str(invocation / "Contents/Resources/application/libvlc" / runtime / "lib/vlc/plugins/libfixture_plugin.dylib")], cwd=self.root, capture_output=True, text=True, check=False)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("BUNDLE IDENTITY PASS", result.stdout)
        self.assertIn("@rpath/libvlccore.dylib", packaging.signing_module().load_commands(packaging.signing_module().run("/usr/bin/otool", "-l", plugin))[0])
        self.assertTrue(report["signatures_verified"])
        # A sealed resource mutation must invalidate the bundle signature.
        (bundle / "Contents/Resources/meowchan.icns").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "Command failed"):
            packaging.signing_module().validate(bundle, runtime)

    def symlink(self, target, link):
        try:
            link.symlink_to(target)
        except (OSError, NotImplementedError):
            self.skipTest("Symbolic links are not available for this fixture user.")

    def test_relative_internal_symbolic_link_is_preserved(self):
        self.symlink("libAvaloniaNative.dylib", self.publish / "native-alias.dylib")
        bundle, _ = self.package(archive_path=self.archive)
        link = bundle / "Contents" / "MacOS" / "native-alias.dylib"
        self.assertTrue(link.is_symlink())
        self.assertEqual("libAvaloniaNative.dylib", os.readlink(link))
        with tarfile.open(self.archive, "r:gz") as archive:
            entry = archive.getmember("MeowChan.app/Contents/MacOS/native-alias.dylib")
            self.assertTrue(entry.issym())
            self.assertEqual("libAvaloniaNative.dylib", entry.linkname)

    def test_executable_must_not_be_a_symbolic_link(self):
        executable = self.publish / "MeowChan"
        executable.rename(self.publish / "real-apphost")
        self.symlink("real-apphost", executable)
        with self.assertRaisesRegex(packaging.PackagingError, "regular file"):
            self.package()
        self.assert_no_bundle()

    def test_windows_reparse_points_are_rejected(self):
        original_lstat = Path.lstat

        def lstat_with_reparse_point(path):
            result = original_lstat(path)
            if path == self.publish / "licenses":
                return SimpleNamespace(st_mode=result.st_mode, st_file_attributes=0x400)
            return result

        with mock.patch.object(Path, "lstat", new=lstat_with_reparse_point):
            with self.assertRaisesRegex(packaging.PackagingError, "reparse point"):
                self.package()
        self.assert_no_bundle()

    def test_external_and_dangling_symbolic_links_are_rejected(self):
        link = self.publish / "external"
        self.symlink(self.sibling, link)
        with self.assertRaisesRegex(packaging.PackagingError, "within"):
            self.package()
        link.unlink()
        self.symlink("missing", link)
        with self.assertRaisesRegex(packaging.PackagingError, "invalid symbolic"):
            self.package()
        self.assert_no_bundle()

    def test_dangling_destination_links_are_not_overwritten(self):
        destination = self.output / "MeowChan.app"
        self.symlink("missing", destination)
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package()
        self.assertTrue(destination.is_symlink())
        destination.unlink()
        self.symlink("missing", self.archive)
        with self.assertRaisesRegex(packaging.PackagingError, "overwrite"):
            self.package(archive_path=self.archive)
        self.assertTrue(self.archive.is_symlink())

    def test_archive_write_failure_cleans_only_owned_output(self):
        with mock.patch.object(packaging.shutil, "copyfileobj", side_effect=OSError("disk full")):
            with self.assertRaisesRegex(OSError, "disk full"):
                self.package(archive_path=self.archive)
        self.assert_no_bundle()

    def test_archive_collision_during_packaging_is_preserved(self):
        original = packaging.write_archive

        def race(bundle, archive):
            original(bundle, archive)
            self.archive.write_bytes(b"concurrent owner")

        with mock.patch.object(packaging, "write_archive", side_effect=race):
            with self.assertRaises(FileExistsError):
                self.package(archive_path=self.archive)
        self.assertEqual(b"concurrent owner", self.archive.read_bytes())
        self.assertFalse((self.output / "MeowChan.app").exists())

    def test_bundle_collision_during_packaging_is_preserved(self):
        original = packaging.write_archive
        destination = self.output / "MeowChan.app"

        def race(bundle, archive):
            original(bundle, archive)
            destination.mkdir()
            (destination / "keep.txt").write_bytes(b"concurrent owner")

        with mock.patch.object(packaging, "write_archive", side_effect=race):
            with self.assertRaises(FileExistsError):
                self.package(archive_path=self.archive)
        self.assertEqual(b"concurrent owner", (destination / "keep.txt").read_bytes())
        self.assertFalse(self.archive.exists())
        self.assertEqual("keep", self.sibling.read_text(encoding="utf-8"))

    def test_plutil_failure_leaves_existing_data_untouched(self):
        with (
            mock.patch.object(packaging.sys, "platform", "darwin"),
            mock.patch.object(packaging.shutil, "which", return_value="/usr/bin/plutil"),
            mock.patch.object(packaging.subprocess, "run", return_value=SimpleNamespace(returncode=1, stdout="", stderr="invalid plist")) as run,
        ):
            with self.assertRaisesRegex(packaging.PackagingError, "plutil rejected"):
                self.package()
        self.assertEqual(["plutil", "-lint"], run.call_args.args[0][:2])
        self.assert_no_bundle()

    def test_original_repository_icon_is_embedded_unchanged(self):
        png = packaging.png_from_ico(packaging.DEFAULT_ICON)
        self.assertEqual(png, packaging.validate_icns(packaging.make_icns(png)))

    def test_cli_handles_spaces_and_describes_verification_limit(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--publish-dir", str(self.publish), "--output-dir", str(self.output), "--version", "1.0.0", "--icon", str(self.icon), "--archive", str(self.archive)],
            capture_output=True, text=True, check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("unsigned bundle", result.stdout)
        self.assertIn("candidate baseline", result.stdout)
        self.assertIn("native launch verification remains required", result.stdout)
        self.assertTrue(self.archive.is_file())


if __name__ == "__main__":
    unittest.main()
