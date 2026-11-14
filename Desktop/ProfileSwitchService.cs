using System;
using System.IO;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan.Desktop;

internal enum ProfileActionStatus
{
    /// <summary>A new window was started for the profile.</summary>
    Started,
    /// <summary>Nothing was started: the input or the folder was refused (see the message).</summary>
    Refused,
    /// <summary>Nothing was started: the profile is the one of this window.</summary>
    Current,
    /// <summary>Nothing was started: another window has the profile open.</summary>
    InUse,
    /// <summary>Starting or preparing failed; existing folders were not changed.</summary>
    Failed
}

/// <summary>The outcome of a profile action. <see cref="MessageKey"/> names the text to show; <see cref="ListNoteKey"/> an
/// additional note when the profile list could not take the entry (the profile still opened).</summary>
internal sealed record ProfileActionResult(ProfileActionStatus Status, string MessageKey, string? ListNoteKey = null, object? ListNoteArgument = null);

/// <summary>A validated "New profile" request.</summary>
internal sealed record NewProfileRequest(string Name, string Folder, string DownloadFolder, string? Language);

/// <summary>
/// New profile, switch profile and the profile list for one window (plan PLAN-POWER-TAGGEN PR1). Each profile opens in a
/// process of its own (the profile lease allows independent profiles side by side); this window is never repointed at
/// another profile. Nothing here moves, deletes or migrates a profile: a new profile is only ever created in a new or
/// empty folder, and an existing profile is opened as it is.
/// </summary>
internal sealed class ProfileSwitchService
{
    private readonly ProfileRegistry? _registry;
    private readonly IProfileLauncher _launcher;
    private readonly ILogger _logger;

    internal string CurrentProfile { get; }
    internal string CurrentDownloadPath { get; }
    internal ProfileRegistry? Registry => _registry;
    private readonly Func<int> _limit;

    internal ProfileSwitchService(ProfileRegistry? registry, IProfileLauncher launcher, string currentProfile, string currentDownloadPath,
        Func<int> limit, ILogger? logger = null)
    {
        _registry = registry; _launcher = launcher; _limit = limit; _logger = logger ?? Log.Logger;
        CurrentProfile = ProfileLease.NormalizeDirectory(currentProfile);
        CurrentDownloadPath = ProfileLease.NormalizeDirectory(currentDownloadPath);
    }

    /// <summary>The list as it is; an empty list without a known registry location.</summary>
    internal ProfileRegistrySnapshot ReadList() =>
        _registry?.Read() ?? new ProfileRegistrySnapshot(Array.Empty<ProfileRegistryEntry>(), ProfileRegistryState.Missing);

    /// <summary>The first problem of a "New profile" form as a text key, or null when it can be created.</summary>
    internal string? Validate(string? name, string? folder, string? downloadFolder)
    {
        if (string.IsNullOrWhiteSpace(name)) return "profile.error.nameRequired";
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim())) return "profile.error.folderAbsolute";
        string target = ProfileLease.NormalizeDirectory(folder.Trim());
        if (target.Length > ProfileRegistry.MaximumPathLength) return "profile.error.folderAbsolute";
        if (ProfileRegistry.SamePath(target, CurrentProfile)) return "profile.error.folderCurrent";
        if (ProfileOpenPolicy.Overlaps(target, CurrentProfile)) return "profile.error.folderNested";
        if (_registry != null && ProfileOpenPolicy.Overlaps(target, Path.GetDirectoryName(_registry.FilePath)!)) return "profile.error.folderNested";
        ProfileFolderKind kind = ProfileOpenPolicy.Classify(target);
        if (kind == ProfileFolderKind.Foreign) return "profile.error.folderForeign";
        if (kind == ProfileFolderKind.Unusable) return "profile.error.folderUnusable";
        if (kind == ProfileFolderKind.Existing) return null; // an existing profile keeps its own download folder
        if (string.IsNullOrWhiteSpace(downloadFolder) || !Path.IsPathFullyQualified(downloadFolder.Trim())) return "profile.error.downloadsAbsolute";
        string downloads = ProfileLease.NormalizeDirectory(downloadFolder.Trim());
        // Two profiles never share a download folder (each one's cleanup would treat the other's files as its own), and a
        // download folder never overlaps a profile folder.
        if (ProfileOpenPolicy.Overlaps(downloads, CurrentDownloadPath) || ProfileOpenPolicy.Overlaps(downloads, CurrentProfile)
            || ProfileOpenPolicy.Overlaps(downloads, target))
            return "profile.error.downloadsShared";
        return null;
    }

    /// <summary>Creates (or, for a folder that already is a profile, adopts) the profile, lists it and opens it in a new window.</summary>
    internal ProfileActionResult Create(NewProfileRequest request)
    {
        if (Validate(request.Name, request.Folder, request.DownloadFolder) is { } problem) return new(ProfileActionStatus.Refused, problem);
        string target = ProfileLease.NormalizeDirectory(request.Folder.Trim());
        Guid? id = null;
        if (ProfileOpenPolicy.Classify(target) == ProfileFolderKind.New)
        {
            try { id = PrepareNew(target, ProfileLease.NormalizeDirectory(request.DownloadFolder.Trim()), request.Language); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
            {
                _logger.Warning(error, "A new profile could not be prepared.");
                return new(ProfileActionStatus.Failed, "profile.status.prepareFailed");
            }
        }
        else
        {
            switch (ProfileOpenPolicy.CheckOpen(target, CurrentProfile))
            {
                case ProfileOpenCheck.InUse: return new(ProfileActionStatus.InUse, "profile.status.inUse");
                case ProfileOpenCheck.Current: return new(ProfileActionStatus.Current, "profile.status.current");
                case not ProfileOpenCheck.Ready: return new(ProfileActionStatus.Refused, "profile.status.notProfile");
            }
            try { id = ProfileInstanceIdentity.TryRead(target)?.Id; }
            catch (InvalidDataException) { id = null; }
        }
        (string? note, object? argument) = List(target, request.Name, id, includeCurrent: true);
        ProfileActionResult started = Start(target);
        return started.Status == ProfileActionStatus.Started ? started with { ListNoteKey = note, ListNoteArgument = argument } : started;
    }

    /// <summary>Opens a listed (or any existing) profile in a new window, unless this or another window has it open.</summary>
    internal ProfileActionResult Open(string folder)
    {
        string target = ProfileLease.NormalizeDirectory(folder);
        switch (ProfileOpenPolicy.CheckOpen(target, CurrentProfile))
        {
            case ProfileOpenCheck.Current: return new(ProfileActionStatus.Current, "profile.status.current");
            case ProfileOpenCheck.InUse: return new(ProfileActionStatus.InUse, "profile.status.inUse");
            case ProfileOpenCheck.Missing: return new(ProfileActionStatus.Refused, "profile.status.missing");
            case ProfileOpenCheck.NotAProfile: return new(ProfileActionStatus.Refused, "profile.status.notProfile");
        }
        return Start(target);
    }

    /// <summary>Removes a profile from the list only. Its folder and everything in it stay.</summary>
    internal bool Forget(string folder)
    {
        if (_registry == null || ProfileRegistry.SamePath(folder, CurrentProfile)) return false;
        try { return _registry.Forget(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Warning(error, "A profile could not be removed from the profile list.");
            return false;
        }
    }

    /// <summary>Records the profile of this window in the list (at an ordinary start). Failures are logged, never shown.</summary>
    internal ProfileRegistryChange? RecordCurrent(Guid? id)
    {
        if (_registry == null) return null;
        try { return _registry.Register(CurrentProfile, null, id, DateTimeOffset.UtcNow, _limit()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Warning(error, "The profile list could not be updated.");
            return null;
        }
    }

    private (string? Note, object? Argument) List(string target, string name, Guid? id, bool includeCurrent)
    {
        if (_registry == null) return ("profile.note.listUnavailable", null);
        try
        {
            int limit = _limit();
            if (includeCurrent) _registry.Register(CurrentProfile, null, null, null, limit);
            return _registry.Register(target, name, id, null, limit) switch
            {
                ProfileRegistryChange.LimitReached => ("profile.note.listFull", limit),
                ProfileRegistryChange.Refused => ("profile.note.listUnavailable", null),
                _ => (null, null)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.Warning(error, "The profile list could not be updated.");
            return ("profile.note.listUnavailable", null);
        }
    }

    private ProfileActionResult Start(string target)
    {
        try
        {
            _launcher.Launch(target);
            _logger.Information("Opened another profile in a new window.");
            return new(ProfileActionStatus.Started, "profile.status.started");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
            or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            _logger.Warning(error, "A new window for another profile could not be started.");
            return new(ProfileActionStatus.Failed, "profile.status.startFailed");
        }
    }

    /// <summary>
    /// Turns a new or empty folder into a profile with its own download folder and the language of this window, and gives
    /// it its identity. Its first start then offers the usual first-run setup with that download folder prefilled.
    /// </summary>
    internal static Guid PrepareNew(string folder, string downloadFolder, string? language)
    {
        if (ProfileOpenPolicy.Classify(folder) != ProfileFolderKind.New)
            throw new InvalidOperationException("Only a new or empty folder becomes a new profile.");
        using ProfileLease lease = ProfileLease.Acquire(folder);
        Config config = Config.LoadConfig(lease.ProfileDirectory, lease);
        if (config.Origin != ProfileOrigin.ConfirmedNew)
            throw new InvalidOperationException("The folder changed while the new profile was prepared.");
        config.DownloadPath = downloadFolder;
        if (!string.IsNullOrWhiteSpace(language)) config.Language = language;
        config.SaveConfig(lease.ProfileDirectory);
        return ProfileInstanceIdentity.ReadOrCreate(lease).Id;
    }
}
