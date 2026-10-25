#!/usr/bin/env python3
"""Prepare a disposable Windows/macOS runner for real wide GUI checks.

Never called by the application. Native access requires an explicit CLI opt-in.
No persistent display settings, extra drivers, guessed modes or lowered test sizes.
"""
from __future__ import annotations

import argparse
import ctypes
import json
from pathlib import Path
import subprocess
import sys
import time


def sufficient(state, args):
    return (state["physical_width"] >= args.min_width
            and state["physical_height"] >= args.min_height
            and state["work_width_dip"] >= args.min_work_width
            and state["work_height_dip"] >= args.min_work_height)


def candidates(modes, args):
    # Work area is verified after application; leave room for the native menu/taskbar.
    return sorted((mode for mode in modes if mode["usable"]
                   and mode["physical_width"] >= args.min_width
                   and mode["physical_height"] >= args.min_height
                   and mode["logical_width"] >= args.min_work_width
                   and mode["logical_height"] >= args.min_work_height),
                  key=lambda mode: (mode["physical_width"] * mode["physical_height"],
                                    mode["logical_width"] * mode["logical_height"],
                                    -mode.get("refresh_hz", 0)))


class WindowsDisplay:
    def __init__(self):
        from ctypes import wintypes as w
        self.u = ctypes.WinDLL("user32", use_last_error=True)
        self.shcore = ctypes.WinDLL("shcore", use_last_error=True)

        class DevMode(ctypes.Structure):
            _fields_ = [("device", w.WCHAR * 32), ("spec", w.WORD), ("driver", w.WORD),
                        ("size", w.WORD), ("extra", w.WORD), ("fields", w.DWORD),
                        ("x", w.LONG), ("y", w.LONG), ("orientation", w.DWORD),
                        ("fixed_output", w.DWORD), ("color", w.SHORT), ("duplex", w.SHORT),
                        ("y_resolution", w.SHORT), ("tt_option", w.SHORT), ("collate", w.SHORT),
                        ("form", w.WCHAR * 32), ("log_pixels", w.WORD), ("bits", w.DWORD),
                        ("width", w.DWORD), ("height", w.DWORD), ("flags", w.DWORD),
                        ("frequency", w.DWORD), ("icm_method", w.DWORD), ("icm_intent", w.DWORD),
                        ("media_type", w.DWORD), ("dither", w.DWORD), ("reserved1", w.DWORD),
                        ("reserved2", w.DWORD), ("panning_width", w.DWORD), ("panning_height", w.DWORD)]

        class MonitorInfo(ctypes.Structure):
            _fields_ = [("size", w.DWORD), ("monitor", w.RECT), ("work", w.RECT), ("flags", w.DWORD)]

        if ctypes.sizeof(DevMode) != 220:
            raise RuntimeError("Unexpected Windows DEVMODEW ABI")
        self.DevMode, self.MonitorInfo = DevMode, MonitorInfo
        self.u.EnumDisplaySettingsExW.argtypes = [w.LPCWSTR, w.DWORD, ctypes.POINTER(DevMode), w.DWORD]
        self.u.EnumDisplaySettingsExW.restype = w.BOOL
        self.u.ChangeDisplaySettingsExW.argtypes = [w.LPCWSTR, ctypes.POINTER(DevMode), w.HWND, w.DWORD, ctypes.c_void_p]
        self.u.ChangeDisplaySettingsExW.restype = w.LONG
        self.u.MonitorFromPoint.argtypes = [w.POINT, w.DWORD]
        self.u.MonitorFromPoint.restype = w.HMONITOR
        self.u.GetMonitorInfoW.argtypes = [w.HMONITOR, ctypes.POINTER(MonitorInfo)]
        self.u.GetMonitorInfoW.restype = w.BOOL
        self.shcore.GetDpiForMonitor.argtypes = [w.HMONITOR, ctypes.c_int, ctypes.POINTER(w.UINT), ctypes.POINTER(w.UINT)]
        self.shcore.GetDpiForMonitor.restype = ctypes.c_long
        # Avoid DPI-virtualized work-area measurements in this helper process.
        self.u.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
        self.u.SetProcessDpiAwarenessContext.restype = w.BOOL
        if not self.u.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            raise ctypes.WinError(ctypes.get_last_error())
        self.Point, self.UInt = w.POINT, w.UINT
        self.native_modes = []

    def _mode(self, index):
        mode = self.DevMode()
        mode.size = ctypes.sizeof(mode)
        return mode if self.u.EnumDisplaySettingsExW(None, index, ctypes.byref(mode), 0) else None

    def state(self):
        mode = self._mode(0xFFFFFFFF)  # ENUM_CURRENT_SETTINGS
        if mode is None:
            raise RuntimeError("Cannot read current Windows display mode")
        monitor = self.u.MonitorFromPoint(self.Point(0, 0), 1)
        info = self.MonitorInfo()
        info.size = ctypes.sizeof(info)
        if not monitor or not self.u.GetMonitorInfoW(monitor, ctypes.byref(info)):
            raise ctypes.WinError(ctypes.get_last_error())
        dpi_x, dpi_y = self.UInt(), self.UInt()
        error = self.shcore.GetDpiForMonitor(monitor, 0, ctypes.byref(dpi_x), ctypes.byref(dpi_y))
        if error or not dpi_x.value or not dpi_y.value:
            raise RuntimeError(f"GetDpiForMonitor failed: {error}")
        sx, sy = dpi_x.value / 96, dpi_y.value / 96
        return {"physical_width": mode.width, "physical_height": mode.height,
                "scale_x": sx, "scale_y": sy,
                "logical_width": mode.width / sx, "logical_height": mode.height / sy,
                "work_width_dip": (info.work.right - info.work.left) / sx,
                "work_height_dip": (info.work.bottom - info.work.top) / sy,
                "work_area_pixels": [info.work.left, info.work.top, info.work.right, info.work.bottom]}

    def modes(self):
        state = self.state()
        result = []
        index = 0
        while (mode := self._mode(index)) is not None:
            self.native_modes.append(mode)
            result.append({"id": index, "physical_width": mode.width, "physical_height": mode.height,
                           "logical_width": mode.width / state["scale_x"], "logical_height": mode.height / state["scale_y"],
                           "bits_per_pixel": mode.bits, "refresh_hz": mode.frequency,
                           "usable": mode.bits >= 24 and mode.width >= mode.height})
            index += 1
        return result

    def apply(self, mode):
        native = self.native_modes[mode["id"]]
        # CDS_TEST before changing; flags=0 deliberately avoids CDS_UPDATEREGISTRY.
        result = self.u.ChangeDisplaySettingsExW(None, ctypes.byref(native), None, 2, None)
        if result:
            return {"test_result": result, "applied": False}
        result = self.u.ChangeDisplaySettingsExW(None, ctypes.byref(native), None, 0, None)
        return {"test_result": 0, "apply_result": result, "applied": result == 0}

    def close(self):
        pass


class MacDisplay:
    def __init__(self):
        self.cg = ctypes.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
        self.cf = ctypes.CDLL("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")
        ptr = ctypes.c_void_p
        self.cg.CGMainDisplayID.restype = ctypes.c_uint32
        self.display = self.cg.CGMainDisplayID()
        self.cg.CGDisplayCopyAllDisplayModes.argtypes = [ctypes.c_uint32, ptr]
        self.cg.CGDisplayCopyAllDisplayModes.restype = ptr
        for name in ("CGDisplayModeGetWidth", "CGDisplayModeGetHeight", "CGDisplayModeGetPixelWidth", "CGDisplayModeGetPixelHeight"):
            function = getattr(self.cg, name)
            function.argtypes = [ptr]
            function.restype = ctypes.c_size_t
        self.cg.CGDisplayModeGetRefreshRate.argtypes = [ptr]
        self.cg.CGDisplayModeGetRefreshRate.restype = ctypes.c_double
        self.cg.CGDisplayModeIsUsableForDesktopGUI.argtypes = [ptr]
        self.cg.CGDisplayModeIsUsableForDesktopGUI.restype = ctypes.c_bool
        self.cg.CGDisplaySetDisplayMode.argtypes = [ctypes.c_uint32, ptr, ptr]
        self.cg.CGDisplaySetDisplayMode.restype = ctypes.c_int32
        self.cf.CFArrayGetCount.argtypes = [ptr]
        self.cf.CFArrayGetCount.restype = ctypes.c_long
        self.cf.CFArrayGetValueAtIndex.argtypes = [ptr, ctypes.c_long]
        self.cf.CFArrayGetValueAtIndex.restype = ptr
        self.cf.CFDictionaryCreate.argtypes = [ptr, ctypes.POINTER(ptr), ctypes.POINTER(ptr), ctypes.c_long, ptr, ptr]
        self.cf.CFDictionaryCreate.restype = ptr
        self.cf.CFRelease.argtypes = [ptr]
        self.native_modes = []
        self.array = None

    def state(self):
        # AppKit supplies the actual usable point area (menu bar and Dock excluded).
        # JXA avoids architecture-sensitive Objective-C struct-return calling conventions.
        source = '''ObjC.import('AppKit');
const s = $.NSScreen.mainScreen;
if (!s) throw new Error('No main NSScreen');
const f = s.frame; const v = s.visibleFrame; const z = Number(s.backingScaleFactor);
JSON.stringify({logical_width:f.size.width, logical_height:f.size.height,
 physical_width:Math.round(f.size.width*z), physical_height:Math.round(f.size.height*z),
 scale_x:z, scale_y:z, work_width_dip:v.size.width, work_height_dip:v.size.height,
 display_id:Number(ObjC.unwrap(s.deviceDescription.objectForKey('NSScreenNumber')))});'''
        completed = subprocess.run(["/usr/bin/osascript", "-l", "JavaScript", "-e", source],
                                   check=True, capture_output=True, text=True, timeout=20)
        state = json.loads(completed.stdout)
        if state["display_id"] != self.display:
            raise RuntimeError(f"Main NSScreen and CGMainDisplayID differ: {state}")
        return state

    def modes(self):
        ptr = ctypes.c_void_p
        key = ptr.in_dll(self.cg, "kCGDisplayShowDuplicateLowResolutionModes")
        true = ptr.in_dll(self.cf, "kCFBooleanTrue")
        options = self.cf.CFDictionaryCreate(None, (ptr * 1)(key.value), (ptr * 1)(true.value), 1, None, None)
        try:
            if not options:
                raise RuntimeError("Cannot allocate CoreGraphics mode dictionary")
            self.array = self.cg.CGDisplayCopyAllDisplayModes(self.display, options)
        finally:
            if options:
                self.cf.CFRelease(options)
        if not self.array:
            raise RuntimeError("CoreGraphics returned no supported display modes")
        result = []
        for index in range(self.cf.CFArrayGetCount(self.array)):
            native = self.cf.CFArrayGetValueAtIndex(self.array, index)
            self.native_modes.append(native)
            result.append({"id": index,
                           "physical_width": self.cg.CGDisplayModeGetPixelWidth(native),
                           "physical_height": self.cg.CGDisplayModeGetPixelHeight(native),
                           "logical_width": self.cg.CGDisplayModeGetWidth(native),
                           "logical_height": self.cg.CGDisplayModeGetHeight(native),
                           "refresh_hz": self.cg.CGDisplayModeGetRefreshRate(native),
                           "usable": bool(self.cg.CGDisplayModeIsUsableForDesktopGUI(native))})
        return result

    def apply(self, mode):
        result = self.cg.CGDisplaySetDisplayMode(self.display, self.native_modes[mode["id"]], None)
        return {"apply_result": result, "applied": result == 0}

    def close(self):
        if self.array:
            self.cf.CFRelease(self.array)
            self.array = None


def prepare(backend, args, report):
    report["before"] = backend.state()
    report["supported_modes"] = backend.modes()
    report["attempts"] = []
    if sufficient(report["before"], args):
        report["after"] = report["before"]
        report["status"] = "already_sufficient"
        return
    for mode in candidates(report["supported_modes"], args):
        attempt = {"mode": mode, **backend.apply(mode)}
        report["attempts"].append(attempt)
        if not attempt["applied"]:
            continue
        # Wait only for native geometry propagation; never infer success from the API return.
        deadline = time.monotonic() + 5
        while True:
            state = backend.state()
            attempt["observed"] = report["after"] = state
            if sufficient(state, args):
                report["status"] = "prepared"
                return
            if time.monotonic() >= deadline:
                break
            time.sleep(0.25)
    report["after"] = backend.state()
    raise RuntimeError("No supported display mode supplied the required physical pixels and usable DIP area")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--allow-disposable-desktop", action="store_true",
                        help="Explicitly permit transient resolution changes on this disposable CI desktop")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--min-width", type=int, default=1920)
    parser.add_argument("--min-height", type=int, default=1080)
    parser.add_argument("--min-work-width", type=int, default=1600, help="Minimum usable width in DIP")
    parser.add_argument("--min-work-height", type=int, default=1000, help="Minimum usable height in DIP")
    args = parser.parse_args(argv)
    if not args.allow_disposable_desktop:
        parser.error("Native desktop access requires --allow-disposable-desktop; never use this on a personal desktop")
    if any(value <= 0 for value in (args.min_width, args.min_height, args.min_work_width, args.min_work_height)):
        parser.error("Minimum dimensions must be positive")
    report = {"platform": sys.platform, "status": "failed", "requirements": {
        "physical_width": args.min_width, "physical_height": args.min_height,
        "work_width_dip": args.min_work_width, "work_height_dip": args.min_work_height}}
    backend = None
    try:
        if sys.platform == "win32":
            backend = WindowsDisplay()
        elif sys.platform == "darwin":
            backend = MacDisplay()
        else:
            raise RuntimeError("Only disposable Windows and macOS runner desktops are supported")
        prepare(backend, args, report)
    except Exception as error:
        report["status"] = "failed"
        report["error"] = f"{type(error).__name__}: {error}"
        if isinstance(error, subprocess.CalledProcessError):
            report["native_probe_stdout"] = error.stdout
            report["native_probe_stderr"] = error.stderr
    finally:
        if backend is not None:
            backend.close()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, allow_nan=False))
    return 1 if report["status"] == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main())
