#!/usr/bin/env python3
import copy
import importlib.util
import tempfile
from pathlib import Path
import unittest
from unittest.mock import Mock, patch
spec = importlib.util.spec_from_file_location('author', Path(__file__).with_name('apphost-source-activation-smoke.py'))
author = importlib.util.module_from_spec(spec)
spec.loader.exec_module(author)

class AuthorEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.closure = {'apphostMember': 'MeowChan', 'entries': [{'path': path, 'sha256': str(i) * 64} for i, path in enumerate(author.SOURCES.values(), 1)]}
        self.metrics = dict(schemaVersion=1, completed=True, failure=None, phase='source-activation', sourcePhase='acquire',
            checks=['author-normal-shutdown-and-lease', 'author-source-cache-hit-on-restart', 'author-archive-verified', 'author-durable-filter-decisions', 'author-production-workers-download-filter'],
            profile='/profile', workingDirectory='/working', renderScaling=1, unexpectedRequests=0, revisions={plugin_id: str(i) for i, plugin_id in enumerate(sorted(author.plugin.PLUGIN_IDS))}, processId=5,
            requests=list(author.AUTHOR_REQUESTS), candidates=[dict(pluginId=plugin, deliveredSource=path, localSource='plugins/author' + str(i) + '.cs',
            sourceSha256=str(i)*64, candidateId='a'*64, assemblySha256='b'*64, executionRevision='c'*64,
            cacheHit=True, activeCandidateId='a'*64, activeExecutionRevision='c'*64) for i, (plugin, path) in enumerate(author.SOURCES.items(), 1)])
        self.metrics['revisions'] = dict(self.metrics['revisions'])
        for proof in self.metrics['candidates']:
            self.metrics['revisions'][proof['pluginId']] = proof['executionRevision']
        self.metrics['startup'] = dict(paused=True, warnings=[], plugins=[])
        for plugin_id, revision in self.metrics['revisions'].items():
            targeted = plugin_id in author.SOURCES
            row = dict(pluginId=plugin_id, enabled=True, problem=author.SELECTED_ADVISORY if targeted else None,
                selectedCandidateId='a'*64, activeCandidateId='a'*64, activeExecutionRevision=revision,
                pendingCandidateId=None, activePluginName=plugin_id, presentCandidateIds=['a'*64, 'b'*64] if targeted else ['a'*64])
            self.metrics['startup']['plugins'].append(row)
            if targeted: self.metrics['startup']['warnings'].append(plugin_id + ': ' + author.SELECTED_ADVISORY)
    def test_startup_rejects_each_unexpected_state(self):
        good = copy.deepcopy(self.metrics)
        for fault in ('unpaused', 'missing', 'extra', 'duplicate', 'wrong_id', 'wrong_selection', 'pending'):
            with self.subTest(fault=fault):
                self.metrics = copy.deepcopy(good)
                startup = self.metrics['startup']
                target = next(row for row in startup['plugins'] if row['pluginId'] in author.SOURCES)
                if fault == 'unpaused': startup['paused'] = False
                elif fault == 'missing': startup['warnings'].pop()
                elif fault == 'extra': startup['warnings'].append('unrelated warning')
                elif fault == 'duplicate': startup['warnings'][0] = startup['warnings'][1]
                elif fault == 'wrong_id': next(row for row in startup['plugins'] if row['pluginId'] not in author.SOURCES)['problem'] = author.SELECTED_ADVISORY
                elif fault == 'wrong_selection': target['selectedCandidateId'] = 'wrong'
                elif fault == 'pending': target['pendingCandidateId'] = 'pending'
                with self.assertRaises(ValueError): self.validate()
        self.metrics = good
    def validate(self):
        with patch.object(author.plugin, 'validate_assembly_bindings', return_value=[]):
            return author.validate_metrics(self.metrics, 'acquire', Path('/profile'), Path('/working'), 1, self.closure)
    def test_exact_proof_accepted(self):
        self.validate()
    def test_author_requests_stay_at_two_sources_despite_expanded_bundles(self):
        self.assertEqual(6, len(author.AUTHOR_REQUESTS))
        self.assertEqual(12, len(author.plugin.REQUESTS))
        self.assertEqual(11, len(self.metrics['revisions']))
        self.metrics['requests'] = list(author.plugin.REQUESTS)
        with self.assertRaises(ValueError): self.validate()

    def test_eleven_startup_rows_require_the_exact_bundled_plugin_ids(self):
        self.metrics['revisions']['foreign'] = self.metrics['revisions'].pop('meowchan.source.endchan')
        with self.assertRaises(ValueError): self.validate()

    def test_wrong_active_candidate_rejected(self):
        self.metrics['candidates'][0]['activeCandidateId'] = 'd'*64
        with self.assertRaises(ValueError): self.validate()
    def test_undelivered_source_rejected(self):
        self.metrics['candidates'][0]['sourceSha256'] = 'd'*64
        with self.assertRaises(ValueError): self.validate()
    def test_duplicate_request_rejected(self):
        self.metrics['requests'][0] = self.metrics['requests'][1]
        with self.assertRaises(ValueError): self.validate()
    def test_cache_miss_on_restart_rejected(self):
        self.metrics['candidates'][0]['cacheHit'] = False
        with self.assertRaises(ValueError): self.validate()
    def test_source_escape_rejected(self):
        self.metrics['candidates'][0]['localSource'] = '../author.cs'
        with self.assertRaises(ValueError): self.validate()
    def test_continuity_needs_new_processes_and_exact_cache(self):
        phases = [dict(phase=phase, passed=True, processId=i, candidates=copy.deepcopy(self.metrics['candidates']), revisions={}, templateSha256='f'*64) for i, phase in enumerate(author.PHASES, 1)]
        self.assertTrue(author.validate_continuity(phases))
        phases[2]['processId'] = 2
        self.assertFalse(author.validate_continuity(phases))
        phases[2]['processId'] = 3
        phases[2]['candidates'][0]['executionRevision'] = 'e'*64
        self.assertFalse(author.validate_continuity(phases))
    def test_template_explicit_zero_and_unset_required(self):
        data = dict(schemaVersion=1, completed=True, checks=list(author.TEMPLATE_CHECKS), pluginId='meowchan.example.image-dimensions', filterKey='dimensions', schema='1',
            ruleParameters=author.PARAMETERS, templateParameters=author.PARAMETERS, initialRevision='a', formRevision='b', finalRevision='c', templateId='x', rulesSha256='f'*64)
        author.validate_template(data)
        data['ruleParameters'] = dict(author.PARAMETERS, aspectRatio='')
        with self.assertRaises(ValueError): author.validate_template(data)


class AuthorTemporaryRootTests(unittest.TestCase):
    def test_all_phase_children_use_owned_root_parent_without_changing_profile(self):
        # Mock the process boundary: this test starts no DBus/GUI/apphost process.
        with tempfile.TemporaryDirectory(prefix='meowchan-workflow-tmpdir-test-') as temporary:
            root = Path(temporary).resolve()
            (root / '.workflow-owner').write_text('a' * 32)
            def phase_environment(root, phase, profile, isolated, scale):
                working = root / ('cwd-' + phase)
                working.mkdir()
                return working, {'TMPDIR': '/wrong-parent', 'MEOWCHAN_PROFILE': str(profile), 'XDG_RUNTIME_DIR': str(working / 'runtime')}
            process = Mock()
            process.wait.return_value = 2
            with patch.object(author.workflow, 'phase_environment', side_effect=phase_environment), \
                    patch.object(author.plugin, 'sandbox', side_effect=lambda *args: ['bwrap', '--smoke-plugin-workflow']), \
                    patch.object(author.subprocess, 'Popen', return_value=process) as popen, \
                    patch.object(author.desktop, 'stop_owned_processes'):
                for phase in author.PHASES:
                    author.run_phase(root / 'package/MeowChan', root, phase, root / ('evidence-' + phase), 1, 1, {})
            self.assertEqual(3, popen.call_count)
            for call, phase in zip(popen.call_args_list, author.PHASES):
                environment = call.kwargs['env']
                self.assertEqual(str(root.parent), environment['TMPDIR'])
                self.assertEqual(str(root / 'profile-download'), environment['MEOWCHAN_PROFILE'])
                self.assertEqual(str(root / ('cwd-' + phase) / 'runtime'), environment['XDG_RUNTIME_DIR'])
                self.assertIn('--smoke-source-activation', call.args[0])
                self.assertEqual(str(root / 'download.json'), call.args[0][-1])

if __name__ == '__main__': unittest.main()
