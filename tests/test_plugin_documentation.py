"""Public inventory contract; reads allowlists/source text only, never builds plugins."""
import ast
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET


REPOSITORY = Path(__file__).resolve().parents[1]


def verifier_allowlist(name):
    tree = ast.parse((REPOSITORY / 'scripts/package-plugins.py').read_text(encoding='utf-8'))
    return next(ast.literal_eval(node.value) for node in tree.body
                if isinstance(node, ast.Assign)
                and any(isinstance(target, ast.Name) and target.id == name for target in node.targets))


def plain_markdown(text):
    return re.sub(r'\s+', ' ', text.replace('**', '').replace('`', '')).strip()


class PluginDocumentationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        delivery = ET.parse(REPOSITORY / 'packaging/PluginDelivery.targets').getroot()

        def paths(item):
            return [Path(value.removeprefix('$(_PluginRepositoryRoot)'))
                    for node in delivery.iter(item) for value in node.attrib['Include'].split(';')]

        cls.projects = paths('_ShippedPluginProject')
        cls.editable = paths('_PluginExampleSource')
        cls.entries = []
        for project in cls.projects:
            # The maintained plugins are complete single-file entries named after their folder.
            entry = project.parent / (project.parent.name + '.cs')
            text = (REPOSITORY / entry).read_text(encoding='utf-8')
            declaration = re.search(r'\bpublic\s+(?:sealed\s+)?class\s+\w+\s*:\s*([^\{]+)\{', text)
            if declaration is None:
                raise AssertionError(f'No plugin entry declaration: {entry}')
            interfaces = set(re.findall(r'\bI(?:Source|BooruSource|ImageFilter)Plugin\b', declaration[1]))
            if len(interfaces) != 1:
                raise AssertionError(f'Update inventory classification for {entry}: {interfaces}')
            assembly = ET.parse(REPOSITORY / project).findtext('.//AssemblyName')
            cls.entries.append((project.parts[1], interfaces.pop(), entry, assembly))

    def inventory(self, group=None):
        entries = [entry for entry in self.entries if group is None or entry[0] == group]
        return (
            len(entries),
            sum(interface in ('ISourcePlugin', 'IBooruSourcePlugin') for _, interface, _, _ in entries),
            sum(interface == 'IImageFilterPlugin' for _, interface, _, _ in entries),
            sum(entry in self.editable for _, _, entry, _ in entries),
        )

    def test_inventory_is_derived_from_both_packaging_allowlists(self):
        assemblies = [entry[3] for entry in self.entries]
        sources = [path.name for path in self.editable]
        self.assertCountEqual(verifier_allowlist('PLUGINS'), assemblies)
        self.assertCountEqual(verifier_allowlist('SOURCES'), sources)
        self.assertEqual(len(assemblies), len(set(assemblies)))
        self.assertEqual(len(sources), len(set(sources)))
        self.assertEqual({entry for group, _, entry, _ in self.entries if group == 'Examples'},
                         set(self.editable))

    def test_guide_inventory_table_matches_official_example_and_filter_split(self):
        text = (REPOSITORY / 'docs/plugins/README.md').read_text(encoding='utf-8')
        # Match table semantics, not paragraph wording, line wrapping or Markdown emphasis.
        rows = {}
        for line in text.splitlines():
            cells = [plain_markdown(cell) for cell in line.strip().strip('|').split('|')]
            if len(cells) == 5 and all(cell.isdecimal() for cell in cells[1:]):
                rows[cells[0]] = tuple(map(int, cells[1:]))
        for label, group in (('Official', 'Official'), ('Examples / filters', 'Examples'), ('Total', None)):
            with self.subTest(row=label):
                self.assertEqual(self.inventory(group), rows.get(label),
                                 'Update the public inventory table when an allowlist changes')

    def test_public_summary_counts_match_packaging_not_historical_source_counts(self):
        plugins, sources, filters, editable = self.inventory()
        for name in ('README.md', 'using-plugins.md'):
            text = plain_markdown((REPOSITORY / 'docs/plugins' / name).read_text(encoding='utf-8'))
            for count, noun in ((plugins, r'plugins'), (sources, r'source-plugin entries'),
                                (filters, r'image filters'), (editable, r'editable example/filter source files')):
                with self.subTest(document=name, noun=noun):
                    matches = re.findall(r'(\d+)\s+' + noun + r'\b', text)
                    self.assertTrue(matches, f'Missing inventory count for {noun}')
                    self.assertEqual({str(count)}, set(matches), f'Stale inventory count for {noun}')

    def test_official_readme_describes_only_prebuilt_delivery(self):
        text = (REPOSITORY / 'plugins/Official/README.md').read_text(encoding='utf-8')
        rows = [line for line in text.splitlines() if line.startswith('|') and '.dll' in line]
        official = [entry for entry in self.entries if entry[0] == 'Official']
        self.assertEqual(len(official), len(rows))
        for _, _, entry, assembly in official:
            with self.subTest(plugin=assembly):
                row = next((line for line in rows if f'`{assembly}.dll`' in line), '')
                self.assertIn(f'`{entry.relative_to("plugins/Official").as_posix()}`', row)
                self.assertEqual('No', plain_markdown(row.strip('|').split('|')[-1]))
                self.assertNotIn(entry, self.editable)


if __name__ == '__main__':
    unittest.main()
