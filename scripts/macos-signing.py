#!/usr/bin/env python3
"""Native macOS ad-hoc signing and fail-closed bundle dependency validation.

No hardened runtime, Developer ID, timestamp, notarization or Gatekeeper claim.
CoreCLR permissions apply only to the main executable, the two known plugin
helper apphosts, and the outer bundle signature.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import subprocess
import sys
import tempfile

MACHO_MAGICS = {bytes.fromhex(s) for s in ('cffaedfe', 'feedfacf', 'cefaedfe', 'feedface', 'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca')}
# Microsoft's CoreCLR baseline. No --options runtime: these permissions do not
# turn an ad-hoc build into a hardened-runtime distribution.
ENTITLEMENTS = {f'com.apple.security.cs.{key}': True for key in (
    'allow-jit', 'allow-unsigned-executable-memory', 'allow-dyld-environment-variables', 'disable-library-validation')}


def run(*args: object) -> str:
    result = subprocess.run([str(a) for a in args], capture_output=True, text=True)
    if result.returncode:
        raise ValueError(f"Command failed: {args!r}\n{result.stdout}{result.stderr}")
    return result.stdout


def inventory(bundle: Path) -> list[Path]:
    bundle = bundle.resolve(strict=True)
    binaries = []
    for path in sorted(bundle.rglob('*')):
        if path.is_symlink():
            if Path(os.readlink(path)).is_absolute() or not path.resolve(strict=True).is_relative_to(bundle):
                raise ValueError(f'Unsafe bundle symlink: {path}')
        elif path.is_file():
            with path.open('rb') as stream:
                if stream.read(4) in MACHO_MAGICS:
                    binaries.append(path)
        elif not path.is_dir():
            raise ValueError(f'Unsupported bundle entry: {path}')
    for required in coreclr_apphosts(bundle):
        if required not in binaries:
            raise ValueError(f'Missing regular Mach-O executable: {required}')
    return binaries


def load_commands(text: str) -> tuple[list[str], list[str]]:
    dependencies, rpaths = [], []
    command = ''
    for line in text.splitlines():
        line = line.strip()
        if line.startswith('cmd '):
            command = line[4:]
        elif command == 'LC_RPATH' and line.startswith('path '):
            rpaths.append(line[5:].rsplit(' (offset ', 1)[0])
        elif command in {'LC_LOAD_DYLIB', 'LC_LOAD_WEAK_DYLIB', 'LC_REEXPORT_DYLIB', 'LC_LOAD_UPWARD_DYLIB', 'LC_LAZY_LOAD_DYLIB'} and line.startswith('name '):
            dependencies.append(line[5:].rsplit(' (offset ', 1)[0])
    return dependencies, rpaths


def expand(value: str, loader: Path, executable: Path) -> Path:
    for token, base in (('@loader_path', loader.parent), ('@executable_path', executable.parent)):
        if value == token or value.startswith(token + '/'):
            return base / value[len(token):].lstrip('/')
    return Path(value)


def system_dependency(value: str) -> bool:
    return value.startswith(('/usr/lib/', '/System/Library/')) and '..' not in Path(value).parts


def apphost_path(bundle: Path) -> Path:
    return bundle / 'Contents/MacOS/MeowChan'


def coreclr_apphosts(bundle: Path) -> list[Path]:
    hosts = [apphost_path(bundle)]
    # Signed bundles seal payloads as resources; unsigned source fixtures may
    # use the original MacOS payload location. Never follow alias directories.
    for payload in (bundle / 'Contents/Resources/application', bundle / 'Contents/MacOS'):
        for directory, name in (('plugin-worker', 'MeowChan.PluginWorker'), ('plugin-compiler', 'MeowChan.PluginCompiler')):
            parent = payload / directory
            if parent.is_dir() and not parent.is_symlink():
                hosts.append(parent / name)
    return hosts


def validate(bundle: Path, runtime: str, signatures: bool = True) -> dict:
    if sys.platform != 'darwin':
        raise ValueError('Native macOS is required for signing/validation')
    bundle = bundle.resolve(strict=True)
    expected = {'osx-arm64': 'arm64', 'osx-x64': 'x86_64'}[runtime]
    if platform.machine() != expected:
        raise ValueError(f'Native runner architecture {platform.machine()} does not match {expected}')
    binaries = inventory(bundle)
    executable = apphost_path(bundle)
    apphosts = coreclr_apphosts(bundle)
    commands = {p: load_commands(run('/usr/bin/otool', '-l', p)) for p in binaries}
    binary_set = set(binaries)
    for path in binaries:
        # Universal dependencies are acceptable only if they include the selected
        # native slice; the apphost itself must be the exact selected architecture.
        arches = run('/usr/bin/lipo', '-archs', path).strip().split()
        if expected not in arches or (path in apphosts and arches != [expected]):
            raise ValueError(f'Architecture mismatch: {path}: {arches}')
        dependencies, rpaths = commands[path]
        owner = next((host for host in apphosts[1:] if path.is_relative_to(host.parent)), executable)
        owner_rpaths = [expand(r, owner, owner) for r in commands[owner][1]]
        search = [expand(r, path, owner) for r in rpaths] + owner_rpaths
        for base in search:
            if not base.is_absolute() or (not base.resolve().is_relative_to(bundle) and not system_dependency(str(base) + '/')):
                raise ValueError(f'Unsafe runtime search path in {path}: {base}')
        for dependency in dependencies:
            if system_dependency(dependency):
                continue  # Apple libraries may live only in the dyld shared cache.
            if dependency.startswith('@rpath/'):
                candidates = [base / dependency[7:] for base in search]
            elif dependency.startswith(('@loader_path/', '@executable_path/')):
                candidates = [expand(dependency, path, owner)]
            else:
                raise ValueError(f'Non-relocatable install name in {path}: {dependency}')
            if not any(system_dependency(str(p)) or (p.exists() and p.resolve().is_relative_to(bundle) and p.resolve() in binary_set) for p in candidates):
                raise ValueError(f'Unresolved bundle dependency in {path}: {dependency}')
        if signatures:
            run('/usr/bin/codesign', '--verify', '--strict', path)
    if signatures:
        run('/usr/bin/codesign', '--verify', '--strict', bundle)
        for host in apphosts:
            actual = plistlib.loads(run('/usr/bin/codesign', '--display', '--entitlements', ':-', host).encode())
            if actual != ENTITLEMENTS:
                raise ValueError(f'Unexpected CoreCLR apphost entitlements for {host}: {actual}')
    return {'runtime': runtime, 'native_host': platform.machine(), 'macho_files': len(binaries),
            'signatures_verified': signatures, 'dependency_closure': True,
            'apphost': str(executable.relative_to(bundle)),
            'coreclr_entitlements_verified': signatures,
            'coreclr_apphosts': [str(host.relative_to(bundle)) for host in apphosts],
            'files': {str(p.relative_to(bundle)): hashlib.sha256(p.read_bytes()).hexdigest() for p in binaries}}


def repair_vlc_references(bundle: Path, runtime: str) -> None:
    """Make upstream VLC's unqualified @rpath core reference relocatable.

    VLC's shipped plugin tree has no LC_RPATH for libvlccore. Only this known
    dependency in the selected VLC subtree is rewritten; unknown dependencies
    remain fail-closed in validate(). Never flatten or replace the plugin tree.
    """
    tree = (apphost_path(bundle.resolve()).parent / 'libvlc' / runtime / 'lib').resolve()
    core = tree / 'libvlccore.dylib'
    for path in inventory(bundle):
        if not path.is_relative_to(tree):
            continue
        dependencies, _ = load_commands(run('/usr/bin/otool', '-l', path))
        if '@rpath/libvlccore.dylib' in dependencies:
            if not core.is_file():
                raise ValueError('Missing VLC core for install-name repair')
            relative = os.path.relpath(core, path.parent)
            run('/usr/bin/install_name_tool', '-change', '@rpath/libvlccore.dylib', '@loader_path/' + relative, path)


def sign(bundle: Path, runtime: str) -> dict:
    if sys.platform != 'darwin':
        raise ValueError('Native macOS is required for signing')
    repair_vlc_references(bundle, runtime)
    validate(bundle, runtime, signatures=False)
    executable = apphost_path(bundle.resolve())
    apphosts = coreclr_apphosts(bundle.resolve())
    with tempfile.TemporaryDirectory(prefix='meowchan-entitlements-') as temporary:
        entitlements = Path(temporary) / 'coreclr.plist'
        entitlements.write_bytes(plistlib.dumps(ENTITLEMENTS))
        for path in sorted(inventory(bundle), key=lambda p: len(p.parts), reverse=True):
            if path != executable:
                options = ('--entitlements', entitlements) if path in apphosts else ()
                run('/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', *options, path)
        # Seal nested code containers after their contents, before the outer app.
        nested = [p for p in bundle.rglob('*') if not p.is_symlink() and p.is_dir() and p.suffix in {'.framework', '.app', '.xpc', '.bundle'}]
        for path in sorted(nested, key=lambda p: len(p.parts), reverse=True):
            run('/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', path)
        # Signing an apphost inside Contents/MacOS can make codesign inspect
        # sibling code as part of its enclosing bundle. All sibling libraries
        # and nested containers therefore must already have their final seals.
        run('/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', '--entitlements', entitlements, executable)
        run('/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', '--entitlements', entitlements, bundle)
    return validate(bundle, runtime)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    parser.add_argument('--runtime', required=True, choices=['osx-arm64', 'osx-x64'])
    parser.add_argument('--sign-adhoc', action='store_true')
    args = parser.parse_args()
    try:
        print(json.dumps((sign if args.sign_adhoc else validate)(args.bundle, args.runtime), indent=2))
    except (OSError, ValueError) as error:
        parser.exit(1, f'{error}\n')
