"""Public package inventory contract; synthetic and source-only checks only."""
import importlib.util
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts/package-plugins.py'
spec = importlib.util.spec_from_file_location('public_plugin_package', SCRIPT)
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class PublicPackageInventoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.delivery = ET.parse(ROOT / 'packaging/PluginDelivery.targets').getroot()
        cls.properties = {node.tag: (node.text or '') for node in cls.delivery.iter()
                         if node.tag in ('_ShippedPluginAssemblies', '_PluginExampleSourceNames')}
        # The public contract is intentionally read from the explicit MSBuild
        # items; this test never walks any user/profile plugin directory.
        cls.projects = [value for node in cls.delivery.iter('_ShippedPluginProject')
                        for value in node.attrib['Include'].split(';')]
        cls.sources = [value for node in cls.delivery.iter('_PluginExampleSource')
                       for value in node.attrib['Include'].split(';')]

    def test_build_allowlist_is_explicit_and_matches_project_identities(self):
        self.assertEqual(len(package.PLUGINS), 20)
        self.assertEqual(len(package.SOURCES), 17)
        self.assertEqual(set(package.PLUGINS), set(self.properties['_ShippedPluginAssemblies'].split(';')))
        self.assertEqual(set(package.SOURCES), set(self.properties['_PluginExampleSourceNames'].split(';')))
        self.assertEqual(len(self.projects), len(package.PLUGINS))
        self.assertEqual(len(self.sources), len(package.SOURCES))
        self.assertTrue(all('*' not in path and '?' not in path for path in (*self.projects, *self.sources)))

        assemblies = []
        for project in self.projects:
            path = ROOT / project.removeprefix('$(_PluginRepositoryRoot)')
            self.assertTrue(path.is_file(), path)
            assemblies.append(ET.parse(path).getroot().findtext('.//AssemblyName'))
        self.assertCountEqual(assemblies, package.PLUGINS)
        self.assertCountEqual([Path(path).name for path in self.sources], package.SOURCES)

    def test_delivery_task_has_fail_closed_identity_inputs(self):
        task = ET.tostring(self.delivery, encoding='unicode')
        self.assertIn('ExpectedPluginAssemblies', task)
        self.assertIn('ExpectedSourceFiles', task)
        self.assertIn('Plugin delivery input', task)
        self.assertIn('ReparsePoint', task)
        self.assertIn('actualAssemblies.SetEquals(expectedAssemblies)', task)
        self.assertIn('actualSources.SetEquals(expectedSources)', task)

    def test_application_project_does_not_wildcard_include_plugin_sources(self):
        project = (ROOT / 'MeowChan.csproj').read_text(encoding='utf-8')
        self.assertIn('plugins/**', project)  # explicit default-item exclusion
        self.assertNotRegex(project, r'(?:Include|Update)="[^"]*plugins/[^\"]*[?*]')
        self.assertNotIn('profiles/**', project.casefold())

    def test_public_inventory_document_has_all_and_only_bundled_identities(self):
        text = (ROOT / 'docs/releases/public-plugin-inventory.md').read_text(encoding='utf-8')
        listed = set(re.findall(r'`(MeowChan\.[A-Za-z0-9_.]+)`', text))
        self.assertEqual(listed, set(package.PLUGINS))
        for source in package.SOURCES:
            self.assertIn(f'`{source}`', text)
        self.assertIn('active-profile', text)
        self.assertNotIn('Coomer', text)
        self.assertNotIn('Kemono', text)


if __name__ == '__main__':
    unittest.main()
