import importlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

planner = importlib.import_module('plan-validation-selection')


def inventory_for(sha, drop_project=None, skip_normal=None):
    projects = {project: [f'Tests.Example.Method{i}' for i in range(25)] for project in planner.PROJECTS
                if project != drop_project}
    if planner.DESKTOP in projects:
        projects[planner.DESKTOP].extend(f'Tests.{name}.Run' for name in planner.GALLERY_CLASSES)
    for project, methods in planner.WINDOWS_METHODS.items():
        if project in projects:
            projects[project].extend(methods)
    smokes = dict.fromkeys(('normal', *planner.balance.scenes.full_scene_inventory(),
                            *planner.GALLERY_SMOKES, *planner.BOORU_SMOKES, 'settings-reader-display', 'media-page'))
    smokes.update(normal=skip_normal, **{'external-driver': 'needs its own runner'})
    return planner.inventory_record(sha, projects, smokes)


class SelectionTest(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.workspace = Path(directory.name)
        self.repo = self.workspace / 'repo'
        self.repo.mkdir()
        self.git('init', '-q', '-b', 'main')
        self.git('config', 'user.name', 'Selection Contract')
        self.git('config', 'user.email', 'contract@example.invalid')
        for project in (*planner.PROJECTS, planner.APP, planner.LIBRARY):
            self.write(project, '<Project />\n')
        self.write(planner.CORE, '<Project><ItemGroup><ProjectReference Include="../../libmeowchan/libmeowchan.csproj"/>'
                              '</ItemGroup></Project>\n')
        self.write(planner.DESKTOP, '<Project><ItemGroup><ProjectReference Include="../../MeowChan.csproj"/>'
                                 '</ItemGroup></Project>\n')
        self.write('libmeowchan/Http/Old name.cs', 'class Source {}\n')
        self.write('libmeowchan/Http/Delete.cs', 'class Deleted {}\n')
        self.write('Desktop/GalleryVideoPlayer.cs', 'class Player {}\n')
        self.base = self.commit('base')

    def git(self, *args):
        return subprocess.run(['git', '-C', str(self.repo), *args], check=True, capture_output=True,
                              text=True).stdout.strip()

    def write(self, path, content):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding='utf-8')

    def commit(self, message):
        self.git('add', '-A')
        self.git('commit', '-qm', message)
        return self.git('rev-parse', 'HEAD')

    def push(self, after, **overrides):
        return dict({'before': self.base, 'after': after, 'ref': 'refs/heads/main'}, **overrides)

    def plan(self, payload, *, event='push', seed='run-123', percent=10, load=inventory_for, checkout=None):
        plan, _ = planner.plan(self.repo, event, payload, seed, percent, load, checkout)
        return plan

    def required(self, report, project, target=planner.LINUX):
        return next(row for row in report['required']['tests'] if (row['project'], row['platform']) == (project, target))

    def test_push_uses_both_endpoints_and_both_rename_owners(self):
        self.git('mv', 'libmeowchan/Http/Old name.cs', 'Desktop/Renamed\nFile.cs')
        self.commit('rename across project ownership')
        self.git('rm', 'libmeowchan/Http/Delete.cs')
        new_test = str(Path(planner.DESKTOP).parent / 'NewTests.cs')
        self.write(new_test, 'class NewTests {}\n')
        after = self.commit('delete source and add test')
        report = self.plan(self.push(after))
        self.assertEqual('selected', report['scope'], report['fallback_reasons'])
        self.assertEqual(self.base, report['source']['diff_base_sha'])
        self.assertIn(dict(status='R100', old='libmeowchan/Http/Old name.cs', new='Desktop/Renamed\nFile.cs'),
                      report['changes'])
        self.assertIn(dict(status='D', old='libmeowchan/Http/Delete.cs', new=None), report['changes'])
        self.assertIn('libmeowchan/Http/Old name.cs', self.required(report, planner.CORE)['reasons'])
        self.assertIn('Desktop/Renamed\nFile.cs', self.required(report, planner.DESKTOP)['reasons'])
        self.assertIn(new_test, self.required(report, planner.DESKTOP)['reasons'])
        self.assertEqual(planner.WINDOWS_METHODS[planner.DESKTOP],
                         set(self.required(report, planner.DESKTOP, planner.WINDOWS)['methods']))
        self.assertFalse(any(row['project'] in (planner.CORE, planner.DESKTOP) for row in report['sample']['tests']))

    def test_pr_merge_base_uses_checkout_graph_and_records_actual_sha(self):
        self.git('checkout', '-qb', 'feature')
        self.write('libmeowchan/Http/Changed.cs', 'class Changed {}\n')
        head = self.commit('feature')
        self.git('checkout', '-q', 'main')
        self.write('UnmappedBaseOnly.cs', 'class BaseOnly {}\n')
        other_consumer = next(project for project in planner.PROJECTS if 'PluginSdk.Tests' in project)
        self.write(other_consumer, '<Project><ItemGroup><ProjectReference Include="../../libmeowchan/libmeowchan.csproj"'
                                   ' ReferenceOutputAssembly="false" Condition="Release" /></ItemGroup></Project>\n')
        branch_tip = self.commit('base branch moved')
        self.git('merge', '-q', '--no-ff', 'feature', '-m', 'test merge')
        checkout = self.git('rev-parse', 'HEAD')
        payload = dict(ref='refs/pull/1/merge', pull_request=dict(base={'sha': branch_tip}, head={'sha': head}))
        report = self.plan(payload, event='pull_request', checkout=checkout)
        self.assertEqual('selected', report['scope'], report['fallback_reasons'])
        self.assertEqual(dict(event='pull_request', ref=payload['ref'], checkout_sha=checkout,
                              event_base_sha=branch_tip, diff_base_sha=self.base, diff_head_sha=head), report['source'])
        self.assertEqual(['libmeowchan/Http/Changed.cs'], [change['new'] for change in report['changes']])
        self.assertEqual(['libmeowchan/Http/Changed.cs'], self.required(report, other_consumer)['reasons'])
        payload['pull_request']['base']['sha'] = self.base
        self.assertEqual(['checkout_does_not_match_pr'], self.plan(payload, event='pull_request')['fallback_reasons'])

    def test_gallery_rule_is_narrow_and_native_driver_is_required(self):
        path = 'Desktop/GalleryVideoPlayer.cs'
        self.write(path, 'partial class Player {}\n')
        after = self.commit('playback')
        report = self.plan(self.push(after), percent=100)
        self.assertEqual('selected', report['scope'], report['fallback_reasons'])
        self.assertEqual(sorted(f'Tests.{name}.Run' for name in planner.GALLERY_CLASSES),
                         self.required(report, planner.DESKTOP)['methods'])
        self.assertCountEqual(['normal', *planner.GALLERY_SMOKES], [row['id'] for row in report['required']['smokes']])
        self.assertCountEqual(planner.balance.PLATFORMS, [row['platform'] for row in report['required']['checks']
                                                       if row['id'] == 'validate:media_input'])
        for row in report['required']['tests']:
            for sample in report['sample']['tests']:
                if (row['project'], row['platform']) == (sample['project'], sample['platform']):
                    self.assertFalse(set(row['methods']) & set(sample['methods']))

    def test_replay_canonicalizes_inventory_order_and_uses_seed_and_ceiling(self):
        first = self.plan(self.push(self.base))
        raw = inventory_for(self.base)
        unordered = {**raw, 'projects': {p: list(reversed(m)) for p, m in raw['projects'].items()}}
        unordered.pop('sha256')
        unordered['sha256'] = planner.vstest.digest(unordered)
        self.assertEqual(first, self.plan(self.push(self.base), load=lambda _: unordered))
        self.assertNotEqual(first['sample'], self.plan(self.push(self.base), seed='another-seed')['sample'])
        self.assertEqual(3, len(next(entry for entry in first['sample']['tests']
                                     if (entry['project'], entry['platform']) == (planner.CORE, planner.LINUX))['methods']))
        windows = [row for row in first['sample']['tests'] if row['platform'] == planner.WINDOWS]
        self.assertEqual(set(planner.WINDOWS_METHODS), {row['project'] for row in windows})
        for row in windows:
            self.assertEqual(1, len(row['methods']))
            self.assertTrue(set(row['methods']) <= planner.WINDOWS_METHODS[row['project']])
        self.assertEqual(dict(tests=[], smokes=[]), self.plan(self.push(self.base), percent=0)['sample'])
        self.assertNotIn('normal', {row['id'] for row in first['sample']['smokes']})
        self.assertNotIn('external-driver', {row['id'] for row in first['sample']['smokes']})
        contents = {key: value for key, value in first.items() if key != 'plan_sha256'}
        self.assertEqual(planner.vstest.digest(contents), first['plan_sha256'])

    def test_conservative_fallbacks_skip_discovery(self):
        self.write('future-production code.cs', 'class Unknown {}\n')
        after = self.commit('unowned source')
        load = mock.Mock(side_effect=AssertionError('fallback already determined'))
        cases = (
            ('push', self.push(after), 'unknown_productive_path:future-production code.cs'),
            ('push', self.push(after, before='0' * 40), 'missing_push_before_sha'),
            ('push', self.push(after, before='1' * 40), 'unavailable_push_before_commit'),
            ('push', self.push(self.base), 'checkout_does_not_match_push'),
            ('push', self.push(after, ref='refs/tags/v1.2.0'), 'event_requires_full_validation'),
            ('workflow_dispatch', {'inputs': {'mode': 'prerelease-candidate'}}, 'event_requires_full_validation'),
            ('schedule', {}, 'event_requires_full_validation'),
        )
        for event, payload, reason in cases:
            with self.subTest(reason=reason):
                report = self.plan(payload, event=event, load=load)
                self.assertEqual('full', report['scope'])
                self.assertEqual([reason], report['fallback_reasons'])
                self.assertCountEqual(planner.balance.PLATFORMS, report['target_platforms'])
        load.assert_not_called()

    def test_unknown_edge_forces_full(self):
        self.write(planner.APP, '<Project><ItemGroup><ProjectReference Include="$(Unknown)" /></ItemGroup></Project>')
        change = self.commit('unsupported graph')
        self.write('Desktop/GalleryVideoPlayer.cs', 'partial class Player {}\n')
        after = self.commit('ordinary source change on unsupported graph')
        report = self.plan(self.push(after, before=change))
        self.assertEqual('full', report['scope'])
        self.assertTrue(report['fallback_reasons'][0].startswith('unresolved_msbuild_path:'))

    def test_baseline_adds_analyzer_contract_even_on_full_fallback(self):
        path = 'build/analyzer-baseline-fixture.tsv'
        self.write(path, 'baseline\n')
        report = self.plan(self.push(self.commit('analyzer baseline')))
        self.assertEqual('full', report['scope'])
        self.assertEqual([f'central_path:{path}'], report['fallback_reasons'])
        self.assertEqual([f'dotnet-test:{planner.ANALYZER}'], [row['id'] for row in report['required']['checks']])

    def test_documentation_contracts_are_required_without_sampling(self):
        matrix = 'docs/development/booru-test-matrix.md'
        plugin_guide = 'docs/plugins/using-plugins.md'
        readme = 'README.md'
        for path in (matrix, plugin_guide, readme, 'docs/development/meeting-notes.md'):
            self.write(path, '# Documentation\n')
        report = self.plan(self.push(self.commit('documentation changes')), percent=0)
        self.assertEqual('selected', report['scope'], report['fallback_reasons'])
        self.assertCountEqual([
            dict(id='python:tests/test_plugin_packaging.py', platform=planner.LINUX, reasons=[matrix]),
            dict(id='python:tests/test_plugin_documentation.py', platform=planner.LINUX, reasons=[plugin_guide]),
        ], report['required']['checks'])
        self.assertEqual([readme], self.required(report, planner.DESKTOP)['reasons'])

    def test_unassigned_test_project_invalidates_complete_inventory_claim(self):
        path = 'tests/NewTests/NewTests.csproj'
        self.write(path, '<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>')
        before = self.commit('solution changed before this push')
        self.write('libmeowchan/Http/Another.cs', 'class Source {}\n')
        after = self.commit('normal source change')
        report = self.plan(self.push(after, before=before))
        self.assertEqual('full', report['scope'])
        self.assertEqual([f'unassigned_test_projects:{path}'], report['fallback_reasons'])

    def test_inventory_is_bound_complete_and_required_scenarios_are_runnable(self):
        tampered = inventory_for(self.base)
        tampered['projects'].pop(planner.CORE)
        cases = (
            (tampered, 'inventory_hash_mismatch'),
            (inventory_for('1' * 40), 'stale_inventory_checkout'),
            (inventory_for(self.base, drop_project=planner.CORE), 'incomplete_project_inventory'),
            (inventory_for(self.base, skip_normal='external'), 'missing_or_skipped_required_smokes:normal'),
        )
        for inventory, reason in cases:
            with self.subTest(reason=reason):
                report = self.plan(self.push(self.base), load=lambda _: inventory)
                self.assertEqual('full', report['scope'])
                self.assertEqual([reason], report['fallback_reasons'])
        report = self.plan(self.push(self.base), load=mock.Mock(side_effect=subprocess.TimeoutExpired('dotnet', 1)))
        self.assertEqual(['inventory_unavailable:TimeoutExpired'], report['fallback_reasons'])

    def test_discovery_inventory_groups_theory_rows_and_rejects_partial_protocol(self):
        run = subprocess.run
        cases = [dict(Id=identifier, FullyQualifiedName='Tests.Example.Theory',
                      ExecutorUri='executor://xunit/VsTestRunner2/netcoreapp',
                      Properties=[dict(Key={'Id': 'XunitTestCase'}, Value='Xunit.Sdk.XunitTestCase,')])
                 for identifier in ('theory-row-a', 'theory-row-b')]
        final = dict(IsAborted=False, TotalTests=len(cases), LastDiscoveredTests=cases)

        def discovery_fixture(command, **options):
            if command[0] != 'dotnet':
                return run(command, **options)
            self.assertEqual(self.repo, options['cwd'])
            self.assertNotIn('MEOWCHAN_VIDEO_FIXTURE', options['env'])
            if command[1] == 'vstest':
                diag = Path(command[-1].removeprefix('--Diag:'))
                payload = dict(MessageType='TestDiscovery.Completed', Payload=final)
                diag.write_text('TestRequestSender.OnDiscoveryMessageReceived: Received message: ' +
                                json.dumps(payload) + '\n', encoding='utf-8')
            entries = inventory_for(self.base)['smokes'] if command[-1] == '--list-scenarios' else {}
            stdout = '\n'.join(json.dumps(dict(id=name, skip=skip, error=None)) for name, skip in entries.items())
            return subprocess.CompletedProcess(command, 0, stdout=stdout)

        with (mock.patch.object(subprocess, 'run', side_effect=discovery_fixture),
              mock.patch.object(planner.sys, 'platform', 'linux'),
              mock.patch.object(planner.platform, 'machine', return_value='x86_64'),
              mock.patch.dict(os.environ, {'MEOWCHAN_VIDEO_FIXTURE': 'machine-local'}),
              mock.patch.object(planner.sys, 'stderr')):
            inventory = planner.verify_inventory(planner.discover(self.repo, self.base), self.base)
            self.assertTrue(all(methods == ['Tests.Example.Theory'] for methods in inventory['projects'].values()))
            final['TotalTests'] += 1
            report = self.plan(self.push(self.base), load=lambda sha: planner.discover(self.repo, sha))
            self.assertEqual('full', report['scope'])
            self.assertEqual(['invalid_discovery_evidence'], report['fallback_reasons'])

    def test_cli_archives_replay_inputs_and_identical_json(self):
        payload, inventory = self.workspace / 'event.json', self.workspace / 'inventory.json'
        payload.write_text(json.dumps(self.push(self.base)), encoding='utf-8')
        inventory.write_text(json.dumps(inventory_for(self.base)), encoding='utf-8')
        script = Path(planner.__file__).resolve()
        first, replay = self.workspace / 'first', self.workspace / 'replay'
        summary = self.workspace / 'summary.md'

        def run(event, inventory, output):
            subprocess.run([sys.executable, str(script), '--repo', str(self.repo), '--event', 'push',
                            '--event-file', str(event), '--inventory', str(inventory), '--seed', 'run-123',
                            '--checkout', self.base, '--summary', str(summary), '--output', str(output)],
                           check=True, capture_output=True)

        run(payload, inventory, first)
        run(first / 'diff-input.json', first / 'inventory.json', replay)
        self.assertEqual((first / 'plan.json').read_bytes(), (replay / 'plan.json').read_bytes())
        self.assertIn('"plan_sha256"', summary.read_text(encoding='utf-8'))


class RepositorySelectionContractTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.graph = planner.ProjectGraph(planner.ROOT, 'HEAD')

    def assert_consumers(self, path, expected):
        owners = self.graph.owners(path)
        consumers = self.graph.consumers(owners)
        self.assertFalse(set(expected) - {Path(project).stem for project in consumers})
        return owners, consumers

    def test_repository_compiled_links_and_plugin_graph(self):
        self.assert_consumers('libmeowchan/Http/TransferMeter.cs', ('MeowChan.Tests', 'MeowChan.OriginalMediaHttp.Tests'))
        self.assert_consumers('MeowChan.PluginHost/Filters/ImageHeader.cs',
                              ('MeowChan.ImageHeader.Tests', 'MeowChan.PluginFilters.Tests'))
        self.assert_consumers('tests/Fixtures/persistence102/archive-searches.json', ('MeowChan.Tests',))
        self.assert_consumers('tests/MeowChan.DesktopSmoke/SmokeScenarios.cs', ('MeowChan.DesktopTests',))
        self.assert_consumers('tests/Shared/Apphost/ApphostAcceptanceAssertions.cs',
                              ('MeowChan', 'MeowChan.DesktopSmoke', 'MeowChan.DesktopTests'))
        path = 'plugins/Official/Danbooru/Danbooru.cs'
        owners, consumers = self.assert_consumers(path, ('MeowChan.OfficialPlugins.Tests', 'MeowChan.PluginRuntime.Tests',
                                                        'MeowChan', 'PluginDeliverySmoke'))
        requirements = planner.Requirements()
        requirements.route(path, owners, consumers, self.graph.delivered, True)
        self.assertIn(('plugin-delivery', planner.LINUX), requirements.checks)
        self.assertTrue(set(planner.BOORU_SMOKES) <= requirements.smokes.keys())
        self.assert_consumers('plugins/Official/FourChan/FourChan.cs', ('MeowChan.SourceAdapter.Tests', 'libmeowchan'))
        self.assert_consumers('tests/PluginReauthorizationProbe/Program.cs', ('MeowChan.PluginRuntime.Tests',))

    def test_regular_profile_file_requires_windows_identity_test(self):
        path = 'libmeowchan/Persistence/RegularProfileFile.cs'
        owners, consumers = self.assert_consumers(path, ('MeowChan.Tests',))
        requirements = planner.Requirements()
        requirements.route(path, owners, consumers, self.graph.delivered, True)
        identity = 'MeowChan.Tests.Transfer.RegularProfileFileTests.IdentityChangesWhenASameSizeRewriteRestoresTheTimestamps'
        portable = 'MeowChan.Tests.Transfer.RegularProfileFileTests.WindowsRegularFileFilterRejectsLinksDirectoriesAndDevices'
        required, sample = planner.select(requirements, {
            'projects': {planner.CORE: [identity, portable]}, 'smokes': inventory_for('HEAD')['smokes'],
        }, 'test', 0)
        self.assertCountEqual([
            dict(project=planner.CORE, platform=planner.LINUX, methods=[portable], reasons=[path]),
            dict(project=planner.CORE, platform=planner.WINDOWS, methods=[identity], reasons=[path]),
        ], required['tests'])
        self.assertEqual([], sample['tests'])

    def test_shadow_job_has_no_matrix_or_gate_consumer(self):
        text = (planner.ROOT / '.github/workflows/dotnet-desktop.yml').read_text(encoding='utf-8')
        remainder, shadow = text.split('  validation-selection-shadow:\n', 1)
        self.assertIn("github.event_name == 'pull_request'", shadow)
        self.assertIn("github.event_name == 'push' && github.ref_type == 'branch'", shadow)
        self.assertNotRegex(shadow, r'workflow_dispatch|schedule|pull_request_target')
        self.assertNotRegex(shadow, r'(?m)^    (needs|outputs):')
        self.assertIn('continue-on-error: true', shadow)
        self.assertIn('fetch-depth: 0', shadow)
        self.assertIn('plan-validation-selection.py --discover', shadow)
        self.assertIn('actions/upload-artifact@', shadow)
        self.assertNotRegex(remainder, r'validation-selection-shadow|plan-validation-selection')
        self.assertRegex(remainder, re.escape('${{ fromJSON(needs.validate-plan.outputs.matrix) }}'))


if __name__ == '__main__':
    unittest.main()
