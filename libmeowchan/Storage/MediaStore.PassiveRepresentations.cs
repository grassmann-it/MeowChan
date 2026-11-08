using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    // Persisted evidence is not an execution capability. The exact owner-issued plan and
    // live Exclusive admission must be checked before the candidate path is consulted.
    internal async Task<PassiveMediaImportCandidate> MaterializePassiveImportCandidateAdmittedAsync(
        ProfileAdmissionToken admission, NativeMediaImportPlan plan,
        NativeMediaPersistedImportOperation preparing, CancellationToken cancellationToken = default,
        Action<string>? childDirectoryCreated = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NativeMediaImportV2State state = plan.Owner.RequireIssuedNativeMediaImportV2Plan(admission, plan);
        ArgumentNullException.ThrowIfNull(preparing);
        if (!state.MatchesOperation(preparing, NativeMediaImportOperationState.Preparing))
            throw new InvalidDataException("The preparing operation differs from the issued representation import plan.");
        cancellationToken.ThrowIfCancellationRequested();
        PassiveMediaImportCandidate candidate = await MaterializePassiveImportCandidateAsync(
            state.Materialization, state.Selection, plan.OperationsParent, plan.OperationId, plan.ImportedAtUtc,
            cancellationToken, checkpoint: null, childDirectoryCreated: childDirectoryCreated).ConfigureAwait(false);
        // Keep the low-level materializer's post-Ready cancellation semantics. This checks
        // its complete semantic result, never just the original-only Bindings projection.
        if (!PathsEqual(Path.GetDirectoryName(candidate.Directory)!, plan.OperationsParent)
            || !state.MatchesCandidate(candidate))
            throw new PassiveMediaCandidateWriteException(candidate.Directory,
                new InvalidDataException("The candidate differs from the issued representation import plan."));
        return candidate;
    }

    // Reuse the strict Journal2 inspector only while holding the EXISTING child lock.
    // The awaited callback shares that same lease; no DTO/token can transfer verification.
    internal async Task<T> WithVerifiedPassiveImportCandidateAdmittedAsync<T>(
        ProfileAdmissionToken admission, NativeMediaImportPlan plan,
        NativeMediaPersistedImportOperation prepared, Func<Task<T>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        NativeMediaImportV2State state = plan.Owner.RequireIssuedNativeMediaImportV2Plan(admission, plan);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(callback);
        if (!state.MatchesOperation(prepared, NativeMediaImportOperationState.Prepared))
            throw new InvalidDataException("The prepared operation differs from the issued representation import plan.");
        cancellationToken.ThrowIfCancellationRequested();
        if (prepared.Full.Directory is not { } persistedDirectory)
            throw new InvalidDataException("The prepared operation has no candidate directory.");
        string directory = ValidateCandidateDirectory(persistedDirectory, requireCandidateName: true);
        if (!PathsEqual(Path.GetDirectoryName(directory)!, plan.OperationsParent))
            throw new InvalidDataException("The candidate is not an immediate child of this import plan's parent.");
        using FileStream lease = await OpenExistingCandidateLeaseAsync(Path.Combine(directory, DirectoryLeaseFileName),
            cancellationToken).ConfigureAwait(false);
        PassiveMediaCandidateInspection inspection = await InspectPassiveImportCandidateV2Async(directory,
            cancellationToken).ConfigureAwait(false);
        if (inspection.Status != PassiveMediaCandidateStatus.Ready || inspection.Candidate is not { } candidate
            || !PathsEqual(candidate.Directory, directory)
            || candidate.ManifestGeneration != prepared.Full.ManifestGeneration || !state.MatchesCandidate(candidate))
            throw new InvalidDataException("The verified candidate differs from the prepared representation import operation.");
        cancellationToken.ThrowIfCancellationRequested();
        return await callback().ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares only a fresh INACTIVE Manifest6 child. A semantic selection and a Ready
    /// journal are not profile admission, registered-root publication or source authority.
    /// </summary>
    internal async Task<PassiveMediaImportCandidate> MaterializePassiveImportCandidateAsync(
        NativeMediaMaterializationPlan plan, NativeMediaCandidateSelection selection, string candidateRoot,
        Guid operationId, DateTimeOffset importedAtUtc, CancellationToken cancellationToken = default,
        Action<PassiveMediaCandidatePhase, string>? checkpoint = null, Action<string>? childDirectoryCreated = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        plan.ValidateSelection(selection);
        if (selection.IsEmpty || !plan.Package.IsChanV4 || plan.Package.PrivateMetadata != null
            || plan.Package.Envelope.PrivateMetadata != null || plan.Package.Catalog.Threads.Length != 1
            || plan.Package.Catalog.Threads[0].Id != plan.Resource)
            throw new InvalidDataException("A nonempty selection from one public chan-v4 thread is required.");
        importedAtUtc = importedAtUtc.ToUniversalTime();
        PassiveRepresentationCandidateJournal journal = new(2, "copying", operationId.ToString("D"),
            plan.Package.Envelope.Fingerprint, plan.Resource, importedAtUtc, selection.CanonicalJson,
            selection.Sha256, null, null);
        // Reconstruct the inventory from the exact persistent selection before any filesystem
        // access. Neither caller-owned DTOs nor a self-declared inventory hash are evidence.
        var decoded = ValidateRepresentationCandidateJournal(journal);
        selection = decoded.Selection;
        plan.ValidateSelection(selection);
        NativeMediaCandidateInventory inventory = decoded.Inventory;
        MediaManifest manifest = CreateRepresentationCandidateManifest(inventory, cancellationToken);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaximumManifestBytes)
            throw new InvalidDataException("The candidate manifest exceeds its limit.");
        _ = EncodeRepresentationCandidateJournal(journal);
        // Also preflight the larger Ready record, not just the copying marker, before writes.
        _ = EncodeRepresentationCandidateJournal(journal with
        { Phase = "ready", ManifestGeneration = new string('0', 64), InventorySha256 = inventory.Sha256 });
        cancellationToken.ThrowIfCancellationRequested();
        string parent = ValidateCandidateDirectory(candidateRoot, requireCandidateName: false);
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
            await WriteRepresentationCandidateJournalAsync(directory, journal, createOnly: true, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(PassiveMediaCandidatePhase.JournalFlushed, directory);
            foreach (NativeMediaCandidatePayload payload in inventory.Payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string temporary = Path.Combine(directory, ".meowchan-payload-" + Guid.NewGuid().ToString("N") + ".part");
                await using (FileStream output = OpenCandidateNewFile(temporary))
                {
                    TransferMeasuredBlob measured = await plan.Stage.CopyPayloadToAsync(payload.Measured.Sha256,
                        output, cancellationToken).ConfigureAwait(false);
                    if (measured != payload.Measured) throw new InvalidDataException("The staged payload changed.");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    checkpoint?.Invoke(PassiveMediaCandidatePhase.PayloadFlushed, directory);
                }
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(directory, payload.FileName);
                ValidateCandidateRegularFile(temporary);
                ArchiveExportMetadataFile.RejectLinks(destination);
                _moveFile(temporary, destination, false);
                checkpoint?.Invoke(PassiveMediaCandidatePhase.PayloadRenamed, directory);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRepresentationCandidateManifestAsync(directory, manifestBytes, cancellationToken).ConfigureAwait(false);
            checkpoint?.Invoke(PassiveMediaCandidatePhase.ManifestSaved, directory);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
            MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, cancellationToken).ConfigureAwait(false);
            journal = journal with { Phase = "ready", ManifestGeneration = snapshot.Generation, InventorySha256 = inventory.Sha256 };
            _ = ValidateRepresentationCandidateJournal(journal);
            await VerifyRepresentationCandidateSnapshotAsync(journal, inventory, directory, snapshot,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Same completion semantics as Journal1: verification and cancellation precede
            // the noncancelled, atomically replaced final marker. No cleanup/activation.
            await WriteRepresentationCandidateJournalAsync(directory, journal, createOnly: false, CancellationToken.None).ConfigureAwait(false);
            ready = RepresentationCandidateFrom(journal, selection, inventory, directory, snapshot);
            checkpoint?.Invoke(PassiveMediaCandidatePhase.ReadyJournalSaved, directory);
            return ready;
        }
        catch (OperationCanceledException) when (ready != null) { return ready; }
        catch (OperationCanceledException) { throw new PassiveMediaCandidateCanceledException(directory, cancellationToken); }
        catch (Exception error) when (CandidateIoError(error) || error is ArgumentException or InvalidOperationException
            or FormatException or KeyNotFoundException or OverflowException)
        { throw new PassiveMediaCandidateWriteException(directory, error); }
        // Unknown/incomplete children, including partial files, deliberately remain INACTIVE.
    }

    // Called only after the existing inspection method has acquired the existing child lock.
    private async Task<PassiveMediaCandidateInspection> InspectPassiveImportCandidateV2Async(string directory, CancellationToken token)
    {
        byte[] bytes = await ReadCandidateJournalBytesAsync(Path.Combine(directory, PassiveCandidateJournalName), token).ConfigureAwait(false);
        ValidateRepresentationJournalEncoding(bytes, token);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateUniqueJson(document.RootElement);
        ValidateRepresentationJournalShape(document.RootElement);
        PassiveRepresentationCandidateJournal journal = JsonSerializer.Deserialize<PassiveRepresentationCandidateJournal>(bytes, CandidateJsonOptions)
            ?? throw new InvalidDataException("Missing candidate journal.");
        var decoded = ValidateRepresentationCandidateJournal(journal);
        if (journal.Phase == "copying")
        {
            await VerifyIncompleteRepresentationCandidateAsync(journal, decoded.Inventory, directory, token).ConfigureAwait(false);
            return new(PassiveMediaCandidateStatus.Incomplete);
        }
        ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
        MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, token).ConfigureAwait(false);
        await VerifyRepresentationCandidateSnapshotAsync(journal, decoded.Inventory, directory, snapshot, token).ConfigureAwait(false);
        return new(PassiveMediaCandidateStatus.Ready,
            RepresentationCandidateFrom(journal, decoded.Selection, decoded.Inventory, directory, snapshot));
    }

    private static async Task<int> ReadCandidateJournalVersionAsync(string directory, CancellationToken token)
    {
        byte[] bytes = await ReadCandidateJournalBytesAsync(Path.Combine(directory, PassiveCandidateJournalName), token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        ValidateUniqueJson(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number is not (1 or 2))
            throw new InvalidDataException("Unsupported candidate journal.");
        // Version1 is still read by its original DTO/options/validator, which reject all
        // unknown Version2 fields (including explicit nulls), never a downlevel projection.
        return number;
    }

    private static (NativeMediaCandidateSelection Selection, NativeMediaCandidateInventory Inventory)
        ValidateRepresentationCandidateJournal(PassiveRepresentationCandidateJournal journal)
    {
        if (journal.Version != 2 || journal.Phase is not ("copying" or "ready")
            || !Guid.TryParseExact(journal.OperationId, "D", out Guid operation) || operation == Guid.Empty
            || operation.ToString("D") != journal.OperationId || !IsLowerCandidateHash(journal.PackageFingerprint)
            || journal.Resource == null || journal.ImportedAtUtc.UtcDateTime == DateTime.MinValue
            || journal.ImportedAtUtc.Offset != TimeSpan.Zero || journal.SelectionJson == null
            || !IsLowerCandidateHash(journal.SelectionSha256)
            || (journal.Phase == "ready"
                ? !IsLowerCandidateHash(journal.ManifestGeneration) || !IsLowerCandidateHash(journal.InventorySha256)
                : journal.ManifestGeneration != null || journal.InventorySha256 != null))
            throw new InvalidDataException("Invalid representation candidate journal.");
        ArchiveValidation.ValidateId(journal.Resource);
        NativeMediaCandidateSelection selection = NativeMediaCandidateSelection.Parse(journal.SelectionJson);
        if (selection.IsEmpty || selection.Sha256 != journal.SelectionSha256
            || selection.Members.Any(m => m.Id.Occurrence.Resource != journal.Resource))
            throw new InvalidDataException("The candidate selection differs from its operation.");
        NativeMediaCandidateInventory inventory = selection.CreateInventory(operation, journal.PackageFingerprint, journal.ImportedAtUtc);
        if (journal.Phase == "ready" && inventory.Sha256 != journal.InventorySha256)
            throw new InvalidDataException("The candidate inventory differs from its canonical selection.");
        return (selection, inventory);
    }

    private static MediaManifest CreateRepresentationCandidateManifest(NativeMediaCandidateInventory inventory, CancellationToken token)
    {
        MediaManifest manifest = new()
        {
            Version = RepresentationManifestVersion,
            OriginalEvidence = inventory.OriginalEvidence.ToArray(),
            DerivedRepresentations = inventory.DerivedRepresentations.ToArray()
        };
        foreach (MediaAttachmentBinding binding in inventory.Bindings)
        {
            token.ThrowIfCancellationRequested();
            string key = binding.Reference.RevisionKey;
            MediaManifestEntry entry = new()
            {
                MediaIdentifier = binding.Provider.MediaId, Variant = "original", SourceUrl = binding.Provider.Url,
                OriginalFileName = binding.Provider.OriginalFileName, ExpectedMd5 = binding.Provider.ExpectedMd5,
                FileName = binding.Actual.Sha256 + ".original.bin", Length = binding.Actual.Length,
                Sha256 = binding.Actual.Sha256, Md5 = binding.Actual.Md5, Sha1 = binding.Actual.Sha1
            };
            string requested = key[7..] + ".original.bin";
            if (GetAssetId(requested, entry) != binding.AssetId || !manifest.Entries.TryAdd(requested, entry)
                || !manifest.AttachmentBindings.TryAdd(key, binding))
                throw new InvalidDataException("The canonical candidate has conflicting original bindings.");
            AddAsset(manifest, requested, entry);
        }
        ValidateManifestBindings(manifest);
        ValidateManifestRepresentations(manifest, token);
        return manifest;
    }

    private async Task VerifyRepresentationCandidateSnapshotAsync(PassiveRepresentationCandidateJournal journal,
        NativeMediaCandidateInventory inventory, string directory, MediaAssetSnapshot snapshot,
        CancellationToken token, bool allowIncompleteTemporaries = false)
    {
        token.ThrowIfCancellationRequested();
        if (snapshot.Status != MediaManifestStatus.Available || snapshot.ManifestVersion != RepresentationManifestVersion
            || !IsLowerCandidateHash(snapshot.Generation)
            || journal.Phase == "ready" && (snapshot.Generation != journal.ManifestGeneration || inventory.Sha256 != journal.InventorySha256)
            || snapshot.AttachmentBindings.Count != inventory.Bindings.Length
            || !snapshot.OriginalEvidence.SequenceEqual(inventory.OriginalEvidence)
            || !snapshot.DerivedRepresentations.SequenceEqual(inventory.DerivedRepresentations))
            throw new InvalidDataException("The candidate manifest differs from its canonical inventory.");
        foreach (MediaAttachmentBinding binding in inventory.Bindings)
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.AttachmentBindings.TryGetValue(binding.Reference.RevisionKey, out MediaAttachmentBinding? actual) || actual != binding)
                throw new InvalidDataException("The candidate binding differs from the selected observation.");
            MediaAssetReference? asset = snapshot.FindById(binding.AssetId);
            if (asset == null || !asset.IsCurrent || asset.Variant != "original" || asset.MediaIdentifier != binding.Provider.MediaId
                || asset.SourceUrl != binding.Provider.Url || asset.OriginalFileName != binding.Provider.OriginalFileName
                || asset.RequestedFileName != binding.Reference.RevisionKey[7..] + ".original.bin"
                || asset.Path != Path.Combine(directory, binding.Actual.Sha256 + ".original.bin")
                || asset.Length != binding.Actual.Length || asset.Sha256 != binding.Actual.Sha256
                || asset.Md5 != binding.Actual.Md5 || asset.Sha1 != binding.Actual.Sha1)
                throw new InvalidDataException("The candidate original asset differs from its binding.");
        }
        if (snapshot.Count != inventory.Bindings.Select(b => b.AssetId).Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("Unbound candidate original assets.");
        // Snapshot loading projects current Entries into Assets for legacy compatibility. Check
        // the unprojected wire too, so that projection cannot hide missing/extra/altered records.
        await VerifyRepresentationCandidateManifestWireAsync(directory, snapshot.Generation!, inventory, token).ConfigureAwait(false);
        // Every semantic member above has been checked against the reconstructed inventory,
        // including its exact parent, source tuple, role and provenance. Physical byte work is
        // separate: shared previews/posters must not multiply reads by the number of parents.
        // A preview sharing a file with a poster still requires the PNG structural proof.
        HashSet<string> posterFiles = RepresentationCandidatePosterFiles(inventory, token);
        foreach (NativeMediaCandidatePayload payload in inventory.Payloads)
            await VerifyRepresentationCandidatePayloadAsync(directory, payload,
                posterFiles.Contains(payload.FileName), token).ConfigureAwait(false);
        VerifyRepresentationCandidateFiles(directory, inventory, allowIncompleteTemporaries, token);
    }

    private static async Task VerifyRepresentationCandidateManifestWireAsync(string directory, string generation,
        NativeMediaCandidateInventory inventory, CancellationToken token)
    {
        ValidateCandidateRegularFile(Path.Combine(directory, ManifestFileName));
        byte[] actual = await ReadManifestBytesAsync(Path.Combine(directory, ManifestFileName), token).ConfigureAwait(false);
        if (Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant() != generation)
            throw new InvalidDataException("The candidate manifest changed during inspection.");
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(CreateRepresentationCandidateManifest(inventory, token), JsonOptions);
        using JsonDocument actualJson = JsonDocument.Parse(actual, new JsonDocumentOptions { MaxDepth = 16 });
        using JsonDocument expectedJson = JsonDocument.Parse(expected, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateUniqueJson(actualJson.RootElement);
        if (!CandidateRepresentationJsonEquals(actualJson.RootElement, expectedJson.RootElement, token))
            throw new InvalidDataException("The candidate manifest wire differs from its exact expected inventory.");
    }

    private async Task VerifyIncompleteRepresentationCandidateAsync(PassiveRepresentationCandidateJournal journal,
        NativeMediaCandidateInventory inventory, string directory, CancellationToken token)
    {
        var expected = inventory.Payloads.ToDictionary(p => p.FileName, StringComparer.Ordinal);
        var finalized = new HashSet<string>(StringComparer.Ordinal);
        bool hasManifest = false;
        int count = 0;
        foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (++count > inventory.Payloads.Length + 6) throw new InvalidDataException("Unexpected incomplete candidate content.");
            ValidateCandidateRegularFile(path);
            string name = Path.GetFileName(path);
            if (name is PassiveCandidateJournalName or DirectoryLeaseFileName) continue;
            if (name == ManifestFileName) { hasManifest = true; continue; }
            if (IsRepresentationCandidateTemporary(name)) continue;
            if (!expected.ContainsKey(name) || !finalized.Add(name))
                throw new InvalidDataException("Unknown incomplete representation candidate file.");
        }
        if (hasManifest)
        {
            MediaAssetSnapshot snapshot = await ReadAssetSnapshotAsync(directory, token).ConfigureAwait(false);
            // No invented Ready generation/hash; a copying marker remains Incomplete even
            // if all expected files and a complete Manifest6 have already been published.
            await VerifyRepresentationCandidateSnapshotAsync(journal, inventory, directory, snapshot,
                token, allowIncompleteTemporaries: true).ConfigureAwait(false);
            return;
        }
        // Before a manifest exists, the canonical inventory supplies every exact descriptor.
        // Check only finalized outputs; permitted temporary files never count as payload proof.
        HashSet<string> posterFiles = RepresentationCandidatePosterFiles(inventory, token);
        foreach (string name in finalized)
            await VerifyRepresentationCandidatePayloadAsync(directory, expected[name],
                posterFiles.Contains(name), token).ConfigureAwait(false);
    }

    private static HashSet<string> RepresentationCandidatePosterFiles(NativeMediaCandidateInventory inventory,
        CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (MediaDerivedRepresentation member in inventory.DerivedRepresentations)
        {
            token.ThrowIfCancellationRequested();
            if (member.Kind == MediaDerivedKind.VideoPosterPng && member.Blob is { } blob)
                files.Add(blob.RelativeFileName);
        }
        return files;
    }

    // One leased-inspection-local pass per exact physical role/path/integrity tuple. No proof
    // is cached between inspections or borrowed from an equal-hash file in another role.
    // PNG structure and all digests are gathered from the same open handle and byte stream.
    private async Task VerifyRepresentationCandidatePayloadAsync(string directory,
        NativeMediaCandidatePayload payload, bool requirePng, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = Path.Combine(directory, payload.FileName);
        ValidateCandidateRegularFile(path);
        TransferMeasuredBlob expected = payload.Measured;
        FileStamp? before = ReadFileStamp(path);
        if (before == null || before.Length != expected.Length)
            throw new InvalidDataException("The candidate payload is missing or changed length.");
        await using FileStream stream = OpenRead(path);
        if (stream.Length != expected.Length)
            throw new InvalidDataException("The candidate payload changed length.");
        _onFileHashStarting?.Invoke(path);
        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        NativeTransferDerivedPayloadValidator.PngStructure? png = requirePng ? new(expected.Length) : null;
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            total = checked(total + count);
            if (total > expected.Length)
                throw new InvalidDataException("The candidate payload changed length.");
            sha256.AppendData(buffer, 0, count);
            md5.AppendData(buffer, 0, count);
            sha1.AppendData(buffer, 0, count);
            png?.Append(buffer.AsSpan(0, count), token);
        }
        token.ThrowIfCancellationRequested();
        ValidateCandidateRegularFile(path);
        if (total != expected.Length || stream.Length != expected.Length || before != ReadFileStamp(path)
            || Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant() != expected.Sha256
            || Convert.ToBase64String(md5.GetHashAndReset()) != expected.Md5
            || Convert.ToBase64String(sha1.GetHashAndReset()) != expected.Sha1 || png != null && !png.IsValid)
            throw new InvalidDataException("The candidate payload differs from its measured integrity or required structure.");
    }

    private static void VerifyRepresentationCandidateFiles(string directory, NativeMediaCandidateInventory inventory,
        bool allowIncompleteTemporaries, CancellationToken token)
    {
        var files = new HashSet<string>(inventory.Payloads.Select(p => p.FileName), StringComparer.Ordinal)
            { PassiveCandidateJournalName, DirectoryLeaseFileName, ManifestFileName };
        int count = 0;
        foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            if (++count > inventory.Payloads.Length + 6) throw new InvalidDataException("Unexpected candidate content.");
            ValidateCandidateRegularFile(path);
            string name = Path.GetFileName(path);
            if (allowIncompleteTemporaries && IsRepresentationCandidateTemporary(name)) continue;
            if (!files.Remove(name)) throw new InvalidDataException("Unexpected representation candidate content.");
        }
        if (files.Count != 0) throw new InvalidDataException("Missing representation candidate content.");
    }

    private static bool IsRepresentationCandidateTemporary(string name)
        => IsCandidateTemporaryName(name, ".meowchan-payload-") || IsCandidateTemporaryName(name, ".meowchan-journal-")
            || IsCandidateTemporaryName(name, ".meowchan-manifest-");

    private static PassiveMediaImportCandidate RepresentationCandidateFrom(PassiveRepresentationCandidateJournal journal,
        NativeMediaCandidateSelection selection, NativeMediaCandidateInventory inventory, string directory, MediaAssetSnapshot snapshot)
        => new(directory, Guid.ParseExact(journal.OperationId, "D"), journal.PackageFingerprint, journal.Resource,
            journal.ImportedAtUtc, snapshot.Generation!, inventory.Bindings)
        {
            PlanVersion = 2, Selection = selection, InventorySha256 = inventory.Sha256,
            OriginalEvidence = snapshot.OriginalEvidence, DerivedRepresentations = snapshot.DerivedRepresentations
        };

    private async Task WriteRepresentationCandidateManifestAsync(string directory, byte[] bytes, CancellationToken token)
    {
        string temporary = Path.Combine(directory, ".meowchan-manifest-" + Guid.NewGuid().ToString("N") + ".part");
        await using (FileStream output = OpenCandidateNewFile(temporary))
        {
            await output.WriteAsync(bytes, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(flushToDisk: true);
        }
        token.ThrowIfCancellationRequested();
        ValidateCandidateRegularFile(temporary);
        string destination = Path.Combine(directory, ManifestFileName);
        ArchiveExportMetadataFile.RejectLinks(destination);
        _moveFile(temporary, destination, false);
    }

    private static byte[] EncodeRepresentationCandidateJournal(PassiveRepresentationCandidateJournal journal)
    {
        _ = ValidateRepresentationCandidateJournal(journal);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, CandidateJsonOptions);
        if (bytes.Length > MaximumCandidateJournalBytes) throw new InvalidDataException("The candidate journal exceeds its limit.");
        return bytes;
    }
    private Task WriteRepresentationCandidateJournalAsync(string directory, PassiveRepresentationCandidateJournal journal,
        bool createOnly, CancellationToken token)
        => WriteCandidateJournalBytesAsync(directory, EncodeRepresentationCandidateJournal(journal), createOnly, token);

    private static void ValidateRepresentationJournalShape(JsonElement root)
    {
        RepresentationCandidateFields(root, "Version", "Phase", "OperationId", "PackageFingerprint", "Resource",
            "ImportedAtUtc", "SelectionJson", "SelectionSha256", "InventorySha256", "ManifestGeneration");
        RepresentationCandidateFields(root.GetProperty("Resource"), "Source", "Board", "Thread");
        foreach (string name in new[] { "Source", "Board", "Thread" })
            if (root.GetProperty("Resource").GetProperty(name).ValueKind != JsonValueKind.String) throw InvalidRepresentations();
        foreach (string name in new[] { "Phase", "OperationId", "PackageFingerprint", "SelectionJson", "SelectionSha256" })
            if (root.GetProperty(name).ValueKind != JsonValueKind.String) throw InvalidRepresentations();
        foreach (string name in new[] { "InventorySha256", "ManifestGeneration" })
            if (root.GetProperty(name).ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw InvalidRepresentations();
        JsonElement version = root.GetProperty("Version"), time = root.GetProperty("ImportedAtUtc");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value != 2
            || time.ValueKind != JsonValueKind.String || !time.TryGetDateTimeOffset(out _)) throw InvalidRepresentations();
    }
    private static void RepresentationCandidateFields(JsonElement node, params string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object) throw InvalidRepresentations();
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in node.EnumerateObject()) if (!remaining.Remove(property.Name)) throw InvalidRepresentations();
        if (remaining.Count != 0) throw InvalidRepresentations();
    }
    private static bool CandidateRepresentationJsonEquals(JsonElement actual, JsonElement expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (actual.ValueKind != expected.ValueKind) return false;
        if (actual.ValueKind == JsonValueKind.Object)
        {
            // Large Assets/Entries maps need linear lookup, not one DOM scan per key.
            var properties = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
            if (properties.Count != expected.EnumerateObject().Count()) return false;
            foreach (JsonProperty property in expected.EnumerateObject())
                if (!properties.TryGetValue(property.Name, out JsonElement value)
                    || !CandidateRepresentationJsonEquals(value, property.Value, token)) return false;
            return true;
        }
        if (actual.ValueKind == JsonValueKind.Array)
        {
            if (actual.GetArrayLength() != expected.GetArrayLength()) return false;
            var first = actual.EnumerateArray(); var second = expected.EnumerateArray();
            while (first.MoveNext())
                if (!second.MoveNext() || !CandidateRepresentationJsonEquals(first.Current, second.Current, token)) return false;
            return !second.MoveNext();
        }
        return actual.ValueKind == JsonValueKind.String ? actual.GetString() == expected.GetString()
            : actual.GetRawText() == expected.GetRawText();
    }

    private static void ValidateRepresentationJournalEncoding(byte[] bytes, CancellationToken token)
    {
        _ = RepresentationUtf8.GetCharCount(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName)) continue;
            ReadOnlySpan<byte> raw = reader.ValueSpan;
            // Preserve exact scalars: no decoder replacement of an escaped lone surrogate
            // in Resource or the outer SelectionJson wrapper before canonical parsing.
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != (byte)'\\') continue;
                if (++i >= raw.Length) throw InvalidRepresentations();
                if (raw[i] != (byte)'u') continue;
                int code = RepresentationJournalHex4(raw, i + 1); i += 4;
                if (code is >= 0xdc00 and <= 0xdfff) throw InvalidRepresentations();
                if (code is < 0xd800 or > 0xdbff) continue;
                if (i + 6 >= raw.Length || raw[i + 1] != (byte)'\\' || raw[i + 2] != (byte)'u') throw InvalidRepresentations();
                int low = RepresentationJournalHex4(raw, i + 3);
                if (low is < 0xdc00 or > 0xdfff) throw InvalidRepresentations();
                i += 6;
            }
        }
    }
    private static int RepresentationJournalHex4(ReadOnlySpan<byte> raw, int start)
    {
        if (start > raw.Length - 4) throw InvalidRepresentations();
        int code = 0;
        for (int i = start; i < start + 4; i++)
        {
            int nibble = raw[i] switch
            {
                >= (byte)'0' and <= (byte)'9' => raw[i] - '0',
                >= (byte)'a' and <= (byte)'f' => raw[i] - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => raw[i] - 'A' + 10,
                _ => throw InvalidRepresentations()
            };
            code = (code << 4) | nibble;
        }
        return code;
    }

    // Journal1 deliberately retains its separate DTO and exact reader/writer branch.
    private sealed record PassiveRepresentationCandidateJournal(int Version, string Phase, string OperationId,
        string PackageFingerprint, ResourceId Resource, DateTimeOffset ImportedAtUtc, string SelectionJson,
        string SelectionSha256, string? InventorySha256, string? ManifestGeneration);
}
