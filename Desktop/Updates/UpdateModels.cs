using System;
using System.Runtime.InteropServices;

namespace MeowChan.Desktop.Updates;

/// <summary>Browser destinations only; checking never downloads or installs release assets.</summary>
public sealed record UpdateInfo(SemanticVersion Version, Uri ReleaseUri, Uri DownloadUri);

public sealed record UpdateState(DateTimeOffset? LastCheckedUtc = null, UpdateInfo? FoundUpdate = null,
    SemanticVersion? SkippedVersion = null);

public enum UpdateCheckKind { Automatic, Manual }
public enum UpdateCheckStatus { Disabled, Throttled, UpToDate, Available, Skipped, Failed }
public enum UpdateCheckError { None, Network, RateLimited, Http, InvalidResponse, Timeout, Persistence }
public enum UpdateDisabledReason { None, Environment, AutomaticChecksOff }

/// <summary>UI maps status/error/reason to localized text; diagnostics are for logs only.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null,
    UpdateCheckError Error = UpdateCheckError.None, UpdateDisabledReason DisabledReason = UpdateDisabledReason.None);

public sealed record UpdateServiceOptions
{
    public required SemanticVersion CurrentVersion { get; init; }
    public string RuntimeIdentifier { get; init; } = CurrentRuntimeIdentifier();

    // Distribution-provided .NET builds can report e.g. arch-x64. Release assets use portable RIDs.
    internal static string CurrentRuntimeIdentifier()
    {
        string? architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        string? platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx" : null;
        return architecture != null && platform != null ? platform + "-" + architecture : RuntimeInformation.RuntimeIdentifier;
    }

    public bool IncludePrereleases { get; init; }
    public bool AutomaticChecksEnabled { get; init; } = true;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumResponseBytes { get; init; } = 1024 * 1024;
}
