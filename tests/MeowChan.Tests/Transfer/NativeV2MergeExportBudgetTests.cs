using System.Collections.Immutable;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Small authentic writer/stage fixtures; the budget itself only sees metadata.
// These are not planner, filesystem-verification, or merge-admission proofs.
public sealed class NativeV2MergeExportBudgetTests
{
    private static readonly Guid Operation = new("32b532b5-aaaa-4aaa-baaa-000000000001");
    private static readonly DateTimeOffset At = NativeV2ImportFixture.At;

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task FullSafeMatchesExactStoredPackageIncludingRetainedAndZeroBindings(bool originals, bool retained)
    {
        var content = NativeV2ImportFixture.Content(originals, derived: true, retainedOnly: retained, presence: false);
        byte[][] bytes = originals ? [NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview]
            : [NativeV2ImportFixture.Preview];
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content, bytes);
        var materialization = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var view = View(content.Threads[0]);
        // The stage is already disposed: budgeting must not open or re-read it.
        await stage.DisposeAsync();
        var budget = NativeMediaMergeInventoryExportBudget.Measure(materialization, view, [],
            materialization.FullSelection, Operation, At);
        await AssertExact(content, bytes, budget);
        Assert.All(materialization.FullSelection.Members, m => Assert.Null(m.Id.SourceVersion?.Sha1));
        Assert.Equal(originals ? 1 : 0, materialization.FullSelection.CreateInventory(Operation,
            stage.Package.Envelope.Fingerprint, At).Bindings.Length);
    }

    [Fact]
    public async Task DerivedOnlySafeAddsContextButNeverOriginalBytesOrBinding()
    {
        var content = NativeV2ImportFixture.Content(presence: false);
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content,
            NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview);
        var plan = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var safe = plan.Select(plan.FullSelection.Members.Where(m => m.Id.Role == NativeMediaMemberRole.Preview).Select(m => m.Id));
        var inventory = safe.CreateInventory(Operation, stage.Package.Envelope.Fingerprint, At);
        Assert.Empty(inventory.Bindings);
        Assert.True(Assert.Single(safe.Members.Where(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence)).ContextOnly);
        Assert.Null(Assert.Single(inventory.OriginalEvidence).OriginalByteVersion!.Sha1);
        var thread = content.Threads[0];
        var post = thread.Posts[0];
        var expectedAttachment = post.Attachments[0] with { Status = TransferContentStatus.Unavailable, PayloadSha256 = null };
        var expected = content with { Threads = [thread with { Posts = [post with { Attachments = [expectedAttachment] }] }] };
        var budget = NativeMediaMergeInventoryExportBudget.Measure(plan, View(thread), [], safe, Operation, At);
        await AssertExact(expected, [NativeV2ImportFixture.Preview], budget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySafeCountsExistingRetainedOmittedInventoryAndDeduplicatesOnlyPackage(bool selectSafe)
    {
        // Same derived SHA in two distinct target roots, plus the optional Safe root.
        // No original assets exist, but retained metadata and preview bytes must survive.
        var content = NativeV2ImportFixture.Content(false, derived: true, retainedOnly: true);
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content, NativeV2ImportFixture.Preview);
        var plan = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var inventory = plan.FullSelection.CreateInventory(Operation, stage.Package.Envelope.Fingerprint, At);
        MediaAssetSnapshot Target(string label) => new(label, MediaManifestStatus.Available, 6, inventory.Sha256, [],
            originalEvidence: inventory.OriginalEvidence, derivedRepresentations: inventory.DerivedRepresentations);
        var a = Target("nonexistent-target-a");
        var b = Target("nonexistent-target-b");
        var safe = selectSafe ? plan.FullSelection : plan.Select([]);
        var view = View(content.Threads[0]);
        var before = a.OriginalEvidence;
        var budget = NativeMediaMergeInventoryExportBudget.Measure(plan, view, [a, b], safe, Operation, At);
        await AssertExact(content, [NativeV2ImportFixture.Preview], budget);
        Assert.Equal(NativeV2ImportFixture.Preview.LongLength, budget.PayloadBytes);
        Assert.Equal(before, a.OriginalEvidence);
        Assert.Empty(view.Posts);
        Assert.Empty(a.AttachmentBindings);
        Assert.Empty(b.AttachmentBindings);
    }

    [Fact]
    public async Task CurrentDerivedSafeAndRetainedTargetOriginalSharePackageBytesNotMetadata()
    {
        var current = NativeV2ImportFixture.Attachment(status: TransferContentStatus.Omitted);
        current = current with { DerivedRepresentations = [current.DerivedRepresentations[0] with
            { PayloadSha256 = NativeV2ImportFixture.Hash(NativeV2ImportFixture.Original) }] };
        var content = NativeV2ImportFixture.Content(false);
        var thread = content.Threads[0];
        // Keep the fixture's explicit-null and explicit-empty attachment posts as well.
        content = content with { Threads = [thread with
            { Posts = thread.Posts.SetItem(0, NativeV2ImportFixture.Post("15", current)) }] };
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content, NativeV2ImportFixture.Original);
        var plan = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var retained = NativeV2ImportFixture.Attachment("999", derived: false) with
            { History = new("strict", At.AddDays(-2), false, false) };
        var raw = retained.Metadata;
        var provider = Provider(raw);
        var measured = stage.Package.Envelope.MeasuredPayloads[retained.PayloadSha256!];
        var actual = new MediaActualIntegrity(measured.Length, measured.Md5, measured.Sha256) { Sha1 = measured.Sha1 };
        string assetId = NativeV2ImportFixture.Hash([1, 2, 3]);
        var asset = new MediaAssetReference(assetId, provider.MediaId, "original", "target.original.bin",
            "nonexistent-target/target.original.bin", actual.Length, actual.Sha256, provider.Url,
            provider.OriginalFileName, true) { Md5 = actual.Md5, Sha1 = actual.Sha1 };
        var binding = new MediaAttachmentBinding(new(NativeV2ImportFixture.Id, "999", 0, raw.AttachmentRevisionKey!),
            MediaRevisionKeys.CreateAcquisitionKey(NativeV2ImportFixture.Id, provider), assetId, provider, actual,
            new(MediaAcceptanceKind.Strict, false, false, At.AddDays(-2)));
        MediaIntegrityValidation.ValidateBinding(raw.AttachmentRevisionKey!, binding);
        var evidence = new MediaOriginalEvidence(new(NativeV2ImportFixture.Id, "999", 0, raw.AttachmentRevisionKey),
            raw, MediaObservedContentStatus.Contained, actual with { Sha1 = null },
            new(Operation.ToString("D"), stage.Package.Envelope.Fingerprint, At.AddDays(-2)));
        var target = new MediaAssetSnapshot("nonexistent-target", MediaManifestStatus.Available, 6,
            stage.Package.Envelope.Fingerprint, [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [raw.AttachmentRevisionKey!] = binding },
            originalEvidence: [evidence]);
        DateTimeOffset importedAt = At.AddHours(3);
        var expected = content with { ExportedAtUtc = importedAt, Threads = [content.Threads[0] with
            { RetainedBindings = [new("999", retained)] }] };
        var view = View(content.Threads[0]);
        var budget = NativeMediaMergeInventoryExportBudget.Measure(plan, view, [target], plan.FullSelection, Operation, importedAt);
        await AssertExact(expected, [NativeV2ImportFixture.Original], budget);
        Assert.Equal(actual.Length, budget.PayloadBytes);
        Assert.Null(Assert.Single(target.OriginalEvidence).OriginalByteVersion!.Sha1);
        Assert.Equal(binding, Assert.Single(target.AttachmentBindings.Values));
        Assert.Equal(asset, Assert.Single(target.GetPage()));
        Assert.Null(view.Posts.Single(p => p.PostId == "16").Attachments);
        Assert.Empty(view.Posts.Single(p => p.PostId == "17").Attachments!);
    }

    [Fact]
    public async Task ForeignZeroBindingInventoryRejectsInsteadOfDisappearingFromBudget()
    {
        var content = NativeV2ImportFixture.Content(false, retainedOnly: true);
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content, NativeV2ImportFixture.Preview);
        var plan = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var inventory = plan.FullSelection.CreateInventory(Operation, stage.Package.Envelope.Fingerprint, At);
        var original = Assert.Single(inventory.OriginalEvidence);
        var other = new ResourceId("4chan", "b", "7002");
        string key = MediaRevisionKeys.CreateAttachmentKey(other, "999", 0, Provider(original.Metadata));
        var occurrence = new MediaOccurrence(other, "999", 0, key);
        var foreign = original with { Occurrence = occurrence,
            Metadata = original.Metadata with { AttachmentRevisionKey = key } };
        var derived = Assert.Single(inventory.DerivedRepresentations) with { Parent = occurrence };
        var target = new MediaAssetSnapshot("nonexistent-foreign-target", MediaManifestStatus.Available, 6,
            inventory.Sha256, [], originalEvidence: [foreign], derivedRepresentations: [derived]);
        Assert.Empty(target.AttachmentBindings);
        Assert.Throws<InvalidDataException>(() => NativeMediaMergeInventoryExportBudget.Measure(plan,
            View(content.Threads[0]), [target], plan.Select([]), Operation, At));
    }

    [Fact]
    public async Task OmittedDerivedDescriptorCostsCatalogBytesButNoPayload()
    {
        var content = NativeV2ImportFixture.Content(false, derived: true, presence: false);
        var thread = content.Threads[0];
        var post = thread.Posts[0];
        var attachment = post.Attachments[0];
        attachment = attachment with { DerivedRepresentations = [attachment.DerivedRepresentations[0] with
            { Status = TransferContentStatus.Omitted, PayloadSha256 = null }] };
        content = content with { Threads = [thread with { Posts = [post with { Attachments = [attachment] }] }] };
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content);
        var plan = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var budget = NativeMediaMergeInventoryExportBudget.Measure(plan, View(content.Threads[0]), [],
            plan.FullSelection, Operation, At);
        await AssertExact(content, [], budget);
        Assert.Equal(0, budget.PayloadBytes);
        var withoutDerived = content with { Threads = [content.Threads[0] with
            { Posts = [post with { Attachments = [attachment with { DerivedRepresentations = [] }] }] }] };
        Assert.True(budget.CatalogBytes > NativeTransferCatalogSerializer.Serialize(withoutDerived).Length);
    }

    [Fact]
    public async Task EmptySafeWithoutTargetInventoryDoesNotCountIncomingFull()
    {
        var content = NativeV2ImportFixture.Content(presence: false);
        using var files = new NativeV2ImportFixture.Workspace();
        await using var stage = await NativeV2ImportFixture.StageAsync(files, content,
            NativeV2ImportFixture.Original, NativeV2ImportFixture.Preview);
        var plan = NativeMediaMaterializationPlan.Create(stage, NativeV2ImportFixture.Id);
        var view = View(content.Threads[0]);
        var expected = new NativeTransferCatalog(At, [NativeTransferArchiveProjection.From(view)]);
        var budget = NativeMediaMergeInventoryExportBudget.Measure(plan, view, [], plan.Select([]), Operation, At);
        await AssertExact(expected, [], budget);
        Assert.Equal(0, budget.PayloadBytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => NativeMediaMergeInventoryExportBudget.Measure(
            plan, view, [], plan.Select([]), Operation, At, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static MediaProviderMetadata Provider(CapturedAttachment raw) => new(raw.MediaId!, raw.Variant,
        raw.Url!, raw.OriginalFileName, raw.ExpectedSize, raw.ExpectedMd5, raw.ExpectedSha256)
        { ExpectedSha1 = raw.ExpectedSha1, ProviderSizeHint = raw.ProviderSizeHint };

    private static ArchivedThreadView View(TransferThread thread) => new(new(thread.Id, thread.PageUrl!, thread.Title,
        "metadata-only-target", thread.SourceState, false, thread.FirstSeenAtUtc, thread.CapturedAtUtc,
        thread.Posts.Length, thread.CaptureRevision ?? 0), thread.Posts.Select(p => p.Metadata), [], thread.DecimalPostOrder);

    private static async Task AssertExact(NativeTransferCatalog expected, byte[][] bytes,
        NativeTransferPackageWriter.TransferPackageBudget actual)
    {
        TransferBlobDescriptor[] descriptors = bytes.DistinctBy(NativeV2ImportFixture.Hash)
            .Select(b => new TransferBlobDescriptor(b.LongLength, NativeV2ImportFixture.Hash(b)))
            .OrderBy(d => d.Sha256, StringComparer.Ordinal).ToArray();
        Assert.Equal(NativeTransferPackageWriter.MeasureBudget(expected, descriptors, Operation), actual);
        using var output = new MemoryStream();
        var sources = descriptors.Select(d => new TransferPayloadSource(d,
            _ => Task.FromResult<Stream>(new MemoryStream(bytes.First(b => NativeV2ImportFixture.Hash(b) == d.Sha256), false)))).ToArray();
        var written = await NativeTransferPackageWriter.WriteAsync(output, expected, sources, Operation);
        Assert.True(written.IsComplete, written.ToString());
        Assert.Equal(output.Length, actual.ContainerBytes);
        Assert.Equal(actual.ContainerBytes, actual.PayloadBytes + actual.CatalogBytes + actual.ManifestBytes + actual.ZipOverhead);
        Assert.True(actual.ZipOverhead > 0);
        Assert.True(actual.WithinContainerCap);
    }
}
