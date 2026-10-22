#!/usr/bin/env python3
"""Fast failure-contract checks; these do not execute an apphost or desktop."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('plugin_workflow', Path(__file__).with_name('apphost-plugin-workflow-smoke.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class PluginWorkflowRunnerTests(unittest.TestCase):
    def metrics(self, phase='download'):
        checks = runner.COMMON | ({'delivered-compiler-uncached', 'real-source-and-filter-workers'} if phase == 'download' else {'paused-offline-restore'})
        return dict(schemaVersion=1, completed=True, failure=None, phase=phase, checks=list(checks),
                    profile='/profile', workingDirectory='/work', renderScaling=1, unexpectedRequests=0,
                    revisions={str(i): str(i) for i in range(5)}, processId=123,
                    requests=sorted(runner.REQUESTS) if phase == 'download' else [])

    def validate(self, metrics):
        with patch.object(runner.workflow, 'validate_loaded_assemblies', return_value=['bound']):
            return runner.validate_metrics(metrics, metrics['phase'], Path('/profile'), Path('/work'), 1, {})

    def test_valid_download_and_restore(self):
        self.assertEqual(['bound'], self.validate(self.metrics()))
        self.assertEqual(['bound'], self.validate(self.metrics('restore')))

    def test_missing_compiler_proof_fails(self):
        metrics = self.metrics()
        metrics['checks'].remove('delivered-compiler-uncached')
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_restore_network_attempt_fails(self):
        metrics = self.metrics('restore')
        metrics['requests'] = [next(iter(runner.REQUESTS))]
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_unknown_or_duplicate_download_request_fails(self):
        for replacement in ('https://example.org/live', sorted(runner.REQUESTS)[0]):
            metrics = self.metrics()
            metrics['requests'][-1] = replacement
            with self.assertRaises(ValueError): self.validate(metrics)

    def test_product_failure_cannot_pass(self):
        metrics = self.metrics()
        metrics['failure'] = 'shutdown failed'
        with self.assertRaises(ValueError): self.validate(metrics)

    def test_namespace_mounts_do_not_expose_home_or_original_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            command = runner.sandbox(root / 'package/MeowChan', root, root / 'cwd')
            self.assertIn('--unshare-net', command)
            self.assertIn('/no-sdk', command)
            self.assertIn('/no-nuget', command)
            self.assertNotIn('/home', command)
            self.assertNotIn('/opt', command)
            self.assertNotIn('/', command)
            index = command.index(str(root / 'package'))
            self.assertEqual('--ro-bind', command[index - 1])
            self.assertEqual('--smoke-plugin-workflow', command[-1])


if __name__ == '__main__': unittest.main()
