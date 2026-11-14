using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>The <c>--profile &lt;folder&gt;</c> start argument (plan PLAN-POWER-TAGGEN PR1). It selects the profile of this
/// process like <c>MEOWCHAN_PROFILE</c>, without the environment; every other argument passes through unchanged.</summary>
internal static class ProfileCommandLine
{
    internal const string Switch = "--profile";

    /// <summary>Removes <c>--profile &lt;folder&gt;</c> or <c>--profile=&lt;folder&gt;</c> from <paramref name="args"/>.
    /// <paramref name="profile"/> is the absolute folder, or null without the argument.</summary>
    /// <exception cref="ArgumentException">The argument has no folder or is given twice.</exception>
    internal static string[] Extract(IReadOnlyList<string> args, out string? profile)
    {
        ArgumentNullException.ThrowIfNull(args);
        profile = null;
        List<string> rest = new(args.Count);
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            string? value;
            if (arg == Switch)
            {
                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Usage: MeowChan --profile <folder>");
                value = args[++index];
            }
            else if (arg.StartsWith(Switch + "=", StringComparison.Ordinal)) value = arg[(Switch.Length + 1)..];
            else { rest.Add(arg); continue; }
            if (profile != null) throw new ArgumentException("--profile may be given only once.");
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new ArgumentException("Usage: MeowChan --profile <folder>");
            profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        return rest.ToArray();
    }
}

/// <summary>How the profile of this process was chosen. Only a profile opened by <c>--profile</c> or the default
/// profile records itself in the profile list; a profile chosen by an environment variable (tests, smokes,
/// scripted starts) leaves the list as it is, so the variables keep their exact former behaviour.</summary>
internal static class ProfileStartup
{
    /// <summary>Set by the app's entry point for an ordinary start; false everywhere else (tests, smokes, tools).</summary>
    internal static bool RecordsInProfileList { get; set; }

    internal static bool SelectedByEnvironment(string? meowChanProfile, string? oChanProfile, string? argument) =>
        string.IsNullOrWhiteSpace(argument) && (!string.IsNullOrWhiteSpace(meowChanProfile) || !string.IsNullOrWhiteSpace(oChanProfile));
}

/// <summary>Opens a profile in a new app process. The test seam: smokes use a fake that records instead of starting.</summary>
internal interface IProfileLauncher
{
    void Launch(string profileDirectory);
}

/// <summary>Starts this app again with <c>--profile</c>. The child never inherits <c>MEOWCHAN_PROFILE</c>/<c>OCHAN_PROFILE</c>:
/// the argument alone chooses its profile.</summary>
internal sealed class ProcessProfileLauncher : IProfileLauncher
{
    public void Launch(string profileDirectory)
    {
        ProcessStartInfo info = CreateStartInfo(profileDirectory, Environment.ProcessPath,
            System.Reflection.Assembly.GetEntryAssembly()?.Location, Environment.GetEnvironmentVariable("APPIMAGE"));
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("The new window could not be started.");
    }

    internal static ProcessStartInfo CreateStartInfo(string profileDirectory, string? processPath, string? entryAssembly, string? appImage)
    {
        if (!Path.IsPathFullyQualified(profileDirectory)) throw new ArgumentException("The profile folder must be absolute.", nameof(profileDirectory));
        // An AppImage is restarted through its image: the mount of this process ends with it.
        string executable = !string.IsNullOrWhiteSpace(appImage) && Path.IsPathFullyQualified(appImage) && File.Exists(appImage)
            ? appImage : processPath ?? throw new InvalidOperationException("The app's own executable is unknown.");
        ProcessStartInfo info = new(executable) { UseShellExecute = false };
        // A framework-dependent start (dotnet MeowChan.dll) needs the app assembly as the first argument.
        if (ReferenceEquals(executable, processPath) && IsDotnetHost(executable) && !string.IsNullOrWhiteSpace(entryAssembly))
            info.ArgumentList.Add(entryAssembly);
        info.ArgumentList.Add(ProfileCommandLine.Switch);
        info.ArgumentList.Add(ProfileLease.NormalizeDirectory(profileDirectory));
        info.Environment.Remove("MEOWCHAN_PROFILE");
        info.Environment.Remove("OCHAN_PROFILE");
        return info;
    }

    private static bool IsDotnetHost(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
}

internal enum ProfileFolderKind
{
    /// <summary>Nothing there yet, or an empty folder: a new profile.</summary>
    New,
    /// <summary>A folder that already holds a MeowChan profile.</summary>
    Existing,
    /// <summary>A folder with other content: never turned into a profile.</summary>
    Foreign,
    /// <summary>A file, link or unreadable entry.</summary>
    Unusable
}

internal enum ProfileOpenCheck { Ready, Current, InUse, Missing, NotAProfile }

/// <summary>The decisions before a profile opens in a new window. Pure checks on the file system; nothing is written.</summary>
internal static class ProfileOpenPolicy
{
    // Only the lease's own files prove a profile: a settings.json alone could belong to any other program.
    private static readonly string[] ProfileMarkers = { ".meowchan-profile.protocol", ".meowchan-profile.lock", ".ochan-profile.lock" };

    internal static ProfileFolderKind Classify(string folder)
    {
        try
        {
            string path = ProfileLease.NormalizeDirectory(folder);
            FileSystemInfo info = new DirectoryInfo(path);
            if (!info.Exists) return File.Exists(path) || new FileInfo(path).LinkTarget != null ? ProfileFolderKind.Unusable : ProfileFolderKind.New;
            string[] names = Directory.EnumerateFileSystemEntries(path).Select(entry => Path.GetFileName(entry)).ToArray();
            if (names.Length == 0) return ProfileFolderKind.New;
            return names.Any(name => ProfileMarkers.Contains(name, StringComparer.Ordinal)) ? ProfileFolderKind.Existing : ProfileFolderKind.Foreign;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ProfileFolderKind.Unusable; }
    }

    /// <summary>Whether <paramref name="target"/> can open in a new window now. The current profile and a profile another
    /// window holds are not started again (one window per profile); a vanished folder is not recreated from the list.</summary>
    internal static ProfileOpenCheck CheckOpen(string target, string currentProfile)
    {
        if (ProfileRegistry.SamePath(target, currentProfile)) return ProfileOpenCheck.Current;
        switch (Classify(target))
        {
            case ProfileFolderKind.New when !Directory.Exists(target): return ProfileOpenCheck.Missing;
            case ProfileFolderKind.Foreign or ProfileFolderKind.Unusable: return ProfileOpenCheck.NotAProfile;
        }
        return ProfileLease.IsInUse(target) ? ProfileOpenCheck.InUse : ProfileOpenCheck.Ready;
    }

    /// <summary>True when one folder is the other or lies inside it.</summary>
    internal static bool Overlaps(string left, string right)
    {
        string a = ProfileLease.NormalizeDirectory(left) + Path.DirectorySeparatorChar;
        string b = ProfileLease.NormalizeDirectory(right) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, ProfileRegistry.PathComparison) || b.StartsWith(a, ProfileRegistry.PathComparison);
    }

    /// <summary>The folder name for a profile called <paramref name="name"/>: letters, digits, '-' and '_' only.</summary>
    internal static string Slug(string name)
    {
        char[] characters = name.Trim().Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray();
        string slug = string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "profile" : slug;
    }

    /// <summary>The first of <c>base</c>, <c>base-2</c>, <c>base-3</c>, … that does not exist yet.</summary>
    internal static string FreeFolder(string parent, string slug)
    {
        string candidate = Path.Combine(parent, slug);
        for (int number = 2; (Directory.Exists(candidate) || File.Exists(candidate)) && number < 1000; number++)
            candidate = Path.Combine(parent, slug + "-" + number);
        return candidate;
    }

    /// <summary>Where new profiles go by default: <c>.MeowChan-profiles</c> next to the default profile.</summary>
    internal static string? DefaultProfilesParent()
    {
        string home = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home) ? null : Path.Combine(home, ".MeowChan-profiles");
    }

    /// <summary>Where a new profile downloads by default: <c>MeowChan-&lt;name&gt;</c> beside the current download folder,
    /// never inside it, so two profiles never share (and clean up) the same download folder.</summary>
    internal static string SuggestDownloadFolder(string currentDownloadPath, string slug)
    {
        string current = ProfileLease.NormalizeDirectory(currentDownloadPath);
        string parent = Path.GetDirectoryName(current) ?? current;
        return FreeFolder(parent, "MeowChan-" + slug);
    }
}
