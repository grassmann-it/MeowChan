using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Persistence;

/// <summary>Only explicit group edits; never owns a lease, watch or archive writer.</summary>
internal sealed class ReaderGroupStore
{
    private readonly ProfileLease _lease;
    private readonly string _path;
    // Instance-local observers; neither hook creates or owns admission authority.
    internal Action<ProfileAdmissionToken>? BeforeAdmittedSaveMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterAdmittedSaveMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? BeforeAdmittedRestoreMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterAdmittedRestoreMutationForTests { get; set; }
    public ReaderGroupStore(string profileDirectory, ProfileLease lease)
    {
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "reader-groups.json");
    }

    public Task<ReaderGroupsLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => _lease.RunExclusive(() => LoadCore(cancellationToken)), cancellationToken);

    public Task<ReaderGroupsSnapshot> SaveAsync(ReaderGroupsSnapshot draft, long expectedRevision, CancellationToken cancellationToken = default)
    {
        // Retain synchronous defensive capture before returning the task to the caller.
        ReaderGroupsSnapshot captured = ReaderGroupsValidation.Copy(draft, normalizeNames: true);
        return SaveStandaloneAsync();

        async Task<ReaderGroupsSnapshot> SaveStandaloneAsync()
        {
            using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
            return await SaveAdmittedAsync(captured, expectedRevision, admission, cancellationToken).ConfigureAwait(false);
        }
    }

    // The caller keeps the exact accepted owner through actual task completion.
    // The worker pins synchronous work before its wait on the physical lease monitor.
    internal Task<ReaderGroupsSnapshot> SaveAdmittedAsync(ReaderGroupsSnapshot draft, long expectedRevision,
        ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ReaderGroupsSnapshot captured = ReaderGroupsValidation.Copy(draft, normalizeNames: true);
        _lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary);
        return Task.Run(() => _lease.RunStoreWrite(admission, () =>
        {
            ReaderGroupsLoadResult current = LoadCore(cancellationToken);
            if (!current.CanWrite) throw new InvalidOperationException(current.Error);
            if (current.Snapshot!.Revision != expectedRevision) throw new ReaderGroupsConflictException();
            // Even an unchanged draft is an admitted mutation request, not a passive read.
            if (ReaderGroupsValidation.SameGroups(current.Snapshot.Groups, captured.Groups)) return current.Snapshot;
            // Only growth is refused: with the limit lowered below the saved count, renames and removals still work.
            if (captured.Groups.Count > _lease.StoreLimits.ReaderGroups && captured.Groups.Count > current.Snapshot.Groups.Count)
                throw PrivateStoreLimit.Refusal(PrivateStoreLimitKind.ReaderGroups, _lease.StoreLimits.ReaderGroups,
                    "The reader group limit is reached.");
            ReaderGroupsSnapshot next = captured with { Revision = checked(expectedRevision + 1) };
            Publish(next, backup: true, admission, cancellationToken);
            AfterAdmittedSaveMutationForTests?.Invoke(admission);
            return next;
        }), cancellationToken);
    }

    public async Task<ReaderGroupsSnapshot> RestoreBackupAsync(string recoveryToken, CancellationToken cancellationToken = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return await RestoreBackupAdmittedAsync(recoveryToken, admission, cancellationToken).ConfigureAwait(false);
    }

    // recoveryToken is only content freshness confirmation, never write authority.
    internal Task<ReaderGroupsSnapshot> RestoreBackupAdmittedAsync(string recoveryToken, ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default)
    {
        _lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary);
        return Task.Run(() => _lease.RunStoreWrite(admission, () =>
        {
            ReaderGroupsLoadResult current = LoadCore(cancellationToken);
            if (current.State != ReaderGroupsLoadState.RecoveryAvailable || current.RecoveryToken != recoveryToken)
                throw new ReaderGroupsConflictException();
            ReaderGroupsSnapshot recovered = current.Snapshot! with { Revision = checked(current.Snapshot!.Revision + 1) };
            // AtomicJsonFile preserves the corrupt primary and leaves the valid backup intact.
            Publish(recovered, backup: false, admission, cancellationToken);
            AfterAdmittedRestoreMutationForTests?.Invoke(admission);
            return recovered;
        }), cancellationToken);
    }

    private void Publish(ReaderGroupsSnapshot snapshot, bool backup, ProfileAdmissionToken admission, CancellationToken cancellationToken)
    {
        ReaderGroupsValidation.Copy(snapshot);
        string json = JsonSerializer.Serialize(snapshot, ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot);
        if (Encoding.UTF8.GetByteCount(json) > ReaderGroupsValidation.MaximumBytes)
            throw new InvalidDataException("Reader groups exceed the 16 MiB file limit.");
        if (backup && File.Exists(_path))
        {
            Read(_path + ".bak", out bool futureBackup, out _);
            if (futureBackup) throw new InvalidOperationException(
                "The available backup uses a newer reader groups schema. No files were changed.");
        }
        if (backup) BeforeAdmittedSaveMutationForTests?.Invoke(admission);
        else BeforeAdmittedRestoreMutationForTests?.Invoke(admission);
        try { AtomicJsonFile.WriteAllText(_path, json, backup, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Distinguish an already published write from a pre-commit failure; never apply twice.
            try
            {
                ReaderGroupsSnapshot? committed = Read(_path, out _, out _);
                if (committed?.Revision == snapshot.Revision && ReaderGroupsValidation.SameGroups(committed.Groups, snapshot.Groups)) return;
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            throw;
        }
    }

    private ReaderGroupsLoadResult LoadCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReaderGroupsSnapshot? main;
        string? error = null;
        string? mainHash = null;
        try
        {
            main = Read(_path, out bool future, out mainHash);
            if (future) return new(null, ReaderGroupsLoadState.FutureVersion, _path, "These reader groups were written by a newer version. They are read-only.");
            if (main != null) return new(main, ReaderGroupsLoadState.Ready, _path);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        { error = exception.Message; }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ReaderGroupsSnapshot? backup = Read(_path + ".bak", out bool futureBackup, out string? backupHash);
            if (futureBackup) return new(null, ReaderGroupsLoadState.FutureVersion, _path, "The available backup uses a newer reader groups schema. No files were changed.");
            if (backup != null)
                return new(backup, ReaderGroupsLoadState.RecoveryAvailable, _path,
                    "The primary groups file is missing or invalid. Confirm restoration of the valid backup before editing. " + error,
                    (mainHash ?? HashFile(_path)) + ":" + backupHash);
            if (error == null) return new(ReaderGroupsSnapshot.Empty, ReaderGroupsLoadState.Ready, _path);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        { error = (error ?? "Primary file missing.") + " Backup: " + exception.Message; }
        return new(null, ReaderGroupsLoadState.Invalid, _path, error ?? "No valid reader groups backup is available.");
    }

    private static string HashFile(string path)
    {
        try { using FileStream stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        catch (FileNotFoundException) { return "missing"; }
    }

    private static ReaderGroupsSnapshot? Read(string path, out bool future, out string? hash)
    {
        future = false; hash = null;
        byte[] bytes;
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (stream.Length > ReaderGroupsValidation.MaximumBytes) throw new InvalidDataException("Reader groups exceed the 16 MiB file limit.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return null; }
        hash = Convert.ToHexString(SHA256.HashData(bytes));
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema))
            throw new InvalidDataException("Reader groups schema version is missing.");
        if (schema > 1) { future = true; return null; }
        ReaderGroupsSnapshot snapshot = JsonSerializer.Deserialize(bytes, ReaderGroupsJsonContext.Default.ReaderGroupsSnapshot)
            ?? throw new InvalidDataException("Reader groups file is empty.");
        return ReaderGroupsValidation.Copy(snapshot);
    }
}
