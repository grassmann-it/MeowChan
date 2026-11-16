"""Source and executable CI orchestration contracts; no GUI or build required."""
import re
from pathlib import Path
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SMOKE = ROOT / 'tests/MeowChan.DesktopSmoke'


def workflow_script(scenario='all', platform='Linux'):
    text = (ROOT / '.github/workflows/dotnet-desktop.yml').read_text()
    region = text.split('        id: smoke_normal\n', 1)[1].split('      # Dedicated Program.cs', 1)[0]
    script = region.split("          python - <<'PY'\n", 1)[1].rsplit('          PY', 1)[0]
    script = '\n'.join(line[10:] for line in script.splitlines())
    return script.replace('${{ inputs.scenario }}', scenario).replace(
        "${{ runner.os == 'macOS' && '300' || '180' }}", '300' if platform == 'macOS' else '180')


class NormalSplitTests(unittest.TestCase):
    def execute(self, scenario='all', platform='Linux', failure=0):
        calls = []
        def run(command, env):
            calls.append((command, env))
            return mock.Mock(returncode=1 if len(calls) == failure else 0)
        with mock.patch('subprocess.run', side_effect=run), mock.patch.dict('os.environ', {}, clear=True):
            with self.assertRaises(SystemExit) as exited:
                exec(compile(workflow_script(scenario, platform), '<smoke_normal>', 'exec'), {})
        self.assertEqual(exited.exception.code, int(bool(failure)))
        return calls

    def test_exact_tail_coverage_without_overlapping_suites(self):
        program = (SMOKE / 'Program.cs').read_text()
        runs = (SMOKE / 'SmokeScenarios.Runs.cs').read_text()
        registry = (SMOKE / 'SmokeScenarios.cs').read_text()
        tail = program.split('await synthetic.DisposeAsync();', 1)[1].split('Console.WriteLine', 1)[0]
        expected = re.findall(r'await (\w+Checks)\.RunAsync\(mainWindow\)', tail)
        self.assertEqual(len(expected), 8)
        observed = []
        calls = self.execute()
        for command, env in calls[:9]:
            selector, = env
            identifier = re.search(r'Root\("([^"]+)", "' + selector + '"', registry)[1]
            self.assertEqual(command[command.index('--expect-scenario') + 1], 'normal+' + selector)
            body = runs.split('["' + identifier + '"] = ', 1)[1].split('\n', 1)[0]
            observed += re.findall(r'(\w+Checks)\.RunAsync\(mainWindow\)', body)
        self.assertCountEqual(observed, expected)
        self.assertEqual(len(observed), len(set(observed)))
        self.assertIn('RunSelectedAsync(SmokeStage.NormalHead, mainWindow)', tail)
        self.assertIn('["normal-core"] = new(SmokeStage.NormalHead, _ => Task.CompletedTask)', runs)

    def test_normal_core_preserves_manual_diagnostics_and_caption_close(self):
        program = (SMOKE / 'Program.cs').read_text()
        manual = program.split('await SettingsChecks.RunAsync(mainWindow);', 1)[1].split('catch (Exception', 1)[0]
        self.assertIn('CloseNormalThroughCaption(mainWindow);', manual)
        dispatch = program.split('private static async Task<bool> RunSelectedAsync', 1)[1].split('private static bool VisibleText', 1)[0]
        self.assertRegex(dispatch, r'if \(stage == SmokeStage.NormalHead\)\s+CloseNormalThroughCaption\(mainWindow\);\s+else\s*\{\s*_checksPassed = true;\s*mainWindow.Close\(\);')
        close = program.split('private static void CloseNormalThroughCaption', 1)[1].split('// Runs the selected', 1)[0]
        for diagnostic in ('mainWindow.OwnedWindows.Count', 'mainWindow.IsEnabled',
                           'mainWindow.IsEffectivelyEnabled', 'foreach (Window ownedWindow in mainWindow.OwnedWindows)',
                           'ownedWindow.GetType().Name', 'ownedWindow.IsVisible', 'ownedWindow.IsEnabled',
                           'SMOKE requesting close through the custom window caption.',
                           'SMOKE caption close returned: closed={_closed}, visible={mainWindow.IsVisible}'):
            self.assertIn(diagnostic, close)
        self.assertIn('mainWindow.FindControl<Button>("WindowCloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));', close)
        self.assertLess(close.index('_checksPassed = true;'), close.index('RaiseEvent('))
        self.assertNotIn('mainWindow.Close()', close)

    def test_failure_collects_every_scene_and_mode_on_all_platforms(self):
        for platform in ('Linux', 'Windows', 'macOS'):
            calls = self.execute(platform=platform, failure=1)
            self.assertEqual(len(calls), 15)
            outputs = [cmd[cmd.index('--output') + 1] for cmd, _ in calls]
            self.assertEqual(len(set(outputs)), 15)
            for cmd, _ in calls:
                self.assertEqual(cmd[cmd.index('--timeout') + 1], '300' if platform == 'macOS' else '180')
                self.assertIn('--allow-native-desktop', cmd)
            self.assertEqual([cmd[cmd.index('--mode') + 1] for cmd, _ in calls[9:]],
                             ['locked-profile', 'no-tray', 'tray-failure', 'download-session', 'reader-ux', 'selection'])

    def test_selection_bypasses_normal_scenes(self):
        calls = self.execute(scenario='selection')
        self.assertEqual(len(calls), 1)
        cmd, env = calls[0]
        self.assertEqual(env, {})
        self.assertEqual(cmd[cmd.index('--mode') + 1], 'selection')


if __name__ == '__main__':
    unittest.main()
