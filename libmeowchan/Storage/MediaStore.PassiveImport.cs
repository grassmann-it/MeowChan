using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Storage;

internal enum PassiveMediaCandidatePhase { JournalFlushed, PayloadFlushed, PayloadRenamed, ManifestSaved, ReadyJournalSaved }
internal enum PassiveMediaCandidateStatus { Ready, Incomplete, Invalid, Busy }
internal sealed record PassiveMediaImportCandidate(string Directory, Guid OperationId, string PackageFingerprint,
    ResourceId Resource, DateTimeOffset ImportedAtUtc, string ManifestGeneration, ImmutableArray<MediaAttachmentBinding> Bindings);
internal sealed record PassiveMediaCandidateInspection(PassiveMediaCandidateStatus Status, PassiveMediaImportCandidate? Candidate = null);
internal sealed class PassiveMediaCandidateWriteException(string directory, Exception error)
    : IOException("Passive media candidate preparation failed; the inactive candidate is retained.", error)
{ internal string CandidateDirectory { get; } = directory; }
internal sealed class PassiveMediaCandidateCanceledException(string directory, CancellationToken token)
    : OperationCanceledException("Passive media candidate preparation was cancelled; the inactive candidate is retained.", token)
{ internal string CandidateDirectory { get; } = directory; }

public sealed partial class MediaStore
{
    private const string PassiveCandidatePrefix = ".meowchan-passive-";
    private const string PassiveCandidateJournalName = ".meowchan-import-candidate.json";
    private const int MaximumCandidateJournalBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions CandidateJsonOptions = new()
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    /// <summary>
    /// Produces a NEW INACTIVE candidate root, never changes a registered root or catalog. Only
    /// selected exact original observations from the owned validated stage can become bindings.
    /// Ready means verified local files, NOT catalog/publication authority. Conflicting candidates
    /// must remain outside the active resolver roots until the future fenced SQL coordinator acts.
    /// </summary>
    internal Task<PassiveMediaImportCandidate> MaterializePassiveImportCandidateAsync(
        NativeTransferImportStage stage, ResourceId resource, IReadOnlySet<string> revisionKeys,
        string candidateRoot, Guid operationId, DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken = default, Action<PassiveMediaCandidatePhase, string>? checkpoint = null)
        => MaterializePassiveImportCandidateCoreAsync(stage, stage.Package, resource, revisionKeys, candidateRoot,
            operationId, importedAtUtc, cancellationToken, checkpoint, null);

    // The coordinator supplies the exact immutable metadata captured at plan issuance. Only
    // payload copies consult the owned stage handle; no package/source reinspection occurs.
    internal Task<PassiveMediaImportCandidate> MaterializePassiveImportCandidateAdmittedAsync(
        ProfileAdmissionToken admission, NativeMediaImportPlan plan,
        CancellationToken cancellationToken = default, Action<string>? childDirectoryCreated = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Owner.EnsureTransferAdmission(admission);
        return MaterializePassiveImportCandidateCoreAsync(plan.Stage, plan.Package, plan.Resource,
            plan.Attachments.Select(item => item.RevisionKey).ToHashSet(StringComparer.Ordinal),
            plan.OperationsParent, plan.OperationId, plan.ImportedAtUtc, cancellationToken, null, childDirectoryCreated);
    }

    private async Task<PassiveMediaImportCandidate> MaterializePassiveImportCandidateCoreAsync(
        NativeTransferImportStage stage, NativeTransferPackage package, ResourceId resource, IReadOnlySet<string> revisionKeys,
        string candidateRoot, Guid operationId, DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken, Action<PassiveMediaCandidatePhase, string>? checkpoint,
        Action<string>? childDirectoryCreated)
    {
        ArgumentNullException.ThrowIfNull(stage); ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(revisionKeys);
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty || importedAtUtc.UtcDateTime == DateTime.MinValue)
            throw new ArgumentException("An import operation and timestamp are required.");
        ArchiveValidation.ValidateId(resource);
        string parent = ValidateCandidateDirectory(candidateRoot, requireCandidateName: false);
        // Copy the caller's selection before yielding; its comparer/mutations do not define identity.
        string[] keys = revisionKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (keys.Length is 0 or > 100_000 || keys.Any(key => !IsCandidateRevisionKey(key))
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidDataException("Invalid passive media selection.");
        TransferThread thread = package.Catalog.Threads.SingleOrDefault(item => item.Id == resource)
            ?? throw new InvalidDataException("The thread does not belong to the stage.");
        Dictionary<string, PassiveCandidateInput> available = new(StringComparer.Ordinal);
        foreach ((string post, TransferAttachment attachment) in thread.Posts
            .SelectMany(post => post.Attachments.Select(attachment => (post.Metadata.PostId, attachment)))
            .Concat(thread.RetainedBindings.Select(binding => (binding.PostId, binding.Attachment))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedAttachment raw = attachment.Metadata;
            if (raw.MediaId == null || raw.Url == null) continue;
            MediaProviderMetadata provider = new(raw.MediaId, raw.Variant, raw.Url, raw.OriginalFileName,
                raw.ExpectedSize, raw.ExpectedMd5, raw.ExpectedSha256)
            { ExpectedSha1 = raw.ExpectedSha1, ProviderSizeHint = raw.ProviderSizeHint };
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, post, raw.Position, provider);
            if (!available.TryAdd(key, new(post, attachment, provider)))
                throw new InvalidDataException("An incoming attachment identity is ambiguous.");
        }
        Dictionary<string, (MediaManifestEntry Entry, MediaAttachmentBinding Binding)> planned = new(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!available.TryGetValue(key, out PassiveCandidateInput? input)
                || input.Attachment.Role != TransferMediaRole.Original || input.Provider.Variant != "original"
                || input.Attachment.Status != TransferContentStatus.Contained || input.Attachment.PayloadSha256 == null)
                throw new InvalidDataException("The selection is not a contained original observation.");
            TransferAttachment attachment = input.Attachment;
            TransferMeasuredBlob measured = package.Envelope.MeasuredPayloads[attachment.PayloadSha256];
            MediaActualIntegrity actual = new(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 };
            ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(input.Provider, actual);
            string requestedName = key[7..] + ".original.bin";
            MediaManifestEntry entry = new()
            {
                MediaIdentifier = input.Provider.MediaId, Variant = "original", SourceUrl = input.Provider.Url,
                FileName = measured.Sha256 + ".original.bin", Length = measured.Length, Sha256 = measured.Sha256,
                Md5 = measured.Md5, Sha1 = measured.Sha1, ExpectedMd5 = input.Provider.ExpectedMd5,
                OriginalFileName = input.Provider.OriginalFileName
            };
            MediaImportedHistory? history = attachment.History is not { } observed ? null
                : new(observed.Kind switch
                { "strict" => MediaAcceptanceKind.Strict, "provider-mismatch" => MediaAcceptanceKind.ExplicitProviderMismatch,
                    _ => throw new InvalidDataException("Invalid imported history.") }, observed.AcceptedAtUtc, observed.SizeMismatch, observed.Md5Mismatch);
            MediaAttachmentBinding binding = new(new(resource, input.PostId, attachment.Metadata.Position, key),
                MediaRevisionKeys.CreateAcquisitionKey(resource, input.Provider), GetAssetId(requestedName, entry),
                input.Provider, actual, new(MediaAcceptanceKind.PassiveImport, assessment.SizeMismatch, assessment.Md5Mismatch, importedAtUtc))
            { PassiveImport = new(operationId.ToString("D"), package.Envelope.Fingerprint,
                importedAtUtc, attachment.Metadata.AttachmentRevisionKey, history) };
            MediaIntegrityValidation.ValidateBinding(key, binding);
            planned.Add(key, (entry, binding));
        }
        // No writes before all selection/provenance validation has succeeded.
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.Combine(parent, PassiveCandidatePrefix + Guid.NewGuid().ToString("N"));
        ArchiveExportMetadataFile.RejectLinks(directory);
        if (File.Exists(directory) || System.IO.Directory.Exists(directory))
            throw new IOException("The new candidate path is occupied.");
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        childDirectoryCreated?.Invoke(directory);
        PassiveMediaImportCandidate? ready = null;
        try
        {
            ArchiveExportMetadataFile.RejectLinks(directory);
            if (System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidDataException("The candidate directory is not new and empty.");
            using FileStream lease = await AcquireDirectoryLeaseAsync(directory, cancellationToken).ConfigureAwait(false);
            PassiveCandidateJournal journal = new(1, "copying", operationId.ToString("D"), package.Envelope.Fingerprint,
                resource, importedAtUtc, keys, null);
            await WriteCandidateJournalAsync(directory, journal, createOnly: true, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(PassiveMediaCandidatePhase.JournalFlushed, directory);
            foreach (var group in planned.Values.GroupBy(value => value.Entry.Sha256, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string temporary = Path.Combine(directory, ".meowchan-payload-" + Guid.NewGuid().ToString("N") + ".part");
                await using (FileStream output = OpenCandidateNewFile(temporary))
                {
                    TransferMeasuredBlob measured = await stage.CopyPayloadToAsync(group.Key, output, cancellationToken).ConfigureAwait(false);
                    if (measured != package.Envelope.MeasuredPayloads[group.Key]) throw new InvalidDataException("The payload changed.");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(flushToDisk: true);
                    checkpoint?.Invoke(PassiveMediaCandidatePhase.PayloadFlushed, directory);
                }
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(directory, group.First().Entry.FileName);
                ArchiveExportMetadataFile.RejectLinks(temporary); ArchiveExportMetadataFile.RejectLinks(destination);
                _moveFile(temporary, destination, false);
                checkpoint?.Invoke(PassiveMediaCandidatePhase.PayloadRenamed, directory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            MediaManifest manifest = new() { Version = ManifestVersion };
            foreach (var pair in planned)
            {
                string requested = pair.Key[7..] + ".original.bin";
                manifest.Entries.Add(requested, pair.Value.Entry); AddAsset(manifest, requested, pair.Value.Entry);
                manifest.AttachmentBindings.Add(pair.Key, pair.Value.Binding);
            }
            ValidateManifestBindings(manifest);
            await SaveManifestAsync(directory, new(manifest, false, true, null, null), cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(PassiveMediaCandidatePhase.ManifestSaved, directory);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
            MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, cancellationToken).ConfigureAwait(false);
            if (snapshot.Status != MediaManifestStatus.Available || snapshot.Generation == null)
                throw new InvalidDataException("The candidate manifest failed verification.");
            journal = journal with { Phase = "ready", ManifestGeneration = snapshot.Generation };
            await VerifyCandidateSnapshotAsync(journal, directory, snapshot, cancellationToken).ConfigureAwait(false);
            // The final cancellation check is before this noncancelled completion marker. File
            // content was flushed, renamed and verified before Ready can become observable.
            cancellationToken.ThrowIfCancellationRequested();
            await WriteCandidateJournalAsync(directory, journal, createOnly: false, CancellationToken.None).ConfigureAwait(false);
            ready = CandidateFrom(journal, directory, snapshot);
            checkpoint?.Invoke(PassiveMediaCandidatePhase.ReadyJournalSaved, directory);
            return ready;
        }
        catch (OperationCanceledException) when (ready != null) { return ready; }
        catch (OperationCanceledException) { throw new PassiveMediaCandidateCanceledException(directory, cancellationToken); }
        catch (Exception error) when (CandidateIoError(error)) { throw new PassiveMediaCandidateWriteException(directory, error); }
        // Deliberately no recursive cleanup: incomplete/unknown candidates remain INACTIVE.
    }

    /// <summary>Read-only reconciliation of an inactive root; never registers, resumes or repairs it.</summary>
    internal async Task<PassiveMediaCandidateInspection> InspectPassiveImportCandidateAsync(string directory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            directory = ValidateCandidateDirectory(directory, requireCandidateName: true);
            string lockPath = Path.Combine(directory, DirectoryLeaseFileName);
            ValidateCandidateRegularFile(lockPath);
            using FileStream lease = await OpenExistingCandidateLeaseAsync(lockPath, cancellationToken).ConfigureAwait(false);
            PassiveCandidateJournal journal = await ReadCandidateJournalAsync(directory, cancellationToken).ConfigureAwait(false);
            if (journal.Phase == "copying")
            {
                await VerifyIncompleteCandidateAsync(journal, directory, cancellationToken).ConfigureAwait(false);
                return new(PassiveMediaCandidateStatus.Incomplete);
            }
            ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
            MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, cancellationToken).ConfigureAwait(false);
            await VerifyCandidateSnapshotAsync(journal, directory, snapshot, cancellationToken).ConfigureAwait(false);
            return new(PassiveMediaCandidateStatus.Ready, CandidateFrom(journal, directory, snapshot));
        }
        catch (PassiveCandidateBusyException) { return new(PassiveMediaCandidateStatus.Busy); }
        catch (Exception error) when (CandidateIoError(error) || error is ArgumentException or FormatException or NullReferenceException)
        { return new(PassiveMediaCandidateStatus.Invalid); }
    }

    // A Ready DTO is not a lease. Verify under the EXISTING child lock and keep that very
    // handle alive across the short asynchronous SQL callback. No ProfileLease monitor here.
    internal async Task<T> WithVerifiedPassiveImportCandidateAdmittedAsync<T>(
        ProfileAdmissionToken admission, NativeMediaImportPlan plan,
        PassiveMediaImportCandidate expected, Func<Task<T>> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Owner.EnsureTransferAdmission(admission);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(callback);
        string directory = ValidateCandidateDirectory(expected.Directory, requireCandidateName: true);
        if (!PathsEqual(Path.GetDirectoryName(directory)!, plan.OperationsParent)
            || expected.OperationId != plan.OperationId || expected.Resource != plan.Resource
            || expected.PackageFingerprint != plan.Package.Envelope.Fingerprint || expected.ImportedAtUtc != plan.ImportedAtUtc)
            throw new InvalidDataException("The candidate is not owned by this import plan.");
        using FileStream lease = await OpenExistingCandidateLeaseAsync(Path.Combine(directory, DirectoryLeaseFileName), cancellationToken).ConfigureAwait(false);
        PassiveCandidateJournal journal = await ReadCandidateJournalAsync(directory, cancellationToken).ConfigureAwait(false);
        string[] keys = plan.Attachments.Select(item => item.RevisionKey).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (journal.Phase != "ready" || journal.OperationId != plan.OperationId.ToString("D")
            || journal.Resource != plan.Resource || journal.PackageFingerprint != plan.Package.Envelope.Fingerprint
            || journal.ImportedAtUtc != plan.ImportedAtUtc || journal.ManifestGeneration != expected.ManifestGeneration
            || !keys.SequenceEqual(journal.RevisionKeys, StringComparer.Ordinal))
            throw new InvalidDataException("The candidate completion marker differs from the issued plan.");
        ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
        MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, cancellationToken).ConfigureAwait(false);
        await VerifyCandidateSnapshotAsync(journal, directory, snapshot, cancellationToken).ConfigureAwait(false);
        if (expected.Bindings.Length != keys.Length || !expected.Bindings.OrderBy(b => b.Reference.RevisionKey, StringComparer.Ordinal)
            .SequenceEqual(keys.Select(key => snapshot.AttachmentBindings[key])))
            throw new InvalidDataException("The candidate bindings changed after materialization.");
        foreach (NativeMediaImportAttachment input in plan.Attachments)
        {
            MediaAttachmentBinding binding = snapshot.AttachmentBindings[input.RevisionKey];
            TransferMeasuredBlob blob = plan.Package.Envelope.MeasuredPayloads[input.Incoming.PayloadSha256!];
            MediaActualIntegrity actual = new(blob.Length, blob.Md5, blob.Sha256) { Sha1 = blob.Sha1 };
            TransferHistoricalIntegrity? incomingHistory = input.Incoming.History;
            MediaImportedHistory? history = incomingHistory == null ? null : new(
                incomingHistory.Kind == "strict" ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch,
                incomingHistory.AcceptedAtUtc, incomingHistory.SizeMismatch, incomingHistory.Md5Mismatch);
            ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(input.Provider, actual);
            if (binding.Reference != new AttachmentRevisionReference(plan.Resource, input.PostId, input.Incoming.Metadata.Position, input.RevisionKey)
                || binding.Provider != input.Provider || binding.Actual != actual
                || binding.AcquisitionRevisionKey != MediaRevisionKeys.CreateAcquisitionKey(plan.Resource, input.Provider)
                || binding.Acceptance != new MediaAcceptanceRecord(MediaAcceptanceKind.PassiveImport,
                    assessment.SizeMismatch, assessment.Md5Mismatch, plan.ImportedAtUtc)
                || binding.PassiveImport != new MediaPassiveImportRecord(plan.OperationId.ToString("D"), plan.Package.Envelope.Fingerprint,
                    plan.ImportedAtUtc, input.Incoming.Metadata.AttachmentRevisionKey, history))
                throw new InvalidDataException("The candidate observation differs from the issued plan.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await callback().ConfigureAwait(false);
    }

    private static async Task<PassiveCandidateJournal> ReadCandidateJournalAsync(string directory, CancellationToken token)
    {
        string path = Path.Combine(directory, PassiveCandidateJournalName);
        byte[] bytes = await ReadCandidateJournalBytesAsync(path, token).ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        ValidateUniqueJson(json.RootElement);
        PassiveCandidateJournal journal = JsonSerializer.Deserialize<PassiveCandidateJournal>(bytes, CandidateJsonOptions)
            ?? throw new InvalidDataException("Missing candidate journal.");
        ValidateCandidateJournal(journal);
        return journal;
    }

    private async Task VerifyCandidateSnapshotAsync(PassiveCandidateJournal journal, string directory,
        MediaAssetSnapshot snapshot, CancellationToken cancellationToken, bool allowIncompleteTemporaries = false)
    {
        if (snapshot.Status != MediaManifestStatus.Available || snapshot.ManifestVersion != 5
            || snapshot.Generation != journal.ManifestGeneration || snapshot.AttachmentBindings.Count != journal.RevisionKeys.Length
            || !snapshot.AttachmentBindings.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(journal.RevisionKeys, StringComparer.Ordinal))
            throw new InvalidDataException("The candidate manifest differs from its completion marker.");
        Dictionary<string, FileDigest> measuredPaths = new(StringComparer.Ordinal);
        HashSet<string> assets = new(StringComparer.Ordinal), files = new(StringComparer.Ordinal)
            { PassiveCandidateJournalName, DirectoryLeaseFileName, ManifestFileName };
        foreach (MediaAttachmentBinding binding in snapshot.AttachmentBindings.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MediaPassiveImportRecord? import = binding.PassiveImport;
            if (binding.Reference.Resource != journal.Resource || binding.Acceptance.Kind != MediaAcceptanceKind.PassiveImport
                || import == null || import.OperationId != journal.OperationId || import.PackageFingerprint != journal.PackageFingerprint
                || import.ImportedAtUtc != journal.ImportedAtUtc)
                throw new InvalidDataException("The candidate binding differs from its local operation.");
            MediaAssetReference asset = snapshot.FindById(binding.AssetId)!;
            if (asset.RequestedFileName != binding.Reference.RevisionKey[7..] + ".original.bin"
                || Path.GetFileName(asset.Path) != binding.Actual.Sha256 + ".original.bin")
                throw new InvalidDataException("The candidate uses an unexpected local path.");
            files.Add(Path.GetFileName(asset.Path));
            if (!assets.Add(asset.AssetId)) continue;
            ArchiveExportMetadataFile.RejectLinks(asset.Path);
            if (!measuredPaths.TryGetValue(asset.Path, out FileDigest? actual))
            {
                actual = await HashCandidateFileAsync(asset.Path, cancellationToken, asset.Length).ConfigureAwait(false);
                if (actual == null) throw new InvalidDataException("The candidate file is missing or incomplete.");
                measuredPaths.Add(asset.Path, actual);
            }
            if (!MediaIntegrityValidation.MatchesMeasured(binding.Actual, Actual(actual)))
                throw new InvalidDataException("The candidate bytes failed verification.");
        }
        if (snapshot.Count != assets.Count) throw new InvalidDataException("Unbound candidate assets.");
        foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            ValidateCandidateRegularFile(path);
            if (allowIncompleteTemporaries && (IsCandidateTemporaryName(name, ".meowchan-payload-")
                || IsCandidateTemporaryName(name, ".meowchan-journal-")
                || IsCandidateTemporaryName(name, ".meowchan-manifest-"))) continue;
            if (!files.Remove(name)) throw new InvalidDataException("Unexpected candidate content.");
        }
        if (files.Count != 0) throw new InvalidDataException("Missing candidate content.");
    }

    // Copying is not completion authority. Inspect recognizable partial files without parsing
    // them as finished data; validate every finalized payload and any published manifest.
    private async Task VerifyIncompleteCandidateAsync(PassiveCandidateJournal journal, string directory,
        CancellationToken token)
    {
        bool hasManifest = false;
        List<string> payloads = [];
        int count = 0;
        foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (++count > journal.RevisionKeys.Length + 6)
                throw new InvalidDataException("Unexpected incomplete candidate content.");
            ValidateCandidateRegularFile(path);
            string name = Path.GetFileName(path);
            if (name is PassiveCandidateJournalName or DirectoryLeaseFileName) continue;
            if (name == ManifestFileName) { hasManifest = true; continue; }
            if (IsCandidateTemporaryName(name, ".meowchan-payload-")
                || IsCandidateTemporaryName(name, ".meowchan-journal-")
                || IsCandidateTemporaryName(name, ".meowchan-manifest-")) continue;
            if (name.Length != 77 || !name.EndsWith(".original.bin", StringComparison.Ordinal)
                || !IsLowerCandidateHash(name[..64]))
                throw new InvalidDataException("Unknown incomplete candidate file.");
            payloads.Add(path);
        }
        if (hasManifest)
        {
            MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, token).ConfigureAwait(false);
            // The copying journal deliberately has no completion-generation claim. A complete
            // manifest must nevertheless match the operation, selected bindings and real bytes.
            await VerifyCandidateSnapshotAsync(journal with { ManifestGeneration = snapshot.Generation },
                directory, snapshot, token, allowIncompleteTemporaries: true).ConfigureAwait(false);
            return;
        }
        foreach (string path in payloads)
        {
            FileDigest? digest = await HashCandidateFileAsync(path, token).ConfigureAwait(false);
            if (digest == null || digest.Sha256 != Path.GetFileName(path)[..64])
                throw new InvalidDataException("An incomplete candidate payload failed verification.");
        }
    }

    private static bool IsCandidateTemporaryName(string name, string prefix)
        => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".part", StringComparison.Ordinal)
            && name.Length == prefix.Length + 32 + 5
            && Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out Guid value)
            && value.ToString("N") == name.Substring(prefix.Length, 32);

    private async Task<FileDigest?> HashCandidateFileAsync(string path, CancellationToken token, long? length = null)
    {
        ValidateCandidateRegularFile(path);
        FileStamp? before = ReadFileStamp(path);
        if (before == null || (length.HasValue && before.Length != length.Value)) return null;
        FileDigest? digest = await TryHashFileAsync(path, token, length).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return before == ReadFileStamp(path) ? digest : null;
    }

    private static void ValidateCandidateRegularFile(string path)
    {
        ArchiveExportMetadataFile.RejectLinks(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("A candidate entry is not a regular file.");
        if (OperatingSystem.IsWindows()) return;
        // FileAttributes does not distinguish Unix FIFO/socket/device from ordinary files.
        // Use the same normalized, fixed PAL ABI as this net10 runtime, not a platform-specific
        // libc stat layout or a blocking open. No external program or new native package.
        try
        {
            if (CandidateLStat(path, out CandidateFileStatus status) != 0 || (status.Mode & 0xF000) != 0x8000)
                throw new InvalidDataException("A candidate entry is not a regular file.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { throw new NotSupportedException("The runtime cannot safely inspect candidate file types.", error); }
    }

    // ABI declarations only. Reference: dotnet/runtime v10.0.1 (MIT),
    // src/libraries/Common/src/Interop/Unix/System.Native/Interop.Stat.cs and
    // src/native/libs/System.Native/pal_io.h. Unlike libc struct stat this PAL layout is normalized.
    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int CandidateLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out CandidateFileStatus output);
    [StructLayout(LayoutKind.Sequential)]
    private struct CandidateFileStatus
    {
        internal int Flags, Mode;
        internal uint Uid, Gid;
        internal long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec;
        internal long BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        internal uint UserFlags;
    }

    private static string ValidateCandidateDirectory(string directory, bool requireCandidateName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidDataException("An existing absolute candidate root is required.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!PathsEqual(full, Path.TrimEndingDirectorySeparator(directory)))
            throw new InvalidDataException("The candidate root must be canonical and absolute.");
        ArchiveExportMetadataFile.RejectLinks(full);
        if (!System.IO.Directory.Exists(full)) throw new InvalidDataException("The candidate root does not exist.");
        if (requireCandidateName)
        {
            string name = Path.GetFileName(full);
            if (!name.StartsWith(PassiveCandidatePrefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[PassiveCandidatePrefix.Length..], "N", out Guid suffix)
                || suffix.ToString("N") != name[PassiveCandidatePrefix.Length..])
                throw new InvalidDataException("Not a passive candidate directory.");
        }
        return full;
    }

    private static bool IsCandidateRevisionKey(string? key) => key is { Length: 71 }
        && (key.StartsWith("att-v1:", StringComparison.Ordinal) || key.StartsWith("att-v2:", StringComparison.Ordinal))
        && IsLowerCandidateHash(key[7..]);
    private static bool IsLowerCandidateHash(string? hash) => hash is { Length: 64 }
        && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateCandidateJournal(PassiveCandidateJournal journal)
    {
        if (journal.Version != 1 || journal.Phase is not ("copying" or "ready")
            || !Guid.TryParseExact(journal.OperationId, "D", out Guid operation) || operation == Guid.Empty
            || operation.ToString("D") != journal.OperationId || !IsLowerCandidateHash(journal.PackageFingerprint)
            || journal.Resource == null || journal.ImportedAtUtc.UtcDateTime == DateTime.MinValue
            || journal.RevisionKeys == null || journal.RevisionKeys.Length is 0 or > 100_000
            || journal.RevisionKeys.Any(key => !IsCandidateRevisionKey(key))
            || !journal.RevisionKeys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(journal.RevisionKeys, StringComparer.Ordinal)
            || journal.RevisionKeys.Distinct(StringComparer.Ordinal).Count() != journal.RevisionKeys.Length
            || (journal.Phase == "ready" ? !IsLowerCandidateHash(journal.ManifestGeneration) : journal.ManifestGeneration != null))
            throw new InvalidDataException("Invalid candidate journal.");
        ArchiveValidation.ValidateId(journal.Resource);
    }
    private static FileStream OpenCandidateNewFile(string path)
    {
        ArchiveExportMetadataFile.RejectLinks(path);
        FileStreamOptions options = new()
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 64 * 1024, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }
    private async Task WriteCandidateJournalAsync(string directory, PassiveCandidateJournal journal,
        bool createOnly, CancellationToken token)
    {
        ValidateCandidateJournal(journal);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, CandidateJsonOptions);
        if (bytes.Length > MaximumCandidateJournalBytes) throw new InvalidDataException("The candidate journal exceeds its limit.");
        string target = Path.Combine(directory, PassiveCandidateJournalName);
        string temporary = createOnly ? target : Path.Combine(directory, ".meowchan-journal-" + Guid.NewGuid().ToString("N") + ".part");
        await using (FileStream output = OpenCandidateNewFile(temporary))
        { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(flushToDisk: true); }
        if (!createOnly)
        { ArchiveExportMetadataFile.RejectLinks(target); _moveFile(temporary, target, true); }
    }
    private static async Task<byte[]> ReadCandidateJournalBytesAsync(string path, CancellationToken token)
    {
        ValidateCandidateRegularFile(path);
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (input.Length > MaximumCandidateJournalBytes) throw new InvalidDataException("The candidate journal exceeds its limit.");
        using MemoryStream output = new(); byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumCandidateJournalBytes) throw new InvalidDataException("The candidate journal exceeds its limit.");
            output.Write(buffer, 0, read);
        }
    }
    private async Task<FileStream> OpenExistingCandidateLeaseAsync(string path, CancellationToken token)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested(); ValidateCandidateRegularFile(path);
            try { return new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (IsSharingViolation(error))
            {
                if (elapsed.Elapsed >= _directoryLeaseTimeout) throw new PassiveCandidateBusyException();
                await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
            }
        }
    }
    private static PassiveMediaImportCandidate CandidateFrom(PassiveCandidateJournal journal, string directory, MediaAssetSnapshot snapshot)
        => new(directory, Guid.ParseExact(journal.OperationId, "D"), journal.PackageFingerprint, journal.Resource,
            journal.ImportedAtUtc, journal.ManifestGeneration!, journal.RevisionKeys.Select(key => snapshot.AttachmentBindings[key]).ToImmutableArray());
    private static bool CandidateIoError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or NotSupportedException or ObjectDisposedException or System.Security.SecurityException or JsonException;
    private sealed class PassiveCandidateBusyException : IOException { }
    private sealed record PassiveCandidateInput(string PostId, TransferAttachment Attachment, MediaProviderMetadata Provider);
    private sealed record PassiveCandidateJournal(int Version, string Phase, string OperationId, string PackageFingerprint,
        ResourceId Resource, DateTimeOffset ImportedAtUtc, string[] RevisionKeys, string? ManifestGeneration);
}
