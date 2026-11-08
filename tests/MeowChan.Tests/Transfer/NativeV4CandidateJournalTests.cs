using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real synthetic package -> owned stage -> inactive Journal2 candidate -> fresh inspector.
// No SQL, profile activation, network, external fixtures, or claim of full restart/merge acceptance.
public sealed class NativeV4CandidateJournalTests
{
    private const string JournalName = ".meowchan-import-candidate.json";
    private const string ManifestName = ".meowchan-manifest.json";
    private static readonly ResourceId Resource = new("4chan", "b", "404");
    private static readonly Guid Operation = Guid.Parse("32b132b1-aca1-4a41-b111-000000000001");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic original; not a decoder fixture");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic ordinary preview bytes");

    [Fact]
    public async Task ReadyDerivedOnlyCandidateSurvivesFreshInspectionWithoutInventingOriginalBindings()
    {
        await using var f = await Fixture.CreateAsync();
        var candidate = await f.Materialize();
        Assert.Equal(2, candidate.PlanVersion);
        Assert.Empty(candidate.Bindings);
        Assert.Single(candidate.OriginalEvidence);
        Assert.Single(candidate.DerivedRepresentations);
        Assert.Equal(f.Plan.FullSelection.CanonicalJson, candidate.Selection!.CanonicalJson);
        Assert.Equal(f.Plan.FullSelection.CreateInventory(Operation, f.Stage.Package.Envelope.Fingerprint, At).Sha256,
            candidate.InventorySha256);
        var inspection = await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Ready);
        Assert.Equal(candidate.ManifestGeneration, inspection.Candidate!.ManifestGeneration);
        Assert.Equal(candidate.Selection.CanonicalJson, inspection.Candidate.Selection!.CanonicalJson);
        Assert.Equal(candidate.InventorySha256, inspection.Candidate.InventorySha256);
        Assert.Empty(inspection.Candidate.Bindings);
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.original.bin"));
        f.AssertInactive();
    }

    [Fact]
    public async Task SelectedPreviewOfContainedParentRemainsContextOnlyAfterIndependentInspection()
    {
        await using var f = await Fixture.CreateAsync(containedOriginal: true);
        var previewId = Assert.Single(f.Plan.FullSelection.Members.Where(m => m.Id.Role == NativeMediaMemberRole.Preview)).Id;
        var selection = f.Plan.Select([previewId]);
        Assert.True(Assert.Single(selection.Members.Where(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence)).ContextOnly);
        var candidate = await f.Materialize(selection);
        var inspection = await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Ready);
        Assert.Empty(inspection.Candidate!.Bindings);
        Assert.Equal(selection.CanonicalJson, inspection.Candidate.Selection!.CanonicalJson);
        Assert.Empty(Directory.GetFiles(candidate.Directory, "*.original.bin"));
        f.AssertInactive();
    }

    public static IEnumerable<object[]> Checkpoints() =>
        Enum.GetValues<PassiveMediaCandidatePhase>().Select(p => new object[] { (int)p });

    [Theory]
    [MemberData(nameof(Checkpoints))]
    public async Task CancellationKeepsInactivePartialStateAndReadyMarkerIsTheCompletionBoundary(int value)
    {
        var stop = (PassiveMediaCandidatePhase)value;
        await using var f = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        List<PassiveMediaCandidatePhase> seen = [];
        PassiveMediaImportCandidate? candidate = null;
        Exception? error = await Record.ExceptionAsync(async () =>
        {
            candidate = await f.Materialize(checkpoint: (phase, directory) =>
            {
                seen.Add(phase);
                AssertCheckpointJournal(directory, phase);
                if (phase == stop) cancellation.Cancel();
            }, ct: cancellation.Token);
        });
        Assert.Equal(Enum.GetValues<PassiveMediaCandidatePhase>().Take(value + 1), seen);
        string path = f.OnlyCandidate();
        if (stop == PassiveMediaCandidatePhase.ReadyJournalSaved)
        {
            Assert.Null(error);
            Assert.NotNull(candidate);
            await InspectUnchanged(path, PassiveMediaCandidateStatus.Ready);
        }
        else
        {
            var cancelled = Assert.IsType<PassiveMediaCandidateCanceledException>(error);
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            Assert.Equal(path, cancelled.CandidateDirectory);
            Assert.Null(candidate);
            await InspectUnchanged(path, PassiveMediaCandidateStatus.Incomplete);
        }
        f.AssertInactive();
    }

    [Theory]
    [MemberData(nameof(Checkpoints))]
    public async Task CheckpointFailureRetainsExactInspectableCandidateAndNeverPublishes(int value)
    {
        var stop = (PassiveMediaCandidatePhase)value;
        await using var f = await Fixture.CreateAsync();
        List<PassiveMediaCandidatePhase> seen = [];
        var error = await Assert.ThrowsAsync<PassiveMediaCandidateWriteException>(async () =>
            await f.Materialize(checkpoint: (phase, directory) =>
            {
                seen.Add(phase);
                AssertCheckpointJournal(directory, phase);
                if (phase == stop) throw new IOException("synthetic Journal2 checkpoint loss");
            }));
        Assert.Equal(Enum.GetValues<PassiveMediaCandidatePhase>().Take(value + 1), seen);
        Assert.Equal(f.OnlyCandidate(), error.CandidateDirectory);
        await InspectUnchanged(error.CandidateDirectory, stop == PassiveMediaCandidatePhase.ReadyJournalSaved
            ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete);
        f.AssertInactive();
    }

    [Fact]
    public async Task PreCancelledMaterializerCreatesNoChild()
    {
        await using var f = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = Tree.Capture(f.Parent);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await f.Materialize(ct: cancellation.Token));
        before.AssertUnchanged(f.Parent);
        f.AssertInactive();
    }

    [Fact]
    public async Task EmptySelectionCannotCreateCandidate()
    {
        await using var f = await Fixture.CreateAsync();
        var empty = f.Plan.Select(Array.Empty<NativeMediaMemberId>());
        Assert.True(empty.IsEmpty);
        var before = Tree.Capture(f.Parent);
        var error = await Record.ExceptionAsync(async () => await f.Materialize(empty));
        Assert.True(error is InvalidDataException or ArgumentException, error?.ToString() ?? "Empty selection materialized");
        before.AssertUnchanged(f.Parent);
        f.AssertInactive();
    }

    [Theory]
    [InlineData("extra-file")] [InlineData("extra-directory")]
    [InlineData("missing-payload")] [InlineData("changed-payload")] [InlineData("missing-manifest")]
    [InlineData("missing-journal")] [InlineData("missing-lock")] [InlineData("extra-original-alias")]
    public async Task ReadyFileInventoryIsExactAndFreshInspectionDoesNotRepair(string fault)
    {
        await using var f = await Fixture.CreateAsync();
        var candidate = await f.Materialize();
        var inventory = f.Plan.FullSelection.CreateInventory(Operation, f.Stage.Package.Envelope.Fingerprint, At);
        string payload = Path.Combine(candidate.Directory, Assert.Single(inventory.Payloads).FileName);
        switch (fault)
        {
            case "extra-file": await File.WriteAllBytesAsync(Path.Combine(candidate.Directory, "foreign.bin"), [1]); break;
            case "extra-directory": Directory.CreateDirectory(Path.Combine(candidate.Directory, "foreign")); break;
            case "missing-payload": File.Delete(payload); break;
            case "changed-payload":
                byte[] bytes = await File.ReadAllBytesAsync(payload); bytes[0] ^= 0x55;
                await File.WriteAllBytesAsync(payload, bytes); break;
            case "missing-manifest": File.Delete(Path.Combine(candidate.Directory, ManifestName)); break;
            case "missing-journal": File.Delete(Path.Combine(candidate.Directory, JournalName)); break;
            case "missing-lock": File.Delete(Path.Combine(candidate.Directory, ".meowchan-store.lock")); break;
            case "extra-original-alias":
                File.Copy(payload, Path.Combine(candidate.Directory, Hash(Preview) + ".original.bin")); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    public static IEnumerable<object[]> JournalFaults()
    {
        foreach (string field in new[] { "Version", "Phase", "OperationId", "PackageFingerprint", "Resource",
            "ImportedAtUtc", "ManifestGeneration", "SelectionJson", "SelectionSha256", "InventorySha256" })
        {
            yield return new object[] { "missing:" + field };
            yield return new object[] { "null:" + field };
        }
        foreach (string fault in new[] { "duplicate-version", "duplicate-phase", "unknown", "legacy-field",
            "version-zero", "version-one", "version-future", "version-string", "phase-unknown", "copying-with-ready-hashes",
            "operation-empty", "operation-malformed", "operation-other", "fingerprint", "fingerprint-other",
            "resource-other", "time-other", "selection-digest", "inventory-digest",
            "manifest-digest", "selection-shape", "selection-empty", "selection-noncanonical", "trailing-garbage" })
            yield return new object[] { fault };
    }

    [Theory]
    [MemberData(nameof(JournalFaults))]
    public async Task StrictJournal2ShapeIdentitySelectionAndDigestsRejectWithoutRepair(string fault)
    {
        await using var f = await Fixture.CreateAsync();
        var candidate = await f.Materialize();
        string path = Path.Combine(candidate.Directory, JournalName);
        string raw = await File.ReadAllTextAsync(path);
        var journal = JsonNode.Parse(raw)!.AsObject();
        if (fault.StartsWith("missing:", StringComparison.Ordinal))
            Assert.True(journal.Remove(fault[8..]), "Required journal field not emitted: " + fault);
        else if (fault.StartsWith("null:", StringComparison.Ordinal)) journal[fault[5..]] = null;
        else switch (fault)
        {
            case "unknown": journal["Unexpected"] = 0; break;
            case "legacy-field": journal["RevisionKeys"] = null; break;
            case "version-zero": journal["Version"] = 0; break;
            case "version-one": journal["Version"] = 1; break;
            case "version-future": journal["Version"] = 999; break;
            case "version-string": journal["Version"] = "2"; break;
            case "phase-unknown": journal["Phase"] = "published"; break;
            case "copying-with-ready-hashes": journal["Phase"] = "copying"; break;
            case "operation-empty": journal["OperationId"] = Guid.Empty.ToString("D"); break;
            case "operation-malformed": journal["OperationId"] = "not-a-guid"; break;
            case "fingerprint": journal["PackageFingerprint"] = new string('z', 64); break;
            case "fingerprint-other": journal["PackageFingerprint"] = new string('0', 64); break;
            case "operation-other": journal["OperationId"] = "32b132b1-aca1-4a41-b111-000000000002"; break;
            case "resource-other": journal["Resource"]!["Thread"] = "405"; break;
            case "time-other": journal["ImportedAtUtc"] = At.AddSeconds(1).ToString("O"); break;
            case "selection-digest": journal["SelectionSha256"] = new string('0', 64); break;
            case "inventory-digest": journal["InventorySha256"] = new string('0', 64); break;
            case "manifest-digest": journal["ManifestGeneration"] = new string('0', 64); break;
            case "selection-shape": journal["SelectionJson"] = new JsonObject(); break;
            case "selection-empty":
                var empty = f.Plan.Select(Array.Empty<NativeMediaMemberId>());
                SetSelection(journal, empty, f); break;
            case "selection-noncanonical":
                string padded = f.Plan.FullSelection.CanonicalJson + " ";
                journal["SelectionJson"] = padded; journal["SelectionSha256"] = Hash(Encoding.UTF8.GetBytes(padded)); break;
            case "duplicate-version": case "duplicate-phase": case "trailing-garbage": break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        string damaged = fault switch
        {
            "duplicate-version" => raw.TrimEnd()[..^1] + ",\"Version\":2}",
            "duplicate-phase" => raw.TrimEnd()[..^1] + ",\"Phase\":\"ready\"}",
            "trailing-garbage" => raw + "not-json",
            _ => journal.ToJsonString()
        };
        await File.WriteAllTextAsync(path, damaged);
        await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    [Fact]
    public async Task SelfConsistentChangedSelectionAndInventoryHashDoNotAuthorizeOldManifest()
    {
        await using var f = await Fixture.CreateAsync();
        var candidate = await f.Materialize();
        // Both descriptors are individually valid. The old manifest still records Omitted,
        // so merely verifying the journal's self-declared selection/inventory digests is insufficient.
        var changed = NativeMediaCandidateSelection.Create(f.Plan.FullSelection.Members.Select(m =>
            m.Id.Role == NativeMediaMemberRole.OriginalEvidence
                ? m with { Descriptor = m.Descriptor with { Status = TransferContentStatus.Unavailable } } : m));
        Assert.NotEqual(f.Plan.FullSelection.Sha256, changed.Sha256);
        Assert.Equal(changed.CanonicalJson, NativeMediaCandidateSelection.Parse(changed.CanonicalJson).CanonicalJson);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidate.Directory, JournalName)))!.AsObject();
        SetSelection(journal, changed, f);
        Assert.NotEqual(candidate.InventorySha256, journal["InventorySha256"]!.GetValue<string>());
        await File.WriteAllTextAsync(Path.Combine(candidate.Directory, JournalName), journal.ToJsonString());
        await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    [Fact]
    public async Task ValidSubsetAndRecomputedDigestsCannotHideUnselectedManifestMembersOrFiles()
    {
        await using var f = await Fixture.CreateAsync();
        var candidate = await f.Materialize();
        var evidence = Assert.Single(f.Plan.FullSelection.Members.Where(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence));
        var reduced = f.Plan.Select([evidence.Id]);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidate.Directory, JournalName)))!.AsObject();
        SetSelection(journal, reduced, f);
        await File.WriteAllTextAsync(Path.Combine(candidate.Directory, JournalName), journal.ToJsonString());
        await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    [Fact]
    public async Task DerivedMutationAtManifestCheckpointCannotBePromotedToReady()
    {
        await using var f = await Fixture.CreateAsync();
        var inventory = f.Plan.FullSelection.CreateInventory(Operation, f.Stage.Package.Envelope.Fingerprint, At);
        var error = await Assert.ThrowsAsync<PassiveMediaCandidateWriteException>(async () =>
            await f.Materialize(checkpoint: (phase, directory) =>
            {
                if (phase != PassiveMediaCandidatePhase.ManifestSaved) return;
                string payload = Path.Combine(directory, Assert.Single(inventory.Payloads).FileName);
                byte[] bytes = File.ReadAllBytes(payload); bytes[0] ^= 0x55; File.WriteAllBytes(payload, bytes);
            }));
        Assert.Equal(f.OnlyCandidate(), error.CandidateDirectory);
        await InspectUnchanged(error.CandidateDirectory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    [Fact]
    public async Task ExistingJournal1StillInspectsWithOriginalBindingAndNoInventedV2Inventory()
    {
        await using var f = await Fixture.CreateAsync(legacy: true);
        var candidate = await MaterializeLegacy(f);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(candidate.Directory, JournalName)))!.AsObject();
        Assert.Equal(1, journal["Version"]!.GetValue<int>());
        var inspection = await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Ready);
        Assert.Equal(1, inspection.Candidate!.PlanVersion);
        Assert.Single(inspection.Candidate.Bindings);
        Assert.Null(inspection.Candidate.Selection);
        Assert.Null(inspection.Candidate.InventorySha256);
        Assert.Empty(inspection.Candidate.OriginalEvidence);
        Assert.Empty(inspection.Candidate.DerivedRepresentations);
        f.AssertInactive();
    }

    [Theory]
    [InlineData("SelectionJson", true)] [InlineData("SelectionJson", false)]
    [InlineData("SelectionSha256", true)] [InlineData("SelectionSha256", false)]
    [InlineData("InventorySha256", true)] [InlineData("InventorySha256", false)]
    public async Task Journal1RejectsV2FieldsEvenWhenNull(string field, bool nullValue)
    {
        await using var f = await Fixture.CreateAsync(legacy: true);
        var candidate = await MaterializeLegacy(f);
        string path = Path.Combine(candidate.Directory, JournalName);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.False(journal.ContainsKey(field));
        journal[field] = nullValue ? null : JsonValue.Create(field == "SelectionJson" ? "{}" : new string('a', 64));
        await File.WriteAllTextAsync(path, journal.ToJsonString());
        await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    [Fact]
    public async Task ConcurrentFreshInspectorReturnsBusyAndCancellationDoesNotReleaseProducerLease()
    {
        await using var f = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = TimeSpan.FromSeconds(10);
        var write = Task.Run(() => f.Materialize(checkpoint: (phase, path) =>
        {
            if (phase != PassiveMediaCandidatePhase.JournalFlushed) return;
            entered.TrySetResult(path);
            release.Task.GetAwaiter().GetResult();
        }));
        try
        {
            string path = await entered.Task.WaitAsync(deadline);
            byte[] journalBefore = await File.ReadAllBytesAsync(Path.Combine(path, JournalName));
            string[] namesBefore = Directory.GetFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var inspector = new MediaStore(File.Move, directoryLeaseTimeout: TimeSpan.FromMilliseconds(75));
            var busy = await inspector.InspectPassiveImportCandidateAsync(path).WaitAsync(deadline);
            Assert.Equal(PassiveMediaCandidateStatus.Busy, busy.Status);
            Assert.Null(busy.Candidate);
            using var cancel = new CancellationTokenSource();
            var waiting = new MediaStore(File.Move, directoryLeaseTimeout: TimeSpan.FromSeconds(30))
                .InspectPassiveImportCandidateAsync(path, cancel.Token);
            cancel.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting.WaitAsync(deadline));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.False(write.IsCompleted);
            Assert.Equal(journalBefore, await File.ReadAllBytesAsync(Path.Combine(path, JournalName)));
            Assert.Equal(namesBefore, Directory.GetFileSystemEntries(path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            release.TrySetResult(true);
            await write.WaitAsync(deadline);
        }
        await InspectUnchanged((await write).Directory, PassiveMediaCandidateStatus.Ready);
        f.AssertInactive();
    }

    [Theory]
    [InlineData("ManifestGeneration")] [InlineData("InventorySha256")]
    public async Task CopyingJournalCannotClaimReadyDigests(string field)
    {
        await using var f = await Fixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var error = await Assert.ThrowsAsync<PassiveMediaCandidateCanceledException>(async () =>
            await f.Materialize(ct: cancel.Token, checkpoint: (phase, _) =>
            {
                if (phase == PassiveMediaCandidatePhase.JournalFlushed) cancel.Cancel();
            }));
        await InspectUnchanged(error.CandidateDirectory, PassiveMediaCandidateStatus.Incomplete);
        string path = Path.Combine(error.CandidateDirectory, JournalName);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(journal.ContainsKey(field));
        Assert.Null(journal[field]);
        journal[field] = new string('a', 64);
        await File.WriteAllTextAsync(path, journal.ToJsonString());
        await InspectUnchanged(error.CandidateDirectory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task FailureAfterActualRenamePreservesDurableMarkerBoundary(int failingRename)
    {
        await using var f = await Fixture.CreateAsync();
        int renames = 0;
        var store = new MediaStore((source, destination, overwrite) =>
        {
            File.Move(source, destination, overwrite);
            if (++renames == failingRename) throw new IOException("synthetic failure after actual rename");
        });
        var error = await Assert.ThrowsAsync<PassiveMediaCandidateWriteException>(async () =>
            await store.MaterializePassiveImportCandidateAsync(f.Plan, f.Plan.FullSelection, f.Parent, Operation, At));
        Assert.Equal(failingRename, renames);
        Assert.Equal(f.OnlyCandidate(), error.CandidateDirectory);
        await InspectUnchanged(error.CandidateDirectory, failingRename == 3
            ? PassiveMediaCandidateStatus.Ready : PassiveMediaCandidateStatus.Incomplete);
        f.AssertInactive();
    }

    [Theory]
    [InlineData("empty-derived")] [InlineData("empty-evidence")] [InlineData("duplicate-version")]
    public async Task RehashingChangedManifestDoesNotMakeItMatchCanonicalSelection(string fault)
    {
        await using var f = await Fixture.CreateAsync();
        var candidate = await f.Materialize();
        string path = Path.Combine(candidate.Directory, ManifestName);
        string raw = await File.ReadAllTextAsync(path);
        var manifest = JsonNode.Parse(raw)!.AsObject();
        string damaged;
        if (fault == "duplicate-version") damaged = raw.TrimEnd()[..^1] + ",\"Version\":6}";
        else
        {
            string field = fault == "empty-derived" ? "DerivedRepresentations" : "OriginalEvidence";
            Assert.Single(manifest[field]!.AsArray());
            manifest[field] = new JsonArray();
            damaged = manifest.ToJsonString();
        }
        await File.WriteAllTextAsync(path, damaged);
        string journalPath = Path.Combine(candidate.Directory, JournalName);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        journal["ManifestGeneration"] = Hash(await File.ReadAllBytesAsync(path));
        await File.WriteAllTextAsync(journalPath, journal.ToJsonString());
        await InspectUnchanged(candidate.Directory, PassiveMediaCandidateStatus.Invalid);
        f.AssertInactive();
    }

    private static Task<PassiveMediaImportCandidate> MaterializeLegacy(Fixture f)
    {
        string key = f.Stage.Package.Catalog.Threads[0].Posts[0].Attachments[0].Metadata.AttachmentRevisionKey!;
        return new MediaStore().MaterializePassiveImportCandidateAsync(f.Stage, Resource,
            new HashSet<string>(StringComparer.Ordinal) { key }, f.Parent, Operation, At);
    }

    private static void SetSelection(JsonObject journal, NativeMediaCandidateSelection selection, Fixture f)
    {
        journal["SelectionJson"] = selection.CanonicalJson;
        journal["SelectionSha256"] = selection.Sha256;
        journal["InventorySha256"] = selection.CreateInventory(Operation, f.Stage.Package.Envelope.Fingerprint, At).Sha256;
    }

    private static void AssertCheckpointJournal(string directory, PassiveMediaCandidatePhase phase)
    {
        Assert.True(File.Exists(Path.Combine(directory, ".meowchan-store.lock")));
        var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, JournalName)))!.AsObject();
        Assert.Equal(2, journal["Version"]!.GetValue<int>());
        Assert.Equal(phase == PassiveMediaCandidatePhase.ReadyJournalSaved ? "ready" : "copying",
            journal["Phase"]!.GetValue<string>());
        if (phase != PassiveMediaCandidatePhase.ReadyJournalSaved)
        {
            Assert.Null(journal["ManifestGeneration"]);
            Assert.Null(journal["InventorySha256"]);
        }
        else
        {
            Assert.Equal(64, journal["ManifestGeneration"]!.GetValue<string>().Length);
            Assert.Equal(64, journal["InventorySha256"]!.GetValue<string>().Length);
        }
    }

    private static async Task<PassiveMediaCandidateInspection> InspectUnchanged(string path, PassiveMediaCandidateStatus status)
    {
        var before = Tree.Capture(path);
        PassiveMediaCandidateInspection? last = null;
        for (int i = 0; i < 2; i++)
        {
            last = await new MediaStore().InspectPassiveImportCandidateAsync(path);
            Assert.Equal(status, last.Status);
            if (status == PassiveMediaCandidateStatus.Ready) Assert.NotNull(last.Candidate);
            else Assert.Null(last.Candidate);
            before.AssertUnchanged(path);
        }
        return last!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; }
        internal string Parent { get; }
        internal NativeTransferImportStage Stage { get; }
        private readonly NativeMediaMaterializationPlan? plan;
        internal NativeMediaMaterializationPlan Plan => plan ?? throw new InvalidOperationException("Legacy fixture has no v4 plan.");
        private string SentinelRoot { get; }
        private Tree Sentinel { get; }
        private Fixture(string root, NativeTransferImportStage stage)
        {
            Root = root; Stage = stage;
            Parent = Path.Combine(root, "inactive-candidates"); Directory.CreateDirectory(Parent);
            SentinelRoot = Path.Combine(root, "unrelated-media"); Directory.CreateDirectory(SentinelRoot);
            File.WriteAllBytes(Path.Combine(SentinelRoot, "keep.bin"), [9, 8, 7]);
            Sentinel = Tree.Capture(SentinelRoot);
            if (stage.Package.IsChanV4) plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        }
        internal Task<PassiveMediaImportCandidate> Materialize(NativeMediaCandidateSelection? selection = null,
            Action<PassiveMediaCandidatePhase, string>? checkpoint = null, CancellationToken ct = default) =>
            new MediaStore().MaterializePassiveImportCandidateAsync(Plan, selection ?? Plan.FullSelection,
                Parent, Operation, At, ct, checkpoint);
        internal string OnlyCandidate() => Assert.Single(Directory.GetDirectories(Parent, ".meowchan-passive-*"));
        internal void AssertInactive()
        {
            Sentinel.AssertUnchanged(SentinelRoot);
            Assert.Empty(Directory.GetFiles(Root, "archive.db*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Parent));
            Assert.All(Directory.GetDirectories(Parent), p => Assert.StartsWith(".meowchan-passive-", Path.GetFileName(p)));
        }
        internal static async Task<Fixture> CreateAsync(bool containedOriginal = false, bool legacy = false)
        {
            string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-v4-journal-");
            NativeTransferImportStage? stage = null;
            try
            {
                var originalVersion = new TransferOriginalByteVersion(Original.Length, Hash(Original), Md5(Original), null);
                var provider = new MediaProviderMetadata("111", "original", "https://i.4cdn.org/b/111.webm", "original.webm",
                    Original.Length, Md5(Original), Hash(Original));
                string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "404", 0, provider);
                var metadata = new CapturedAttachment(0, provider.MediaId, "original", provider.Url, provider.OriginalFileName,
                    ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
                { AttachmentRevisionKey = key, ExpectedSha256 = Hash(Original) };
                bool contained = containedOriginal || legacy;
                var attachment = new TransferAttachment(metadata, TransferMediaRole.Original,
                    contained ? TransferContentStatus.Contained : TransferContentStatus.Omitted,
                    contained ? Hash(Original) : null, null);
                if (!legacy) attachment = attachment with
                {
                    OriginalByteVersion = originalVersion,
                    DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview, key, originalVersion,
                        TransferContentStatus.Contained, Hash(Preview))]
                };
                var post = new TransferPost(new CapturedPost("404", 0, PostFieldPresence.All,
                    Html: "<p>synthetic journal</p>", Attachments: [metadata]), [attachment]);
                var catalog = new NativeTransferCatalog(At, [new TransferThread(Resource,
                    "https://boards.4chan.org/b/thread/404", "synthetic candidate", ThreadSourceState.NotFound,
                    At, null, 0, false, [post])]);
                byte[][] payloads = legacy ? [Original] : contained ? [Original, Preview] : [Preview];
                var sources = payloads.Select(bytes => new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                    _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
                using var package = new MemoryStream();
                var written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources, Operation);
                Assert.True(written.IsComplete, written.ToString());
                Assert.Equal(!legacy, written.Package!.IsChanV4);
                package.Position = 0;
                string stageRoot = Path.Combine(root, "staging"); Directory.CreateDirectory(stageRoot);
                var staged = await NativeTransferImportStage.CreateAsync(package, stageRoot);
                stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
                return new Fixture(root, stage);
            }
            catch
            {
                if (stage != null) await stage.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try { AssertInactive(); }
            finally
            {
                try { await Stage.DisposeAsync(); }
                finally { Directory.Delete(Root, recursive: true); }
            }
        }
    }

    private sealed record Tree(string[] Directories, SortedDictionary<string, string> Files)
    {
        internal static Tree Capture(string root) => new(
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            new SortedDictionary<string, string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(root, p), p => Hash(File.ReadAllBytes(p))), StringComparer.Ordinal));
        internal void AssertUnchanged(string root)
        {
            var actual = Capture(root);
            Assert.Equal(Directories, actual.Directories);
            Assert.Equal(Files.ToArray(), actual.Files.ToArray());
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
}
