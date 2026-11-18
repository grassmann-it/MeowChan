"""Source and executable CI orchestration contracts; no GUI or build required."""
import importlib.util
import json
import re
import sys
import tempfile
from pathlib import Path
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SMOKE = ROOT / 'tests/MeowChan.DesktopSmoke'


SPEC = importlib.util.spec_from_file_location('normal_scene_collector', ROOT / 'scripts/run-gui-ci-scenes.py')
collector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(collector)

# Independent inventory: dropping a collector entry must fail, not shrink the expected coverage.
NORMAL = ['normal-core', 'normal-grid', 'normal-settings-page', 'normal-diagnostics',
          'sources-page', 'normal-board-admission', 'pause-controls', 'rules-page', 'settings-core']
MODES = ['locked-profile', 'no-tray', 'tray-failure', 'download-session', 'selection']
READER = ['reader-navigation', 'reader-owned-navigation', 'reader-gallery-roundtrip',
          'reader-list-gallery-return', 'reader-returns', 'reader-stability',
          'reader-groups', 'gallery-close', 'video-lifecycle', 'window-minimum']
GIF = ['gif-playback-core', 'gif-successors', 'gif-verified-failures', 'gif-reader-association', 'gif-ownership']
ERRORS = ['gallery-errors-retirement-clip', 'gallery-errors-layout-roots',
          'gallery-errors-notifications', 'gallery-errors-failure-reader-return']
ALL = NORMAL + MODES + READER + GIF + ERRORS


class NormalSplitTests(unittest.TestCase):
    def execute(self, scenario='all', platform='Linux', failure=0):
        registry = (SMOKE / 'SmokeScenarios.cs').read_text()
        entries = []
        for identifier in ALL:
            if identifier in MODES:
                self.assertIn('Mode("' + identifier + '"', registry)
                mode, selector = identifier, None
            else:
                match = re.search(r'(Root|Alternative)\("' + identifier + r'", "([^"]+)"', registry)
                self.assertIsNotNone(match, identifier)
                mode = 'reader-ux' if match[1] == 'Alternative' else 'normal'
                selector = match[2]
            entries.append(dict(id=identifier, mode=mode, scenario=mode + ('+' + selector if selector else ''),
                                environment=[selector + '=1'] if selector else [], error=None, skip=None))
        calls = []
        def run(command, env, **kwargs):
            calls.append((command, env))
            passed = len(calls) != failure
            destination = Path(command[command.index('--output') + 1])
            mode = command[command.index('--mode') + 1]
            expected = command[command.index('--expect-scenario') + 1]
            (destination / 'results.json').write_text(json.dumps(dict(
                passed=passed, selected_modes=[mode], results=[dict(mode=mode, scenario=expected,
                    passed=passed, exit_code=0 if passed else 1, error=None)])))
            if '--report' in command:
                Path(command[command.index('--report') + 1]).write_text(json.dumps(dict(
                    status='passed', readiness=dict(before=True, after=True),
                    cleanup=dict(complete=True, processes=dict(child={})))))
            return mock.Mock(returncode=0 if passed else 1, outer_timeout=False, forced_kill=False)
        collect = collector.collect
        with tempfile.TemporaryDirectory() as tmp:
            args = ['run-gui-ci-scenes.py', '--assembly', 'fixture.dll', '--output', tmp,
                    '--timeout', '300' if platform == 'macOS' else '180']
            if platform != 'Linux':
                args.append('--allow-native-desktop')
            if scenario == 'selection':
                args.append('--selection-only')
            with mock.patch.object(sys, 'argv', args), \
                    mock.patch.object(sys, 'platform', {'Linux': 'linux', 'Windows': 'win32', 'macOS': 'darwin'}[platform]), \
                    mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout='\n'.join(map(json.dumps, entries)))), \
                    mock.patch.dict('os.environ', {}, clear=True), \
                    mock.patch.object(collector, 'collect', side_effect=lambda *a: collect(*a, run=run)):
                self.assertEqual(collector.main(), int(bool(failure)))
            report = json.loads((Path(tmp) / 'collection.json').read_text())
            required = ['selection'] if scenario == 'selection' else ALL
            self.assertEqual(report['required'], required)
            self.assertEqual([r['id'] for r in report['results']], required)
            self.assertTrue(report['complete'])
            self.assertTrue(report['coverage_complete'])
            self.assertEqual(report['passed'], not bool(failure))
            self.assertEqual([r['id'] for r in report['results'] if not r['passed']],
                             [required[failure - 1]] if failure else [])
        return calls

    def test_workflow_invokes_collector_with_platform_isolation_and_selection(self):
        text = (ROOT / '.github/workflows/dotnet-desktop.yml').read_text()
        region = text.split('        id: smoke_normal\n', 1)[1].split('      # Dedicated Program.cs', 1)[0]
        self.assertIn('continue-on-error: true', region)
        self.assertIn('python scripts/run-gui-ci-scenes.py', region)
        self.assertIn("runner.os != 'Linux' && '--allow-native-desktop'", region)
        self.assertIn("inputs.scenario == 'selection' && '--selection-only'", region)
        self.assertIn("runner.os == 'macOS' && '300' || '180'", region)
        self.assertIn('--shard {0} --workers {1}', region)
        self.assertNotIn("python - <<'PY'", region)

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
        # An ordinary failure in every family must still collect all later scenes.
        for platform in ('Linux', 'Windows', 'macOS'):
            for failed_id in (NORMAL[0], MODES[0], READER[0], GIF[0], ERRORS[0]):
                with self.subTest(platform=platform, failed=failed_id):
                    calls = self.execute(platform=platform, failure=ALL.index(failed_id) + 1)
                    self.assertEqual(len(calls), len(ALL))
                    outputs = [cmd[cmd.index('--output') + 1] for cmd, _ in calls]
                    self.assertEqual(len(set(outputs)), len(ALL))
                    for identifier, (cmd, _) in zip(ALL, calls):
                        self.assertEqual(cmd[cmd.index('--timeout') + 1],
                                         '300' if platform == 'macOS' or identifier in GIF + ERRORS else '180')
                        self.assertEqual('--allow-native-desktop' in cmd, platform != 'Linux')
                    self.assertEqual([cmd[cmd.index('--mode') + 1] for cmd, _ in calls[9:14]], MODES)
                    self.assertEqual([cmd[cmd.index('--mode') + 1] for cmd, _ in calls[14:24]], ['reader-ux'] * 10)

    def test_selection_bypasses_normal_scenes(self):
        calls = self.execute(scenario='selection')
        self.assertEqual(len(calls), 1)
        cmd, env = calls[0]
        self.assertEqual(env, {})
        self.assertEqual(cmd[cmd.index('--mode') + 1], 'selection')


if __name__ == '__main__':
    unittest.main()
