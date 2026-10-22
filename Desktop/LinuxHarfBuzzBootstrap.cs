using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MeowChan.Desktop;

/// <summary>Keep packaged HarfBuzz's private ABI separate from a previously loaded system HarfBuzz.</summary>
internal static class LinuxHarfBuzzBootstrap
{
    // glibc dlopen flags: resolve every relocation now, against this object before
    // the global scope. RTLD_LOCAL is zero. The release Linux RIDs target glibc.
    internal const int LoadFlags = 0x2 | 0x8; // RTLD_NOW | RTLD_DEEPBIND
    private static readonly Lazy<IntPtr> Loaded = new(LoadAndProbe, LazyThreadSafetyMode.ExecutionAndPublication);
    private static int _resolvedImports;
    private static IntPtr _handle; // Process lifetime: managed P/Invokes retain pointers into this exact object.

    internal static void EnsureLoaded()
    {
        if (OperatingSystem.IsLinux()) _ = Loaded.Value;
    }

    internal static string ResolveDeliveredPath(string directory, Architecture architecture)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Application directory must be absolute.", nameof(directory));
        string root = Path.GetFullPath(directory);
        string published = Path.Combine(root, "libHarfBuzzSharp.so");
        if (File.Exists(published)) return published;
        string rid = architecture switch
        {
            Architecture.X64 => "linux-x64", Architecture.Arm64 => "linux-arm64",
            Architecture.X86 => "linux-x86", Architecture.Arm => "linux-arm",
            _ => throw new PlatformNotSupportedException("Unsupported Linux HarfBuzz architecture: " + architecture)
        };
        // Framework-dependent development builds retain NuGet's RID asset layout.
        // Never consult PATH, LD_LIBRARY_PATH, a user directory or system HarfBuzz.
        string development = Path.Combine(root, "runtimes", rid, "native", "libHarfBuzzSharp.so");
        if (File.Exists(development)) return development;
        throw new DllNotFoundException("The delivered Linux text library libHarfBuzzSharp.so is missing from " + root);
    }

    private static IntPtr LoadAndProbe()
    {
        string path = ResolveDeliveredPath(AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture);
        IntPtr handle = Dlopen(path, LoadFlags);
        if (handle == IntPtr.Zero)
            throw new DllNotFoundException("The delivered Linux text library could not be loaded with isolated symbol binding: "
                + path + ": " + Marshal.PtrToStringUTF8(Dlerror()));
        _handle = handle;
        // Returning the deepbound handle for every generated HarfBuzzSharp P/Invoke
        // prevents a second default lookup from selecting a system/other instance.
        // A conflicting existing resolver is an initialization error, never a fallback.
        try { NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Font).Assembly, ResolveImport); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException("Linux text initialization cannot isolate HarfBuzzSharp because a native resolver is already registered.", error);
        }
        ProbeManagedFont();
        if (Volatile.Read(ref _resolvedImports) == 0)
            throw new InvalidOperationException("The managed Linux font API did not use the isolated text-library resolver.");
        return handle;
    }

    private static IntPtr ResolveImport(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name is not ("libHarfBuzzSharp" or "libHarfBuzzSharp.so")) return IntPtr.Zero;
        Interlocked.Increment(ref _resolvedImports);
        return _handle;
    }

    // Keep the first actual managed/native font call after resolver registration.
    // This tiny empty font needs no font files, user settings, display or network.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeManagedFont()
    {
        using var font = new HarfBuzzSharp.Font(HarfBuzzSharp.Face.Empty);
        if (font.Handle == IntPtr.Zero) throw new InvalidOperationException("The delivered Linux text library could not create a font.");
    }

    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern IntPtr Dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libdl.so.2", EntryPoint = "dlerror")]
    private static extern IntPtr Dlerror();
}
