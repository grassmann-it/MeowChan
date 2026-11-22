#!/usr/bin/env python3
"""Record the proposed validation scope for a PR or branch push.

To replay a selected plan at its recorded checkout, pass its event type,
diff-input.json, inventory.json, seed and sample percentage to this script.
"""

import argparse
from collections import defaultdict
import importlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import posixpath
import re
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

balance = importlib.import_module('balance-validation')
vstest = balance.native
offline = importlib.import_module('run-dotnet-shards')

ROOT = Path(__file__).resolve().parents[1]
RULES_VERSION = 3
INVENTORY_VERSION = 1
DEFAULT_PERCENT = 10
DISCOVERY_BUDGET = 35 * 60
LINUX = 'ubuntu-24.04'
WINDOWS = 'windows-2022'
MACOS = vstest.PLATFORMS
APP = 'MeowChan.csproj'
LIBRARY = 'libmeowchan/libmeowchan.csproj'
CORE = offline.SHARDS['shard-01'][0]
DESKTOP = offline.SHARDS['shard-02'][0]
SMOKE = 'tests/MeowChan.DesktopSmoke/MeowChan.DesktopSmoke.csproj'
PROBE = 'tests/PluginReauthorizationProbe/PluginReauthorizationProbe.csproj'
DELIVERY = 'packaging/PluginDelivery.targets'
ANALYZER = 'scripts/AnalyzerBaseline.Tests/AnalyzerBaseline.Tests.csproj'
PROJECTS = sorted(project for group in offline.SHARDS.values() for project in group)
APP_FILES = {'App.axaml', 'App.axaml.cs', 'Program.cs', 'Program.Development.cs', 'app.manifest'}
FILE_ITEMS = {'Compile', 'Content', 'None', 'EmbeddedResource', 'AvaloniaResource', 'AvaloniaXaml'}
GALLERY_CLASSES = ('ViewerTransportTests', 'GalleryVideoPlayerScrubTests', 'GalleryDockTransportTests',
                   'GalleryPlaybackContinuityRegressionTests', 'VideoPlaybackLifetimeTests')
GALLERY_SMOKES = ('gallery-playback-features', 'video-lifecycle')
BOORU_SMOKES = ('booru-workbench', 'booru-media')
CENTRAL_PREFIXES = ('.github/', 'Shared/', 'tests/Shared/', 'Desktop/Localization/', 'Desktop/Config',
                    'MeowChan.PluginHost/Configuration/', 'scripts/AnalyzerBaseline/', 'build/')
CENTRAL_FILES = {
    'global.json', '.editorconfig', '.gitattributes', '.gitignore', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
    'scripts/plan-validation-selection.py', 'scripts/balance-validation.py',
    'scripts/compute-validation-required.py', 'scripts/aggregate-release-gates.py',
}
SHA = re.compile(r'[a-f0-9]{40}|[a-f0-9]{64}')
ID = re.compile(r'[a-z0-9][a-z0-9-]*')
METHOD = re.compile(r'[\w.`+]+\.[\w`]+')


class FullValidation(ValueError):
    """A diff, dependency or inventory cannot support a selected plan."""


def require(condition, reason):
    if not condition:
        raise FullValidation(reason)


def git(repo, *args):
    result = subprocess.run(['git', '-C', str(repo), *args], capture_output=True, timeout=30)
    require(result.returncode == 0, f'git_{args[0]}_failed')
    return result.stdout.decode('utf-8')


def commit(repo, sha, role):
    require(isinstance(sha, str) and SHA.fullmatch(sha) and sha.strip('0'), f'missing_{role}_sha')
    try:
        return git(repo, 'rev-parse', '--verify', f'{sha}^{{commit}}').strip()
    except FullValidation:
        raise FullValidation(f'unavailable_{role}_commit') from None


def revisions(repo, event, payload, source):
    checkout = source['checkout_sha']
    if event == 'pull_request':
        pr = payload['pull_request']
        source['event_base_sha'] = pr['base']['sha']
        source['diff_head_sha'] = pr['head']['sha']
        base = commit(repo, source['event_base_sha'], 'pr_base')
        head = commit(repo, source['diff_head_sha'], 'pr_head')
        if checkout != head:
            parents = git(repo, 'rev-list', '--parents', '-n', '1', checkout).split()[1:]
            require(parents == [base, head], 'checkout_does_not_match_pr')
        bases = git(repo, 'merge-base', '--all', base, head).split()
        require(len(bases) == 1, 'ambiguous_merge_base')
        source['diff_base_sha'] = bases[0]
    elif event == 'push' and (payload.get('ref') or '').startswith('refs/heads/'):
        source['event_base_sha'] = source['diff_base_sha'] = payload.get('before')
        source['diff_head_sha'] = payload.get('after')
        require(commit(repo, source['diff_head_sha'], 'push_after') == checkout, 'checkout_does_not_match_push')
        commit(repo, source['diff_base_sha'], 'push_before')
    else:
        raise FullValidation('event_requires_full_validation')


def changes_between(repo, base, head):
    fields = iter(git(repo, 'diff', '--name-status', '-z', '--find-renames=50%', '-l0', '--no-ext-diff',
                      '--no-relative', '--ignore-submodules=none',
                      base, head, '--').split('\0')[:-1])
    changes = []
    for status in fields:
        path = next(fields)
        kind = status[0]
        require(kind in 'ADMR', f'unsupported_diff_status:{status}')
        old = None if kind == 'A' else path
        new = next(fields) if kind == 'R' else None if kind == 'D' else path
        changes.append(dict(status=status, old=old, new=new))
    return sorted(changes, key=lambda change: (change['new'] or change['old'], change['old'] or ''))


def msbuild_path(value, document):
    directory = posixpath.dirname(document)
    for variable, prefix in (('$(MSBuildThisFileDirectory)', directory),
                             ('$(_PluginRepositoryRoot)', '' if document == DELIVERY else None)):
        if prefix is not None and value.startswith(variable):
            value = value.removeprefix(variable)
            directory = prefix
            break
    path = posixpath.normpath(posixpath.join(directory, value.replace('\\', '/')))
    require(not any(token in path for token in ('$(', '@(', '%('))
            and not path.startswith(('/', '../')) and path != '..', f'unresolved_msbuild_path:{document}:{value}')
    return path


def file_pattern(path):
    # MSBuild's * stays within a path segment and **/ also matches zero directories.
    replacements = {'**/': '(?:.*/)?', '**': '.*', '*': '[^/]*', '?': '[^/]'}
    return re.compile(''.join(replacements.get(part, re.escape(part))
                              for part in re.split(r'(\*\*/|\*\*|\*|\?)', path)))


class ProjectGraph:
    def __init__(self, repo, sha):
        self.paths = set(git(repo, 'ls-tree', '-rz', '--name-only', sha).split('\0')[:-1])
        self.reverse = defaultdict(set)
        self.directories = defaultdict(set)
        self.inputs = defaultdict(set)
        self.delivered = set()
        self.test_projects = set()
        projects = {path for path in self.paths if path.endswith('.csproj')
                    and not path.startswith(('samples/', 'tests/fixtures/'))}
        documents = {}

        def read(path):
            if path not in documents:
                require(path in self.paths, f'missing_msbuild_file:{path}')
                documents[path] = ET.fromstring(git(repo, 'show', f'{sha}:{path}'))
            return documents[path]

        def visit(project, document, parents=()):
            require(document not in parents, f'cyclic_msbuild_import:{document}')
            root = read(document)
            items = root.findall('./ItemGroup/*')
            imports = root.findall('./Import') + root.findall('./ImportGroup/Import')
            require(set(root.iter('ProjectReference')) <= set(items) and set(root.iter('Import')) == set(imports)
                    and root.find('.//Choose') is None, f'unsupported_msbuild_graph:{document}')
            delivery_items = {f'@({item.tag})': item.attrib['Include'].split(';') for item in items
                              if document == DELIVERY and item.tag in ('_PluginHelperProject', '_ShippedPluginProject')}
            for item in items:
                if item.tag == 'ProjectReference':
                    for entry in item.attrib['Include'].split(';'):
                        for value in delivery_items.get(entry, [entry]):
                            dependency = msbuild_path(value, document)
                            require(dependency in projects, f'missing_project_reference:{dependency}')
                            self.reverse[dependency].add(project)
                            if entry in delivery_items:
                                self.delivered.add(dependency)
                elif item.tag in FILE_ITEMS:
                    for value in (item.get('Include') or item.get('Update') or '').split(';'):
                        if value:
                            path = msbuild_path(value, document)
                            if '*' in path or '?' in path:
                                for matched in filter(file_pattern(path).fullmatch, self.paths):
                                    self.inputs[matched].add(project)
                            else:
                                require(path in self.paths or 'Update' in item.attrib, f'missing_project_input:{path}')
                                self.inputs[path].add(project)
            for imported in imports:
                dependency = msbuild_path(imported.attrib['Project'], document)
                visit(project, dependency, (*parents, document))

        for project in sorted(projects):
            directory = posixpath.dirname(project)
            if directory:
                self.directories[directory].add(project)
            visit(project, project)
            root = documents[project]
            if (root.find('./PropertyGroup/IsTestProject[.="true"]') is not None
                    or root.find('./ItemGroup/PackageReference[@Include="Microsoft.NET.Test.Sdk"]') is not None):
                self.test_projects.add(project)

    def owners(self, path):
        if path not in self.paths:
            return set()
        owners = set(self.inputs[path])
        for directory in PurePosixPath(path).parents:
            if directory.as_posix() in self.directories:
                owners.update(self.directories[directory.as_posix()])
                break
        if (path.startswith('Desktop/') and path.endswith(('.cs', '.axaml'))) or path in APP_FILES:
            owners.add(APP)
        return owners

    def consumers(self, owners):
        seen, pending = set(), list(owners)
        while pending:
            project = pending.pop()
            if project not in seen:
                seen.add(project)
                pending.extend(self.reverse[project] - seen)
        return seen


def central(path):
    name = posixpath.basename(path).lower()
    return (path in CENTRAL_FILES or path.startswith(CENTRAL_PREFIXES) or name.startswith('directory.build.')
            or name.endswith(('.csproj', '.fsproj', '.vbproj', '.sln', '.slnx', '.props', '.targets', '.runsettings'))
            or name.endswith(('lock.json', '.lock')) or name in ('nuget.config', 'global.json'))


def native_targets(path):
    name = path.lower()
    if re.search(r'(?:^|[/._-])(?:linux|x11|wayland)', name):
        return (LINUX, 'ubuntu-24.04-arm')
    if re.search(r'(?:^|[/._-])(?:windows|win32|win-x64)', name):
        return (WINDOWS,)
    if re.search(r'(?:^|[/._-])(?:macos|darwin|osx)', name):
        return MACOS
    return ()


def pick(seed, unit, identifiers, percent):
    ranked = sorted(identifiers, key=lambda identifier: (vstest.digest([seed, unit, identifier]), identifier))
    return sorted(ranked[:(len(ranked) * percent + 99) // 100])


def inventory_record(checkout, projects, smokes):
    body = dict(version=INVENTORY_VERSION, checkout_sha=checkout, platform=LINUX, configuration='Release',
                projects={key: sorted(value) for key, value in projects.items()}, smokes=smokes)
    return dict(**body, sha256=vstest.digest(body))


def verify_inventory(inventory, checkout):
    require(isinstance(inventory, dict), 'invalid_inventory')
    body = {key: value for key, value in inventory.items() if key != 'sha256'}
    require(inventory.get('sha256') == vstest.digest(body), 'inventory_hash_mismatch')
    require(inventory.get('checkout_sha') == checkout, 'stale_inventory_checkout')
    require(set(body) == {'version', 'checkout_sha', 'platform', 'configuration', 'projects', 'smokes'}
            and inventory.get('version') == INVENTORY_VERSION and inventory.get('platform') == LINUX
            and inventory.get('configuration') == 'Release', 'unsupported_inventory')
    projects, smokes = inventory.get('projects'), inventory.get('smokes')
    require(isinstance(projects, dict) and projects.keys() == set(PROJECTS), 'incomplete_project_inventory')
    for project, methods in projects.items():
        require(isinstance(methods, list) and methods
                and all(isinstance(method, str) and METHOD.fullmatch(method) for method in methods)
                and len(set(methods)) == len(methods), f'invalid_method_inventory:{project}')
    require(isinstance(smokes, dict) and {'normal', *balance.scenes.full_scene_inventory()} <= smokes.keys()
            and all(ID.fullmatch(key) and (value is None or isinstance(value, str) and value)
                    for key, value in smokes.items()), 'incomplete_smoke_inventory')
    return inventory_record(checkout, projects, smokes)


def discover(repo, checkout):
    require(sys.platform == 'linux' and platform.machine() == 'x86_64', 'discovery_requires_linux_x64')
    require(not git(repo, 'status', '--porcelain=v1'), 'discovery_requires_clean_checkout')
    env = {key: val for key, val in os.environ.items() if not key.startswith(('MEOWCHAN_', 'GIT_'))}
    deadline = time.monotonic() + DISCOVERY_BUDGET

    def dotnet(*args, timeout):
        remaining = deadline - time.monotonic()
        require(remaining > 0, 'discovery_deadline_exceeded')
        print('Discovery: dotnet', *args[:2], file=sys.stderr, flush=True)
        completed = subprocess.run(['dotnet', *args], cwd=repo, env=env, text=True,
                                   stdout=subprocess.PIPE, timeout=min(remaining, timeout))
        if completed.returncode:
            print(completed.stdout[-8000:], file=sys.stderr)
            operation = 'registry' if args[0] == str(smoke_assembly) else args[0]
            raise FullValidation(f'discovery_command_failed:{operation}')
        return completed.stdout

    projects = {}
    smoke_assembly = repo / PurePosixPath(SMOKE).parent / 'bin/Release/net10.0/MeowChan.DesktopSmoke.dll'
    for target in ('MeowChan.sln', SMOKE):
        dotnet('restore', target, '-p:Configuration=Release', '--locked-mode', '-warnaserror:NU1900', timeout=300)
        build_scope = '--no-incremental' if target == 'MeowChan.sln' else '--no-dependencies'
        dotnet('build', target, '-c', 'Release', '--no-restore', build_scope, timeout=600)
    with tempfile.TemporaryDirectory(prefix='validate-discovery-') as temporary:
        for project in PROJECTS:
            name = PurePosixPath(project).stem
            path = Path(temporary) / f'{name}.log'
            assembly = repo / PurePosixPath(project).parent / 'bin/Release/net10.0' / f'{name}.dll'
            dotnet('vstest', str(assembly), '--ListTests', f'--Diag:{path}', timeout=90)
            projects[project] = {case['method'] for case in vstest.discovery_inventory(path)}
    listing = dotnet(str(smoke_assembly), '--list-scenarios', timeout=120)
    smokes = {}
    for line in listing.splitlines():
        entry = json.loads(line)
        require(not entry['error'] and entry['id'] not in smokes, f'invalid_registry_entry:{entry["id"]}')
        smokes[entry['id']] = entry['skip']
    require(git(repo, 'rev-parse', 'HEAD').strip() == checkout and not git(repo, 'status', '--porcelain=v1'),
            'checkout_changed_during_discovery')
    return inventory_record(checkout, projects, smokes)


WINDOWS_METHODS = {
    CORE: {
        'MeowChan.Tests.Transfer.RegularProfileFileTests.IdentityChangesWhenASameSizeRewriteRestoresTheTimestamps',
    },
    DESKTOP: {
        'MeowChan.DesktopTests.ArchiveCleanupSpecialFileHostTests.SharingViolationKeepsFullErrorsOpaqueAndDoesNotRecover',
        'MeowChan.DesktopTests.UrlTextExportFileWriterTests.NativeWindowsDeviceAdsAndTrailingAliasesAreRejected',
        'MeowChan.DesktopTests.UrlTextExportFileWriterTests.NativeWindowsMixedUncAndDriveRelativePathsAreRejected',
        'MeowChan.DesktopTests.UrlTextExportFileWriterTests.WindowsExclusiveHandleIsHeldUntilRealTaskFinishes',
    },
    'tests/MeowChan.PluginHost.Configuration.Tests/MeowChan.PluginHost.Configuration.Tests.csproj': {
        'MeowChan.PluginHost.Configuration.Tests.PluginProfileStoreTests.WindowsReaderWithoutDeleteSharingRejectsWriteWithoutChangingRevision',
        'MeowChan.PluginHost.Configuration.Tests.PluginProfileStoreTests.WindowsReadOnlyCoordinationFileStillAllowsProfileReads',
    },
}
MEDIA_FIXTURES = ('tests/Fixtures/gallery-playback-', 'tests/Fixtures/gif-playback/', 'tests/Fixtures/release102/')
MEDIA_INPUTS = (*MEDIA_FIXTURES, 'scripts/generate-playback-fixtures.py')
PACKAGE_INPUTS = {
    'scripts/package-macos.py': ('macos-package', MACOS),
    'scripts/package-macos-dmg.py': ('macos-package', MACOS),
    'scripts/package-linux.py': ('linux-distribution', (LINUX, 'ubuntu-24.04-arm')),
    'scripts/linux-distribution-gates.py': ('linux-distribution', (LINUX, 'ubuntu-24.04-arm')),
    'scripts/package-plugins.py': ('library-package', (LINUX,)),
    'packaging/homebrew/': ('homebrew-macos', (MACOS[0],)),
}
# Test inputs read directly from the repository and thus invisible to the MSBuild graph.
DOCUMENT_CHECKS = {
    'docs/development/booru-test-matrix.md': 'tests/test_plugin_packaging.py',
    'docs/plugins/README.md': 'tests/test_plugin_documentation.py',
    'docs/plugins/using-plugins.md': 'tests/test_plugin_documentation.py',
    'plugins/Official/README.md': 'tests/test_plugin_documentation.py',
    'docs/releases/public-plugin-inventory.md': 'tests/test_public_package_inventory.py',
    'docs/releases/v1.1.0-api-review.md': 'tests/test_core_startup_docs.py',
    'libmeowchan/README.md': 'tests/test_core_startup_docs.py',
    'docs/releases/v1.1.0.md': 'tests/test_ap04_run.py',
}
DESKTOP_DOCUMENT_INPUTS = {
    'README.md', 'CHANGELOG.md', 'docs/Help.md', 'docs/releases/v1.1.0.md',
    'docs/user-guide/danbooru.md', 'docs/user-guide/settings-export-and-limits.md',
}
GALLERY = re.compile(r'Desktop/GalleryVideoPlayer[^/]*\.cs')
PLAYBACK = ('gallery-playback-features', 'video-lifecycle', *balance.scenes.GIF)


class Requirements:
    def __init__(self):
        self.tests = defaultdict(set)
        self.classes = defaultdict(set)
        self.smokes = defaultdict(set, normal={'linux_baseline'})
        self.checks = defaultdict(set)

    def check(self, identifier, path, platforms=(LINUX,)):
        for target in platforms:
            self.checks[identifier, target].add(path)

    def route(self, path, owners, consumers, delivered, exists_at_checkout):
        gallery = GALLERY.fullmatch(path) is not None
        targets = native_targets(path)
        for project in sorted(consumers & set(PROJECTS)):
            if gallery and project == DESKTOP and owners == {APP}:
                self.classes[project].add(path)
            else:
                self.tests[project, LINUX].add(path)
            for target in set(targets) - {LINUX}:
                if target in balance.offline.ARCHITECTURES or project in (CORE, DESKTOP):
                    self.tests[project, target].add(path)
                else:
                    self.check(f'build:{project}', path, (target,))

        if gallery:
            smoke_ids = GALLERY_SMOKES
            self.check('validate:media_input', path, balance.PLATFORMS)
        elif path.startswith('plugins/Official/Danbooru/') or path.startswith('Desktop/') and 'Booru' in path:
            smoke_ids = BOORU_SMOKES
        elif path.startswith('Desktop/') and 'ReaderDisplay' in path:
            smoke_ids = ('settings-reader-display',)
        elif path.startswith(MEDIA_INPUTS):
            smoke_ids = (*PLAYBACK, 'media-page', 'booru-media')
            self.tests[DESKTOP, LINUX].add(path)
            self.check('validate:media_input', path, balance.PLATFORMS)
            self.check('package-media', path, (WINDOWS,))
            self.check('macos-package', path, MACOS)
            self.check('linux-distribution', path, (LINUX, 'ubuntu-24.04-arm'))
        elif SMOKE in owners or path == 'scripts/run-desktop-smoke.py':
            smoke_ids = ('*',)
        elif SMOKE in consumers:
            smoke_ids = balance.scenes.full_scene_inventory()
        else:
            smoke_ids = ()
        for identifier in smoke_ids:
            self.smokes[identifier].add(path)

        if owners & delivered and not owners & {APP, LIBRARY}:
            self.check('plugin-delivery', path)
        if LIBRARY in consumers:
            self.check('library-package', path)
        if PROBE in consumers:
            self.check('validate:process_probe', path)
        if targets and APP in consumers:
            self.check('validate:smoke_normal', path, targets)

        if path in ('scripts/test-media-interaction-smoke.py', 'tests/MeowChan.DesktopSmoke/MediaNativeInput.cs'):
            self.check('validate:media_input', path, balance.PLATFORMS)
            self.check('python:scripts/test-media-interaction-contract.py', path)
            return
        if path == 'scripts/check-desktop-package.py':
            for identifier, platforms in (
                ('package', (WINDOWS,)), ('macos-package', MACOS),
                ('linux-distribution', (LINUX, 'ubuntu-24.04-arm')), ('package-media', (WINDOWS,)),
            ):
                self.check(identifier, path, platforms)
            self.check('validate:package_fixtures', path)
            return
        if path.startswith('tests/fixtures/'):
            if path.startswith('tests/fixtures/upgrade-v1x/'):
                self.check('validate:upgrade_replay', path, balance.PLATFORMS)
            elif path.startswith('tests/fixtures/upgrade-v1.0.2/'):
                self.check('validate:upgrade_102_replay', path, balance.PLATFORMS)
                self.check('ap04-upgrade', path)
            else:
                raise FullValidation(f'unmapped_fixture:{path}')
            return
        if path in PACKAGE_INPUTS or path.startswith('packaging/homebrew/'):
            identifier, platforms = PACKAGE_INPUTS.get(path, PACKAGE_INPUTS['packaging/homebrew/'])
            self.check(identifier, path, platforms)
            self.check('validate:package_fixtures', path)
            return
        if re.fullmatch(r'(tests/test_[^/]+|scripts/test[-_][^/]+)\.py', path):
            self.check(f'python:{path}' if exists_at_checkout else 'python:workflow-contracts', path)
            return
        if path == 'scripts/run-desktop-smoke.py':
            self.check('validate:smoke_runner_contract', path)
            return
        if path in DOCUMENT_CHECKS or path in DESKTOP_DOCUMENT_INPUTS:
            if path in DOCUMENT_CHECKS:
                self.check(f'python:{DOCUMENT_CHECKS[path]}', path)
            if path in DESKTOP_DOCUMENT_INPUTS:
                self.tests[DESKTOP, LINUX].add(path)
            return
        if path.startswith('docs/development/') and path.endswith('.md'):
            return
        require(consumers or path.startswith(MEDIA_INPUTS), f'unknown_productive_path:{path}')
        for project in sorted(owners - set(PROJECTS) - {SMOKE}):
            if exists_at_checkout:
                self.check(f'build:{project}', path, targets or (LINUX,))


def requirements_for(repo, source, changes, result):
    paths = sorted({path for change in changes for path in (change['old'], change['new']) if path})
    for path in paths:
        if path.startswith(('build/analyzer-baseline-', 'scripts/AnalyzerBaseline')):
            result.check(f'dotnet-test:{ANALYZER}', path)
    central_paths = [f'central_path:{path}' for path in paths if central(path)]
    if central_paths:
        raise FullValidation(*central_paths)

    base, head, checkout = (source[key] for key in ('diff_base_sha', 'diff_head_sha', 'checkout_sha'))
    graphs = {sha: ProjectGraph(repo, sha) for sha in dict.fromkeys((base, head, checkout))}
    require(set(PROJECTS) <= graphs[checkout].paths, 'missing_validation_project')
    unassigned = graphs[checkout].test_projects - set(PROJECTS) - {ANALYZER}
    require(not unassigned, 'unassigned_test_projects:' + ','.join(sorted(unassigned)))
    reasons = set()
    for path in paths:
        # A rename may change its owner, and the merge checkout can introduce a consumer.
        # Follow each snapshot independently so edges from different commits do not form a fake chain.
        owners, consumers, delivered = set(), set(), set()
        for graph in graphs.values():
            roots = graph.owners(path)
            owners.update(roots)
            consumers.update(graph.consumers(roots))
            delivered.update(graph.delivered)
        try:
            result.route(path, owners, consumers, delivered, path in graphs[checkout].paths)
        except FullValidation as exc:
            reasons.update(exc.args)
    if reasons:
        raise FullValidation(*sorted(reasons))


def checks_from(requirements):
    return [dict(id=identifier, platform=target, reasons=sorted(reasons))
            for (identifier, target), reasons in sorted(requirements.checks.items())]


def select(requirements, inventory, seed, percent):
    required_tests, sampled_tests = [], []
    for project, inventory_methods in inventory['projects'].items():
        methods = set(inventory_methods)
        windows = methods.intersection(WINDOWS_METHODS.get(project, ()))
        classes = requirements.classes.get(project)
        linux_required = requirements.tests.get((project, LINUX), set())
        targets = {target for key, target in requirements.tests if key == project}
        if windows:
            targets.add(WINDOWS)
        for target in sorted(targets | {LINUX}):
            whole = requirements.tests.get((project, target), set())
            mandatory = set(methods) if whole else set()
            reasons = set(whole)
            if not whole and target == LINUX and classes:
                for name in GALLERY_CLASSES:
                    matched = {method for method in methods if method.rsplit('.', 2)[-2] == name}
                    require(matched, f'missing_required_test_class:{project}:{name}')
                    mandatory = mandatory | matched
                reasons.update(classes)
            if target == LINUX:
                # Route explicitly Windows-only methods to their runnable target, even in a required suite.
                mandatory -= windows
            elif target == WINDOWS and linux_required and windows:
                mandatory.update(windows)
                reasons.update(linux_required)
            if mandatory:
                required_tests.append(dict(project=project, platform=target, methods=sorted(mandatory),
                                           reasons=sorted(reasons)))
            eligible = methods - windows if target == LINUX else windows if target == WINDOWS else set()
            sample = pick(seed, project, eligible - mandatory, percent)
            if sample:
                sampled_tests.append(dict(project=project, platform=target, methods=sample))

    registry = inventory['smokes']
    runnable = {identifier for identifier, skipped in registry.items() if skipped is None}
    required_smokes = {identifier: set(reasons) for identifier, reasons in requirements.smokes.items() if identifier != '*'}
    if '*' in requirements.smokes:
        for identifier in runnable:
            required_smokes.setdefault(identifier, set()).update(requirements.smokes['*'])
    missing = set(required_smokes) - runnable
    require(not missing, 'missing_or_skipped_required_smokes:' + ','.join(sorted(missing)))
    sample_smokes = pick(seed, 'smoke-registry', runnable - required_smokes.keys(), percent)
    required = dict(tests=required_tests, checks=checks_from(requirements),
                    smokes=[dict(id=identifier, platform=LINUX, reasons=sorted(reasons))
                            for identifier, reasons in sorted(required_smokes.items())])
    sample = dict(tests=sampled_tests, smokes=[dict(id=identifier, platform=LINUX) for identifier in sample_smokes])
    return required, sample


def plan(repo, event, payload, seed, percent, load_inventory, expected_checkout=None):
    require(0 <= percent <= 100, 'sample_percent_out_of_range')
    source = dict(event=event, ref=payload.get('ref'), checkout_sha=None,
                  event_base_sha=None, diff_base_sha=None, diff_head_sha=None)
    report = dict(rules_version=RULES_VERSION, mode='shadow', scope='full', source=source,
                  seed=seed, sample_percent=percent, inventory=None, changes=[], fallback_reasons=[],
                  target_platforms=list(balance.PLATFORMS), required=dict(tests=[], smokes=[], checks=[]),
                  sample=dict(tests=[], smokes=[]))
    requirements = Requirements()
    evidence = None
    try:
        source['checkout_sha'] = git(repo, 'rev-parse', 'HEAD').strip()
        require(expected_checkout is None or source['checkout_sha'] == expected_checkout, 'unexpected_checkout_sha')
        revisions(repo, event, payload, source)
        report['changes'] = changes_between(repo, source['diff_base_sha'], source['diff_head_sha'])
        requirements_for(repo, source, report['changes'], requirements)
        try:
            evidence = verify_inventory(load_inventory(source['checkout_sha']), source['checkout_sha'])
        except (OSError, subprocess.SubprocessError, KeyError, TypeError, json.JSONDecodeError) as exc:
            raise FullValidation(f'inventory_unavailable:{type(exc).__name__}') from exc
        except FullValidation:
            raise
        except ValueError as exc:
            print(f'Invalid discovery evidence: {exc}', file=sys.stderr)
            raise FullValidation('invalid_discovery_evidence') from exc
        report['inventory'] = {key: value for key, value in evidence.items() if key not in ('projects', 'smokes')}
        report['required'], report['sample'] = select(requirements, evidence, seed, percent)
        selected = {row['platform'] for group in ('required', 'sample')
                    for rows in report[group].values() for row in rows}
        report['target_platforms'] = [target for target in balance.PLATFORMS if target in selected]
        report['scope'] = 'selected'
    except FullValidation as exc:
        report['fallback_reasons'] = list(exc.args)
        report['required']['checks'] = checks_from(requirements)
    except (OSError, subprocess.SubprocessError, KeyError, TypeError, ValueError, ET.ParseError, StopIteration) as exc:
        report['fallback_reasons'] = [f'diff_or_graph_unavailable:{type(exc).__name__}']
    report['plan_sha256'] = vstest.digest(report)
    return report, evidence


def write_summary(path, report):
    # Long suites and path lists have a preview here and remain complete in plan.json.
    data = {key: report[key] for key in ('scope', 'seed', 'sample_percent', 'source', 'rules_version',
                                        'inventory', 'fallback_reasons', 'target_platforms', 'plan_sha256')}
    for group in ('required', 'sample'):
        data[group] = {}
        for key, rows in report[group].items():
            data[group][key] = previews = []
            for row in rows:
                preview = dict(row)
                if 'methods' in row:
                    preview['method_count'] = len(row['methods'])
                    preview['methods'] = row['methods'][:10]
                if 'reasons' in row:
                    preview['reasons'] = row['reasons'][:10]
                previews.append(preview)
    with path.open('a', encoding='utf-8') as summary:
        # Indented Markdown keeps strings from the event and repository literal.
        print('## Desktop validation selection (shadow)\n', file=summary)
        for line in json.dumps(data, indent=2, sort_keys=True).splitlines():
            print('    ' + line, file=summary)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--repo', type=Path, default=ROOT)
    parser.add_argument('--event', default=os.environ.get('GITHUB_EVENT_NAME'), required='GITHUB_EVENT_NAME' not in os.environ)
    parser.add_argument('--event-file', type=Path, default=os.environ.get('GITHUB_EVENT_PATH'),
                        required='GITHUB_EVENT_PATH' not in os.environ)
    parser.add_argument('--checkout', default=os.environ.get('GITHUB_SHA'))
    parser.add_argument('--seed', default=os.environ.get('GITHUB_RUN_ID'), required='GITHUB_RUN_ID' not in os.environ)
    parser.add_argument('--sample-percent', type=int, default=DEFAULT_PERCENT, choices=range(101), metavar='0..100')
    inputs = parser.add_mutually_exclusive_group()
    inputs.add_argument('--discover', action='store_true', help='Build and discover on Linux x64')
    inputs.add_argument('--inventory', type=Path, help='Replay the archived, checkout-bound inventory')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--summary', type=Path, default=os.environ.get('GITHUB_STEP_SUMMARY'))
    args = parser.parse_args()

    def load_inventory(checkout):
        if args.discover:
            return discover(args.repo.resolve(), checkout)
        if args.inventory:
            return json.loads(args.inventory.read_text(encoding='utf-8'))
        raise FullValidation('missing_inventory')

    try:
        payload = json.loads(args.event_file.read_text(encoding='utf-8'))
    except (ValueError, OSError):
        payload = None
    if isinstance(payload, dict):
        payload.setdefault('ref', os.environ.get('GITHUB_REF'))
    report, inventory = plan(args.repo, args.event, payload if isinstance(payload, dict) else {},
                             args.seed, args.sample_percent, load_inventory, args.checkout)
    vstest.write_json(args.output / 'plan.json', report)
    if inventory is not None:
        vstest.write_json(args.output / 'inventory.json', inventory)
        source = report['source']
        payload = dict(ref=source['ref'])
        if args.event == 'pull_request':
            payload['pull_request'] = dict(base=dict(sha=source['event_base_sha']), head=dict(sha=source['diff_head_sha']))
        else:
            payload.update(before=source['diff_base_sha'], after=source['diff_head_sha'])
        vstest.write_json(args.output / 'diff-input.json', payload)
    if args.summary:
        write_summary(args.summary, report)
    print(f"Desktop selection {report['scope']} ({report['plan_sha256']})")


if __name__ == '__main__':
    main()
