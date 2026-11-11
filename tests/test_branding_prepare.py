"""Focused, stdlib-only prepare tests; no GUI, dotnet, package build or live IO.

Synthetic RGBA fixtures substitute the pinned digest only inside tests. The CLI
has no digest override. Run the selected real source separately after the lease.
"""
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zlib


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("branding_prepare", ROOT / "scripts/prepare-branding-ux103.py")
branding = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(branding)
PINNED_SHA256 = "6c9024b7220b6e015eeeb36aec9763fa5d804224b2b9e260caa7c4947c8436b1"


def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def png(width, height, pixels, filters=None, metadata=False):
    filters = filters or [0] * height
    raw = bytearray()
    stride = width * 4
    previous = bytes(stride)
    for y in range(height):
        row = pixels[y * stride:(y + 1) * stride]
        raw.append(filters[y])
        for x, value in enumerate(row):
            left = row[x - 4] if x >= 4 else 0
            above = previous[x]
            upper_left = previous[x - 4] if x >= 4 else 0
            if filters[y] == 4:
                p = left + above - upper_left
                predictor = min((left, above, upper_left), key=lambda v: abs(p - v))
            else:
                predictor = (0, left, above, (left + above) // 2)[filters[y]]
            raw.append((value - predictor) & 255)
        previous = row
    return (branding.PNG_SIGNATURE + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + (chunk(b"tEXt", b"fixture\0master metadata") if metadata else b"")
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def ico_fixture(frames):
    """Independent container with opaque fixture pixels, PNG or bottom-up DIB."""
    entries = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    payloads = bytearray()
    for width, height, encoding in frames:
        if encoding == "png":
            data = png(width, height, bytes((20, 40, 60, 255)) * width * height)
        else:
            data = (struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, width * height * 4, 0, 0, 0, 0)
                    + bytes((60, 40, 20, 255)) * width * height
                    + bytes(((width + 31) // 32) * 4 * height))
        offset = 6 + 16 * len(frames) + len(payloads)
        entries.extend(struct.pack("<BBBBHHII", width % 256, height % 256, 0, 0, 1, 32, len(data), offset))
        payloads.extend(data)
    return bytes(entries + payloads)


def ico_payloads(data):
    count = struct.unpack_from("<H", data, 4)[0]
    result = []
    for index in range(count):
        entry = struct.unpack_from("<BBBBHHII", data, 6 + index * 16)
        size, offset = entry[-2:]
        result.append(data[offset:offset + size])
    return result


class RgbaConversionTests(unittest.TestCase):
    def test_pinned_source_has_no_cli_override(self):
        self.assertEqual(PINNED_SHA256, branding.SELECTED_SOURCE_SHA256)
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            branding.main(["--source", "source.png", "--output", "output", "--sha256", "arbitrary"])
        self.assertEqual(2, caught.exception.code)

    def test_all_png_row_filters_and_rgba_round_trip(self):
        pixels = bytes((i * 37 + 17) % 256 for i in range(3 * 5 * 4))
        image = png(3, 5, pixels, filters=[0, 1, 2, 3, 4])
        self.assertEqual((3, 5, pixels), branding.decode_rgba(image))
        self.assertEqual((3, 5, pixels), branding.decode_rgba(branding.encode_rgba(3, 5, pixels)))

    def test_invalid_png_data_is_refused(self):
        valid = png(1, 1, bytes((1, 2, 3, 4)))
        header = chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0))
        end = chunk(b"IEND", b"")
        invalid = [b"", valid[:-1], valid + b"extra", valid[:20], valid[:-1] + bytes((valid[-1] ^ 1,)),
                   branding.PNG_SIGNATURE + header + header + chunk(b"IDAT", zlib.compress(b"\0" * 5)) + end]
        for compressed in (b"not zlib", zlib.compress(b"\0" * 4), zlib.compress(b"\0" * 6),
                           zlib.compress(b"\0" * 5) + b"trailing", zlib.compress(b"\0" * 5)[:-1],
                           zlib.compress(b"\x05\0\0\0\0")):
            invalid.append(branding.PNG_SIGNATURE + header + chunk(b"IDAT", compressed) + end)
        for data in invalid:
            with self.subTest(length=len(data)), self.assertRaises(branding.PreparationError):
                branding.decode_rgba(data)

    def test_unsupported_modes_critical_chunks_animation_and_dimensions_are_refused(self):
        for width, height, depth, colour, interlace in ((0, 1, 8, 6, 0), (99999, 99999, 8, 6, 0),
                                                       (1, 1, 16, 6, 0), (1, 1, 8, 2, 0), (1, 1, 8, 6, 1)):
            data = (branding.PNG_SIGNATURE + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, colour, 0, 0, interlace))
                    + chunk(b"IDAT", zlib.compress(b"\0" * 5)) + chunk(b"IEND", b""))
            with self.subTest(header=(width, height, depth, colour, interlace)), self.assertRaises(branding.PreparationError):
                branding.decode_rgba(data)
        valid = png(1, 1, bytes((1, 2, 3, 4)))
        for extra in (chunk(b"acTL", struct.pack(">II", 1, 0)), chunk(b"ABCD", b"")):
            with self.assertRaises(branding.PreparationError):
                branding.decode_rgba(valid[:33] + extra + valid[33:])
        data = valid[:-12] + chunk(b"tEXt", b"x\0y") + chunk(b"IDAT", b"") + valid[-12:]
        with self.assertRaisesRegex(branding.PreparationError, "Non-contiguous"):
            branding.decode_rgba(data)

    def test_area_sampling_uses_alpha_not_transparent_hidden_rgb(self):
        pixels = bytes((255, 0, 0, 255, 0, 0, 255, 0))
        self.assertEqual(bytes((255, 0, 0, 128)), branding.resize_contain(2, 1, pixels, 1, 1))
        transparent = bytes((20, 30, 40, 0)) * 4
        self.assertEqual(bytes(4), branding.resize_contain(2, 2, transparent, 1, 1))

    def test_area_sampling_preserves_solid_partial_alpha_and_identity(self):
        colour = bytes((12, 80, 200, 91))
        for target in (1, 2, 3, 8):
            with self.subTest(target=target):
                self.assertEqual(colour * target * target, branding.resize_contain(4, 4, colour * 16, target, target))
        pixels = bytes(range(1, 65))
        self.assertEqual(pixels, branding.resize_contain(4, 4, pixels, 4, 4))

    def test_non_integer_area_weights_conserve_coverage_and_alpha(self):
        for source, target in ((1254, 512), (1254, 24), (3, 2), (2, 3)):
            weights = branding.area_weights(source, target)
            self.assertTrue(all(sum(weight for _, weight in sample) == source for sample in weights))
            self.assertTrue(all(0 <= index < source and weight > 0 for sample in weights for index, weight in sample))
        pixels = bytes((255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0))
        self.assertEqual(bytes((204, 51, 0, 213, 0, 255, 0, 43)), branding.resize_contain(3, 1, pixels, 2, 1))

    def test_non_square_content_is_contained_not_stretched_or_cropped(self):
        red, green = bytes((255, 0, 0, 255)), bytes((0, 255, 0, 255))
        actual = branding.resize_contain(2, 1, red + green, 4, 4)
        self.assertEqual(bytes(16) + (red * 2 + green * 2) * 2 + bytes(16), actual)
        actual = branding.resize_contain(1, 2, red + green, 4, 4)
        self.assertEqual((bytes(4) + red * 2 + bytes(4)) * 2 + (bytes(4) + green * 2 + bytes(4)) * 2, actual)

    def test_ico_preserves_dimensions_order_and_payload_conventions(self):
        frames = branding.ico_frames(ico_fixture([(256, 256, "png"), (24, 24, "dib"), (16, 16, "dib")]))
        pixels = bytes((10, 20, 30, 100)) * 4
        generated = branding.encode_ico(2, 2, pixels, frames)
        self.assertEqual(frames, branding.ico_frames(generated))
        self.assertEqual((256, 256, bytes((10, 20, 30, 100)) * 256 * 256), branding.decode_rgba(ico_payloads(generated)[0]))
        dib = ico_payloads(generated)[1]
        self.assertEqual(bytes((30, 20, 10, 100)), dib[40:44])

    def test_dib_is_bottom_up_bgra_and_transparent_mask_is_padded(self):
        pixels = bytes((255, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 128, 10, 20, 30, 0))
        dib = branding.encode_dib(2, 2, pixels)
        self.assertEqual((40, 2, 4, 1, 32), struct.unpack_from("<IiiHH", dib))
        self.assertEqual(bytes((255, 0, 0, 128, 30, 20, 10, 0, 0, 0, 255, 255, 0, 255, 0, 0)), dib[40:56])
        self.assertEqual(b"\x40\0\0\0\x40\0\0\0", dib[56:])

    def test_invalid_ico_directories_and_layouts_are_refused(self):
        valid = ico_fixture([(16, 16, "dib")])
        bad_offset = bytearray(valid)
        struct.pack_into("<I", bad_offset, 18, 0)
        bad_dimensions = bytearray(valid)
        bad_dimensions[6] = 17
        bad_depth = bytearray(valid)
        struct.pack_into("<H", bad_depth, 12, 24)
        duplicate = ico_fixture([(16, 16, "dib"), (16, 16, "dib")])
        overlap = bytearray(ico_fixture([(16, 16, "dib"), (24, 24, "dib")]))
        struct.pack_into("<I", overlap, 34, 38)
        for data in (b"", b"invalid", valid[:-1], bad_offset, bad_dimensions, bad_depth, duplicate, overlap):
            with self.subTest(length=len(data)), self.assertRaises(branding.PreparationError):
                branding.ico_frames(data)


class PrepareContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="branding-prepare-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / "checkout"
        self.repo.mkdir()
        self.source = self.root / "selected source.png"
        self.source_bytes = png(2, 1, bytes((255, 0, 0, 255, 0, 255, 0, 80)), metadata=True)
        self.source.write_bytes(self.source_bytes)
        fixtures = {branding.MASTER: png(12, 12, bytes((1, 2, 3, 255)) * 144),
                    branding.PNG: png(6, 6, bytes((4, 5, 6, 255)) * 36),
                    branding.ICO: ico_fixture([(8, 8, "png"), (4, 4, "dib")]),
                    "Desktop/Logo.axaml": b'<Image Source="avares://MeowChan/Assets/meowchan.png"/>',
                    "MeowChan.csproj": b'<ApplicationIcon>Assets/meowchan.ico</ApplicationIcon>\n<AvaloniaResource Include="Assets/**"/>',
                    "scripts/package-macos.py": b'ICON = root / "Assets" / "meowchan.ico"\nDEST = "meowchan.icns"',
                    "scripts/package-linux.py": b'source = "Assets/meowchan.png"\ndest = "MeowChan.png"',
                    "tests/test_icons.py": b'ICON = "meowchan.png"',
                    "README.md": b'<img src="Assets/meowchan.png">',
                    "Plugin.csproj": b'<AssemblyName>MeowChan.PngMetadataFilter</AssemblyName>',
                    "THIRD-PARTY-NOTICES.md": b'Existing branding notice: Assets/branding/meowchan-source.png',
                    "scripts/prepare-branding-ux103.py": b'"meowchan.png"',
                    "tests/test_branding_prepare.py": b'"meowchan.ico"'}
        for name, data in fixtures.items():
            path = self.repo / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        self.before = fixtures
        self.tracked = list(fixtures)
        self.output = self.root / "prepared candidates"
        for patcher in (patch.object(branding, "REPO_ROOT", self.repo),
                        patch.object(branding, "SELECTED_SOURCE_SHA256", hashlib.sha256(self.source_bytes).hexdigest()),
                        patch.object(branding, "git_output", side_effect=self.git_output)):
            patcher.start()
            self.addCleanup(patcher.stop)

    def git_output(self, repo, *args):
        self.assertEqual(self.repo, repo)
        if args == ("ls-files", "-z"):
            return ("\0".join(self.tracked) + "\0").encode("utf-8")
        self.assertEqual(("rev-parse", "HEAD"), args)
        return b"b5cbccc591f30eb50c90dbaa530b5bbf4b7696b6\n"

    def assert_inputs_unchanged(self):
        self.assertEqual(self.source_bytes, self.source.read_bytes())
        for name, data in self.before.items():
            self.assertEqual(data, (self.repo / name).read_bytes(), name)

    def test_prepare_creates_only_candidates_and_hashed_machine_readable_inventory(self):
        manifest_path = branding.prepare(self.source, self.output)
        self.assertEqual(self.output / "adoption-inventory.json", manifest_path)
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertEqual("meowchan.branding-prepare.v1", manifest["schema"])
        self.assertFalse(manifest["adoption_authorized"])
        self.assertEqual("prepare-only", manifest["mode"])
        self.assertEqual(self.source_bytes, (self.output / branding.MASTER).read_bytes())
        self.assertEqual((6, 6), branding.png_size((self.output / branding.PNG).read_bytes()))
        self.assertEqual(branding.ico_frames(self.before[branding.ICO]), branding.ico_frames((self.output / branding.ICO).read_bytes()))
        self.assertEqual({branding.MASTER, branding.PNG, branding.ICO, "adoption-inventory.json"},
                         {p.relative_to(self.output).as_posix() for p in self.output.rglob("*") if p.is_file()})
        for record in manifest["prepared_files"]:
            data = (self.output / record["path"]).read_bytes()
            self.assertEqual(hashlib.sha256(data).hexdigest(), record["sha256"])
            self.assertEqual(len(data), record["bytes"])
        for record in manifest["existing_inputs"]:
            self.assertEqual(hashlib.sha256(self.before[record["path"]]).hexdigest(), record["sha256"])
        self.assertIn("open user decision", manifest["gates"]["global_both_themes_all_surfaces"])
        self.assertEqual("not assessed", manifest["gates"]["rights_and_provenance"])
        self.assert_inputs_unchanged()

    def test_candidates_and_inventory_are_reproducible_at_different_output_locations(self):
        branding.prepare(self.source, self.output)
        other = self.root / "second preparation"
        branding.prepare(self.source, other)
        for first in self.output.rglob("*"):
            if first.is_file():
                self.assertEqual(first.read_bytes(), (other / first.relative_to(self.output)).read_bytes())
        self.assert_inputs_unchanged()

    def test_inventory_includes_package_scripts_resource_glob_docs_tests_and_no_plugin_false_positive(self):
        inventory = branding.adoption_inventory(self.repo)
        consumers = {item["path"]: item for item in inventory["consumers"]}
        self.assertEqual({"Desktop/Logo.axaml", "MeowChan.csproj", "scripts/package-macos.py", "scripts/package-linux.py",
                          "tests/test_icons.py", "README.md", "THIRD-PARTY-NOTICES.md"}, set(consumers))
        self.assertEqual("packaging", consumers["scripts/package-macos.py"]["category"])
        self.assertEqual("resource-glob", consumers["MeowChan.csproj"]["references"][1]["kind"])
        self.assertEqual(2, consumers["scripts/package-linux.py"]["references"][1]["line"])
        self.assertEqual(["MeowChan.png"], consumers["scripts/package-linux.py"]["references"][1]["assets"])
        self.assertEqual("documentation", consumers["README.md"]["category"])
        self.assert_inputs_unchanged()

    def test_inventory_reports_missing_and_symlinked_tracked_paths_and_ignores_untracked(self):
        self.tracked.append("missing.py")
        (self.repo / "untracked.py").write_text('"meowchan.png"', encoding="utf-8")
        self.assertEqual(["missing.py"], branding.adoption_inventory(self.repo)["skipped_missing_or_symlink"])
        if os.name != "nt":
            link = self.repo / "linked.py"
            link.symlink_to(self.source)
            self.tracked.append("linked.py")
            self.assertEqual(["linked.py", "missing.py"], branding.adoption_inventory(self.repo)["skipped_missing_or_symlink"])

    def test_hash_mismatch_is_rejected_before_any_output(self):
        with patch.object(branding, "SELECTED_SOURCE_SHA256", PINNED_SHA256):
            with self.assertRaisesRegex(branding.PreparationError, "SHA-256 mismatch"):
                branding.prepare(self.source, self.output)
        self.assertFalse(self.output.exists())
        self.assert_inputs_unchanged()

    def test_existing_outputs_files_directories_and_product_inputs_are_never_overwritten(self):
        self.output.mkdir()
        marker = self.output / "keep.txt"
        marker.write_bytes(b"existing")
        for output in (self.output, self.source, *(self.repo / name for name in (branding.MASTER, branding.PNG, branding.ICO))):
            with self.subTest(output=output), self.assertRaisesRegex(branding.PreparationError, "overwrite"):
                branding.prepare(self.source, output)
        self.assertEqual(b"existing", marker.read_bytes())
        self.assert_inputs_unchanged()

    def test_even_new_output_inside_checkout_is_refused(self):
        for output in (self.repo / "Assets" / "new", self.repo / "candidate-output", self.repo / "artifacts"):
            with self.subTest(output=output), self.assertRaisesRegex(branding.PreparationError, "outside the checkout"):
                branding.prepare(self.source, output)
            self.assertFalse(output.exists())
        self.assert_inputs_unchanged()

    @unittest.skipIf(os.name == "nt", "Symlink privileges are platform-specific")
    def test_symlink_aliases_and_dangling_output_are_refused(self):
        alias = self.root / "checkout alias"
        alias.symlink_to(self.repo, target_is_directory=True)
        with self.assertRaisesRegex(branding.PreparationError, "outside the checkout"):
            branding.prepare(self.source, alias / "new")
        self.output.symlink_to(self.root / "missing")
        with self.assertRaisesRegex(branding.PreparationError, "overwrite"):
            branding.prepare(self.source, self.output)
        self.assertFalse((self.root / "missing").exists())
        self.assert_inputs_unchanged()

    @unittest.skipIf(os.name == "nt", "Hardlinks may require platform-specific privileges")
    def test_hardlinked_output_is_not_overwritten(self):
        os.link(self.source, self.output)
        with self.assertRaisesRegex(branding.PreparationError, "overwrite"):
            branding.prepare(self.source, self.output)
        self.assert_inputs_unchanged()

    def test_missing_parent_input_or_invalid_product_layout_does_not_create_output(self):
        with self.assertRaisesRegex(branding.PreparationError, "parent"):
            branding.prepare(self.source, self.root / "missing" / "output")
        with self.assertRaises(OSError):
            branding.prepare(self.root / "missing.png", self.output)
        with patch.object(branding, "ico_frames", side_effect=branding.PreparationError("bad product layout")):
            with self.assertRaisesRegex(branding.PreparationError, "bad product"):
                branding.prepare(self.source, self.output)
        self.assertFalse(self.output.exists())
        self.assertFalse((self.root / "missing").exists())
        self.assert_inputs_unchanged()

    def test_git_inventory_failure_does_not_create_output(self):
        with patch.object(branding, "git_output", side_effect=branding.PreparationError("Read-only Git inventory failed")):
            with self.assertRaisesRegex(branding.PreparationError, "Git inventory"):
                branding.prepare(self.source, self.output)
        self.assertFalse(self.output.exists())
        self.assert_inputs_unchanged()

    def test_cli_reports_success_as_json_and_refusal_as_nonzero(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            self.assertEqual(0, branding.main(["--source", str(self.source), "--output", str(self.output)]))
        self.assertEqual("", stderr.getvalue())
        self.assertFalse(json.loads(stdout.getvalue())["adoption_authorized"])
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            self.assertEqual(1, branding.main(["--source", str(self.source), "--output", str(self.output)]))
        self.assertIn("Refusing to overwrite", stderr.getvalue())
        self.assert_inputs_unchanged()

    def test_write_failure_is_nonzero_and_partial_output_cannot_be_reused(self):
        original_open = Path.open

        def fail_candidate(path, *args, **kwargs):
            if path == self.output / branding.PNG:
                raise OSError("fixture disk full")
            return original_open(path, *args, **kwargs)

        stderr = io.StringIO()
        with patch.object(Path, "open", fail_candidate), redirect_stderr(stderr):
            self.assertEqual(1, branding.main(["--source", str(self.source), "--output", str(self.output)]))
        self.assertIn("fixture disk full", stderr.getvalue())
        self.assertTrue(self.output.exists())
        self.assertFalse((self.output / "adoption-inventory.json").exists())
        with self.assertRaisesRegex(branding.PreparationError, "overwrite"):
            branding.prepare(self.source, self.output)
        self.assert_inputs_unchanged()

    def test_cli_requires_both_explicit_paths(self):
        for args in ([], ["--source", str(self.source)], ["--output", str(self.output)]):
            with self.subTest(args=args), redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
                branding.main(args)
            self.assertEqual(2, caught.exception.code)
        self.assertFalse(self.output.exists())


class RepositoryContractTests(unittest.TestCase):
    def test_current_product_sizes_and_package_consumer_inventory(self):
        self.assertEqual((512, 512), branding.png_size((ROOT / branding.PNG).read_bytes()))
        frames = branding.ico_frames((ROOT / branding.ICO).read_bytes())
        self.assertEqual([256, 128, 64, 48, 32, 24, 16], [frame["width"] for frame in frames])
        self.assertEqual(["png"] + ["dib"] * 6, [frame["encoding"] for frame in frames])
        consumers = {item["path"]: item for item in branding.adoption_inventory(ROOT)["consumers"]}
        for name in ("MeowChan.csproj", "Desktop/Shell/MainWindow.axaml", "Desktop/Shell/MainWindow.axaml.cs",
                     "Desktop/Views/About/AboutWindow.axaml", "Desktop/Views/Settings/FirstRunSetupWindow.axaml",
                     "Desktop/Views/Settings/SettingsWindow.axaml", "Desktop/StartupErrorWindow.cs",
                     "scripts/package-linux.py", "scripts/package-macos.py", "scripts/check-desktop-package.py", "README.md"):
            self.assertIn(name, consumers)

    def test_generated_256_png_round_trips_existing_macos_ico_and_icns_contract(self):
        spec = importlib.util.spec_from_file_location("branding_existing_macos", ROOT / "scripts/package-macos.py")
        macos = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(macos)
        frames = branding.ico_frames((ROOT / branding.ICO).read_bytes())
        data = branding.encode_ico(2, 2, bytes((30, 50, 90, 180)) * 4, frames)
        with tempfile.TemporaryDirectory(prefix="branding-ico-") as temp:
            icon = Path(temp) / "candidate.ico"
            icon.write_bytes(data)
            embedded = macos.png_from_ico(icon)
        self.assertEqual(ico_payloads(data)[0], embedded)
        self.assertEqual(embedded, macos.validate_icns(macos.make_icns(embedded)))
        self.assertEqual((256, 256, bytes((30, 50, 90, 180)) * 256 * 256), branding.decode_rgba(embedded))

    def test_git_helper_is_read_only_and_surfaces_failure(self):
        with patch.object(branding.subprocess, "run") as run:
            run.return_value.stdout = b"tracked\0"
            self.assertEqual(b"tracked\0", branding.git_output(ROOT, "ls-files", "-z"))
            run.assert_called_once_with(["git", "-C", str(ROOT), "ls-files", "-z"], check=True, capture_output=True)
        with patch.object(branding.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "git")):
            with self.assertRaises(branding.PreparationError):
                branding.git_output(ROOT, "ls-files", "-z")


if __name__ == "__main__":
    unittest.main()
