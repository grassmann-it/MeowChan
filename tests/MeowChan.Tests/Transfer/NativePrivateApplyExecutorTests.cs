using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;
using Operation = MeowChan.Transfer.NativePrivateApplyOperationPhase;
using Phase = MeowChan.Transfer.NativePrivateApplyStepPhase;
using Store = MeowChan.Transfer.NativePrivateApplyStoreKind;

namespace MeowChan.Tests.Transfer;

// Synthetic selected-empty plans exercise executor control flow through real
// owner-issued authority. They do not establish planner/original-media readiness.
public sealed class NativePrivateApplyExecutorTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "9101");
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private static readonly string[] StoreNames =
        ["thread-favorites.json", "media-favorites.json", "post-bookmarks.json"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NonthrowingRecoveryChainStopsAtTheAffectedStoreWithoutCompleting(int affectedIndex)
    {
        await WithCapabilityAsync((capability, lease, profile) =>
        {
            // Pending evidence is classified Unreadable without a thrown store
            // error. It is neither cleaned up nor treated as an empty store.
            string pending = Path.Combine(profile, StoreNames[affectedIndex] + ".private-pending-uncertain");
            File.WriteAllText(pending, "preserve uncertain evidence");

            // The old fallthrough attempted the next store, whose observation
            // the terminal journal rejected. Execute caught that protocol error,
            // hiding the attempt from result/byte assertions alone. Observe only
            // protocol errors on this synchronous call's thread; always detach.
            int executingThread = Environment.CurrentManagedThreadId;
            List<Type> protocolErrors = [];
            void ObserveError(object? sender, FirstChanceExceptionEventArgs args)
            {
                if (Environment.CurrentManagedThreadId == executingThread
                    && args.Exception is InvalidDataException or InvalidOperationException)
                    protocolErrors.Add(args.Exception.GetType());
            }

            NativePrivateApplyExecutionResult result;
            AppDomain.CurrentDomain.FirstChanceException += ObserveError;
            try
            {
                result = NativePrivateApplyExecutor.Execute(capability);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= ObserveError;
            }

            Assert.Empty(protocolErrors);
            Assert.False(result.IsReplay);
            NativePrivateApplyJournalInspection stopped = result.Inspection;
            Assert.Equal(Operation.RecoveryRequired, stopped.State.Phase);
            Assert.False(stopped.HasTerminalReceipt);
            Assert.Equal(3 + affectedIndex, stopped.EventCount); // Prepared, Applying, prefix, recovery record.
            Assert.Equal(Enum.GetValues<Store>(), stopped.State.Steps.Select(step => step.StoreKind));
            for (int index = 0; index < StoreNames.Length; index++)
            {
                Assert.Equal(index < affectedIndex ? Phase.NoChange
                    : index == affectedIndex ? Phase.Unreadable : Phase.Planned,
                    stopped.State.Steps[index].Phase);
                Assert.False(File.Exists(Path.Combine(profile, StoreNames[index])));
                Assert.False(File.Exists(Path.Combine(profile, StoreNames[index] + ".bak")));
            }
            Assert.Equal("preserve uncertain evidence", File.ReadAllText(pending));
            string operationDirectory = OperationDirectory(profile, capability);
            Assert.False(File.Exists(Path.Combine(operationDirectory, "receipt.json")));
            NativePrivateApplyJournalInspection persisted = NativePrivateApplyJournal.Open(lease)
                .Inspect(capability.OperationId);
            Assert.Equal(stopped.LastRecordSha256, persisted.LastRecordSha256);
            Assert.Equal(Operation.RecoveryRequired, persisted.State.Phase);

            // Even when the pending artifact disappears, an exact retry is
            // inspection/replay, not permission to resume the remaining stores.
            File.Delete(pending);
            NativePrivateApplyExecutionResult replay = NativePrivateApplyExecutor.Execute(capability);
            Assert.True(replay.IsReplay);
            Assert.Equal(Operation.RecoveryRequired, replay.Inspection.State.Phase);
            Assert.False(replay.Inspection.HasTerminalReceipt);
            Assert.Equal(stopped.EventCount, replay.Inspection.EventCount);
            Assert.Equal(stopped.LastRecordSha256, replay.Inspection.LastRecordSha256);
            Assert.Equal(stopped.State.Steps.Select(step => step.Phase),
                replay.Inspection.State.Steps.Select(step => step.Phase));
            Assert.False(File.Exists(Path.Combine(operationDirectory, "receipt.json")));
        });
    }

    [Fact]
    public async Task SettledSelectedEmptyStoresStillCompleteAndReplayReceiptFirst()
    {
        await WithCapabilityAsync((capability, _, profile) =>
        {
            NativePrivateApplyExecutionResult completed = NativePrivateApplyExecutor.Execute(capability);
            Assert.False(completed.IsReplay);
            Assert.True(completed.Inspection.HasTerminalReceipt);
            Assert.Equal(Operation.Completed, completed.Inspection.State.Phase);
            Assert.All(completed.Inspection.State.Steps, step => Assert.Equal(Phase.NoChange, step.Phase));

            string operationDirectory = OperationDirectory(profile, capability);
            byte[] receipt = File.ReadAllBytes(Path.Combine(operationDirectory, "receipt.json"));
            File.Delete(Path.Combine(operationDirectory, "request.json"));
            File.Delete(Path.Combine(operationDirectory, "claim.json"));
            foreach (string name in StoreNames)
                File.WriteAllText(Path.Combine(profile, name), "unreadable current store");
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();

            NativePrivateApplyExecutionResult replay = NativePrivateApplyExecutor.Execute(capability, cancelled.Token);
            Assert.True(replay.IsReplay);
            Assert.True(replay.Inspection.HasTerminalReceipt);
            Assert.Equal(Operation.Completed, replay.Inspection.State.Phase);
            Assert.Equal(completed.Inspection.LastRecordSha256, replay.Inspection.LastRecordSha256);
            Assert.All(replay.Inspection.State.Steps, step => Assert.Equal(Phase.NoChange, step.Phase));
            Assert.Equal(receipt, File.ReadAllBytes(Path.Combine(operationDirectory, "receipt.json")));
            foreach (string name in StoreNames)
                Assert.Equal("unreadable current store", File.ReadAllText(Path.Combine(profile, name)));
        });
    }

    private static string OperationDirectory(string profile, NativePrivateApplyCapability capability)
        => Path.Combine(profile, NativePrivateApplyJournal.DirectoryName, capability.OperationId.ToString("N"));

    private static async Task WithCapabilityAsync(Action<NativePrivateApplyCapability, ProfileLease, string> inspect)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        using MemoryStream bytes = await WriteSelectedEmptyPackageAsync();
        NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(bytes, directory.Path);
        await using NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
        NativePrivateConsentDecision consent = catalog.IssueNativePrivateConsentDecision(
            stage, NativePrivateConsentScope.ExplicitPrivateMetadata, All);
        NativeMediaImportPlan plan = Assert.IsType<NativeMediaImportPlan>(
            (await catalog.PrepareNativeMediaImportAsync(stage, consent)).Plan);
        NativePrivateMetadataProjection incoming = Assert.IsType<NativePrivateMetadataProjection>(
            plan.SelectedPrivateMetadata);
        NativePrivateMetadataComparisonResult comparison = NativePrivateMetadataComparison.Analyze(
            incoming, plan.Package.Catalog, incoming, All);
        NativePrivateApplyStoreImage missing = new(false, 0, null, 0, 0, null);
        ImmutableArray<NativePrivateApplyStoreFence> fences = Enum.GetValues<Store>()
            .Select(kind => new NativePrivateApplyStoreFence(kind, missing, missing,
                kind == Store.PostBookmarksAndNotes ? null : missing)).ToImmutableArray();
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken admission = await lease.Admission.EnterExclusiveAsync();
        using NativePrivateApplyCapability capability = catalog.IssueNativePrivateApplyCapability(
            lease, admission, plan, comparison, fences);
        inspect(capability, lease, directory.Path);
    }

    private static async Task<MemoryStream> WriteSelectedEmptyPackageAsync()
    {
        DateTimeOffset at = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        // The existing owner-plan API requires a contained public original.
        // This in-memory fixture does not select or resolve any private media.
        byte[] media = [1, 2, 3, 4];
        string hash = Convert.ToHexString(SHA256.HashData(media)).ToLowerInvariant();
        CapturedAttachment attachment = new(0, "9101001", "original",
            "https://i.4cdn.org/b/9101001.jpg", "synthetic.jpg");
        CapturedPost post = new("1", 1, PostFieldPresence.All, at, "", null, "public test post", false, [attachment]);
        NativeTransferCatalog catalog = new(at, [new(Thread,
            "https://boards.4chan.org/b/thread/9101", "synthetic executor thread",
            ThreadSourceState.NotFound, at, at, 1, true,
            [new(post, [new(attachment, TransferMediaRole.Original, TransferContentStatus.Contained, hash, null)])])]);
        NativePrivateMetadataProjection projection = new([Thread], All, [], [], []);
        TransferPayloadSource source = new(new(media.Length, hash),
            _ => Task.FromResult<Stream>(new MemoryStream(media, writable: false)));
        MemoryStream output = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, catalog, [source],
            Guid.Parse("b8d7850a-ea7e-413d-9b68-723c15b66258"), privateMetadata: projection);
        Assert.True(written.IsComplete, written.Error.ToString());
        output.Position = 0;
        return output;
    }
}
