using System;
using System.IO;
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
    private static string MacDirectory => Path.Combine(AppContext.BaseDirectory, "libvlc",
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64", "lib");
    private static readonly Lazy<IntPtr> MacLibVlc = new(() => NativeLibrary.Load(Path.Combine(MacDirectory, "libvlc.dylib")));

    internal static void EnsureInitialized() => _ = Initialized.Value;

    // Both callback entrypoints must come from the very same module as the player.
    // The Mac engine is explicitly initialized from this exact packaged directory;
    // Linux reuses the process-lifetime handle installed in the binding resolver.
    internal static IntPtr GetCallbackLibraryHandle()
    {
        EnsureInitialized();
        IntPtr library = OperatingSystem.IsMacOS() ? MacLibVlc.Value : OperatingSystem.IsLinux()
            ? LinuxLibVlc.Value : throw new PlatformNotSupportedException("The Mac software video surface is tested on macOS and Linux.");
        return library != IntPtr.Zero ? library : throw new DllNotFoundException("The LibVLC 3 runtime could not be loaded.");
    }

    private static bool Initialize()
    {
        if (OperatingSystem.IsMacOS()) { Core.Initialize(MacDirectory); return true; }
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
