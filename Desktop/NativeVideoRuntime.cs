using System;
using System.Reflection;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>Uses the system LibVLC 3 runtime ABI without requiring development-package symlinks.</summary>
internal static class NativeVideoRuntime
{
    private static readonly Lazy<bool> Initialized = new(Initialize);
    // One process-lifetime handle is retained because LibVLCSharp caches native entrypoints.
    // Reopening for every P/Invoke would accumulate loader reference counts; unloading while
    // any player or cached entrypoint remains alive would be unsafe.
    private static readonly Lazy<IntPtr> LinuxLibVlc = new(() =>
        NativeLibrary.TryLoad("libvlc.so.5", out IntPtr handle) ? handle : IntPtr.Zero);

    internal static void EnsureInitialized() => _ = Initialized.Value;

    private static bool Initialize()
    {
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // An embedding host may already own this assembly's resolver. Preserve it;
            // LibVLCSharp's normal unavailable-backend handling still applies if it fails.
            Log.Debug("LibVLCSharp already has a native-library resolver; preserving the host configuration.");
        }
        return true;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == "libvlc" ? LinuxLibVlc.Value : IntPtr.Zero;
}
