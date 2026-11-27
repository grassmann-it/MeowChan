using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace MeowChan.Desktop.Updates;

internal static class InstallChannel
{
    internal const string WindowsPortableZip = "windows-portable-zip";
    internal const string WinGet = "winget";
    internal const string MacOsApp = "macos-app";
    internal const string Homebrew = "homebrew";
    internal const string AppImage = "appimage";
    internal const string LinuxBundledVlc = "linux-tarball-bundled-vlc";
    internal const string LinuxSystemVlc = "linux-tarball-system-vlc";
    internal const string Aur = "aur";
    internal const string Distro = "distro";
    internal const string Flatpak = "flatpak";
    internal const string Source = "source";
    internal const string CiFork = "ci-fork";
    internal const string CiAcceptance = "ci-acceptance";
    internal const string Notify = "notify";
    internal const string None = "none";

    internal static bool IsOfficial(string? channel) =>
        channel is WindowsPortableZip or MacOsApp or AppImage or LinuxBundledVlc or LinuxSystemVlc;

    internal static bool IsKnown(string? channel) => IsOfficial(channel) ||
        channel is WinGet or Homebrew or Aur or Distro or Flatpak or Source or CiFork or CiAcceptance;
}

internal sealed record InstallFacts(OSPlatform Platform, string BaseDirectory, string? InformationalVersion = null,
    string? BuildOrigin = null, string? BuildRepository = null, string? BuildRunId = null,
    string? DistributionChannel = null, bool Acceptance = false, bool TestRun = false,
    ReadOnlyMemory<byte>? DistributionMarker = null, string? LinuxVideoMode = null,
    string? AppImage = null, string? FlatpakId = null, bool FlatpakInfoExists = false,
    bool UpdatesDisabled = false, bool InstallDirectoryWritable = false, bool AppImageWritable = false);

internal sealed record InstallIdentity(string Channel, string Origin, string Commit, bool SelfUpdateEligible,
    string Reason, string? BuildRepository, string? BuildRunId)
{
    internal string FormatVersion(string version) => $"{version} · {Channel} · {Origin} · {Commit}";
}

internal static class InstallChannelDetector
{
    internal const int MaximumMarkerBytes = 4 * 1024;
    private const string MarkerName = "meowchan-distribution.json";
    private const string Official = "official";
    private const string ForkCi = "fork-ci";
    private const string DisableVariable = "MEOWCHAN_DISABLE_UPDATE_CHECK";
    private static readonly Lazy<InstallIdentity> Detected = new(DetectCurrent);

    internal static InstallIdentity Current => Detected.Value;

    internal static InstallIdentity Detect(InstallFacts facts)
    {
        string origin = facts.BuildOrigin is Official or ForkCi ? facts.BuildOrigin : "local";
        string commit = ShortCommit(facts.InformationalVersion);
        string? marker = ParseMarker(facts.DistributionMarker);
        string? stamp = InstallChannel.IsKnown(facts.DistributionChannel) ? facts.DistributionChannel : null;
        string? ignored = facts.DistributionMarker.HasValue && marker is null ? "Distribution marker ignored." : null;
        if (!string.IsNullOrWhiteSpace(facts.DistributionChannel) && stamp is null)
            ignored = JoinReasons(ignored, "Unknown stamped distribution channel ignored.");

        InstallIdentity Result(string channel, string reason, bool eligible = false) =>
            new(channel, origin, commit, eligible, JoinReasons(ignored, reason), facts.BuildRepository, facts.BuildRunId);

        if (facts.UpdatesDisabled)
            return Result(InstallChannel.None, DisableVariable + " is set.");

        string path = DirectoryPath(facts.BaseDirectory, facts.Platform);
        string? managed = ManagedChannel(facts, path);
        bool acceptance = facts.Acceptance || facts.TestRun;
        string channel = marker ?? stamp ?? (acceptance ? InstallChannel.CiAcceptance : Infer(facts, path, managed, origin));
        string source = marker is not null ? "Distribution marker." : stamp is not null ? "Build stamp." :
            acceptance ? "Acceptance build or test run." : "Runtime detection.";

        if (InstallChannel.IsOfficial(channel) && origin != Official)
            return Result(InstallChannel.Source, "Official channel requires official build origin.");
        if (channel is InstallChannel.WindowsPortableZip or InstallChannel.AppImage
            or InstallChannel.LinuxBundledVlc or InstallChannel.LinuxSystemVlc)
        {
            bool appImage = channel == InstallChannel.AppImage;
            bool expectedLayout = channel == InstallChannel.WindowsPortableZip ? facts.Platform == OSPlatform.Windows :
                facts.Platform == OSPlatform.Linux && (appImage ? IsAppImage(facts, path) : string.IsNullOrWhiteSpace(facts.AppImage));
            if (!expectedLayout || managed is not null ||
                IsProtected(appImage ? DirectoryPath(facts.AppImage!, facts.Platform) : path, facts.Platform))
                return Result(InstallChannel.Notify, "Installation layout does not allow self-update.");
            if (!(appImage ? facts.AppImageWritable : facts.InstallDirectoryWritable))
                return Result(InstallChannel.Notify, "Installation target is not writable.");
            return Result(channel, source, eligible: true);
        }
        if (channel == InstallChannel.MacOsApp &&
            (facts.Platform != OSPlatform.OSX || !IsMacApp(path) || managed is not null))
            return Result(InstallChannel.Notify, "Installation layout does not match the macOS app channel.");

        return Result(channel, source);
    }

    private static string JoinReasons(string? first, string second) => first is null ? second : first + " " + second;

    private static string ShortCommit(string? informationalVersion)
    {
        int plus = informationalVersion?.LastIndexOf('+') ?? -1;
        if (plus < 0 || informationalVersion!.Length - plus <= 7) return "?";
        ReadOnlySpan<char> commit = informationalVersion.AsSpan(plus + 1);
        foreach (char digit in commit)
            if (!char.IsAsciiHexDigit(digit)) return "?";
        return commit[..7].ToString();
    }

    private static string Infer(InstallFacts facts, string path, string? managed, string origin)
    {
        if (managed is not null) return managed;
        if (origin == ForkCi) return InstallChannel.CiFork;
        if (origin != Official) return InstallChannel.Source;
        if (facts.Platform == OSPlatform.Windows) return InstallChannel.WindowsPortableZip;
        if (facts.Platform == OSPlatform.OSX && IsMacApp(path)) return InstallChannel.MacOsApp;
        if (facts.Platform != OSPlatform.Linux) return InstallChannel.Source;
        if (IsAppImage(facts, path)) return InstallChannel.AppImage;
        return facts.LinuxVideoMode switch
        {
            "bundled" => InstallChannel.LinuxBundledVlc,
            "system" => InstallChannel.LinuxSystemVlc,
            _ => InstallChannel.Source
        };
    }

    private static string DirectoryPath(string path, OSPlatform platform) =>
        (platform == OSPlatform.Windows ? path.Replace('\\', '/') : path).TrimEnd('/') + "/";

    private static string? ManagedChannel(InstallFacts facts, string path)
    {
        if (facts.Platform == OSPlatform.Windows && path.Contains("/Microsoft/WinGet/Packages/", StringComparison.OrdinalIgnoreCase))
            return InstallChannel.WinGet;
        if ((facts.Platform == OSPlatform.OSX || facts.Platform == OSPlatform.Linux) &&
            path.Contains("/Cellar/meowchan/", StringComparison.Ordinal))
            return InstallChannel.Homebrew;
        if (facts.Platform == OSPlatform.Linux && (!string.IsNullOrWhiteSpace(facts.FlatpakId) || facts.FlatpakInfoExists))
            return InstallChannel.Flatpak;
        return null;
    }

    private static bool IsMacApp(string path) => path.EndsWith(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppImage(InstallFacts facts, string path) =>
        facts.AppImage is { Length: > 1 } appImage && appImage[0] == '/' &&
        path.EndsWith("/usr/lib/meowchan/", StringComparison.Ordinal);

    private static bool IsProtected(string path, OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
        {
            int root = path.IndexOf('/');
            if (root != 2 || path[1] != ':') return true;
            return path.AsSpan(root + 1).StartsWith("Program Files/".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                path.AsSpan(root + 1).StartsWith("Program Files (x86)/".AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
        return path.StartsWith("/usr/", StringComparison.Ordinal) || path.StartsWith("/opt/", StringComparison.Ordinal) ||
            path.StartsWith("/snap/", StringComparison.Ordinal) || path.StartsWith("/nix/store/", StringComparison.Ordinal) ||
            path.StartsWith("/var/lib/flatpak/", StringComparison.Ordinal) ||
            path.Contains("/Cellar/meowchan/", StringComparison.Ordinal);
    }

    private static string? ParseMarker(ReadOnlyMemory<byte>? marker)
    {
        if (marker is not { Length: > 0 and <= MaximumMarkerBytes } bytes || !Utf8.IsValid(bytes.Span)) return null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            bool validSchema = false;
            string? channel = null;
            foreach (JsonProperty property in json.RootElement.EnumerateObject())
            {
                if (property.NameEquals("SchemaVersion") && !validSchema && property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out int version) && version == 1)
                    validSchema = true;
                else if (property.NameEquals("Channel") && channel is null && property.Value.ValueKind == JsonValueKind.String)
                    channel = property.Value.GetString();
                else return null;
            }
            return validSchema && InstallChannel.IsKnown(channel) ? channel : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static InstallIdentity DetectCurrent()
    {
        Assembly assembly = typeof(InstallChannelDetector).Assembly;
        string? Metadata(string key)
        {
            foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (attribute.Key == "MeowChan." + key) return attribute.Value;
            return null;
        }

        string directory = AppContext.BaseDirectory;
        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        string? entryAssembly = Assembly.GetEntryAssembly()?.GetName().Name;
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows :
            OperatingSystem.IsLinux() ? OSPlatform.Linux : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.FreeBSD;
        InstallIdentity identity = Detect(new(platform, directory,
            InformationalVersion: assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            BuildOrigin: Metadata("BuildOrigin"), BuildRepository: Metadata("BuildRepository"), BuildRunId: Metadata("BuildRunId"),
            DistributionChannel: Metadata("DistributionChannel"),
            Acceptance: string.Equals(Metadata("Acceptance"), "true", StringComparison.OrdinalIgnoreCase),
            TestRun: entryAssembly is "testhost" or "MeowChan.DesktopSmoke",
            DistributionMarker: ReadMarker(directory),
            LinuxVideoMode: OperatingSystem.IsLinux() ? ReadLinuxVideoMode(directory) : null,
            AppImage: appImage, FlatpakId: Environment.GetEnvironmentVariable("FLATPAK_ID"),
            FlatpakInfoExists: OperatingSystem.IsLinux() && File.Exists("/.flatpak-info"),
            UpdatesDisabled: Environment.GetEnvironmentVariable(DisableVariable) == "1",
            InstallDirectoryWritable: IsDirectoryWritable(directory), AppImageWritable: IsAppImageWritable(appImage)));
        Log.Information("Installation channel {Channel} ({Origin}): {Reason:l}", identity.Channel, identity.Origin, identity.Reason);
        return identity;
    }

    private static ReadOnlyMemory<byte>? ReadMarker(string directory)
    {
        try
        {
            using FileStream stream = File.OpenRead(Path.Combine(directory, MarkerName));
            byte[] buffer = new byte[MaximumMarkerBytes + 1];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer.AsMemory(0, read);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Information("Distribution marker could not be read ({Error}).", error.GetType().Name);
            return null;
        }
    }

    private static string? ReadLinuxVideoMode(string directory)
    {
        try
        {
            string file = Path.Combine(directory, "linux-video-runtime.json");
            if (!File.Exists(file)) return null;
            using FileStream stream = File.OpenRead(file);
            using JsonDocument json = JsonDocument.Parse(stream);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Mode", out JsonElement mode)
                && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool IsDirectoryWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint addFileAndDirectory = 0x0006;
            const uint openExisting = 3;
            const uint backupSemantics = 0x02000000;
            using SafeFileHandle handle = CreateFileW(path, addFileAndDirectory, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
            return !handle.IsInvalid;
        }
        const int writeAndSearch = 3;
        return OperatingSystem.IsLinux() && HasUnixAccess(path, writeAndSearch);
    }

    private static bool IsAppImageWritable(string? path)
    {
        const int write = 2;
        return OperatingSystem.IsLinux() && path is not null && Path.IsPathFullyQualified(path) && File.Exists(path)
            && HasUnixAccess(path, write) && Path.GetDirectoryName(path) is { } parent && IsDirectoryWritable(parent);
    }

    private static bool HasUnixAccess(string path, int mode) => Access(Encoding.UTF8.GetBytes(path + '\0'), mode) == 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("libc", EntryPoint = "access", ExactSpelling = true)]
    private static extern int Access(byte[] path, int mode);
}
