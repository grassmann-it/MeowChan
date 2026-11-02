using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Failure/recovery contract for the B-owned passive candidate producer.
/// These tests deliberately use the real transfer writer and stage, but only synthetic bytes.
/// They do not exercise the later SQL/catalog merge or conflict-resolution gates.
/// </summary>
public sealed class PassiveMediaCandidateRecoveryTests
{
    private static readonly ResourceId Resource = new("4chan", "b", "404");
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operation = Guid.Parse("b1a4cc1f-83b6-4a1d-9a1c-22e2b3d2a401");
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("passive candidate recovery payload\0\x01\x02\x03");

    [Fact]
    public async Task CancellationAtEveryObservedPhaseLeavesOnlyInspectableInactiveCandidates()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string oldRoot = Path.Combine(fixture.Root, "old-media-root");
        Directory.CreateDirectory(oldRoot);
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "keep.bin"), "old-root-must-not-change");
        TreeSnapshot oldBefore = TreeSnapshot.Capture(oldRoot);

        foreach (PassiveMediaCandidatePhase phaseToCancel in Enum.GetValues<PassiveMediaCandidatePhase>())
        {
            string candidateRoot = fixture.NewCandidateRoot();
            using CancellationTokenSource cancellation = new();
            List<PassiveMediaCandidatePhase> seen = [];
            Exception? error = null;
            try
            {
                await MaterializeAsync(fixture.Stage, candidateRoot, fixture.SelectedKeys, (phase, detail) =>
                {
                    seen.Add(phase);
                    AssertCheckpointFootprint(phase, detail);
                    if (phase == phaseToCancel) cancellation.Cancel();
                }, cancellation.Token);
            }
            catch (Exception caught) { error = caught; }

            Assert.Equal(Enum.GetValues<PassiveMediaCandidatePhase>().Take((int)phaseToCancel + 1), seen);
            string[] candidates = Candidates(candidateRoot);
            string candidateDirectory = Assert.Single(candidates);
            if (phaseToCancel == PassiveMediaCandidatePhase.ReadyJournalSaved)
            {
                // The final marker completes the INACTIVE candidate: cancellation at its checkpoint is
                // deliberately too late to reinterpret a successful result.
                Assert.Null(error);
                Assert.Equal(PassiveMediaCandidateStatus.Ready, (await fixture.Store.InspectPassiveImportCandidateAsync(candidateDirectory)).Status);
            }
            else
            {
                Assert.NotNull(error);
                Assert.IsType<PassiveMediaCandidateCanceledException>(error);
                Assert.Equal(candidateDirectory, ((PassiveMediaCandidateCanceledException)error).CandidateDirectory);
                Assert.Equal(cancellation.Token, ((PassiveMediaCandidateCanceledException)error).CancellationToken);
                Assert.Equal(PassiveMediaCandidateStatus.Incomplete, (await fixture.Store.InspectPassiveImportCandidateAsync(candidateDirectory)).Status);
            }
            await AssertPhaseRetainedAsync(candidateDirectory, phaseToCancel);
            await AssertInspectionUnchangedAsync(candidateDirectory,
                phaseToCancel == PassiveMediaCandidatePhase.ReadyJournalSaved
                    ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete);
            oldBefore.AssertUnchanged(oldRoot);
        }
    }

    [Fact]
    public async Task CheckpointFailureAtEveryObservedPhaseRetainsCandidateWithoutRepairOrDeletion()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        foreach (PassiveMediaCandidatePhase phaseToFail in Enum.GetValues<PassiveMediaCandidatePhase>())
        {
            string phaseName = phaseToFail.ToString();
            string candidateRoot = fixture.NewCandidateRoot();
            Exception? error = null;
            try
            {
                await MaterializeAsync(fixture.Stage, candidateRoot, fixture.SelectedKeys, (phase, detail) =>
                {
                    AssertCheckpointFootprint(phase, detail);
                    if (phase == phaseToFail)
                        throw new IOException("synthetic checkpoint failure: " + phaseName);
                });
            }
            catch (Exception caught) { error = caught; }

            Assert.NotNull(error);
            PassiveMediaCandidateWriteException writeError = Assert.IsType<PassiveMediaCandidateWriteException>(error);
            string[] candidates = Candidates(candidateRoot);
            Assert.Single(candidates);
            string candidate = candidates[0];
            Assert.Equal(candidate, writeError.CandidateDirectory);
            TreeSnapshot beforeInspection = TreeSnapshot.Capture(candidate);
            PassiveMediaCandidateStatus expected = phaseToFail == PassiveMediaCandidatePhase.ReadyJournalSaved
                ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete;
            Assert.Equal(expected, await StatusAsync(fixture.Store, candidate));
            beforeInspection.AssertUnchanged(candidate);
            await AssertPhaseRetainedAsync(candidate, phaseToFail);
            await AssertInspectionUnchangedAsync(candidate, expected);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task IoFailureAfterRealRenameRetainsTheCandidateAndDoesNotPublish(int throwOnRename)
    {
        int renameCount = 0;
        MediaStore store = new((source, destination, overwrite) =>
        {
            File.Move(source, destination, overwrite);
            if (++renameCount == throwOnRename) throw new IOException("synthetic failure after completed rename");
        });
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string root = fixture.NewCandidateRoot();
        Exception? error = null;
        try { await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys, store: store); }
        catch (Exception caught) { error = caught; }

        Assert.Equal(throwOnRename, renameCount);
        PassiveMediaCandidateWriteException writeError = Assert.IsType<PassiveMediaCandidateWriteException>(error);
        string candidate = Assert.Single(Candidates(root));
        Assert.Equal(candidate, writeError.CandidateDirectory);
        Assert.Equal(throwOnRename == 3 ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete, (await store.InspectPassiveImportCandidateAsync(candidate)).Status);
        Assert.Contains(Directory.EnumerateFiles(candidate), path =>
            Path.GetFileName(path).EndsWith(".original.bin", StringComparison.Ordinal));
        await AssertInspectionUnchangedAsync(candidate, throwOnRename == 3
            ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete);
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("manifest")]
    [InlineData("manifest-duplicate")]
    [InlineData("payload")]
    public async Task DamagedJournalManifestOrPayloadIsInvalidAndNeverRepaired(string corruption)
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string candidateRoot = fixture.NewCandidateRoot();
        await MaterializeAsync(fixture.Stage, candidateRoot, fixture.SelectedKeys);
        string candidate = Assert.Single(Candidates(candidateRoot));
        string target = corruption switch
        {
            "journal" => Path.Combine(candidate, ".meowchan-import-candidate.json"),
            "manifest" or "manifest-duplicate" => Assert.Single(Directory.EnumerateFiles(candidate, ".meowchan-manifest.json", SearchOption.TopDirectoryOnly)),
            _ => Assert.Single(Directory.EnumerateFiles(candidate, "*.original.bin", SearchOption.TopDirectoryOnly))
        };
        byte[] damaged = await File.ReadAllBytesAsync(target);
        if (corruption == "manifest-duplicate")
        {
            string raw = Encoding.UTF8.GetString(damaged).TrimEnd();
            damaged = Encoding.UTF8.GetBytes(raw[..^1] + ",\"Version\":5}");
        }
        else damaged[0] ^= 0x7f;
        await File.WriteAllBytesAsync(target, damaged);
        if (corruption == "manifest-duplicate")
        {
            string journalPath = Path.Combine(candidate, ".meowchan-import-candidate.json");
            JsonObject journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
            journal["ManifestGeneration"] = Hash(damaged);
            await File.WriteAllTextAsync(journalPath, journal.ToJsonString());
        }
        TreeSnapshot before = TreeSnapshot.Capture(candidate);

        PassiveMediaCandidateInspection inspection = await fixture.Store.InspectPassiveImportCandidateAsync(candidate);
        Assert.Equal(PassiveMediaCandidateStatus.Invalid, inspection.Status);
        before.AssertUnchanged(candidate);
        Assert.True(File.Exists(target));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(target));
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
    }

    [Fact]
    public async Task PayloadChangedAfterManifestSavedCannotBecomeReady()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string root = fixture.NewCandidateRoot();
        Exception? error = null;
        try
        {
            await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys, (phase, directory) =>
            {
                if (phase != PassiveMediaCandidatePhase.ManifestSaved) return;
                string payload = Directory.EnumerateFiles(directory, "*.original.bin", SearchOption.TopDirectoryOnly).Single();
                byte[] changed = File.ReadAllBytes(payload);
                changed[0] ^= 0x55;
                File.WriteAllBytes(payload, changed);
            });
        }
        catch (Exception caught) { error = caught; }

        Assert.IsType<PassiveMediaCandidateWriteException>(error);
        string candidate = Assert.Single(Candidates(root));
        Assert.Equal(PassiveMediaCandidateStatus.Invalid, (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
        byte[] changed = await File.ReadAllBytesAsync(Directory.EnumerateFiles(candidate, "*.original.bin").Single());
        byte[] expected = Payload.ToArray(); expected[0] ^= 0x55;
        Assert.Equal(expected, changed);
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
    }

    [Fact]
    public async Task ParallelInspectionDuringHeldProducerLeaseIsBusyAndCancellationPropagates()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string root = fixture.NewCandidateRoot();
        TimeSpan watchdog = TimeSpan.FromSeconds(10);
        TaskCompletionSource<string> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PassiveMediaImportCandidate> materialize = Task.Run(() => MaterializeAsync(fixture.Stage, root,
            fixture.SelectedKeys, (phase, directory) =>
            {
                if (phase != PassiveMediaCandidatePhase.JournalFlushed) return;
                reached.TrySetResult(directory);
                release.Task.GetAwaiter().GetResult();
            }));
        string candidate = string.Empty;
        try
        {
            // The producer remains inside the real lease, not a manually simulated lock.
            candidate = await reached.Task.WaitAsync(watchdog);
            string journalPath = Path.Combine(candidate, ".meowchan-import-candidate.json");
            byte[] journalBefore = await File.ReadAllBytesAsync(journalPath);
            string[] namesBefore = Directory.GetFileSystemEntries(candidate).OrderBy(path => path).ToArray();
            MediaStore inspector = new(File.Move, directoryLeaseTimeout: TimeSpan.FromMilliseconds(75));
            PassiveMediaCandidateInspection busy = await inspector.InspectPassiveImportCandidateAsync(candidate).WaitAsync(watchdog);
            Assert.Equal(PassiveMediaCandidateStatus.Busy, busy.Status);
            Assert.Null(busy.Candidate);

            using CancellationTokenSource canceled = new();
            MediaStore waitingInspector = new(File.Move, directoryLeaseTimeout: TimeSpan.FromSeconds(30));
            Task<PassiveMediaCandidateInspection> canceledRead = waitingInspector.InspectPassiveImportCandidateAsync(candidate, canceled.Token);
            canceled.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await canceledRead.WaitAsync(watchdog));
            Assert.Equal(canceled.Token, error.CancellationToken);
            Assert.Equal(journalBefore, await File.ReadAllBytesAsync(journalPath));
            Assert.Equal(namesBefore, Directory.GetFileSystemEntries(candidate).OrderBy(path => path).ToArray());
            Assert.False(materialize.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            await materialize.WaitAsync(watchdog); // never leave a held producer behind after assertion failure
        }
        Assert.Equal(candidate, (await materialize).Directory);
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Ready);
    }

    [Fact]
    public async Task IncompleteOwnTemporaryFilesAreAllowedButNeverTrusted()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string root = fixture.NewCandidateRoot();
        using CancellationTokenSource canceled = new();
        string? candidateDirectory = null;
        Exception? error = null;
        try
        {
            await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys, (phase, directory) =>
            {
                if (phase != PassiveMediaCandidatePhase.PayloadFlushed) return;
                candidateDirectory = directory;
                canceled.Cancel();
            }, canceled.Token);
        }
        catch (Exception caught) { error = caught; }
        Assert.IsType<PassiveMediaCandidateCanceledException>(error);
        Assert.NotNull(candidateDirectory);
        string candidate = candidateDirectory!;
        await File.WriteAllBytesAsync(Assert.Single(Directory.GetFiles(candidate, ".meowchan-payload-*.part")), [0xff]);
        await File.WriteAllBytesAsync(Path.Combine(candidate, ".meowchan-journal-" + Guid.NewGuid().ToString("N") + ".part"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(candidate, ".meowchan-manifest-" + Guid.NewGuid().ToString("N") + ".part"), [3, 4]);
        TreeSnapshot before = TreeSnapshot.Capture(candidate);

        Assert.Equal(PassiveMediaCandidateStatus.Incomplete,
            (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
        before.AssertUnchanged(candidate);
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Incomplete);
    }

    [Theory]
    [InlineData("foreign-file")]
    [InlineData("foreign-directory")]
    [InlineData("reparse")]
    [InlineData("malformed-payload-part")]
    [InlineData("malformed-journal-part")]
    [InlineData("malformed-manifest-part")]
    public async Task CopyingCandidateInventoryRejectsForeignFilesDirectoriesAndReparsePoints(string extra)
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string candidate = await CreateIncompleteCandidateAsync(fixture, PassiveMediaCandidatePhase.PayloadRenamed);
        string path = Path.Combine(candidate, extra switch
        {
            "foreign-directory" => "foreign-dir",
            "malformed-payload-part" => ".meowchan-payload-not-a-guid.part",
            "malformed-journal-part" => ".meowchan-journal-" + Guid.NewGuid().ToString("D") + ".part",
            "malformed-manifest-part" => ".meowchan-manifest-" + Guid.NewGuid().ToString("N") + ".part.bak",
            _ => "foreign.bin"
        });
        if (extra == "foreign-directory") Directory.CreateDirectory(path);
        else if (extra == "reparse")
        {
            path = Directory.EnumerateFiles(candidate, "*.original.bin").Single();
            string target = Path.Combine(fixture.Root, "external-synthetic-payload.bin");
            File.Copy(path, target);
            File.Delete(path);
            File.CreateSymbolicLink(path, target); // otherwise admissible name/bytes, forbidden reparse
        }
        else await File.WriteAllBytesAsync(path, [8, 6, 4, 2]);
        TreeSnapshot before = TreeSnapshot.Capture(candidate);

        Assert.Equal(PassiveMediaCandidateStatus.Invalid,
            (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
        before.AssertUnchanged(candidate);
        Assert.True(File.Exists(path) || Directory.Exists(path));
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
    }

    [Fact]
    public async Task FinalizedOriginalFilenameMustContainItsActualSha256()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string candidate = await CreateIncompleteCandidateAsync(fixture, PassiveMediaCandidatePhase.PayloadRenamed);
        string original = Assert.Single(Directory.GetFiles(candidate, "*.original.bin"));
        string wrong = Path.Combine(candidate, new string('a', 64) + ".original.bin");
        File.Move(original, wrong);
        TreeSnapshot before = TreeSnapshot.Capture(candidate);

        Assert.Equal(PassiveMediaCandidateStatus.Invalid,
            (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
        before.AssertUnchanged(candidate);
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
    }

    [Fact]
    public async Task ExistingCompleteManifestMustMatchCandidateProvenance()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string candidate = await CreateIncompleteCandidateAsync(fixture, PassiveMediaCandidatePhase.ManifestSaved);
        string manifestPath = Path.Combine(candidate, ".meowchan-manifest.json");
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        string key = fixture.SelectedKeys.Single();
        manifest["AttachmentBindings"]![key]!["PassiveImport"]!["OperationId"] = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        TreeSnapshot before = TreeSnapshot.Capture(candidate);

        Assert.Equal(PassiveMediaCandidateStatus.Invalid,
            (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
        before.AssertUnchanged(candidate);
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
    }

    [Fact]
    public async Task ReadyCandidateRejectsExtraSecondPayloadEvenWithMatchingHashFilename()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string root = fixture.NewCandidateRoot();
        PassiveMediaImportCandidate candidate = await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys);
        byte[] extra = Encoding.ASCII.GetBytes("synthetic extra unbound second original");
        string extraPath = Path.Combine(candidate.Directory, Hash(extra) + ".original.bin");
        await File.WriteAllBytesAsync(extraPath, extra);
        Assert.Equal(2, Directory.GetFiles(candidate.Directory, "*.original.bin").Length);
        await AssertInspectionUnchangedAsync(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        Assert.Equal(extra, await File.ReadAllBytesAsync(extraPath));
    }

    [Fact]
    public async Task StrictJournalCorruptionIsInvalidAndNeverRepaired()
    {
        string[] faults = ["duplicate", "unknown", "version", "phase", "keyset", "empty-keyset",
            "operation", "malformed-operation", "missing-resource", "null-resource"];
        foreach (string fault in faults)
        {
            await using StageFixture fixture = await StageFixture.CreateAsync();
            string root = fixture.NewCandidateRoot();
            await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys);
            string candidate = Assert.Single(Candidates(root));
            string journalPath = Path.Combine(candidate, ".meowchan-import-candidate.json");
            string original = await File.ReadAllTextAsync(journalPath);
            string damaged = DamageJournal(original, fault);
            await File.WriteAllTextAsync(journalPath, damaged);
            TreeSnapshot before = TreeSnapshot.Capture(candidate);

            Assert.Equal(PassiveMediaCandidateStatus.Invalid,
                (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
            before.AssertUnchanged(candidate);
            Assert.Equal(damaged, await File.ReadAllTextAsync(journalPath));
            await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
        }
    }

    [Fact]
    public async Task MissingLeaseIsInvalidAndInspectionDoesNotRecreateIt()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string root = fixture.NewCandidateRoot();
        await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys);
        string candidate = Assert.Single(Candidates(root));
        string lockPath = Path.Combine(candidate, ".meowchan-store.lock");
        Assert.True(File.Exists(lockPath));
        File.Delete(lockPath);
        TreeSnapshot before = TreeSnapshot.Capture(candidate);

        Assert.Equal(PassiveMediaCandidateStatus.Invalid,
            (await fixture.Store.InspectPassiveImportCandidateAsync(candidate)).Status);
        before.AssertUnchanged(candidate);
        Assert.False(File.Exists(lockPath));
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
    }

    private static string DamageJournal(string original, string fault)
    {
        if (fault == "duplicate")
        {
            string trimmed = original.TrimEnd();
            return trimmed[..^1] + ",\"Phase\":\"copying\"}";
        }
        JsonObject json = JsonNode.Parse(original)!.AsObject();
        switch (fault)
        {
            case "unknown": json["UnknownField"] = 1; break;
            case "version": json["Version"] = 2; break;
            case "phase": json["Phase"] = "unknown"; break;
            case "keyset": json["RevisionKeys"] = new JsonArray("att-v1:" + new string('f', 64)); break;
            case "empty-keyset": json["RevisionKeys"] = new JsonArray(); break;
            case "operation": json["OperationId"] = "e117dc29-3df1-42dd-bf41-bcb8d889f9ab"; break;
            case "malformed-operation": json["OperationId"] = "not-a-guid"; break;
            case "missing-resource": json.Remove("Resource"); break;
            case "null-resource": json["Resource"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    [Fact]
    public async Task ForeignAndUnownedPartialDirectoriesAreInvalidAndNeverRepaired()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        foreach (bool foreign in new[] { true, false })
        {
            string root = fixture.NewCandidateRoot();
            string candidate = Path.Combine(root, ".meowchan-passive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(candidate);
            string marker = Path.Combine(candidate, foreign ? "foreign.txt" : "payload.part");
            await File.WriteAllTextAsync(marker, foreign ? "foreign content" : "partial content");
            TreeSnapshot before = TreeSnapshot.Capture(candidate);

            PassiveMediaCandidateInspection inspection = await fixture.Store.InspectPassiveImportCandidateAsync(candidate);
            Assert.Equal(PassiveMediaCandidateStatus.Invalid, inspection.Status);
            before.AssertUnchanged(candidate);
            Assert.True(File.Exists(marker));
            await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Invalid);
        }
    }

    [Fact]
    public async Task UnknownAndPreviewKeysAreRejectedBeforeAnyCandidateDirectoryIsCreated()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync(includePreview: true);
        foreach (IReadOnlySet<string> keys in new IReadOnlySet<string>[]
        {
            new HashSet<string>(new[] { "att-v1:" + new string('f', 64) }, StringComparer.Ordinal),
            fixture.PreviewKeys
        })
        {
            string root = fixture.NewCandidateRoot();
            TreeSnapshot beforeRoot = TreeSnapshot.Capture(root);
            Exception? error = null;
            try { await MaterializeAsync(fixture.Stage, root, keys); }
            catch (Exception caught) { error = caught; }
            Assert.NotNull(error);
            Assert.True(error is InvalidDataException or ArgumentException,
                $"unexpected preflight exception: {error.GetType().Name}");
            beforeRoot.AssertUnchanged(root);
            Assert.Empty(Candidates(root));
        }
    }

    [Fact]
    public async Task SymlinkAndTraversalCandidateRootsAreRejectedWithoutTouchingTheTarget()
    {
        await using StageFixture fixture = await StageFixture.CreateAsync();
        string real = Path.Combine(fixture.Root, "real-candidate-root");
        Directory.CreateDirectory(real);
        string link = Path.Combine(fixture.Root, "candidate-link");
        Directory.CreateSymbolicLink(link, real);

        TreeSnapshot realBefore = TreeSnapshot.Capture(real);
        Exception? linkError = null;
        try { await MaterializeAsync(fixture.Stage, link, fixture.SelectedKeys); }
        catch (Exception caught) { linkError = caught; }
        Assert.NotNull(linkError);
        Assert.True(linkError is InvalidDataException or ArgumentException,
            $"unexpected symlink-root exception: {linkError.GetType().Name}");
        realBefore.AssertUnchanged(real);
        Assert.Empty(Candidates(real));

        string parent = Path.Combine(fixture.Root, "traversal-parent");
        string escaped = Path.Combine(fixture.Root, "traversal-escape");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(escaped);
        string traversal = Path.Combine(parent, "..", "traversal-escape");
        TreeSnapshot escapedBefore = TreeSnapshot.Capture(escaped);
        Exception? traversalError = null;
        try { await MaterializeAsync(fixture.Stage, traversal, fixture.SelectedKeys); }
        catch (Exception caught) { traversalError = caught; }
        Assert.NotNull(traversalError);
        Assert.True(traversalError is InvalidDataException or ArgumentException,
            $"unexpected traversal-root exception: {traversalError.GetType().Name}");
        escapedBefore.AssertUnchanged(escaped);
        Assert.Empty(Candidates(escaped));
    }

    private static async Task<string> CreateIncompleteCandidateAsync(StageFixture fixture, PassiveMediaCandidatePhase stop)
    {
        string root = fixture.NewCandidateRoot();
        using CancellationTokenSource canceled = new();
        PassiveMediaCandidateCanceledException error = await Assert.ThrowsAsync<PassiveMediaCandidateCanceledException>(
            async () => await MaterializeAsync(fixture.Stage, root, fixture.SelectedKeys, (phase, _) =>
            {
                if (phase == stop) canceled.Cancel();
            }, canceled.Token));
        string candidate = Assert.Single(Candidates(root));
        Assert.Equal(candidate, error.CandidateDirectory);
        await AssertInspectionUnchangedAsync(candidate, PassiveMediaCandidateStatus.Incomplete);
        return candidate;
    }

    private static void AssertCheckpointFootprint(PassiveMediaCandidatePhase phase, string directory)
    {
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, ".meowchan-store.lock")));
        using JsonDocument journal = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(directory, ".meowchan-import-candidate.json")));
        Assert.Equal(phase == PassiveMediaCandidatePhase.ReadyJournalSaved ? "ready" : "copying",
            journal.RootElement.GetProperty("Phase").GetString());
        string[] payloads = Directory.GetFiles(directory, "*.original.bin");
        string[] parts = Directory.GetFiles(directory, ".meowchan-payload-*.part");
        if (phase == PassiveMediaCandidatePhase.JournalFlushed)
        {
            Assert.Empty(parts); Assert.Empty(payloads);
        }
        else if (phase == PassiveMediaCandidatePhase.PayloadFlushed)
        {
            Assert.Empty(payloads);
            Assert.Equal(Payload.Length, new FileInfo(Assert.Single(parts)).Length);
        }
        else
        {
            Assert.Empty(parts);
            Assert.Equal(Payload, File.ReadAllBytes(Assert.Single(payloads)));
        }
        Assert.Equal(phase is PassiveMediaCandidatePhase.ManifestSaved or PassiveMediaCandidatePhase.ReadyJournalSaved,
            File.Exists(Path.Combine(directory, ".meowchan-manifest.json")));
    }

    private static async Task AssertPhaseRetainedAsync(string directory, PassiveMediaCandidatePhase phase)
    {
        AssertCheckpointFootprint(phase, directory);
        if (phase == PassiveMediaCandidatePhase.PayloadFlushed)
            Assert.Equal(Payload, await File.ReadAllBytesAsync(Assert.Single(
                Directory.GetFiles(directory, ".meowchan-payload-*.part"))));
    }

    private static async Task AssertInspectionUnchangedAsync(string directory, PassiveMediaCandidateStatus expected)
    {
        TreeSnapshot before = TreeSnapshot.Capture(directory);
        // A fresh instance provides the restart/recovery boundary: no producer-local state can
        // claim completion, repair the marker, or mask corruption via a verification cache.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            PassiveMediaCandidateInspection inspection = await new MediaStore().InspectPassiveImportCandidateAsync(directory);
            Assert.Equal(expected, inspection.Status);
            if (expected == PassiveMediaCandidateStatus.Ready) Assert.NotNull(inspection.Candidate);
            else Assert.Null(inspection.Candidate);
            before.AssertUnchanged(directory);
        }
    }

    private static async Task<PassiveMediaImportCandidate> MaterializeAsync(
        NativeTransferImportStage stage, string candidateRoot, IReadOnlySet<string> keys,
        Action<PassiveMediaCandidatePhase, string>? checkpoint = null,
        CancellationToken cancellationToken = default, MediaStore? store = null)
    {
        store ??= new();
        return await store.MaterializePassiveImportCandidateAsync(stage, Resource, keys, candidateRoot,
            Operation, ImportedAt, cancellationToken, checkpoint);
    }

    private static async Task<PassiveMediaCandidateStatus> StatusAsync(MediaStore store, string directory)
        => (await store.InspectPassiveImportCandidateAsync(directory)).Status;

    private static string[] Candidates(string root) => Directory.Exists(root)
        ? Directory.GetDirectories(root, ".meowchan-passive-*", SearchOption.TopDirectoryOnly)
        : [];

    private sealed class StageFixture : IAsyncDisposable
    {
        private StageFixture(string root, NativeTransferImportStage stage, IReadOnlySet<string> selectedKeys, string? preview)
        {
            Root = root; Stage = stage; SelectedKeys = selectedKeys;
            PreviewKeys = preview == null ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(new[] { preview }, StringComparer.Ordinal);
            OldRoot = Path.Combine(root, "old-root-preserved");
            Directory.CreateDirectory(OldRoot);
            File.WriteAllText(Path.Combine(OldRoot, ".meowchan-manifest.json"),
                "{\"Version\":4,\"Entries\":{},\"Assets\":{},\"AttachmentBindings\":{}}");
            File.WriteAllBytes(Path.Combine(OldRoot, "existing-favorite.bin"), [9, 7, 5, 3, 1]);
            _oldRootBefore = TreeSnapshot.Capture(OldRoot);
        }

        private readonly TreeSnapshot _oldRootBefore;
        internal string Root { get; }
        internal string OldRoot { get; }
        internal NativeTransferImportStage Stage { get; }
        internal IReadOnlySet<string> SelectedKeys { get; }
        internal IReadOnlySet<string> PreviewKeys { get; }
        internal MediaStore Store { get; } = new();

        internal string NewCandidateRoot()
        {
            string path = Path.Combine(Root, "candidate-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static async Task<StageFixture> CreateAsync(bool includePreview = false)
        {
            string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-passive-candidate-");
            try
            {
                string staging = Path.Combine(root, "stage"); Directory.CreateDirectory(staging);
                string selected = Key("original");
                string? preview = includePreview ? Key("preview") : null;
                TransferAttachment original = Attachment(selected, "original", TransferMediaRole.Original);
                TransferAttachment[] attachments = preview == null
                    ? [original]
                    : [original, Attachment(preview, "preview", TransferMediaRole.Preview)];
                HashSet<string> selectedKeys = new(StringComparer.Ordinal) { selected };
                TransferPost post = new(new("404", 0, PostFieldPresence.All, ImportedAt, "", "synthetic",
                    "<p>synthetic candidate</p>", Attachments: attachments.Select(a => a.Metadata).ToImmutableArray()),
                    attachments.ToImmutableArray());
                NativeTransferCatalog catalog = new(ImportedAt,
                    ImmutableArray.Create(new TransferThread(Resource, "https://boards.4chan.org/b/thread/404",
                        "synthetic candidate", ThreadSourceState.NotFound, ImportedAt, null, null, true,
                        ImmutableArray.Create(post))));
                string sourcePath = Path.Combine(root, "package.meowchan");
                await using FileStream output = new(sourcePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                List<TransferPayloadSource> sources = [new(new(Payload.Length, Hash(Payload)),
                    _ => Task.FromResult<Stream>(new MemoryStream(Payload, writable: false)))];
                TransferWriteResult write = await NativeTransferPackageWriter.WriteAsync(output, catalog, sources, Operation);
                Assert.True(write.IsComplete, write.Error.ToString());
                output.Position = 0;
                NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(output, staging);
                NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
                return new StageFixture(root, stage, selectedKeys, preview);
            }
            catch { Directory.Delete(root, recursive: true); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stage.DisposeAsync();
                _oldRootBefore.AssertUnchanged(OldRoot);
            }
            finally
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }

        private static string Key(string label) => MediaRevisionKeys.CreateAttachmentKey(Resource, "404",
            label == "original" ? 0 : 1, Provider(label));

        private static TransferAttachment Attachment(string key, string label, TransferMediaRole role)
        {
            MediaProviderMetadata provider = Provider(label);
            byte[] bytes = Payload;
            CapturedAttachment metadata = new(label == "original" ? 0 : 1,
                provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
                ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
            return new(metadata, role, TransferContentStatus.Contained, Hash(bytes), null);
        }

        private static MediaProviderMetadata Provider(string label)
        {
            byte[] bytes = Payload;
            string variant = label == "preview" ? "preview" : "original";
            return new("media-" + label, variant, label == "preview" ? "https://i.4cdn.org/b/404s.jpg" : "https://i.4cdn.org/b/404.jpg",
                label + ".bin", bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), Hash(bytes));
        }
    }

    private sealed record TreeSnapshot(IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlyDictionary<string, (FileAttributes Attributes, DateTime LastWrite)> Entries, string Digest)
    {
        internal static TreeSnapshot Capture(string directory)
        {
            Dictionary<string, byte[]> files = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .ToDictionary(path => Path.GetRelativePath(directory, path),
                        path => File.ReadAllBytes(path), StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            Dictionary<string, (FileAttributes Attributes, DateTime LastWrite)> entries = new(StringComparer.Ordinal);
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Prepend(directory))
                    entries.Add(Path.GetRelativePath(directory, path), (File.GetAttributes(path), File.GetLastWriteTimeUtc(path)));
            }
            using MemoryStream digestInput = new();
            foreach ((string path, byte[] bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                byte[] name = Encoding.UTF8.GetBytes(path); digestInput.Write(name);
                digestInput.WriteByte(0); digestInput.Write(bytes); digestInput.WriteByte(0xff);
            }
            return new(files, entries, Convert.ToHexString(SHA256.HashData(digestInput.ToArray())));
        }

        internal void AssertUnchanged(string directory)
        {
            TreeSnapshot after = Capture(directory);
            Assert.Equal(Digest, after.Digest);
            Assert.Equal(Files.Keys.OrderBy(x => x), after.Files.Keys.OrderBy(x => x));
            foreach (string key in Files.Keys) Assert.Equal(Files[key], after.Files[key]);
            Assert.Equal(Entries.Keys.OrderBy(x => x), after.Entries.Keys.OrderBy(x => x));
            foreach (string key in Entries.Keys) Assert.Equal(Entries[key], after.Entries[key]);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
