using System;
using System.IO;
using System.Linq;
using System.Text;
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
            Assert.Equal("4chan", Assert.Single(restored.Rules).Source);
            HiddenPost hidden = Assert.Single(restored.HiddenPosts);
            Assert.Equal(OtherThread, hidden.Thread);
            Assert.Equal("765433", hidden.Post);
            Assert.True(hidden.Replies);
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
}
