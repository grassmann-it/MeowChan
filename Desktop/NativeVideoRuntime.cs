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
    private static readonly Lazy<LinuxVideoRuntimePolicy> LinuxPolicy = new(() =>
        LinuxVideoRuntimePolicy.Read(AppContext.BaseDirectory, LinuxVideoRuntimePolicy.CurrentRid));
    private static readonly Lazy<IntPtr> LinuxLibVlc = new(LoadLinuxLibrary);
    private static IntPtr _linuxCore; // Retained alongside libvlc for the process lifetime.
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
        LinuxPolicy.Value.ValidateBundle();
        if (LinuxPolicy.Value.Bundled)
        {
            // VLC_PLUGIN_PATH adds locations to the core-derived plugin path. It must
            // not introduce host plugins into a private runtime.
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", null);
            Environment.SetEnvironmentVariable("VLC_DATA_PATH", Path.Combine(LinuxPolicy.Value.BundleRoot, "lib/vlc/share"));
        }
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            if (LinuxPolicy.Value.Bundled)
                throw new InvalidOperationException("A private VLC bundle cannot share an existing native resolver.");
            // An embedding host may already own this assembly's resolver. Preserve it;
            // LibVLCSharp's normal unavailable-backend handling still applies if it fails.
            Log.Debug("LibVLCSharp already has a native-library resolver; preserving the host configuration.");
        }
        return true;
    }

    internal static string GetLinuxVersion()
    {
        IntPtr handle = GetCallbackLibraryHandle();
        var version = Marshal.GetDelegateForFunctionPointer<GetVersion>(NativeLibrary.GetExport(handle, "libvlc_get_version"));
        return Marshal.PtrToStringUTF8(version()) ?? string.Empty;
    }

    internal static void ProbeLinuxEngine()
    {
        EnsureInitialized();
        using var engine = new LibVLC("--no-audio", "--no-video-title-show", "--no-media-library", "--ignore-config");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetVersion();

    private static IntPtr LoadLinuxLibrary()
    {
        var policy = LinuxPolicy.Value;
        if (!policy.Bundled)
            return NativeLibrary.TryLoad("libvlc.so.5", out IntPtr handle) ? handle : IntPtr.Zero;
        policy.ValidateBundle();
        string lib = Path.Combine(policy.BundleRoot, "lib");
        _linuxCore = NativeLibrary.Load(Path.Combine(lib, "libvlccore.so.9"));
        return NativeLibrary.Load(Path.Combine(lib, "libvlc.so.5"));
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "libvlc") return IntPtr.Zero;
        IntPtr handle = LinuxLibVlc.Value;
        // Throw for a broken private bundle; returning zero would enable the
        // default loader's system fallback.
        if (handle == IntPtr.Zero && LinuxPolicy.Value.Bundled)
            throw new DllNotFoundException("The private VLC bundle could not be loaded.");
        return handle;
    }
}
