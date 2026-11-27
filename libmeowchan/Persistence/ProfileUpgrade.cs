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
        Func<string, long>? availableBytes = null, Action? beforePublish = null, Action<string, long>? progress = null, CancellationToken token = default)
    {
        ProfileCopyOperation operation = new(progress, token);
        string directory = ProfileLease.NormalizeDirectory(path);
        if (Directory.GetParent(directory) is null)
            throw new InvalidDataException("A filesystem root cannot be used as an upgrade source.");
        ArchiveExportMetadataFile.RejectLinks(directory);
        ProfileLease? source = null;
        try
        {
            source = ProfileLease.Acquire(directory);
            if (ProfileManifest.Read(directory) is { } existing)
            {
                RequireIdentity(source, existing);
                if (SnapshotFor(directory) is { } backup)
                {
                    ProfileSnapshot saved = ProfileSnapshot.Read(backup);
                    VerifyUpgrade(saved, existing, backup, operation);
                }
                else if (existing.State == ProfileManifest.Preparing)
                    throw new InvalidDataException("The preparing profile has no pre-upgrade snapshot.");
                ProfileManifest.Inspect(directory);
                ProfileLease current = source;
                source = null;
                return current;
            }

            if (temporary || !Directory.Exists(directory + GenerationSuffix) && (source.WasEmptyAtAcquisition || HasOnlyLeaseMetadata(directory)))
            {
                ProfileLease current = source;
                source = null;
                return current;
            }

            string generation = directory + GenerationSuffix;
            ArchiveExportMetadataFile.RejectLinks(generation);
            if (!Directory.Exists(generation))
                Prepare(source, generation, legacyDownloads, availableBytes, beforePublish, operation);
            return OpenWorking(source, generation, operation);
        }
        finally
        {
            source?.Dispose();
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
        if (!string.Equals(Path.GetFileName(profile), WorkingDirectoryName, PathComparison)
            || Directory.GetParent(profile) is not { } parent
            || !parent.Name.EndsWith(GenerationSuffix, PathComparison))
            return null;
        string snapshot = Path.Combine(parent.FullName, SnapshotDirectoryName);
        return File.Exists(Path.Combine(snapshot, ProfileSnapshot.FileName)) ? snapshot : null;
    }

    private static void Prepare(ProfileLease source, string generation, string legacyDownloads,
        Func<string, long>? availableBytes, Action? beforePublish, ProfileCopyOperation operation)
    {
        string staging = generation + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            string snapshotPath = Path.Combine(staging, SnapshotDirectoryName);
            string prepared = Path.Combine(staging, WorkingDirectoryName);
            string target = Path.Combine(generation, WorkingDirectoryName);
            ProfileSnapshot.CreatePrivateDirectory(staging, source.ProfileDirectory);
            ProfileSnapshot.CreatePrivateDirectory(snapshotPath, source.ProfileDirectory);
            string sourceIdentity = ProfileDirectoryIdentity.Capture(source.ProfileDirectory).DiscardToken;
            Guid id = ProfileInstanceIdentity.TryRead(source.ProfileDirectory)?.Id ?? Guid.NewGuid();
            var initial = ProfileManifest.New(id, ProfileManifest.Preparing, new(), []);
            string defaultDownloads = ProfileLease.NormalizeDirectory(legacyDownloads);
            ProfileSnapshot snapshot = ProfileSnapshot.CaptureUpgrade(source, snapshotPath, initial, defaultDownloads,
                sourceIdentity, availableBytes, operation);
            snapshot.RestoreData(snapshotPath, prepared, operation);
            var originalParts = ProfileManifest.Inspect(prepared);
            string[] external = snapshot.ManagedMedia!.SelectMany(media => media.SelectedNames is null
                ? new[] { media.Root } : media.Files.Select(file => Path.Combine(media.Root, file.Name))).ToArray();
            string[] roots = ProfileMediaRelocation.CurrentRoots(prepared, null, external);
            snapshot = snapshot with { Profile = initial with { Parts = originalParts, ExternalMediaRoots = roots } };
            ProfileMediaRelocation.Apply(snapshot, snapshotPath, prepared, target, operation);
            snapshot.Write(snapshotPath);
            using (ProfileLease lease = ProfileLease.Acquire(prepared))
            {
                ProfileInstanceIdentity.ReadOrCreate(lease, id);
                var active = initial with { Parts = originalParts, ExternalMediaRoots = roots };
                active.Write(prepared);
            }
            beforePublish?.Invoke();
            snapshot.VerifyData(snapshotPath, operation);
            snapshot.VerifyExternalMedia(snapshotPath, operation);
            operation.Token.ThrowIfCancellationRequested();
            source.EnsureDirectoryIdentity();
            Directory.Move(staging, generation);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static ProfileLease OpenWorking(ProfileLease source, string generation, ProfileCopyOperation operation)
    {
        string work = Path.Combine(generation, WorkingDirectoryName);
        if (!Directory.Exists(work))
            throw new InvalidDataException("The working profile is missing from the upgrade directory.");
        ArchiveExportMetadataFile.RejectLinks(work);
        string backup = Path.Combine(generation, SnapshotDirectoryName);
        var snapshot = ProfileSnapshot.Read(backup);
        source.EnsureDirectoryIdentity();
        if (snapshot.SourceIdentity != ProfileDirectoryIdentity.Capture(source.ProfileDirectory).DiscardToken)
            throw new InvalidDataException("The source profile directory is no longer the one captured for this upgrade.");
        ProfileLease lease = ProfileLease.Acquire(work);
        try
        {
            ProfileManifest manifest = ProfileManifest.Read(work)
                ?? throw new InvalidDataException("The working profile has no upgrade manifest.");
            var original = ProfileInstanceIdentity.TryRead(source.ProfileDirectory);
            if (!string.Equals(snapshot.SourceDirectory, source.ProfileDirectory, PathComparison)
                || (original is not null && original.Id != manifest.ProfileId))
                throw new InvalidDataException("The working profile does not belong to this upgrade.");
            if (original is null)
                snapshot.VerifyLegacySource(operation);
            RequireIdentity(lease, manifest);
            VerifyUpgrade(snapshot, manifest, backup, operation);
            ProfileManifest.Inspect(work);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void VerifyUpgrade(ProfileSnapshot snapshot, ProfileManifest manifest, string backup, ProfileCopyOperation operation)
    {
        if (snapshot.Version != ProfileSnapshot.UpgradeVersion
            || snapshot.Profile.ProfileId != manifest.ProfileId || snapshot.Profile.Generation != manifest.Generation)
            throw new InvalidDataException("The pre-upgrade snapshot does not identify a supported working generation.");
        if (manifest.State != ProfileManifest.Preparing) return;
        snapshot.VerifyData(backup, operation);
        snapshot.VerifyExternalMedia(backup, operation);
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
