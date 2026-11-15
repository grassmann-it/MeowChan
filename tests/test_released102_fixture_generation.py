"""Small source-contract regression for the released-1.0.2 generator overlay.

No dotnet, subprocesses, profile writes or emulated Config implementation. These
checks pin the empty-profile ordering; only real released generation can prove
admission. Run: python3 -m unittest discover -s tests \
    -p test_released102_fixture_generation.py -v
"""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
GENERATOR = ROOT / "tests/fixtures/upgrade-v1.0.2/Program.cs"


def compact(source):
    # Setup comments are whole lines; retain string literals (including URLs).
    return " ".join(re.sub(r"(?m)^\s*//[^\n]*", "", source).split())


class Released102FixtureGenerationTests(unittest.TestCase):
    def setUp(self):
        source = GENERATOR.read_text(encoding="utf-8")
        self.setup = source[source.index("string profile = Path.GetFullPath(args[0]);"):
                            source.index("using Fixture transport = new();")]

    def test_fresh_path_guard_and_empty_directory_are_the_only_presave_setup(self):
        before_config = self.setup[:self.setup.index("new Config {")]
        # In particular: no exports/media, host/lease, settings seed, deletion or
        # recovery confirmation can make an existing/failed profile admissible.
        self.assertEqual(compact('''
            string profile = Path.GetFullPath(args[0]);
            if (Directory.Exists(profile) || File.Exists(profile) || new FileInfo(profile).LinkTarget != null)
                throw new InvalidOperationException("Generator requires a new profile path.");
            Directory.CreateDirectory(profile);
        '''), compact(before_config))

    def test_released_inventory_seeds_chan_single_site_state_through_the_real_plugin_store(self):
        source = GENERATOR.read_text(encoding="utf-8")
        self.assertIn("new(probe, providers: null, pluginTransportFactory: () => probeClient)", source)
        self.assertIn("new(profile, new IImageBoard[]", source)
        self.assertIn("legacyStore.ApplyStartupAsync(legacyChanObservations, 0)", source)
        for plugin_id, enabled in (
            ("meowchan.source.lainchan", "false"),
            ("meowchan.source.leftypol", "false"),
            ("meowchan.source.sushichan", "false"),
            ("meowchan.source.uboachan", "false"),
            ("meowchan.source.wizchan", "true"),
            ("meowchan.source.zzzchan", "true"),
        ):
            self.assertIn(f'["{plugin_id}"] = {enabled}', source)
        self.assertIn("LegacyChanPlugins = LegacyChanFixture.States", source)
        self.assertIn("legacyStore.SetEnabledAsync", source)

    def test_release_media_sha1_is_seeded_and_checked_across_the_upgrade_fixture(self):
        source = GENERATOR.read_text(encoding="utf-8")
        checker = (ROOT / "tests/fixtures/upgrade-v1.0.2/check.py").read_text(encoding="utf-8")
        current = (ROOT / "tests/fixtures/upgrade-v1.0.2/current/Program.cs").read_text(encoding="utf-8")
        upgrade = (ROOT / "tests/MeowChan.DesktopTests/Released102RestPackageUpgradeTests.cs").read_text(encoding="utf-8")
        self.assertIn("Convert.ToBase64String(SHA1.HashData(Fixture.Bytes))", source)
        self.assertIn("PNG_SHA1 = base64.b64encode(hashlib.sha1(PNG).digest())", checker)
        self.assertIn("string sha1 = Convert.ToBase64String(await SHA1.HashDataAsync(bytes));", current)
        self.assertIn("asset.GetProperty(\"Sha1\").GetString(), bytes.GetProperty(\"Sha1\").GetString()", upgrade)

    def test_exports_are_created_only_after_the_first_ordinary_settings_save(self):
        save = "}.SaveConfig(profile);"
        self.assertEqual(1, self.setup.count(save))
        after_save = self.setup[self.setup.index(save) + len(save):]
        self.assertEqual(
            'Directory.CreateDirectory(Path.Combine(profile, "exports"));',
            compact(after_save))
        config = self.setup[self.setup.index("new Config {"):self.setup.index(save)]
        self.assertIn('LastExportDirectory = Path.Combine(profile, "exports")', config)
        self.assertNotIn("Directory.CreateDirectory", config)


if __name__ == "__main__":
    unittest.main()
