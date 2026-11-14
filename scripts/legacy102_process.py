#!/usr/bin/env python3
"""Small, bounded subprocess support for the legacy 1.0.2 probe runner.

The runner only needs one captured stream, so stdout and stderr are merged into
one pipe.  The pipe is drained while the child is alive, while ``_TailBuffer``
keeps only the final 2 MiB.  This is intentionally not a general process
supervisor: POSIX process groups are the strong descendant-cleanup mechanism;
Windows uses the stdlib-visible ``taskkill /T`` best effort and retains the
normal Windows process-tree limitations.
"""
from __future__ import annotations

import errno
import math
import os
from pathlib import Path
import selectors
import signal
import subprocess
import threading
import time
from typing import Final


MAX_TAIL_BYTES: Final = 2 * 1024 * 1024
_READ_CHUNK_BYTES: Final = 64 * 1024
_MAX_READS_PER_TURN: Final = 16
_POLL_INTERVAL_SECONDS: Final = 0.05
_CLEANUP_GRACE_SECONDS: Final = 0.20
_POST_EXIT_DRAIN_SECONDS: Final = 0.20


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


def _windows_reader(
    process: subprocess.Popen[bytes], capture: _TailBuffer
) -> tuple[threading.Event, list[BaseException]]:
    """Drain a Windows pipe in a daemon thread (selectors do not support pipes)."""
    assert process.stdout is not None
    finished = threading.Event()
    errors: list[BaseException] = []

    def read_pipe() -> None:
        try:
            while True:
                data = process.stdout.read(_READ_CHUNK_BYTES)
                if not data:
                    return
                capture.append(data)
        except BaseException as error:  # report reader failure without masking cleanup
            errors.append(error)
        finally:
            finished.set()

    thread = threading.Thread(target=read_pipe, name="legacy102-command-output", daemon=True)
    thread.start()
    return finished, errors


def _wait_windows(
    process: subprocess.Popen[bytes],
    capture: _TailBuffer,
    deadline: float,
) -> int:
    finished, reader_errors = _windows_reader(process, capture)
    while True:
        returncode = process.poll()
        if returncode is not None:
            # Usually this is already set.  The short wait lets a normal direct
            # child flush its final bytes, but never waits on a grandchild that
            # inherited the pipe.
            remaining = _remaining(deadline)
            finished.wait(min(_POST_EXIT_DRAIN_SECONDS, remaining))
            if not finished and process.stdout is not None:
                try:
                    process.stdout.close()
                except OSError:
                    pass
                finished.wait(_POLL_INTERVAL_SECONDS)
            if reader_errors:
                raise CommandUnavailable(f"Could not read output from child: {reader_errors[0]}")
            return returncode
        remaining = _remaining(deadline)
        if remaining <= 0:
            raise CommandTimedOut("command", 0.0, Path("."), "")
        finished.wait(min(_POLL_INTERVAL_SECONDS, remaining))


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
    """Close parent-side descriptors without waiting for inherited pipe ends."""
    stream = process.stdout
    if stream is None:
        return
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
    exits kill that group before reaping the direct child.  Windows uses
    ``taskkill /T`` where available and cannot promise the same process-group
    semantics for every descendant creation pattern.
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
