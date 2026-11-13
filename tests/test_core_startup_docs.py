"""Source/document contract only; does not replace compiled consumer acceptance."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class CoreStartupDocumentationTests(unittest.TestCase):
    def test_durable_example_uses_one_public_startup_before_mutations(self):
        text = (ROOT / 'libmeowchan/README.md').read_text()
        examples = [code for code in re.findall(r'```csharp\n(.*?)```', text, re.S)
                    if 'Registry session' in code and 'ProfileLease.Acquire' in code]
        self.assertEqual(1, len(examples))
        code = examples[0]
        self.assertEqual(1, code.count('await session.StartAsync(lease);'))
        self.assertNotIn('await session.RestoreAsync()', code)
        self.assertNotIn('await content.InitializeAsync()', code)
        self.assertLess(code.index('contentRuntime: content'), code.index('await session.StartAsync(lease);'))
        self.assertLess(code.index('await session.StartAsync(lease);'), code.index('await session.AddAsync'))
        self.assertIn('await session.StopAsync();', code)

    def test_migration_boundary_remains_explicit(self):
        text = (ROOT / 'libmeowchan/README.md').read_text()
        for phrase in ('Migrating a durable 1.0.2 library consumer',
                       'throws `InvalidOperationException`', 'same `ProfileLease` instance',
                       'not a claim of unchanged 1.0.2 consumer compatibility',
                       'failure after startup is claimed is terminal'):
            self.assertIn(phrase, text)
        self.assertIn('externer kompilierter Consumer',
                      (ROOT / 'docs/releases/v1.1.0-api-review.md').read_text())

    def test_documented_public_startup_still_owns_initialization_and_restore(self):
        source = (ROOT / 'libmeowchan/Registry.Startup.cs').read_text()
        self.assertIn('public Task StartAsync(ProfileLease lease, CancellationToken cancellationToken = default)', source)
        for operation in ('InitializeAdmittedAsync', 'RestoreAdmittedAsync', 'CompleteStartup()',
                          'ActivatePreparedProfileProducersAsync'):
            self.assertIn(operation, source)
        self.assertLess(source.index('RestoreAdmittedAsync'), source.index('CompleteStartup()'))
        self.assertLess(source.index('CompleteStartup()'), source.index('await ActivatePreparedProfileProducersAsync'))


if __name__ == '__main__':
    unittest.main()
