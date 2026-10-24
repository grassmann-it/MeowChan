#!/usr/bin/env python3
import copy
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch
spec = importlib.util.spec_from_file_location('author', Path(__file__).with_name('apphost-source-activation-smoke.py'))
author = importlib.util.module_from_spec(spec)
spec.loader.exec_module(author)

class AuthorEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.closure = {'apphostMember': 'MeowChan', 'entries': [{'path': path, 'sha256': str(i) * 64} for i, path in enumerate(author.SOURCES.values(), 1)]}
        self.metrics = dict(schemaVersion=1, completed=True, failure=None, phase='source-activation', sourcePhase='acquire',
            checks=['author-normal-shutdown-and-lease', 'author-source-cache-hit-on-restart', 'author-archive-verified', 'author-durable-filter-decisions', 'author-production-workers-download-filter'],
            profile='/profile', workingDirectory='/working', renderScaling=1, unexpectedRequests=0, revisions={str(i): str(i) for i in range(5)}, processId=5,
            requests=list(author.plugin.REQUESTS), candidates=[dict(pluginId=plugin, deliveredSource=path, localSource='plugins/author' + str(i) + '.cs',
            sourceSha256=str(i)*64, candidateId='a'*64, assemblySha256='b'*64, executionRevision='c'*64,
            cacheHit=True, activeCandidateId='a'*64, activeExecutionRevision='c'*64) for i, (plugin, path) in enumerate(author.SOURCES.items(), 1)])
        self.metrics['revisions'] = dict(self.metrics['revisions'])
        for proof in self.metrics['candidates']:
            self.metrics['revisions'][proof['pluginId']] = proof['executionRevision']
        self.metrics['revisions'].pop('0')
        self.metrics['revisions'].pop('1')
    def validate(self):
        with patch.object(author.plugin, 'validate_assembly_bindings', return_value=[]):
            return author.validate_metrics(self.metrics, 'acquire', Path('/profile'), Path('/working'), 1, self.closure)
    def test_exact_proof_accepted(self):
        self.validate()
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

if __name__ == '__main__': unittest.main()
