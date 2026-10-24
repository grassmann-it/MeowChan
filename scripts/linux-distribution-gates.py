#!/usr/bin/env python3
"""Offline, native Ubuntu 24.04 acceptance of the three actual Linux packages.

Images are prepared separately, never pulled/built here. Each scenario gets a
fresh non-root, networkless container and private desktop/profile. AppImage is
tested through AppRun and extract-and-run; this is deliberately not FUSE proof.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import signal
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import uuid

RESERVE = 2 * 1024 ** 3
MAX_FILE = 512 * 1024 ** 2
MAX_PAYLOAD = 2 * 1024 ** 3
MAX_ARTIFACT = 1024 ** 3
MAX_ENTRIES = 20000
MAX_JSON = 16 * 1024 ** 2
MAX_LOG = 32 * 1024 ** 2
SHA = re.compile(r'[0-9a-f]{64}')
COMMIT = re.compile(r'[0-9a-f]{40}')
OWNER = re.compile(r'[0-9a-f]{32}')
MACHINES = {'linux-x64': ('amd64', 62, {'x86_64', 'amd64'}),
            'linux-arm64': ('arm64', 183, {'aarch64', 'arm64'})}
SCENARIOS = ('bundled-vlc', 'system-vlc', 'missing-system-vlc', 'appimage')


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 ** 2), b''):
            digest.update(chunk)
    return digest.hexdigest()


def fingerprint(records):
    return hashlib.sha256(json.dumps(records, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def read_json(path):
    path = Path(path)
    if not path.is_file() or path.is_symlink() or path.stat().st_size > MAX_JSON:
        raise ValueError('JSON must be a bounded regular file: ' + str(path))
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_object)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate JSON key: ' + key)
        result[key] = value
    return result


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def relative_name(name):
    if (not isinstance(name, str) or not name or '\x00' in name or '\\' in name
            or name.startswith('/') or '..' in name.split('/') or ':' in name
            or len(name.encode('utf-8')) > 4096 or len(name.split('/')) > 64):
        raise ValueError('Invalid relative payload path')
    canonical = str(PurePosixPath(name))
    if canonical == '.':
        raise ValueError('Payload path must name a file')
    return canonical


def disk_reserve(*paths, additional=0):
    for path in paths:
        if shutil.disk_usage(path).free < RESERVE + additional:
            raise ValueError('The 2 GiB disk reserve would be crossed: ' + str(path))


def native_runtime(runtime, machine=None):
    machine = (machine or platform.machine()).lower()
    if runtime not in MACHINES or machine not in MACHINES[runtime][2]:
        raise ValueError('Runtime does not match the native machine: ' + machine)


def elf_machine(path, runtime):
    with Path(path).open('rb') as stream:
        header = stream.read(20)
    if len(header) != 20 or header[:4] != b'\x7fELF' or header[4:6] != b'\x02\x01':
        raise ValueError('Expected a little-endian ELF64 executable')
    machine = struct.unpack('<H', header[18:20])[0]
    if machine != MACHINES[runtime][1]:
        raise ValueError('ELF architecture differs from native declared runtime')
    return machine


def load_manifest(path, package_dir, runtime, source_commit):
    value = read_json(path)
    if (value.get('schema') != 'meowchan.linux-packages.v1' or value.get('runtime') != runtime
            or value.get('source_commit') != source_commit
            or not isinstance(value.get('version'), str) or not re.fullmatch(r'[0-9A-Za-z.+-]+', value['version'])
            or not SHA.fullmatch(str(value.get('apphost_sha256', '')))):
        raise ValueError('Package manifest schema/runtime/source/version/apphost binding is invalid')
    artifacts = value.get('artifacts')
    if not isinstance(artifacts, list) or len(artifacts) != 3:
        raise ValueError('Exactly three Linux distribution artifacts are required')
    found = set()
    for item in artifacts:
        flavor = item.get('flavor')
        if flavor not in ('bundled-vlc', 'system-vlc', 'appimage') or flavor in found:
            raise ValueError('Unexpected or duplicate artifact flavor')
        found.add(flavor)
        suffix = '.AppImage' if flavor == 'appimage' else '-' + flavor + '.tar.gz'
        expected_name = f'MeowChan-{value["version"]}-{runtime}' + suffix
        expected_member = 'usr/lib/meowchan/MeowChan' if flavor == 'appimage' else 'MeowChan'
        if (item.get('file') != expected_name or item.get('apphost_member') != expected_member
                or item.get('bundled') is not (flavor != 'system-vlc')
                or type(item.get('bytes')) is not int or not 0 < item['bytes'] <= MAX_ARTIFACT
                or not SHA.fullmatch(str(item.get('sha256', '')))):
            raise ValueError('Invalid artifact filename, mode, size or hash')
        files = item.get('payload_files')
        if not isinstance(files, dict) or not 1 <= len(files) <= MAX_ENTRIES:
            raise ValueError('A bounded complete payload file manifest is required')
        for name, digest in files.items():
            if relative_name(name) != name or not isinstance(digest, str) or not SHA.fullmatch(digest):
                raise ValueError('Payload manifest paths/hashes must be canonical')
        for name in files:
            if any(str(parent) in files for parent in PurePosixPath(name).parents):
                raise ValueError('Payload file is nested below another file')
        if files.get(expected_member) != value['apphost_sha256']:
            raise ValueError('Artifact apphost is not bound to the common apphost hash')
        if flavor == 'appimage' and 'AppRun' not in files:
            raise ValueError('AppImage must contain AppRun')
        archive = Path(package_dir) / item['file']
        if (archive.is_symlink() or not archive.is_file() or archive.stat().st_size != item['bytes']
                or sha256(archive) != item['sha256']):
            raise ValueError('Distribution artifact hash/size mismatch: ' + item['file'])
    return value


def payload_inventory(root):
    """No links/special files, bounded traversal, hashes of every physical file."""
    root = Path(root)
    if root.is_symlink() or not root.is_dir() or root.resolve(strict=True) != root:
        raise ValueError('Payload root must be a canonical regular directory')
    files, count, total = {}, 0, 0

    def visit(directory, depth=0):
        nonlocal count, total
        if depth > 64:
            raise ValueError('Payload path exceeds depth limit')
        with os.scandir(directory) as items:
            for entry in items:
                count += 1
                if count > MAX_ENTRIES:
                    raise ValueError('Payload exceeds entry limit')
                name = relative_name(Path(entry.path).relative_to(root).as_posix())
                details = entry.stat(follow_symlinks=False)
                if stat.S_ISDIR(details.st_mode):
                    visit(Path(entry.path), depth + 1)
                elif stat.S_ISREG(details.st_mode):
                    total += details.st_size
                    if details.st_size > MAX_FILE or total > MAX_PAYLOAD:
                        raise ValueError('Payload exceeds byte limits')
                    files[name] = sha256(Path(entry.path))
                else:
                    raise ValueError('Payload links and special files are forbidden: ' + name)
    visit(root)
    return {'files': files, 'sha256': fingerprint(files), 'bytes': total, 'entryCount': count}


def verify_payload(root, artifact, runtime):
    inventory = payload_inventory(root)
    if inventory['files'] != artifact['payload_files']:
        raise ValueError('Extracted payload differs from the artifact manifest')
    apphost = Path(root) / artifact['apphost_member']
    if not os.access(apphost, os.X_OK):
        raise ValueError('Packaged apphost is not executable')
    inventory['elfMachine'] = elf_machine(apphost, runtime)
    inventory['apphost'] = str(apphost)
    inventory['apphostSha256'] = inventory['files'][artifact['apphost_member']]
    return inventory


def inspect_tar(archive_path, artifact):
    """Validate the entire closed TAR before creating even one payload file."""
    files, sizes, directories, seen, total = {}, {}, set(), set(), 0
    with tarfile.open(archive_path, 'r|gz') as archive:
        for entry in archive:
            if entry.name in ('.', './') and entry.isdir():
                name = '.'
            else:
                name = relative_name(entry.name)
            if name in seen or len(seen) >= MAX_ENTRIES:
                raise ValueError('Duplicate or excessive archive entries')
            seen.add(name)
            if entry.isdir():
                if entry.size != 0:
                    raise ValueError('Archive directory has payload bytes')
                directories.add(name)
            elif entry.isfile() and not entry.issparse():
                if not 0 <= entry.size <= MAX_FILE:
                    raise ValueError('Archive member exceeds byte limit')
                total += entry.size
                if total > MAX_PAYLOAD:
                    raise ValueError('Archive exceeds total byte limit')
                digest, count = hashlib.sha256(), 0
                with archive.extractfile(entry) as stream:
                    for chunk in iter(lambda: stream.read(1024 ** 2), b''):
                        count += len(chunk)
                        if count > entry.size:
                            raise ValueError('Archive member exceeds declared size')
                        digest.update(chunk)
                if count != entry.size:
                    raise ValueError('Archive member is truncated')
                files[name] = digest.hexdigest()
                sizes[name] = entry.size
            else:
                raise ValueError('Archive links, sparse files and special entries are forbidden')
    if files != artifact['payload_files']:
        raise ValueError('Archive payload hashes differ from the package manifest')
    for name in seen:
        if any(str(parent) in files for parent in PurePosixPath(name).parents):
            raise ValueError('Archive entry is nested below a file')
    return {'bytes': total, 'files': files, 'sizes': sizes, 'directories': sorted(directories)}


def extract_tar(archive_path, artifact, root):
    expected = inspect_tar(archive_path, artifact)
    root = Path(root)
    if root.exists() or root.is_symlink():
        raise ValueError('Extraction requires a fresh destination')
    disk_reserve(root.parent, additional=expected['bytes'])
    root.mkdir(mode=0o700)
    seen, copied_total = set(), 0
    with tarfile.open(archive_path, 'r|gz') as archive:
        for entry in archive:
            name = '.' if entry.name in ('.', './') and entry.isdir() else relative_name(entry.name)
            if name in seen or len(seen) >= MAX_ENTRIES:
                raise ValueError('Archive entries changed after preflight')
            seen.add(name)
            target = root / name
            if entry.isdir():
                if name not in expected['directories'] or entry.size != 0:
                    raise ValueError('Archive directories changed after preflight')
                target.mkdir(parents=True, exist_ok=True)
            elif entry.isfile() and not entry.issparse() and name in expected['files']:
                target.parent.mkdir(parents=True, exist_ok=True)
                if entry.size != expected['sizes'][name]:
                    raise ValueError('Archive changed after preflight')
                digest, copied = hashlib.sha256(), 0
                with target.open('xb') as destination, archive.extractfile(entry) as source:
                    for chunk in iter(lambda: source.read(1024 ** 2), b''):
                        copied += len(chunk)
                        copied_total += len(chunk)
                        if copied > entry.size or copied_total > expected['bytes']:
                            raise ValueError('Archive expanded beyond its declared size')
                        disk_reserve(root, additional=len(chunk))
                        destination.write(chunk)
                        digest.update(chunk)
                if copied != entry.size or digest.hexdigest() != expected['files'][name]:
                    raise ValueError('Archive bytes changed after preflight')
                # Preserve execute permission; never package setuid/setgid/sticky bits.
                target.chmod(0o755 if entry.mode & 0o111 else 0o644)
                disk_reserve(root)
            else:
                raise ValueError('Archive changed after preflight')
    return expected


def descendants(process_id):
    parents = {}
    for directory in Path('/proc').iterdir():
        if directory.name.isdecimal():
            try:
                status = (directory / 'status').read_text()
                parent = int(re.search(r'^PPid:\s+(\d+)$', status, re.MULTILINE)[1])
                parents[int(directory.name)] = parent
            except (OSError, ValueError, TypeError):
                continue
    owned, pending = {process_id}, True
    while pending:
        added = {pid for pid, parent in parents.items() if parent in owned} - owned
        pending = bool(added)
        owned.update(added)
    return owned


def proc_stat(directory_fd):
    descriptor = os.open('stat', os.O_RDONLY | os.O_CLOEXEC, dir_fd=directory_fd)
    try:
        data = os.read(descriptor, 16385)
    finally:
        os.close(descriptor)
    if len(data) > 16384:
        raise ValueError('Process stat exceeds evidence bound')
    raw = data.decode('utf-8')
    # comm can itself contain spaces and closing parentheses.
    head, separator, tail = raw.rpartition(')')
    fields = tail.split()
    if not separator or len(fields) < 20:
        raise ValueError('Invalid process stat')
    return {'pid': int(head.split('(', 1)[0]), 'state': fields[0], 'ppid': int(fields[1]),
            'pgid': int(fields[2]), 'sid': int(fields[3]), 'starttime': int(fields[19]),
            'uid': os.fstat(directory_fd).st_uid}


def proc_executable(directory_fd):
    path = os.readlink('exe', dir_fd=directory_fd)
    details = os.stat('exe', dir_fd=directory_fd)
    return {'path': path, 'device': details.st_dev, 'inode': details.st_ino,
            'size': details.st_size, 'mtimeNs': details.st_mtime_ns}


def proc_tasks(directory_fd):
    task_fd = os.open('task', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC, dir_fd=directory_fd)
    try:
        names = os.listdir(task_fd)
        if len(names) > 4096:
            raise ValueError('Process task count exceeds evidence bound')
        states = {}
        for name in names:
            if not name.isdecimal():
                continue
            try:
                thread_fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC, dir_fd=task_fd)
            except FileNotFoundError:
                continue  # A task can exit while its group enters the stop.
            try:
                states[name] = proc_stat(thread_fd)['state']
            finally:
                os.close(thread_fd)
        return states
    finally:
        os.close(task_fd)


def same_process(first, second):
    return all(first[key] == second[key] for key in ('pid', 'starttime', 'pgid', 'sid', 'uid'))


def observe_apphosts(process_id, observed, artifact, runtime, work, fixed_root=None, *, attempts, deadline):
    """Authenticate one stopped, pinned process image; preserve every candidate failure."""
    for pid in descendants(process_id):
        directory_fd = pid_fd = executable_fd = None
        stopped = False
        attempt = None
        try:
            try:
                directory_fd = os.open(f'/proc/{pid}', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
                initial = proc_stat(directory_fd)
                initial_exe = proc_executable(directory_fd)
            except (FileNotFoundError, ProcessLookupError):
                continue  # Process exited before an image could be observed.
            except PermissionError as access_error:
                if pid not in observed:
                    continue  # No authenticated apphost identity exists for this PID.
                prior = observed[pid]
                attempt = {'pid': pid, 'priorAuthenticatedIdentity': prior['processIdentity'],
                           'priorAuthenticatedExecutable': prior['finalExecutable'],
                           'expectedHash': prior['exeSha256'], 'actualHash': None}
                raise ValueError('Authenticated apphost identity became unreadable: ' + str(access_error)) from access_error
            if pid in observed:
                prior = observed[pid]
                process_matches = same_process(initial, prior['processIdentity'])
                if not process_matches or initial_exe != prior['finalExecutable']:
                    attempt = {'pid': pid, 'initialIdentity': initial, 'initialExecutable': initial_exe,
                               'priorAuthenticatedIdentity': prior['processIdentity'],
                               'priorAuthenticatedExecutable': prior['finalExecutable'],
                               'expectedHash': prior['exeSha256'], 'actualHash': None}
                    if process_matches:
                        attempt['execTransitionObserved'] = True
                        raise ValueError('An authenticated apphost changed its executable image')
                    raise ValueError('An authenticated apphost PID was reused or changed process identity')
                continue
            if Path(initial_exe['path']).name != 'MeowChan':
                continue
            attempt = {'pid': pid, 'initialIdentity': initial, 'initialExecutable': initial_exe,
                       'expectedHash': artifact['payload_files'][artifact['apphost_member']],
                       'actualHash': None, 'passed': False, 'stopConfirmed': False}
            if initial['pid'] != pid or initial['uid'] != os.getuid():
                raise ValueError('Apphost candidate is not owned by the current runner UID')
            if not hasattr(os, 'pidfd_open') or not hasattr(signal, 'pidfd_send_signal'):
                raise ValueError('Pinned apphost observation requires Linux pidfd signal support')
            # The proc directory stays bound to this task even if its numeric PID is reused.
            # Open pidfd, then validate both the pinned directory and current numeric identity
            # before sending any signal. Never fall back to an unpinned os.kill(pid, ...).
            pid_fd = os.pidfd_open(pid)
            current_fd = os.open(f'/proc/{pid}', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            try:
                current = proc_stat(current_fd)
            finally:
                os.close(current_fd)
            pinned = proc_stat(directory_fd)
            attempt['identityBeforeSignal'] = pinned
            attempt['executableBeforeSignal'] = proc_executable(directory_fd)
            if not same_process(initial, pinned) or not same_process(initial, current) or pid not in descendants(process_id):
                raise ValueError('Apphost candidate identity/ownership changed before its pinned stop')
            if time.monotonic() >= deadline:
                raise ValueError('Existing command deadline expired before apphost stop')
            signal.pidfd_send_signal(pid_fd, signal.SIGSTOP)
            stopped = True
            stop_deadline = min(deadline, time.monotonic() + 1)
            while True:
                state = proc_stat(directory_fd)
                tasks = proc_tasks(directory_fd)
                attempt.update(stopIdentity=state, stopTaskStates=tasks)
                if not same_process(initial, state):
                    raise ValueError('Apphost process identity changed while entering its stop')
                if state['state'] == 'T' and tasks and all(value == 'T' for value in tasks.values()):
                    attempt['stopConfirmed'] = True
                    break
                if time.monotonic() >= stop_deadline:
                    raise ValueError('Apphost signal stop was not confirmed inside the existing command deadline')
                time.sleep(min(.005, max(0, stop_deadline - time.monotonic())))
            if time.monotonic() >= deadline:
                raise ValueError('Existing command deadline expired before apphost hash')
            stopped_exe = proc_executable(directory_fd)
            attempt['stoppedExecutable'] = stopped_exe
            executable_fd = os.open('exe', os.O_RDONLY | os.O_CLOEXEC, dir_fd=directory_fd)
            descriptor_stat = os.fstat(executable_fd)
            if (descriptor_stat.st_dev, descriptor_stat.st_ino, descriptor_stat.st_size, descriptor_stat.st_mtime_ns) != (
                    stopped_exe['device'], stopped_exe['inode'], stopped_exe['size'], stopped_exe['mtimeNs']):
                raise ValueError('Pinned executable descriptor differs from the confirmed stopped image')
            # Hash the already opened image, not a /proc/PID/exe path reopened after payload hashing.
            digest = hashlib.sha256()
            with os.fdopen(os.dup(executable_fd), 'rb') as stream:
                for chunk in iter(lambda: stream.read(1024 ** 2), b''):
                    digest.update(chunk)
            actual_hash = digest.hexdigest()
            attempt['actualHash'] = actual_hash
            if initial_exe != stopped_exe or attempt['executableBeforeSignal'] != stopped_exe:
                attempt['execTransitionObserved'] = True
                raise ValueError('Apphost executable changed before its confirmed stop; candidate not authenticated')
            executable = Path(stopped_exe['path'])
            if not executable.is_relative_to(work):
                raise ValueError('Observed apphost escaped owned writable work directory')
            root = executable
            for _ in PurePosixPath(artifact['apphost_member']).parts:
                root = root.parent
            if fixed_root is not None and root != fixed_root:
                raise ValueError('Executed apphost differs from the verified extracted path')
            if fixed_root is None and not root.is_relative_to(work / 'tmp'):
                raise ValueError('AppImage extract-and-run payload is outside owned TMPDIR')
            proof = verify_payload(root, artifact, runtime)
            attempt.update(payloadRoot=str(root), payloadSha256=proof['sha256'],
                           payloadBytes=proof['bytes'], expectedHash=proof['apphostSha256'])
            final = proc_stat(directory_fd)
            final_exe = proc_executable(directory_fd)
            final_tasks = proc_tasks(directory_fd)
            attempt.update(finalIdentity=final, finalExecutable=final_exe, finalTaskStates=final_tasks)
            if (not same_process(initial, final) or final['state'] != 'T' or not final_tasks
                    or any(value != 'T' for value in final_tasks.values()) or final_exe != stopped_exe):
                raise ValueError('Stopped apphost process/image identity changed during payload authentication')
            if actual_hash != proof['apphostSha256']:
                raise ValueError('Actually executed apphost bytes differ from verified payload')
            if time.monotonic() >= deadline:
                raise ValueError('Apphost authentication exceeded the existing command deadline')
            observed[pid] = {'pid': pid, 'executable': str(executable), 'exeSha256': actual_hash,
                             'payloadRoot': str(root), 'payloadSha256': proof['sha256'],
                             'payloadBytes': proof['bytes'], 'elfMachine': proof['elfMachine'],
                             'brieflyStoppedForPayloadHash': True, 'processIdentity': initial,
                             'stopConfirmed': True, 'stoppedIdentity': state, 'finalIdentity': final,
                             'stoppedExecutable': stopped_exe, 'finalExecutable': final_exe}
            attempt['passed'] = True
        except (OSError, ValueError) as error:
            if attempt is not None:
                attempt.update(passed=False, error=str(error))
                if directory_fd is not None:
                    try:
                        attempt.update(failureIdentity=proc_stat(directory_fd),
                                       failureExecutable=proc_executable(directory_fd))
                    except (OSError, ValueError) as snapshot_error:
                        attempt['failureSnapshotError'] = str(snapshot_error)
                else:
                    attempt['failureSnapshotError'] = 'Process directory could not be opened'
            raise
        finally:
            try:
                if stopped:
                    try:
                        signal.pidfd_send_signal(pid_fd, signal.SIGCONT)
                        if attempt is not None:
                            attempt['resumeSignalSent'] = True
                    except ProcessLookupError:
                        if attempt is not None:
                            attempt['processExitedBeforeResume'] = True
                    except OSError as resume_error:
                        if attempt is not None:
                            attempt.update(passed=False, resumeSignalError=str(resume_error))
                        raise
            finally:
                for descriptor in (executable_fd, pid_fd, directory_fd):
                    if descriptor is not None:
                        os.close(descriptor)
                if attempt is not None:
                    attempts.append(attempt)

def stop_process(process):
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=3)
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=10)


class BoundedLog:
    """Drain stdout without allowing a fast child to exhaust evidence storage."""
    def __init__(self, source, path):
        self.source, self.path = source, Path(path)
        self.exceeded, self.error = False, None
        self.thread = threading.Thread(target=self.collect, daemon=True)
        self.thread.start()

    def collect(self):
        count = 0
        try:
            with self.path.open('xb') as destination:
                for data in iter(lambda: self.source.read(65536), b''):
                    keep = data[:max(0, MAX_LOG - count)]
                    if len(keep) != len(data):
                        self.exceeded = True
                    if keep:
                        disk_reserve(self.path.parent, additional=len(keep))
                        destination.write(keep)
                        count += len(keep)
        except (OSError, ValueError) as error:
            self.error = str(error)
        finally:
            self.source.close()

    def finish(self):
        self.thread.join(timeout=5)
        if self.thread.is_alive():
            raise ValueError('Owned command output stream did not close')
        if self.error:
            raise ValueError('Command output capture failed: ' + self.error)
        if self.exceeded:
            raise ValueError('Command output exceeded its byte limit')


def run_command(command, log_path, *, timeout, cwd=None, environment=None, observer=None, disk_paths=()):
    result = {'command': [str(value) for value in command], 'passed': False, 'timedOut': False}
    process, capture = None, None
    started = time.monotonic()
    try:
        if Path(log_path).exists() or Path(log_path).is_symlink():
            raise ValueError('Command log requires a fresh destination')
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, cwd=cwd,
                                   env=environment, start_new_session=True)
        capture = BoundedLog(process.stdout, log_path)
        while process.poll() is None:
            if observer:
                observer(process.pid, started + timeout)
            if time.monotonic() - started > timeout:
                result['timedOut'] = True
                raise ValueError('Bounded command timeout exceeded')
            if capture.exceeded or capture.error:
                raise ValueError('Command output exceeded its byte limit or could not be retained')
            disk_reserve(*disk_paths, additional=128 * 1024 ** 2)
            time.sleep(0.05)
        result['exitCode'] = process.returncode
        disk_reserve(*disk_paths)
        if process.returncode != 0:
            raise ValueError('Command exited unsuccessfully: ' + str(process.returncode))
        result['passed'] = True
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        result['error'] = str(error)
    finally:
        try:
            if process is not None:
                stop_process(process)
        finally:
            if capture is not None:
                try:
                    capture.finish()
                except (OSError, ValueError) as error:
                    result.update(passed=False, error=str(error))
    result['elapsedSeconds'] = round(time.monotonic() - started, 3)
    return result


def system_vlc_inventory():
    packages = subprocess.run(['dpkg-query', '-W', '-f=${binary:Package}\t${db:Status-Status}\t${Version}\n'],
                              capture_output=True, text=True, check=True, timeout=20).stdout
    selected = sorted(line for line in packages.splitlines() if '\tinstalled\t' in line
                      and re.match(r'^(?:libvlc|vlc(?:[:\t-]))', line))
    cache = subprocess.run(['/sbin/ldconfig', '-p'], capture_output=True, text=True, check=True, timeout=20).stdout
    libraries = sorted(line.strip() for line in cache.splitlines() if re.search(r'\blibvlc(?:core)?\.so', line))
    physical = set()
    for location in ('/usr/lib', '/usr/local/lib', '/lib'):
        base = Path(location).resolve()
        if base.is_dir():
            for pattern in ('libvlc.so*', 'libvlccore.so*'):
                physical.update(str(path) for path in base.rglob(pattern))
    return {'packages': selected, 'loaderCache': libraries, 'physicalLibraries': sorted(physical)}


def assert_vlc(inventory, present):
    if present:
        if not inventory['packages'] or not any('libvlc.so' in name for name in inventory['loaderCache']):
            raise ValueError('System-VLC container has no installed discoverable LibVLC')
    elif any(inventory.values()):
        raise ValueError('No-VLC container contains a system VLC installation')


def validate_container_environment(runtime, owner, work, evidence):
    native_runtime(runtime)
    os_release = dict(line.split('=', 1) for line in Path('/etc/os-release').read_text().splitlines() if '=' in line)
    if os_release.get('ID', '').strip('"') != 'ubuntu' or os_release.get('VERSION_ID', '').strip('"') != '24.04':
        raise ValueError('Acceptance requires an actual Ubuntu 24.04 image')
    if (os.getuid() == 0 or not Path('/.dockerenv').exists()
            or os.environ.get('MEOWCHAN_DISTRIBUTION_OWNER') != owner):
        raise ValueError('Container acceptance requires its owned non-root Docker context')
    if sorted(path.name for path in Path('/sys/class/net').iterdir()) != ['lo']:
        raise ValueError('Acceptance container must have no network interfaces except loopback')
    for path in (work, evidence):
        if (path.resolve(strict=True) != path or path.is_symlink() or (path / '.owner').is_symlink()
                or (path / '.owner').stat().st_size != 32 or (path / '.owner').read_text() != owner):
            raise ValueError('Work/evidence ownership mismatch')
    if (os.environ.get('HOME') != str(work / 'home') or os.environ.get('TMPDIR') != str(work / 'tmp')
            or Path(tempfile.gettempdir()) != work / 'tmp'):
        raise ValueError('HOME and temporary roots must be private owned directories')
    disk_reserve(work, evidence)
    return {'osRelease': os_release, 'machine': platform.machine(), 'uid': os.getuid(),
            'networkInterfaces': ['lo'], 'home': os.environ['HOME'], 'temp': tempfile.gettempdir()}


def validate_positive_report(path, kind, runtime, source_commit, expected_apphost_hash, expected_version):
    report = read_json(path)
    if report.get('passed') is not True or report.get('isolatedDesktop') is not True:
        raise ValueError(kind + ' did not pass on the private desktop')
    if report.get('apphostSha256') != expected_apphost_hash:
        raise ValueError(kind + ' runner executable hash mismatch')
    if kind == 'workflow':
        if (report.get('completed') is not True or report.get('declaredRuntime') != runtime
                or report.get('declaredSourceCommit') != source_commit
                or report.get('selectedScales') != [1, 2] or report.get('fullScaleCoverage') is not True):
            raise ValueError('Workflow did not prove declared source/runtime and both scales')
        scales = report.get('scales', [])
        if len(scales) != 2:
            raise ValueError('Expected two workflow scales')
        for scale in scales:
            phases = scale.get('phases', [])
            if (scale.get('passed') is not True or [phase.get('phase') for phase in phases]
                    != ['download', 'restore', 'removed', 'shutdown']
                    or any(phase.get('passed') is not True for phase in phases)):
                raise ValueError('Every workflow phase must pass at each scale')
            if any(phase.get('assemblyInformationalVersion') != expected_version + '+' + source_commit
                   for phase in phases):
                raise ValueError('Executed managed build does not identify the exact release version/source commit')
    else:
        fixtures = report.get('fixtures', [])
        if len(fixtures) != 2 or any(row.get('passed') is not True for row in fixtures):
            raise ValueError('Both pinned VP8 and VP9 fixtures must pass')
    return report


def run_positive(launcher, root, artifact, manifest, args, label, extra_env=None):
    destination = args.evidence / label
    destination.mkdir()
    results = {}
    for kind, filename, required_count in [('playback', 'apphost-playback-smoke.py', 2),
                                            ('workflow', 'apphost-workflow-smoke.py', 8)]:
        output = destination / kind
        command = [sys.executable, '/repo/scripts/' + filename, str(launcher), '--evidence-dir', str(output)]
        if kind == 'workflow':
            command += ['--source-commit', args.source_commit, '--runtime', args.runtime, '--scale', '1', '--scale', '2']
            if artifact['flavor'] != 'appimage':
                command += ['--archive', str(args.package_dir / artifact['file']), '--archive-member', artifact['apphost_member']]
        observed, attempts = {}, []
        environment = dict(os.environ)
        environment.update(extra_env or {})
        observer = lambda pid, deadline: observe_apphosts(pid, observed, artifact, args.runtime, args.work, root,
                                                        attempts=attempts, deadline=deadline)
        result = run_command(command, destination / (kind + '.log'), timeout=1100 if kind == 'workflow' else 300,
                             environment=environment, observer=observer, disk_paths=(args.work, args.evidence))
        result['observedApphosts'] = list(observed.values())
        result['apphostObservationAttempts'] = attempts
        result['dispatchEnvironment'] = extra_env or {}
        results[kind] = result
        write_json(destination / 'dispatch.json', results)
        if not result['passed']:
            raise ValueError(label + ' ' + kind + ': ' + result.get('error', 'failed'))
        report = validate_positive_report(output / (kind + '.json'), kind, args.runtime, args.source_commit,
                                          sha256(launcher), manifest['version'])
        if len(observed) != required_count:
            raise ValueError('Did not observe every real apphost process: ' + label + ' ' + kind)
        if kind == 'workflow':
            metrics_pids = {phase['productProcessId'] for scale in report['scales'] for phase in scale['phases']}
            if metrics_pids != set(observed):
                raise ValueError('Workflow metrics process IDs differ from the observed real executables')
        result['reportSha256'] = sha256(output / (kind + '.json'))
        if root is not None:
            result['afterPayloadSha256'] = verify_payload(root, artifact, args.runtime)['sha256']
        write_json(destination / 'dispatch.json', results)
    return results


def run_negative(apphost, artifact, args, kind, label):
    output = args.evidence / label
    command = [sys.executable, '/repo/scripts/apphost-video-error-smoke.py', str(apphost),
               '--expected-kind', kind, '--runtime', args.runtime, '--source-commit', args.source_commit,
               '--expected-apphost-sha256', artifact['payload_files'][artifact['apphost_member']], '--evidence-dir', str(output)]
    command += ['--archive', str(args.package_dir / artifact['file']), '--archive-member', artifact['apphost_member']]
    result = run_command(command, args.evidence / (label + '.log'), timeout=150, disk_paths=(args.work, args.evidence))
    if not result['passed']:
        raise ValueError('Video error scenario failed: ' + result.get('error', 'unknown'))
    report = read_json(output / 'video-error.json')
    if (report.get('passed') is not True or report.get('expectedKind') != kind or report.get('observedKind') != kind
            or report.get('declaredSourceCommit') != args.source_commit or report.get('declaredRuntime') != args.runtime
            or report.get('isolatedDesktop') is not True or report.get('result', {}).get('exitCode') != 2
            or report.get('result', {}).get('profileAbsent') is not True):
        raise ValueError('Video error evidence must prove actual hint, normal exit 2 and absent profile')
    result['reportSha256'] = sha256(output / 'video-error.json')
    return result


def run_layout_checks(root, artifact, args):
    """Both checkers inspect package layout only, including missing-system-VLC."""
    base = root / PurePosixPath(artifact['apphost_member']).parent
    results = {}
    for label, filename in [('desktop', 'check-desktop-package.py'), ('native-video', 'check-video-native.py')]:
        command = [sys.executable, '/repo/scripts/' + filename, str(base), args.runtime]
        result = run_command(command, args.evidence / (label + '-layout.log'), timeout=120,
                             disk_paths=(args.work, args.evidence))
        results[label] = result
        if not result['passed']:
            raise ValueError('Extracted package ' + label + ' layout failed: ' + result.get('error', 'failed'))
    return results


def damage_bundle(root, artifact, runtime):
    base = Path(root) / PurePosixPath(artifact['apphost_member']).parent
    candidates = [name for name in artifact['payload_files']
                  if name.startswith(f'libvlc/{runtime}/') and PurePosixPath(name).name.startswith('libvlc.so')]
    if not candidates:
        raise ValueError('Bundled artifact lacks the declared native VLC library to damage')
    name = min(candidates, key=len)
    target = base / name
    original = sha256(target)
    target.unlink()
    if target.exists() or payload_inventory(root)['files'] != {key: value for key, value in artifact['payload_files'].items() if key != name}:
        raise ValueError('Damage operation must remove exactly one declared bundled VLC file')
    return {'kind': 'removed-declared-native-library', 'path': name, 'priorSha256': original}


def container_main(args):
    report = {'schemaVersion': 1, 'scenario': args.scenario, 'passed': False, 'completed': False,
              'sourceCommit': args.source_commit, 'runtime': args.runtime,
              'scope': 'Actual production apphosts on private Xvfb/DBus; HTTP/external handoff fixture boundaries remain. '
                       'AppImage AppRun/extract-and-run only, no FUSE or physical audio/desktop claim.'}
    before, owned = None, False
    try:
        report['container'] = validate_container_environment(args.runtime, args.owner, args.work, args.evidence)
        owned = True
        manifest = load_manifest(args.manifest, args.package_dir, args.runtime, args.source_commit)
        flavor = 'system-vlc' if args.scenario == 'missing-system-vlc' else args.scenario
        artifact = next(item for item in manifest['artifacts'] if item['flavor'] == flavor)
        archive = args.package_dir / artifact['file']
        report['artifact'] = artifact
        report['manifestSha256'] = sha256(args.manifest)
        before = system_vlc_inventory()
        report['systemVlcBefore'] = before
        assert_vlc(before, args.scenario == 'system-vlc')
        root = args.work / 'payload'
        if flavor == 'appimage':
            extraction = args.work / 'extraction'
            extraction.mkdir()
            elf_machine(archive, args.runtime)
            if not os.access(archive, os.X_OK):
                raise ValueError('AppImage artifact must be executable')
            report['extraction'] = run_command([str(archive), '--appimage-extract'], args.evidence / 'extract.log',
                                               timeout=180, cwd=extraction, disk_paths=(args.work, args.evidence))
            if not report['extraction']['passed']:
                raise ValueError('AppImage runtime extraction failed')
            root = extraction / 'squashfs-root'
        else:
            extract_tar(archive, artifact, root)
        report['payloadBefore'] = verify_payload(root, artifact, args.runtime)
        report['layoutChecks'] = run_layout_checks(root, artifact, args)
        if args.scenario == 'missing-system-vlc':
            report['negative'] = run_negative(root / artifact['apphost_member'], artifact, args, 'SystemBackendUnavailable', 'missing-video')
        else:
            launcher = root / ('AppRun' if flavor == 'appimage' else artifact['apphost_member'])
            if not os.access(launcher, os.X_OK):
                raise ValueError('Packaged launcher is not executable')
            report['positive'] = run_positive(launcher, root, artifact, manifest, args, 'extracted')
            if flavor == 'appimage':
                report['extractAndRun'] = run_positive(archive, None, artifact, manifest, args, 'extract-and-run',
                                                       {'APPIMAGE_EXTRACT_AND_RUN': '1'})
        report['payloadAfter'] = verify_payload(root, artifact, args.runtime)
        if args.scenario == 'bundled-vlc':
            report['damage'] = damage_bundle(root, artifact, args.runtime)
            report['negative'] = run_negative(root / artifact['apphost_member'], artifact, args, 'BundleDamaged', 'damaged-video')
        if sha256(archive) != artifact['sha256'] or sha256(args.manifest) != report['manifestSha256']:
            raise ValueError('Artifact or package manifest changed during acceptance')
        report['artifactVerifiedAfter'] = True
        report['passed'] = True
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report['error'] = str(error)
    finally:
        try:
            after = system_vlc_inventory()
            report['systemVlcAfter'] = after
            assert_vlc(after, args.scenario == 'system-vlc')
            if before != after:
                raise ValueError('System VLC inventory changed during acceptance')
            report['systemVlcVerifiedBeforeAndAfter'] = True
            disk_reserve(args.work, args.evidence)
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            report.update(passed=False, finalCheckError=str(error))
        report['completed'] = True
        if owned:
            write_json(args.evidence / 'scenario.json', report)
    print(json.dumps({'scenario': args.scenario, 'passed': report['passed'], 'error': report.get('error')}), flush=True)
    return 0 if report['passed'] else 1


def docker_json(command):
    result = subprocess.run(['docker', *command], capture_output=True, text=True, check=True, timeout=30)
    if len(result.stdout) > MAX_JSON:
        raise ValueError('Docker metadata exceeds byte limit')
    return json.loads(result.stdout)


def local_docker(runtime, image_names):
    # Docker's explicit context wins over DOCKER_HOST. Inspect the same endpoint
    # that subsequent CLI calls will actually use, never a misleading env alias.
    selected_context = os.environ.get('DOCKER_CONTEXT')
    endpoint = None if selected_context else os.environ.get('DOCKER_HOST')
    if not endpoint:
        context = docker_json(['context', 'inspect', *([selected_context] if selected_context else [])])
        if len(context) != 1:
            raise ValueError('Expected exactly one Docker context')
        endpoint = context[0]['Endpoints']['docker']['Host']
    if not endpoint.startswith('unix://'):
        raise ValueError('Acceptance requires a local Unix-socket Docker engine')
    info = docker_json(['info', '--format', '{{json .}}'])
    native_runtime(runtime, info['Architecture'])
    images = {}
    for key, name in image_names.items():
        items = docker_json(['image', 'inspect', name])
        if len(items) != 1 or items[0].get('Os') != 'linux' or items[0].get('Architecture') != MACHINES[runtime][0]:
            raise ValueError('Docker image does not match native Linux runtime')
        identifier = items[0].get('Id', '')
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', identifier):
            raise ValueError('Docker image ID must be immutable SHA256')
        images[key] = {'requested': name, 'id': identifier, 'architecture': items[0]['Architecture']}
    return {'endpoint': endpoint, 'architecture': info['Architecture'], 'images': images}


def docker_command(args, image_id, name, owner, work, evidence, scripts, fixtures):
    command = ['docker', 'run', '--pull=never', '--name', name, '--label', 'meowchan.owner=' + owner,
               '--network=none', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges',
               '--pids-limit=256', '--memory=6g', '--cpus=4', '--shm-size=256m',
               '--user', f'{os.getuid()}:{os.getgid()}', '--tmpfs', '/tmp:rw,nosuid,noexec,size=64m',
               '--env', 'HOME=/work/home', '--env', 'TMPDIR=/work/tmp',
               '--env', 'XDG_RUNTIME_DIR=/work/runtime', '--env', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR=/work/dotnet-bundle',
               '--env', 'MEOWCHAN_DISTRIBUTION_OWNER=' + owner, '--env', 'LANG=C.UTF-8',
               '--env', 'PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
               '--workdir=/work']
    for source, target, readonly in [(scripts, '/repo/scripts', True), (fixtures, '/repo/tests/Fixtures', True),
                                      (args.package_dir, '/packages', True), (work, '/work', False), (evidence, '/evidence', False)]:
        if ',' in str(source):
            raise ValueError('Docker mount source cannot contain a comma')
        command += ['--mount', f'type=bind,source={source},target={target}' + (',readonly' if readonly else '')]
    command += [image_id, 'python3', '/repo/scripts/linux-distribution-gates.py', '_container',
                '--package-dir', '/packages', '--manifest', '/packages/' + args.manifest.name,
                '--source-commit', args.source_commit, '--runtime', args.runtime,
                '--evidence-dir', '/evidence', '--work-dir', '/work', '--owner', owner, '--scenario', args.scenario]
    return command


def remove_owned_container(name, owner):
    result = subprocess.run(['docker', 'inspect', '--format', '{{json .Config.Labels}}', name],
                            capture_output=True, text=True, timeout=20)
    if result.returncode:
        # An inspect failure alone does not prove absence (the engine may be down).
        check = subprocess.run(['docker', 'ps', '--all', '--quiet', '--filter', 'name=^/' + name + '$'],
                               capture_output=True, text=True, check=True, timeout=20)
        if check.stdout.strip():
            raise ValueError('Container exists but its ownership cannot be inspected; retaining work')
        return True
    if json.loads(result.stdout).get('meowchan.owner') != owner:
        raise ValueError('Refusing cleanup of a container without this invocation owner token')
    subprocess.run(['docker', 'rm', '--force', name], check=True, stdout=subprocess.DEVNULL, timeout=30)
    return True


def cleanup_work(work, owner, container_removed):
    if (not container_removed or work.is_symlink() or work.resolve(strict=True) != work
            or (work / '.owner').is_symlink() or (work / '.owner').read_text() != owner):
        raise ValueError('Cleanup ownership/container absence is uncertain; retaining work: ' + str(work))
    shutil.rmtree(work)


def host_main(args):
    if sys.platform != 'linux' or os.getuid() == 0:
        raise ValueError('Host acceptance requires non-root native Linux CI')
    native_runtime(args.runtime)
    args.package_dir = args.package_dir.resolve(strict=True)
    args.manifest = args.manifest.resolve(strict=True)
    if args.manifest.parent != args.package_dir or args.manifest.name != args.runtime + '-package-manifest.json':
        raise ValueError('Manifest must be the runtime manifest directly inside --package-dir')
    disk_reserve(args.package_dir, tempfile.gettempdir(), args.evidence.parent)
    manifest = load_manifest(args.manifest, args.package_dir, args.runtime, args.source_commit)
    docker = local_docker(args.runtime, {'no-vlc': args.no_vlc_image, 'system-vlc': args.system_vlc_image})
    scripts = Path(__file__).resolve().parent
    fixtures = scripts.parent / 'tests/Fixtures'
    if not fixtures.is_dir():
        raise ValueError('Pinned playback fixture directory is missing')
    args.evidence.mkdir(parents=True, exist_ok=False)
    args.evidence = args.evidence.resolve(strict=True)
    owner = uuid.uuid4().hex
    (args.evidence / '.owner').write_text(owner)
    report = {'schemaVersion': 1, 'sourceCommit': args.source_commit, 'runtime': args.runtime,
              'manifestSha256': sha256(args.manifest), 'docker': docker,
              'hostMachine': platform.machine(), 'minimumDiskReserveBytes': RESERVE,
              'scope': 'Native Ubuntu 24.04 containers; real apphosts and private Xvfb/DBus. '
                       'AppImage AppRun and extract-and-run, no FUSE/physical-desktop/audio claim.',
              'scenarios': [], 'passed': False, 'completed': False}
    try:
        for scenario in SCENARIOS:
            args.scenario = scenario
            evidence = args.evidence / scenario
            evidence.mkdir()
            (evidence / '.owner').write_text(owner)
            work = Path(tempfile.mkdtemp(prefix='meowchan-linux-gates-')).resolve(strict=True)
            try:
                (work / '.owner').write_text(owner)
                for child in ('home', 'tmp', 'runtime', 'dotnet-bundle'):
                    (work / child).mkdir(mode=0o700)
                name = 'meowchan-dist-' + owner + '-' + scenario
                image = docker['images']['system-vlc' if scenario == 'system-vlc' else 'no-vlc']['id']
                command = docker_command(args, image, name, owner, work, evidence, scripts, fixtures)
                result = {'scenario': scenario, 'imageId': image, 'passed': False}
                try:
                    result['execution'] = run_command(command, evidence / 'container.log', timeout=args.timeout,
                                                       disk_paths=(work, args.evidence))
                    if not result['execution']['passed']:
                        raise ValueError(result['execution'].get('error', 'Container acceptance failed'))
                    proof = read_json(evidence / 'scenario.json')
                    if (proof.get('passed') is not True or proof.get('completed') is not True
                            or proof.get('scenario') != scenario or proof.get('sourceCommit') != args.source_commit
                            or proof.get('runtime') != args.runtime or proof.get('manifestSha256') != report['manifestSha256']
                            or proof.get('systemVlcVerifiedBeforeAndAfter') is not True
                            or proof.get('artifactVerifiedAfter') is not True):
                        raise ValueError('Container did not produce matching complete distribution proof')
                    result.update(passed=True, reportSha256=sha256(evidence / 'scenario.json'))
                except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
                    result['error'] = str(error)
                finally:
                    # Never delete an active container's tree or an unowned path.
                    try:
                        removed = remove_owned_container(name, owner)
                        cleanup_work(work, owner, removed)
                        result['ownedCleanupCompleted'] = True
                    except (OSError, ValueError, subprocess.SubprocessError) as error:
                        result.update(passed=False, cleanupError=str(error), retainedWorkDirectory=str(work))
                report['scenarios'].append(result)
                write_json(args.evidence / 'linux-distribution-gates.json', report)
                disk_reserve(args.evidence, tempfile.gettempdir())
            except BaseException:
                # Unexpected interruption/early setup failure preserves the owned
                # path unless the guarded cleanup above already completed.
                if work.exists():
                    report['retainedWorkDirectory'] = str(work)
                raise
        load_manifest(args.manifest, args.package_dir, args.runtime, args.source_commit)
        if sha256(args.manifest) != report['manifestSha256']:
            raise ValueError('Package manifest changed during acceptance')
        report['passed'] = all(row['passed'] for row in report['scenarios'])
    finally:
        report['completed'] = True
        write_json(args.evidence / 'linux-distribution-gates.json', report)
    print(json.dumps({'passed': report['passed'], 'report': str(args.evidence / 'linux-distribution-gates.json')}), flush=True)
    return 0 if report['passed'] else 1


def parse_args(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    internal = bool(argv and argv[0] == '_container')
    if internal:
        argv.pop(0)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--package-dir', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--runtime', choices=tuple(MACHINES), required=True)
    parser.add_argument('--evidence-dir', dest='evidence', type=Path, required=True)
    if internal:
        parser.add_argument('--work-dir', dest='work', type=Path, required=True)
        parser.add_argument('--owner', required=True)
        parser.add_argument('--scenario', choices=SCENARIOS, required=True)
    else:
        parser.add_argument('--no-vlc-image', required=True)
        parser.add_argument('--system-vlc-image', required=True)
        parser.add_argument('--timeout', type=float, default=2400)
    args = parser.parse_args(argv)
    args.internal = internal
    if not COMMIT.fullmatch(args.source_commit):
        parser.error('--source-commit must be a full lowercase 40-character SHA')
    if internal and not OWNER.fullmatch(args.owner):
        parser.error('--owner must be the invocation token')
    if not internal and (not math.isfinite(args.timeout) or not 0 < args.timeout <= 3600):
        parser.error('--timeout must be positive and at most 3600 seconds per container')
    return args


def main(argv=None):
    args = parse_args(argv)
    return container_main(args) if args.internal else host_main(args)


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
    sys.exit(main())
