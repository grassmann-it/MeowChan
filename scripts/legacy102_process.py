#!/usr/bin/env python3
"""Small, bounded subprocess support for the legacy 1.0.2 probe runner.

The runner only needs one captured stream, so stdout and stderr are merged into
one pipe.  The pipe is drained while the child is alive, while ``_TailBuffer``
keeps only the final 2 MiB.  This is intentionally not a general process
supervisor: POSIX process groups are the strong descendant-cleanup mechanism;
Windows uses the stdlib/Win32 ``PeekNamedPipe`` polling path plus
``taskkill /T`` best effort, and retains the normal Windows process-tree
limitations.
"""
from __future__ import annotations

import errno
import math
import os
from pathlib import Path
import selectors
import signal
import subprocess
import time
from typing import Final


MAX_TAIL_BYTES: Final = 2 * 1024 * 1024
_READ_CHUNK_BYTES: Final = 64 * 1024
_MAX_READS_PER_TURN: Final = 16
_POLL_INTERVAL_SECONDS: Final = 0.05
_CLEANUP_GRACE_SECONDS: Final = 0.20
_POST_EXIT_DRAIN_SECONDS: Final = 0.20
_WINDOWS_PEEK_NAMED_PIPE = None


class CommandError(RuntimeError):
    """Base class for failures raised by :func:`run_bounded_command`."""


class CommandFailed(CommandError):
    """The child exited with a non-zero status."""

    def __init__(self, label: str, returncode: int, log: Path, output: str):
        self.label = label
        self.returncode = returncode
        self.log = log
        self.output = output
        super().__init__(f"{label} failed with exit code {returncode}; see {log}")


class CommandTimedOut(CommandError):
    """The monotonic command deadline expired."""

    def __init__(self, label: str, timeout_seconds: float, log: Path, output: str):
        self.label = label
        self.timeout_seconds = timeout_seconds
        self.log = log
        self.output = output
        super().__init__(f"{label} exceeded the {timeout_seconds:g}s timeout; see {log}")


class CommandUnavailable(CommandError):
    """The command or its required local process facilities could not start."""


class _TailBuffer:
    """Keep a byte tail without ever retaining the complete child output."""

    __slots__ = ("_data",)

    def __init__(self) -> None:
        self._data = bytearray()

    def append(self, data: bytes) -> None:
        if not data:
            return
        if len(data) >= MAX_TAIL_BYTES:
            self._data[:] = data[-MAX_TAIL_BYTES:]
            return
        excess = len(self._data) + len(data) - MAX_TAIL_BYTES
        if excess > 0:
            del self._data[:excess]
        self._data.extend(data)

    def bytes(self) -> bytes:
        return bytes(self._data)


def _remaining(deadline: float) -> float:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        return 0.0
    return remaining


def _validate_arguments(arguments: list[str]) -> None:
    if not isinstance(arguments, list) or any(not isinstance(item, str) for item in arguments):
        raise TypeError("Child commands must be argument lists of strings.")
    if not arguments:
        raise ValueError("Child command argument list must not be empty.")


def _write_log(log: Path, output: bytes) -> None:
    # The log is deliberately the same bounded tail returned to the caller.
    # This avoids a second unbounded sink while retaining the useful end of a
    # compiler/runtime diagnostic.
    log.write_bytes(output)


def _decode(output: bytes) -> str:
    return output.decode("utf-8", errors="replace")


def _read_nonblocking(fd: int, capture: _TailBuffer) -> tuple[bool, bool]:
    """Read ready bytes from a non-blocking fd.

    Returns ``(eof, made_progress)``.  A non-blocking read is important during
    cleanup: a descendant may have inherited the pipe even after the direct
    child has exited.
    """
    eof = False
    made_progress = False
    for _ in range(_MAX_READS_PER_TURN):
        try:
            data = os.read(fd, _READ_CHUNK_BYTES)
        except BlockingIOError:
            break
        except OSError as error:
            # EIO is how some POSIX platforms report a closed pipe.  Treat it
            # like EOF, but do not hide unrelated read failures.
            if getattr(error, "errno", None) == errno.EIO:
                eof = True
                break
            raise
        if not data:
            eof = True
            break
        capture.append(data)
        made_progress = True
    return eof, made_progress


def _drain_after_exit(fd: int, capture: _TailBuffer, deadline: float | None = None) -> None:
    """Drain currently available bytes, but never wait for descendant EOF."""
    ends = time.monotonic() + _POST_EXIT_DRAIN_SECONDS
    if deadline is not None:
        ends = min(ends, deadline)
    while time.monotonic() < ends:
        eof, made_progress = _read_nonblocking(fd, capture)
        if eof or not made_progress:
            return


def _wait_posix(process: subprocess.Popen[bytes], capture: _TailBuffer, deadline: float) -> int:
    """Wait and drain a POSIX pipe without waiting for inherited descendant fds."""
    assert process.stdout is not None
    fd = process.stdout.fileno()
    os.set_blocking(fd, False)
    selector = selectors.DefaultSelector()
    registered = True
    selector.register(fd, selectors.EVENT_READ)
    try:
        while True:
            remaining = _remaining(deadline)
            if remaining <= 0:
                raise CommandTimedOut("command", 0.0, Path("."), "")
            returncode = process.poll()
            if returncode is not None:
                # The direct child is reaped by poll().  Do not wait for EOF:
                # a grandchild can retain the inherited write end forever.
                _drain_after_exit(fd, capture, deadline)
                return returncode

            events = selector.select(min(_POLL_INTERVAL_SECONDS, remaining))
            if events:
                eof, _ = _read_nonblocking(fd, capture)
                if eof and registered:
                    selector.unregister(fd)
                    registered = False
            elif not registered:
                # An EOF while the direct child is still running can happen
                # when a wrapper closes its descriptor early.  Do not turn the
                # empty selector into a busy loop while waiting for exit.
                time.sleep(min(_POLL_INTERVAL_SECONDS, remaining))
            # poll and the monotonic deadline are checked again on every turn,
            # including after a noisy child filled the pipe.
    finally:
        if registered:
            try:
                selector.unregister(fd)
            except (KeyError, ValueError):
                pass
        selector.close()


def _windows_pipe_available(fd: int) -> int | None:
    """Return ready bytes, or None when the Windows pipe reached EOF.

    ``selectors`` cannot poll anonymous Windows pipes.  ``PeekNamedPipe`` is a
    bounded, non-blocking stdlib/Win32 option; it lets the main thread use
    ``os.read`` only when bytes are ready, avoiding a reader thread that could
    remain blocked on a grandchild-owned write end.
    """
    import ctypes
    import msvcrt
    from ctypes import wintypes

    global _WINDOWS_PEEK_NAMED_PIPE
    if _WINDOWS_PEEK_NAMED_PIPE is None:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        peek = kernel32.PeekNamedPipe
        peek.argtypes = [
            wintypes.HANDLE,
            wintypes.LPVOID,
            wintypes.DWORD,
            ctypes.POINTER(wintypes.DWORD),
            ctypes.POINTER(wintypes.DWORD),
            ctypes.POINTER(wintypes.DWORD),
        ]
        peek.restype = wintypes.BOOL
        _WINDOWS_PEEK_NAMED_PIPE = peek
    handle = msvcrt.get_osfhandle(fd)
    available = wintypes.DWORD()
    ok = _WINDOWS_PEEK_NAMED_PIPE(
        wintypes.HANDLE(handle),
        None,
        0,
        None,
        ctypes.byref(available),
        None,
    )
    if ok:
        return int(available.value)
    error = ctypes.get_last_error()
    if error in (109, 232, 233):  # broken/no-data/not-connected pipe
        return None
    raise OSError(error, "PeekNamedPipe failed")


def _read_windows_available(fd: int, capture: _TailBuffer, available: int) -> bool:
    """Read at most one bounded turn of bytes known to be ready."""
    for _ in range(_MAX_READS_PER_TURN):
        if available <= 0:
            return False
        data = os.read(fd, min(_READ_CHUNK_BYTES, available))
        if not data:
            return True
        capture.append(data)
        available -= len(data)
    return False


def _drain_windows_after_exit(fd: int, capture: _TailBuffer, deadline: float) -> None:
    """Drain ready bytes after exit, never waiting for descendant EOF."""
    ends = min(time.monotonic() + _POST_EXIT_DRAIN_SECONDS, deadline)
    while time.monotonic() < ends:
        available = _windows_pipe_available(fd)
        if available is None or available == 0:
            return
        if _read_windows_available(fd, capture, available):
            return


def _wait_windows(
    process: subprocess.Popen[bytes], capture: _TailBuffer, deadline: float
) -> int:
    assert process.stdout is not None
    fd = process.stdout.fileno()
    while True:
        available = _windows_pipe_available(fd)
        if available:
            _read_windows_available(fd, capture, available)
        returncode = process.poll()
        if returncode is not None:
            # No blocking read is in flight here.  A grandchild retaining the
            # write end therefore cannot make cleanup or return wait forever.
            _drain_windows_after_exit(fd, capture, deadline)
            return returncode
        remaining = _remaining(deadline)
        if remaining <= 0:
            raise CommandTimedOut("command", 0.0, Path("."), "")
        time.sleep(min(_POLL_INTERVAL_SECONDS, remaining))


def _wait_reaped(process: subprocess.Popen[bytes], timeout: float) -> None:
    try:
        process.wait(timeout=max(0.0, timeout))
    except subprocess.TimeoutExpired:
        try:
            process.kill()
        except (OSError, ProcessLookupError):
            pass
        try:
            process.wait(timeout=max(0.05, timeout))
        except (subprocess.TimeoutExpired, OSError):
            # The caller must not get stuck in cleanup.  The process object is
            # still closed by the normal finally path; on ordinary platforms a
            # kill followed by wait above reaps it.
            pass


def _terminate_process_tree(process: subprocess.Popen[bytes]) -> None:
    """Stop the child and descendants, bounded and safe to call repeatedly."""
    if os.name == "nt":
        # taskkill is a Windows system utility, not a shell invocation.  It is
        # the stdlib-compatible way to request a descendant tree kill.  If it
        # is unavailable, direct kill below still prevents the direct child
        # from leaking; descendants are an explicitly weaker Windows boundary.
        if process.poll() is None:
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    check=False,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=max(1.0, _CLEANUP_GRACE_SECONDS * 5),
                    shell=False,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            except (OSError, subprocess.TimeoutExpired):
                try:
                    process.terminate()
                except (OSError, ProcessLookupError):
                    pass
        _wait_reaped(process, _CLEANUP_GRACE_SECONDS)
        return

    # start_new_session=True makes the process its own process-group leader.
    # SIGKILL after a short SIGTERM grace period covers children that ignore
    # termination and makes the timeout path deterministic.
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except (OSError, ProcessLookupError):
        if process.poll() is None:
            try:
                process.terminate()
            except (OSError, ProcessLookupError):
                pass
    try:
        process.wait(timeout=_CLEANUP_GRACE_SECONDS)
    except subprocess.TimeoutExpired:
        pass
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except (OSError, ProcessLookupError):
        pass
    _wait_reaped(process, _CLEANUP_GRACE_SECONDS)


def _cleanup_output(process: subprocess.Popen[bytes]) -> None:
    """Close the parent-side descriptor without waiting on inherited pipe ends."""
    stream = process.stdout
    if stream is None:
        return
    # The Windows path uses PeekNamedPipe and performs no concurrent read, so
    # BufferedReader.close() cannot contend with a reader thread.  A POSIX
    # selector is likewise finished before this point.
    try:
        stream.close()
    except OSError:
        pass


def run_bounded_command(
    arguments: list[str],
    cwd: Path,
    environment: dict,
    log: Path,
    timeout_seconds: float,
    label: str,
) -> str:
    """Run an argument-list command with a monotonic deadline and bounded tail.

    ``stdout`` and ``stderr`` are merged, drained while the child runs, and
    retained only as the final :data:`MAX_TAIL_BYTES` bytes.  That same bounded
    tail is written to ``log`` and returned as UTF-8 with replacement decoding.
    On POSIX the child starts a fresh process group; timeout and exceptional
    exits kill that group before reaping the direct child.  Windows polls the
    pipe with ``PeekNamedPipe`` (no blocking reader thread), then uses
    ``taskkill /T`` for best-effort descendant cleanup; it cannot promise the
    same process-group semantics for every descendant creation pattern.
    """
    _validate_arguments(arguments)
    if not isinstance(cwd, Path):
        raise TypeError("cwd must be a pathlib.Path")
    if not isinstance(log, Path):
        raise TypeError("log must be a pathlib.Path")
    if not isinstance(timeout_seconds, (int, float)) or isinstance(timeout_seconds, bool):
        raise TypeError("timeout_seconds must be a finite number")
    if not math.isfinite(float(timeout_seconds)) or timeout_seconds < 0:
        raise ValueError("timeout_seconds must be finite and non-negative")
    if not isinstance(environment, dict):
        raise TypeError("environment must be a dictionary")
    if not isinstance(label, str) or not label:
        raise ValueError("label must be a non-empty string")

    try:
        log.parent.mkdir(parents=True, exist_ok=True)
    except OSError as error:
        raise CommandUnavailable(f"Could not prepare log for {label}: {error}") from error

    capture = _TailBuffer()
    if timeout_seconds == 0:
        _write_log(log, b"")
        raise CommandTimedOut(label, 0.0, log, "")

    process: subprocess.Popen[bytes] | None = None
    output = b""
    deadline = time.monotonic() + float(timeout_seconds)
    try:
        popen_kwargs: dict = {
            "cwd": cwd,
            "env": environment,
            "shell": False,
            "stdout": subprocess.PIPE,
            "stderr": subprocess.STDOUT,
        }
        if os.name == "nt":
            popen_kwargs["creationflags"] = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
        else:
            popen_kwargs["start_new_session"] = True
        try:
            process = subprocess.Popen(arguments, **popen_kwargs)
        except (OSError, ValueError) as error:
            raise CommandUnavailable(f"Could not start {label}: {error}") from error

        if os.name == "nt":
            try:
                returncode = _wait_windows(process, capture, deadline)
            except CommandTimedOut as error:
                raise CommandTimedOut(label, float(timeout_seconds), log, "") from error
        else:
            try:
                returncode = _wait_posix(process, capture, deadline)
            except CommandTimedOut as error:
                raise CommandTimedOut(label, float(timeout_seconds), log, "") from error
        output = capture.bytes()
        decoded = _decode(output)
        _write_log(log, output)
        if returncode:
            raise CommandFailed(label, returncode, log, decoded)
        return decoded
    except BaseException as error:
        # This includes KeyboardInterrupt, SystemExit, command failures, and
        # reader failures.  Always target the process group: the direct child
        # may already have exited while a grandchild still owns the pipe.
        if process is not None:
            try:
                _terminate_process_tree(process)
            except BaseException:
                pass
        output = capture.bytes()
        if isinstance(error, CommandTimedOut):
            error.output = _decode(output)
        try:
            _write_log(log, output)
        except BaseException:
            pass
        raise
    finally:
        if process is not None:
            _cleanup_output(process)
