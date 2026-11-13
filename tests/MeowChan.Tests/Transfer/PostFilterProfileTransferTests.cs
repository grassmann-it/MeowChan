using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class PostFilterProfileTransferTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "123456");
    private static readonly ResourceId OtherThread = new("8kun", "tech", "765432");

    [Fact]
    public void RulesAndHiddenPostsRoundTripWithQualifiedIdentities()
    {
        using TempDirectory sourceDirectory = new();
        using TempDirectory targetDirectory = new();
        byte[] exported;
        using (ProfileLease sourceLease = ProfileLease.Acquire(sourceDirectory.Path))
        {
            PostFilterStore source = new(sourceDirectory.Path, sourceLease);
            PostFilterSnapshot first = source.Load();
            PostFilterSnapshot withRule = source.AddRule(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                new(PostFilterField.Subject, PostFilterMatch.Exact, "spoiler", PostFilterAction.Stub, true,
                    Source: "4chan", Board: "g", Enabled: true), first.Revision);
            PostFilterSnapshot complete = source.HidePost(OtherThread, "765433", true,
                new DateTimeOffset(2026, 11, 13, 12, 34, 56, TimeSpan.Zero), withRule.Revision);
            PostFilterTransferExport transfer = PostFilterProfileTransfer.Export(source);
            Assert.True(transfer.IsExported);
            Assert.Equal(complete.Revision, transfer.Snapshot!.SourceRevision);
            exported = transfer.Bytes!;
        }

        using (ProfileLease targetLease = ProfileLease.Acquire(targetDirectory.Path))
        {
            PostFilterStore target = new(targetDirectory.Path, targetLease);
            PostFilterTransferImport result = PostFilterProfileTransfer.Import(target, exported, 0);
            Assert.True(result.IsImported);
            PostFilterSnapshot restored = new PostFilterStore(targetDirectory.Path, targetLease).Load();
            Assert.Equal(1, restored.Revision);
            Assert.Equal(new PostFilterRule(Guid.Parse("11111111-1111-1111-1111-111111111111"), true,
                PostFilterField.Subject, PostFilterMatch.Exact, "spoiler", PostFilterAction.Stub, true, "4chan", "g"),
                Assert.Single(restored.Rules));
            HiddenPost hidden = Assert.Single(restored.HiddenPosts);
            Assert.Equal(OtherThread, hidden.Thread);
            Assert.Equal("765433", hidden.Post);
            Assert.True(hidden.Replies);
            Assert.Equal(new DateTimeOffset(2026, 11, 13, 12, 34, 56, TimeSpan.Zero), hidden.Added);
        }

        // A second real store instance must see the same qualified identities, not an in-memory transfer cache.
        using ProfileLease reopened = ProfileLease.Acquire(targetDirectory.Path);
        PostFilterSnapshot afterReopen = new PostFilterStore(targetDirectory.Path, reopened).Load();
        Assert.Equal(1, afterReopen.Revision);
        Assert.Equal(OtherThread, Assert.Single(afterReopen.HiddenPosts).Thread);
    }

    [Fact]
    public void MissingPostFilterStoreExportsAsAnExplicitEmptyImage()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterTransferExport transfer = PostFilterProfileTransfer.Export(new PostFilterStore(directory.Path, lease));
        Assert.True(transfer.IsExported);
        Assert.Empty(transfer.Snapshot!.Rules);
        Assert.Empty(transfer.Snapshot.HiddenPosts);
        Assert.Equal(0, transfer.Snapshot.SourceRevision);
        Assert.True(PostFilterProfileTransfer.Inspect(transfer.Bytes!).IsValid);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not-json")]
    public void CorruptTransferDoesNotTouchTarget(string corrupt)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore target = new(directory.Path, lease);
        PostFilterSnapshot before = target.AddRule(Guid.NewGuid(),
            new(PostFilterField.Text, PostFilterMatch.Contains, "keep", PostFilterAction.Highlight, false), 0);
        PostFilterTransferImport result = PostFilterProfileTransfer.Import(target, Encoding.UTF8.GetBytes(corrupt), before.Revision);
        Assert.False(result.IsImported);
        PostFilterSnapshot after = new PostFilterStore(directory.Path, lease).Load();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal("keep", Assert.Single(after.Rules).Pattern);
    }

    [Fact]
    public void UnsupportedProfileStoreIsNotExportedAsEmpty()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        File.WriteAllText(path, "{\"schemaVersion\":99,\"revision\":1,\"hiddenPostsAction\":\"stub\",\"rules\":[],\"hiddenPosts\":[]}");
        PostFilterStore store = new(directory.Path, lease);
        Assert.Equal(PostFilterStoreState.Unsupported, store.Load().State);
        PostFilterTransferExport transfer = PostFilterProfileTransfer.Export(store);
        Assert.Equal(PostFilterTransferError.UnsupportedVersion, transfer.Error);
        Assert.Contains("schemaVersion\":99", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedTargetStoreFailsClosedAndKeepsItsDocument()
    {
        using TempDirectory sourceDirectory = new();
        using TempDirectory targetDirectory = new();
        byte[] exported;
        using (ProfileLease sourceLease = ProfileLease.Acquire(sourceDirectory.Path))
            exported = PostFilterProfileTransfer.Export(new PostFilterStore(sourceDirectory.Path, sourceLease)).Bytes!;

        using ProfileLease targetLease = ProfileLease.Acquire(targetDirectory.Path);
        string path = Path.Combine(targetDirectory.Path, PostFilterStore.FileName);
        const string unsupported = "{\"schemaVersion\":99,\"revision\":7,\"hiddenPostsAction\":\"stub\",\"rules\":[],\"hiddenPosts\":[]}";
        File.WriteAllText(path, unsupported);
        PostFilterStore target = new(targetDirectory.Path, targetLease);
        Assert.Equal(PostFilterStoreState.Unsupported, target.Load().State);

        PostFilterTransferImport result = PostFilterProfileTransfer.Import(target, exported, 0);
        Assert.Equal(PostFilterTransferError.UnsupportedVersion, result.Error);
        Assert.Equal(unsupported, File.ReadAllText(path));
    }

    [Fact]
    public void UnknownTransferVersionFailsClosedAndLeavesTargetUntouched()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore target = new(directory.Path, lease);
        PostFilterSnapshot before = target.AddRule(Guid.NewGuid(),
            new(PostFilterField.Text, PostFilterMatch.Exact, "keep", PostFilterAction.Hide, false), 0);
        PostFilterTransferExport empty = PostFilterProfileTransfer.Export(new PostFilterStore(directory.Path, lease));
        // Build a separate valid source image so the target's revision remains meaningful.
        string json = Encoding.UTF8.GetString(empty.Bytes!).Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal);
        PostFilterTransferInspection inspection = PostFilterProfileTransfer.Inspect(Encoding.UTF8.GetBytes(json));
        Assert.Equal(PostFilterTransferError.UnsupportedVersion, inspection.Error);
        PostFilterTransferImport result = PostFilterProfileTransfer.Import(target, Encoding.UTF8.GetBytes(json), before.Revision);
        Assert.Equal(PostFilterTransferError.UnsupportedVersion, result.Error);
        PostFilterSnapshot after = new PostFilterStore(directory.Path, lease).Load();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal("keep", Assert.Single(after.Rules).Pattern);
    }

    [Fact]
    public void ConflictAndCancellationPreserveTheTarget()
    {
        using TempDirectory sourceDirectory = new();
        using TempDirectory targetDirectory = new();
        byte[] exported;
        using (ProfileLease sourceLease = ProfileLease.Acquire(sourceDirectory.Path))
        {
            PostFilterStore source = new(sourceDirectory.Path, sourceLease);
            exported = PostFilterProfileTransfer.Export(source).Bytes!;
        }

        using ProfileLease targetLease = ProfileLease.Acquire(targetDirectory.Path);
        PostFilterStore target = new(targetDirectory.Path, targetLease);
        PostFilterSnapshot before = target.AddRule(Guid.NewGuid(),
            new(PostFilterField.Text, PostFilterMatch.Contains, "local", PostFilterAction.Stub, false), 0);
        PostFilterTransferImport conflict = PostFilterProfileTransfer.Import(target, exported, 0);
        Assert.Equal(PostFilterTransferError.Conflict, conflict.Error);
        Assert.Equal("local", Assert.Single(new PostFilterStore(targetDirectory.Path, targetLease).Load().Rules).Pattern);

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PostFilterProfileTransfer.Import(target, exported, before.Revision, cancelled.Token));
        PostFilterSnapshot afterCancel = new PostFilterStore(targetDirectory.Path, targetLease).Load();
        Assert.Equal(before.Revision, afterCancel.Revision);
        Assert.Equal("local", Assert.Single(afterCancel.Rules).Pattern);
    }

    [Fact]
    public void BackupRestoresAfterTwoRealStoreReopens()
    {
        using TempDirectory sourceDirectory = new();
        using TempDirectory targetDirectory = new();
        byte[] backup;
        using (ProfileLease sourceLease = ProfileLease.Acquire(sourceDirectory.Path))
        {
            PostFilterStore source = new(sourceDirectory.Path, sourceLease);
            PostFilterSnapshot first = source.Load();
            source.AddRule(Guid.NewGuid(), new(PostFilterField.Md5, PostFilterMatch.Contains,
                "d41d8cd98f00b204e9800998ecf8427e", PostFilterAction.Hide, false), first.Revision);
            backup = PostFilterProfileBackup.Capture(source).Bytes!;
        }

        using (ProfileLease firstTargetLease = ProfileLease.Acquire(targetDirectory.Path))
        {
            PostFilterStore target = new(targetDirectory.Path, firstTargetLease);
            Assert.True(PostFilterProfileBackup.Restore(target, backup, 0).IsImported);
        }
        using (ProfileLease secondTargetLease = ProfileLease.Acquire(targetDirectory.Path))
        {
            PostFilterStore target = new(targetDirectory.Path, secondTargetLease);
            PostFilterSnapshot loaded = target.Load();
            Assert.Equal(1, loaded.Revision);
            target.SetHiddenPostsAction(HiddenPostsAction.Hide, loaded.Revision);
        }
        using (ProfileLease thirdTargetLease = ProfileLease.Acquire(targetDirectory.Path))
        {
            PostFilterStore target = new(targetDirectory.Path, thirdTargetLease);
            PostFilterSnapshot changed = target.Load();
            Assert.Equal(HiddenPostsAction.Hide, changed.HiddenPostsAction);
            Assert.True(PostFilterProfileBackup.Restore(target, backup, changed.Revision).IsImported);
        }
        using ProfileLease finalLease = ProfileLease.Acquire(targetDirectory.Path);
        PostFilterSnapshot restored = new PostFilterStore(targetDirectory.Path, finalLease).Load();
        Assert.Equal(HiddenPostsAction.Stub, restored.HiddenPostsAction);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", Assert.Single(restored.Rules).Pattern);
    }
    private static byte[] EmptyImage => Encoding.UTF8.GetBytes("""
        {"format":"meowchan-post-filters-transfer","version":1,"sourceRevision":900,
         "hiddenPostsAction":"stub","rules":[],"hiddenPosts":[]}
        """);

    private static PostFilterTransferSnapshot EmptySnapshot => PostFilterProfileTransfer.Inspect(EmptyImage).Snapshot!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultSnapshotArraysFailClosedOnBothImportPaths(bool admitted)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        PostFilterStore target = new(directory.Path, lease);
        foreach (PostFilterTransferSnapshot invalid in new[]
        {
            EmptySnapshot with { Rules = default },
            EmptySnapshot with { HiddenPosts = default },
            EmptySnapshot with { Rules = [null!] },
            EmptySnapshot with { HiddenPosts = [null!] },
            EmptySnapshot with { HiddenPostsAction = (HiddenPostsAction)99 }
        })
        {
            PostFilterTransferImport result = admitted
                ? PostFilterProfileTransfer.ImportAdmitted(target, invalid, 0, admission)
                : PostFilterProfileTransfer.ImportSnapshot(target, invalid, 0);
            Assert.Equal(PostFilterTransferError.InvalidDocument, result.Error);
            Assert.False(result.IsImported);
            Assert.False(File.Exists(Path.Combine(directory.Path, PostFilterStore.FileName)));
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("future")]
    public void MissingPrimaryWithBackupNeverExportsOrRestoresAsEmpty(string kind)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Contains,
            "recover me", PostFilterAction.Hide, false), 0);
        store.HidePost(Thread, "123457", false, DateTimeOffset.UtcNow, 1);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        File.Delete(path);
        if (kind == "corrupt") File.WriteAllText(path + ".bak", "{broken");
        if (kind == "future") File.WriteAllText(path + ".bak", "{\"schemaVersion\":99}");
        byte[] backup = File.ReadAllBytes(path + ".bak");

        PostFilterSnapshot loaded = store.Load();
        Assert.Equal(kind == "valid" ? PostFilterStoreState.RecoveryAvailable : PostFilterStoreState.Failed, loaded.State);
        Assert.Equal(PostFilterTransferError.StoreUnavailable, PostFilterProfileTransfer.Export(store).Error);
        Assert.False(PostFilterProfileBackup.Capture(store).IsExported);
        Assert.Equal(PostFilterTransferError.StoreUnavailable,
            PostFilterProfileBackup.Restore(store, EmptyImage, loaded.Revision).Error);
        Assert.False(File.Exists(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));

        if (kind == "valid")
        {
            PostFilterSnapshot restored = store.Recover(loaded.Revision);
            Assert.Equal(loaded.Revision + 1, restored.Revision);
            Assert.Equal("recover me", Assert.Single(restored.Rules).Pattern);
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            Assert.True(PostFilterProfileTransfer.Export(new PostFilterStore(directory.Path, lease)).IsExported);
        }
    }

    [Theory]
    [InlineData("valid", false)]
    [InlineData("valid", true)]
    [InlineData("corrupt", false)]
    [InlineData("corrupt", true)]
    [InlineData("future", false)]
    [InlineData("future", true)]
    public void BackupAppearingAfterEmptyCaptureInvalidatesRestoreWithoutAnotherLoad(string kind, bool admitted)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken? admission = admitted ? lease.Admission.EnterOrdinaryWriter() : null;
        PostFilterStore store = new(directory.Path, lease);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        PostFilterTransferExport reviewed = PostFilterProfileBackup.Capture(store);
        Assert.True(reviewed.IsExported);
        Assert.Equal(0, reviewed.Snapshot!.SourceRevision);
        Assert.Empty(reviewed.Snapshot.Rules);
        Assert.Empty(reviewed.Snapshot.HiddenPosts);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));

        // Even a supported backup with revision zero must invalidate the cached empty
        // review: checking only the revision, or only the primary, would lose policy.
        string reappeared = kind switch
        {
            "corrupt" => "{broken backup",
            "future" => "{\"schemaVersion\":99}",
            _ => """
                {"schemaVersion":1,"revision":0,"hiddenPostsAction":"stub",
                 "rules":[{"id":"11111111-1111-1111-1111-111111111111","enabled":true,
                           "field":"text","match":"exact","pattern":"unreviewed policy",
                           "action":"hide","replies":false}],"hiddenPosts":[]}
                """
        };
        byte[] backup = Encoding.UTF8.GetBytes(reappeared);
        File.WriteAllBytes(path + ".bak", backup);
        bool reachedWrite = false;
        store.BeforeAdmittedMutationForTests = _ => reachedWrite = true;

        // Deliberately do not Load/Capture again before applying the old empty image.
        PostFilterTransferImport result = admitted
            ? PostFilterProfileBackup.RestoreAdmitted(store, reviewed.Bytes!, reviewed.Snapshot.SourceRevision, admission!)
            : PostFilterProfileBackup.Restore(store, reviewed.Bytes!, reviewed.Snapshot.SourceRevision);
        Assert.Equal(PostFilterTransferError.StoreUnavailable, result.Error);
        Assert.False(result.IsImported);
        Assert.Null(result.Snapshot);
        Assert.False(reachedWrite);
        Assert.False(File.Exists(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, ".post-filters.json.*.tmp"));
        Assert.Equal(kind == "valid" ? PostFilterStoreState.RecoveryAvailable : PostFilterStoreState.Failed,
            new PostFilterStore(directory.Path, lease).Load().State);
    }

    [Fact]
    public void ReappearingPrimaryRequiresAnotherRecoveryReview()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Exact, "keep", PostFilterAction.Hide, false), 0);
        store.SetHiddenPostsAction(HiddenPostsAction.Hide, 1);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        File.Delete(path);
        PostFilterSnapshot reviewed = store.Load();
        Assert.Equal(PostFilterStoreState.RecoveryAvailable, reviewed.State);
        byte[] backup = File.ReadAllBytes(path + ".bak");
        File.WriteAllText(path, "{new damage");
        Assert.Throws<PostFilterRevisionException>(() => store.Recover(reviewed.Revision));
        Assert.Equal("{new damage", File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void ExplicitEmptyRestoreRotatesExactPreviousImageAndUsesTargetRevision()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Exact, "local", PostFilterAction.Stub, true), 0);
        PostFilterSnapshot before = store.HidePost(Thread, "123457", true, DateTimeOffset.UtcNow, 1);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] primary = File.ReadAllBytes(path);
        PostFilterTransferImport result = PostFilterProfileBackup.Restore(store, EmptyImage, before.Revision);
        Assert.True(result.IsImported);
        Assert.Equal(before.Revision + 1, result.Snapshot!.Revision);
        Assert.Equal(primary, File.ReadAllBytes(path + ".bak"));
        PostFilterSnapshot reopened = new PostFilterStore(directory.Path, lease).Load();
        Assert.Equal(result.Snapshot.Revision, reopened.Revision);
        Assert.Empty(reopened.Rules);
        Assert.Empty(reopened.HiddenPosts);
    }

    [Theory]
    [InlineData("{damaged")]
    [InlineData("{\"schemaVersion\":99}")]
    public void RestoreCannotRotateDamagedOrUnsupportedBackup(string backup)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Contains, "keep", PostFilterAction.Hide, false), 0);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        File.WriteAllText(path + ".bak", backup);
        byte[] primary = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => PostFilterProfileBackup.Restore(store, EmptyImage, 1));
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, ".post-filters.json.*.tmp"));
    }

    [Fact]
    public void SameRevisionExternalEditConflictsWithoutRotatingBackup()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Contains, "first", PostFilterAction.Hide, false), 0);
        store.SetHiddenPostsAction(HiddenPostsAction.Hide, 1);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] backup = File.ReadAllBytes(path + ".bak");
        string changed = File.ReadAllText(path).Replace("first", "other", StringComparison.Ordinal);
        File.WriteAllText(path, changed);
        Assert.Equal(PostFilterTransferError.Conflict, PostFilterProfileBackup.Restore(store, EmptyImage, 2).Error);
        Assert.Equal(changed, File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void CancellationImmediatelyBeforeAtomicWritePreservesBothFiles()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Contains, "keep", PostFilterAction.Hide, false), 0);
        store.SetHiddenPostsAction(HiddenPostsAction.Hide, 1);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        using CancellationTokenSource cancelled = new();
        bool observed = false;
        store.BeforeAdmittedMutationForTests = _ => { observed = true; cancelled.Cancel(); };
        Assert.ThrowsAny<OperationCanceledException>(() => PostFilterProfileBackup.Restore(store, EmptyImage, 2, cancelled.Token));
        Assert.True(observed);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, ".post-filters.json.*.tmp"));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("released")]
    [InlineData("exclusive")]
    public void RestoreRejectsAnInvalidAdmissionWithoutCreatingAStore(string kind)
    {
        using TempDirectory directory = new();
        using TempDirectory foreignDirectory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileLease foreign = ProfileLease.Acquire(foreignDirectory.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken admission = kind switch
        {
            "foreign" => foreign.Admission.EnterStoreWriter(),
            "exclusive" => lease.Admission.EnterExclusiveAsync().AsTask().GetAwaiter().GetResult(),
            _ => lease.Admission.EnterOrdinaryWriter()
        };
        if (kind == "released") admission.Dispose();
        PostFilterStore store = new(directory.Path, lease);
        Assert.Throws<InvalidOperationException>(() => PostFilterProfileBackup.RestoreAdmitted(store, EmptyImage, 0, admission));
        Assert.False(File.Exists(Path.Combine(directory.Path, PostFilterStore.FileName)));
    }

    [Fact]
    public void AcceptedAdmissionStillOwnsRestoreAfterShutdownIngressCloses()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        lease.Admission.BeginShutdown();
        Assert.Throws<InvalidOperationException>(() => PostFilterProfileTransfer.Import(new PostFilterStore(directory.Path, lease), EmptyImage, 0));
        PostFilterStore store = new(directory.Path, lease);
        bool before = false, after = false;
        store.BeforeAdmittedMutationForTests = actual => { Assert.Same(admission, actual); before = true; };
        store.AfterAdmittedMutationForTests = actual => { Assert.Same(admission, actual); after = true; };
        Assert.True(PostFilterProfileBackup.RestoreAdmitted(store, EmptyImage, 0, admission).IsImported);
        Assert.True(before && after);
        Assert.Equal(1, new PostFilterStore(directory.Path, lease).Load().Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredLimitsRefuseTransferGrowthButAllowShrinkingOlderData(bool hidden)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        int limit = hidden ? ProfileStoreLimits.MinimumHiddenPosts : ProfileStoreLimits.MinimumPostFilterRules;
        PostFilterTransferSnapshot oversized = hidden
            ? EmptySnapshot with { HiddenPosts = Enumerable.Range(0, limit + 1)
                .Select(i => new HiddenPost(Thread, i.ToString(), false, DateTimeOffset.UnixEpoch)).ToImmutableArray() }
            : EmptySnapshot with { Rules = Enumerable.Range(0, limit + 1)
                .Select(i => new PostFilterRule(Guid.NewGuid(), true, PostFilterField.Text, PostFilterMatch.Contains,
                    "rule " + i, PostFilterAction.Hide, false, null, null)).ToImmutableArray() };
        // Seed old data under the normal defaults, then lower the configured limit.
        Assert.True(PostFilterProfileTransfer.ImportSnapshot(store, oversized, 0).IsImported);
        if (hidden) lease.StoreLimits.HiddenPosts = limit;
        else lease.StoreLimits.PostFilterRules = limit;
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] primary = File.ReadAllBytes(path);
        PostFilterTransferSnapshot growth = hidden
            ? oversized with { HiddenPosts = oversized.HiddenPosts.Add(new HiddenPost(Thread, "extra", false, DateTimeOffset.UnixEpoch)) }
            : oversized with { Rules = oversized.Rules.Add(new PostFilterRule(Guid.NewGuid(), true, PostFilterField.Text,
                PostFilterMatch.Exact, "extra", PostFilterAction.Hide, false, null, null)) };
        Assert.Equal(PostFilterTransferError.InvalidDocument, PostFilterProfileTransfer.ImportSnapshot(store, growth, 1).Error);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.True(PostFilterProfileTransfer.Export(store).IsExported);
        Assert.True(PostFilterProfileTransfer.ImportSnapshot(store, oversized, 1).IsImported);
        Assert.True(PostFilterProfileTransfer.Import(store, EmptyImage, 2).IsImported);
    }

    [Theory]
    [InlineData("unknown-member")]
    [InlineData("duplicate-member")]
    [InlineData("unknown-rule")]
    [InlineData("duplicate-rule-id")]
    [InlineData("duplicate-rule-key")]
    [InlineData("duplicate-hidden")]
    [InlineData("missing-hidden-source")]
    [InlineData("board-without-source")]
    public void AmbiguousOrUnsupportedTransferPreservesExactTargetAndBackup(string kind)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Exact, "keep", PostFilterAction.Hide, false), 0);
        store.HidePost(Thread, "123457", false, DateTimeOffset.UnixEpoch, 1);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        JsonNode root = JsonNode.Parse(PostFilterProfileTransfer.Export(store).Bytes!)!;
        JsonArray rules = root["rules"]!.AsArray(), posts = root["hiddenPosts"]!.AsArray();
        switch (kind)
        {
            case "unknown-member": root["externalPath"] = "/must/not/be/used"; break;
            case "unknown-rule": rules[0]!["match"] = "regex"; break;
            case "duplicate-rule-id": rules.Add(rules[0]!.DeepClone()); break;
            case "duplicate-rule-key":
                JsonNode duplicate = rules[0]!.DeepClone();
                duplicate["id"] = Guid.NewGuid().ToString();
                duplicate["pattern"] = "KEEP";
                rules.Add(duplicate);
                break;
            case "duplicate-hidden": posts.Add(posts[0]!.DeepClone()); break;
            case "missing-hidden-source": posts[0]!.AsObject().Remove("source"); break;
            case "board-without-source": rules[0]!["board"] = "g"; break;
        }
        string json = root.ToJsonString();
        if (kind == "duplicate-member") json = json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
        Assert.Equal(PostFilterTransferError.InvalidDocument,
            PostFilterProfileTransfer.Import(store, Encoding.UTF8.GetBytes(json), 2).Error);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void UnsupportedLocalRuleCannotSilentlyDisappearFromAnExportOrBackup()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore store = new(directory.Path, lease);
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Exact, "keep", PostFilterAction.Hide, false), 0);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        string future = File.ReadAllText(path).Replace("\"exact\"", "\"regex\"", StringComparison.Ordinal);
        File.WriteAllText(path, future);
        Assert.False(Assert.Single(store.Load().Rules).CanApply);
        Assert.Equal(PostFilterTransferError.InvalidDocument, PostFilterProfileTransfer.Export(store).Error);
        Assert.False(PostFilterProfileBackup.Capture(store).IsExported);
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public void EqualPostNumbersOnDifferentSourcesStayDistinct()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        HiddenPost first = new(Thread, "123457", false, DateTimeOffset.UnixEpoch);
        HiddenPost second = first with { Thread = new ResourceId("8kun", Thread.Board, Thread.Thread), Replies = true };
        PostFilterTransferSnapshot incoming = EmptySnapshot with { HiddenPosts = [first, second] };
        PostFilterStore store = new(directory.Path, lease);
        Assert.True(PostFilterProfileTransfer.ImportSnapshot(store, incoming, 0).IsImported);
        PostFilterTransferExport backup = PostFilterProfileBackup.Capture(store);
        Assert.Equal(new[] { first, second }, backup.Snapshot!.HiddenPosts);
        Assert.True(PostFilterProfileBackup.Restore(store, backup.Bytes!, 1).IsImported);
        Assert.Equal(new[] { first, second }, new PostFilterStore(directory.Path, lease).Load().HiddenPosts);
    }

}
