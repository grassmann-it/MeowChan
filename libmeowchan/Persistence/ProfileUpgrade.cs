using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Persistence;

internal static class ProfileUpgrade
{
    internal const string SnapshotDirectoryName = "before-upgrade";
    internal const string WorkingDirectoryName = "profile";
    private const string GenerationSuffix = ".1.2.0";

    internal static ProfileLease Open(string path, string legacyDownloads, bool temporary = false,
        Func<string, long>? availableBytes = null, Action? beforePublish = null, CancellationToken token = default)
    {
        string directory = ProfileLease.NormalizeDirectory(path);
        ArchiveExportMetadataFile.RejectLinks(directory);
        ProfileLease source = ProfileLease.Acquire(directory);
        bool handedOff = false;
        try
        {
            if (ProfileManifest.Read(directory) is { } existing)
            {
                RequireIdentity(source, existing);
                if (existing.State == ProfileManifest.Preparing)
                {
                    string backup = SnapshotFor(directory)
                        ?? throw new InvalidDataException("The preparing profile has no pre-upgrade snapshot.");
                    ProfileSnapshot saved = ProfileSnapshot.Read(backup);
                    if (saved.Profile.ProfileId != existing.ProfileId || saved.Profile.Generation != existing.Generation)
                        throw new InvalidDataException("The pre-upgrade snapshot belongs to another generation.");
                    saved.VerifyData(backup, token);
                }
                ProfileManifest.Inspect(directory);
                handedOff = true;
                return source;
            }

            if (temporary || source.WasEmptyAtAcquisition || HasOnlyLeaseMetadata(directory))
            {
                handedOff = true;
                return source;
            }

            string generation = directory + GenerationSuffix;
            ArchiveExportMetadataFile.RejectLinks(generation);
            if (!Directory.Exists(generation))
                Prepare(source, generation, legacyDownloads, availableBytes, beforePublish, token);
            return OpenWorking(source, generation);
        }
        finally
        {
            if (!handedOff) source.Dispose();
        }
    }

    internal static void Complete(ProfileLease lease, string[] externalRoots)
    {
        lease.RunExclusive(() =>
        {
            ProfileManifest? existing = ProfileManifest.Read(lease.ProfileDirectory);
            ProfileInstanceIdentity identity = ProfileInstanceIdentity.ReadOrCreate(lease);
            var parts = ProfileManifest.Inspect(lease.ProfileDirectory);
            ProfileManifest complete = existing is null
                ? ProfileManifest.New(identity.Id, ProfileManifest.Ready, parts, externalRoots)
                : existing with { State = ProfileManifest.Ready, Parts = parts, ExternalMediaRoots = externalRoots };
            complete.Write(lease.ProfileDirectory);
            return true;
        });
    }

    internal static string? SnapshotFor(string profile)
    {
        if (Path.GetFileName(profile) != WorkingDirectoryName
            || Directory.GetParent(profile) is not { } parent
            || !parent.Name.EndsWith(GenerationSuffix, StringComparison.Ordinal))
            return null;
        string snapshot = Path.Combine(parent.FullName, SnapshotDirectoryName);
        return File.Exists(Path.Combine(snapshot, ProfileSnapshot.FileName)) ? snapshot : null;
    }

    private static void Prepare(ProfileLease source, string generation, string legacyDownloads,
        Func<string, long>? availableBytes, Action? beforePublish, CancellationToken token)
    {
        string staging = generation + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            string snapshotPath = Path.Combine(staging, SnapshotDirectoryName);
            string prepared = Path.Combine(staging, WorkingDirectoryName);
            string target = Path.Combine(generation, WorkingDirectoryName);
            ProfileSnapshot.CreatePrivateDirectory(staging);
            Directory.CreateDirectory(snapshotPath);
            Guid id = ProfileInstanceIdentity.TryRead(source.ProfileDirectory)?.Id ?? Guid.NewGuid();
            var initial = ProfileManifest.New(id, ProfileManifest.Preparing, new(), []);
            ProfileSnapshot snapshot = ProfileSnapshot.Capture(source, snapshotPath, initial, availableBytes, token);
            snapshot.Restore(snapshotPath, prepared, token);
            var originalParts = ProfileManifest.Inspect(prepared);
            var roots = ProfileMediaRelocation.Prepare(source.ProfileDirectory, prepared, target, legacyDownloads, token);
            snapshot = snapshot with { Profile = initial with { Parts = originalParts, ExternalMediaRoots = roots } };
            snapshot.Write(snapshotPath);
            using (ProfileLease lease = ProfileLease.Acquire(prepared))
            {
                ProfileInstanceIdentity.ReadOrCreate(lease, id);
                var active = initial with { Parts = originalParts, ExternalMediaRoots = roots };
                active.Write(prepared);
            }
            beforePublish?.Invoke();
            snapshot.VerifyData(snapshotPath, token);
            token.ThrowIfCancellationRequested();
            source.EnsureDirectoryIdentity();
            Directory.Move(staging, generation);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static ProfileLease OpenWorking(ProfileLease source, string generation)
    {
        string work = Path.Combine(generation, WorkingDirectoryName);
        if (!Directory.Exists(work))
            throw new InvalidDataException("The working profile is missing from the upgrade directory.");
        ArchiveExportMetadataFile.RejectLinks(work);
        var snapshot = ProfileSnapshot.Read(Path.Combine(generation, SnapshotDirectoryName));
        ProfileLease lease = ProfileLease.Acquire(work);
        try
        {
            ProfileManifest manifest = ProfileManifest.Read(work)
                ?? throw new InvalidDataException("The working profile has no upgrade manifest.");
            if (!string.Equals(snapshot.SourceDirectory, source.ProfileDirectory, PathComparison)
                || manifest.ProfileId != snapshot.Profile.ProfileId
                || manifest.Generation != snapshot.Profile.Generation
                || (ProfileInstanceIdentity.TryRead(source.ProfileDirectory) is { } original && original.Id != manifest.ProfileId))
                throw new InvalidDataException("The working profile does not belong to this upgrade.");
            RequireIdentity(lease, manifest);
            if (manifest.State == ProfileManifest.Preparing)
                snapshot.VerifyData(Path.Combine(generation, SnapshotDirectoryName));
            ProfileManifest.Inspect(work);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void RequireIdentity(ProfileLease lease, ProfileManifest manifest)
    {
        if (ProfileInstanceIdentity.TryRead(lease.ProfileDirectory)?.Id != manifest.ProfileId)
            throw new InvalidDataException("The profile identity does not match its manifest.");
    }

    private static bool HasOnlyLeaseMetadata(string directory) =>
        Directory.EnumerateFileSystemEntries(directory).All(path =>
            Path.GetFileName(path) is ".meowchan-profile.lock" or ".meowchan-profile.protocol" or ".ochan-profile.lock");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
