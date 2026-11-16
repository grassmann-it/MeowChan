"""No-build wiring contracts for the nested replay child (not full upgrade acceptance)."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
TESTS = ROOT / 'tests/MeowChan.DesktopTests'


class ReleasedCycleEnvironmentContracts(unittest.TestCase):
    def test_sudo_child_gets_owned_home_before_bwrap_command_separator(self):
        source = (TESTS / 'Released102RestPackageUpgradeTests.cs').read_text()
        cycle = source.split('private async Task RunCycleAsync(', 1)[1].split('private static void AssertCycle', 1)[0]
        self.assertIn('Released102CycleEnvironment.Create(result)', cycle)
        setup = cycle.index('Released102CycleEnvironment.Apply(start, cycleEnvironment, bubblewrap: true);')
        self.assertLess(cycle.index('start.ArgumentList.Add(resultsDirectory);'), setup)
        self.assertLess(setup, cycle.index('start.ArgumentList.Add("--");'))
        self.assertIn('Released102CycleEnvironment.Apply(start, cycleEnvironment, bubblewrap: false);', cycle)

    def test_no_real_home_fallback_or_profile_mutation(self):
        helper = (TESTS / 'Released102CycleEnvironment.cs').read_text()
        self.assertIn('Path.GetFullPath(result) + ".environment"', helper)
        self.assertIn('Directory.CreateDirectory(directory)', helper)
        for key in ('HOME', 'DOTNET_CLI_HOME', 'TMPDIR', 'XDG_CONFIG_HOME', 'XDG_STATE_HOME', 'XDG_CACHE_HOME'):
            self.assertIn('["' + key + '"]', helper)
        self.assertNotIn('GetEnvironmentVariable', helper)
        self.assertNotIn('GetFolderPath', helper)
        self.assertIn('start.ArgumentList.Add("--setenv");', helper)
        self.assertIn('A replay cycle requires a fresh private environment', helper)

    def test_replay_still_requires_namespace_caps_deadline_and_source_proof(self):
        source = (TESTS / 'Released102RestPackageUpgradeTests.cs').read_text()
        for marker in ('"--unshare-user"', '"--disable-userns"', '"--net"', '"--bounding-set=-all"',
                       '"--inh-caps=-all"', '"--ambient-caps=-all"', '"--nnp"',
                       'TimeSpan.FromSeconds(240)', 'AssemblyHash(typeof(Config).Assembly)',
                       'Assert.Empty(current.GetProperty("NetworkRequests").EnumerateArray())',
                       'cycle < 3'):
            self.assertIn(marker, source)


if __name__ == '__main__':
    unittest.main()
