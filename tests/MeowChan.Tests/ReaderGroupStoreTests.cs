using System.Text.Json;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ReaderGroupStoreTests
{
    private static readonly ResourceId A = new("source-A", "board", "42");
    private static readonly ResourceId B = new("source-B", "board", "42");
    private static ReaderGroupsSnapshot Snapshot(params ResourceId[] members) => new(1, 0,
        new[] { new ReaderGroup(Guid.NewGuid(), "Evening", members) });

    [Fact]
    public async Task MissingFileDoesNotWriteAndExactIdentitiesOrderAndNamesSurviveRestart()
    {
        using TempDirectory directory = new();
        ReaderGroupsSnapshot saved;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            ReaderGroupStore store = new(directory.Path, lease);
            Assert.Empty((await store.LoadAsync()).Snapshot!.Groups);
            Assert.False(File.Exists(Path.Combine(directory.Path, "reader-groups.json")));
            saved = await store.SaveAsync(Snapshot(B, A, A with { Board = "Board" }), 0);
            Assert.Equal(1, saved.Revision);
            Assert.Equal(new[] { B, A, A with { Board = "Board" } }, saved.Groups[0].Members);
            ReaderGroupsSnapshot renamed = saved with { Groups = new[] { saved.Groups[0] with { Name = "  夜 🌙  " } } };
            saved = await store.SaveAsync(renamed, saved.Revision);
            Assert.Equal("夜 🌙", saved.Groups[0].Name);
            Assert.Contains("Evening", File.ReadAllText(Path.Combine(directory.Path, "reader-groups.json.bak")));
        }
        using ProfileLease nextOwner = ProfileLease.Acquire(directory.Path);
        ReaderGroupsLoadResult loaded = await new ReaderGroupStore(directory.Path, nextOwner).LoadAsync();
        Assert.True(loaded.CanWrite);
        Assert.Equal(saved.Revision, loaded.Snapshot!.Revision);
        Assert.True(ReaderGroupsValidation.SameGroups(saved.Groups, loaded.Snapshot.Groups));
    }

    [Fact]
    public async Task DefensiveCaptureConflictAndCancellationNeverLoseCommittedGroups()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ResourceId[] members = [A]; ReaderGroupsSnapshot draft = Snapshot(members);
        Task<ReaderGroupsSnapshot> write = store.SaveAsync(draft, 0); members[0] = B;
        ReaderGroupsSnapshot saved = await write;
        Assert.Equal(A, Assert.Single(saved.Groups[0].Members));
        await Assert.ThrowsAsync<ReaderGroupsConflictException>(() => store.SaveAsync(Snapshot(B), 0));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Snapshot(B), 1, cancellation.Token));
        Assert.Equal(A, Assert.Single((await store.LoadAsync()).Snapshot!.Groups[0].Members));
    }

    [Fact]
    public async Task CorruptPrimaryRequiresExplicitCurrentBackupRecoveryAndPreservesOriginal()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsSnapshot first = await store.SaveAsync(Snapshot(A), 0);
        await store.SaveAsync(first with { Groups = [first.Groups[0] with { Members = new[] { B } }] }, 1);
        string path = Path.Combine(directory.Path, "reader-groups.json");
        File.WriteAllText(path, "broken original");
        ReaderGroupsLoadResult recovery = await store.LoadAsync();
        Assert.Equal(ReaderGroupsLoadState.RecoveryAvailable, recovery.State);
        Assert.False(recovery.CanWrite);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Snapshot(B), 1));
        await Assert.ThrowsAsync<ReaderGroupsConflictException>(() => store.RestoreBackupAsync("stale"));
        ReaderGroupsSnapshot restored = await store.RestoreBackupAsync(recovery.RecoveryToken!);
        Assert.Equal(A, Assert.Single(restored.Groups[0].Members));
        Assert.Equal("broken original", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Path, "reader-groups.json.corrupt-*"))));
        Assert.True((await store.LoadAsync()).CanWrite);
    }

    [Fact]
    public async Task FuturePrimaryCannotFallBackToOldBackupOrBeOverwritten()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsSnapshot first = await store.SaveAsync(Snapshot(A), 0);
        await store.SaveAsync(first with { Groups = [first.Groups[0] with { Name = "changed" }] }, 1);
        string path = Path.Combine(directory.Path, "reader-groups.json");
        string future = "{\"schemaVersion\":99,\"foreign\":true}"; File.WriteAllText(path, future);
        Assert.Equal(ReaderGroupsLoadState.FutureVersion, (await store.LoadAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Snapshot(B), 2));
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public async Task InvalidPrimaryWithoutBackupIsNeverReplacedByEmptySnapshot()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, "reader-groups.json"); File.WriteAllText(path, "{bad");
        ReaderGroupStore store = new(directory.Path, lease);
        Assert.Equal(ReaderGroupsLoadState.Invalid, (await store.LoadAsync()).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Snapshot(A), 0));
        Assert.Equal("{bad", File.ReadAllText(path));
    }

    [Fact]
    public void ValidatesFullDocumentWithoutTruncationAndAllowsSameNamesAcrossGroups()
    {
        Assert.ThrowsAny<ArgumentException>(() => ReaderGroupsValidation.Copy(Snapshot(new ResourceId("a", "b"))));
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(Snapshot(A, A)));
        ReaderGroupsSnapshot duplicate = Snapshot(A);
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(duplicate with { Groups = [duplicate.Groups[0], duplicate.Groups[0]] }));
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(duplicate with { Revision = -1 }));
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(duplicate with { Groups = [duplicate.Groups[0] with { Name = "x\ny" }] }));
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(duplicate with { Groups = [duplicate.Groups[0] with { Name = new string('x', 121) }] }));
        ReaderGroupsValidation.Copy(duplicate with { Groups = [duplicate.Groups[0], duplicate.Groups[0] with { Id = Guid.NewGuid() }] });
        ResourceId[] limit = Enumerable.Range(0, 1000).Select(index => A with { Thread = index.ToString() }).ToArray();
        ReaderGroupsValidation.Copy(Snapshot(limit));
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(Snapshot(limit.Append(B).ToArray())));
        ReaderGroupsSnapshot maximum = new(1, 0, Enumerable.Range(0, 500).Select(_ => new ReaderGroup(Guid.NewGuid(), "same", Array.Empty<ResourceId>())).ToArray());
        ReaderGroupsValidation.Copy(maximum);
        ReaderGroupsSnapshot total = new(1, 0, Enumerable.Range(0, 20).Select(_ => new ReaderGroup(Guid.NewGuid(), "same", limit)).ToArray());
        ReaderGroupsValidation.Copy(total);
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(total with { Groups = total.Groups.Append(new(Guid.NewGuid(), "extra", [B])).ToArray() }));
        Assert.Throws<InvalidDataException>(() => ReaderGroupsValidation.Copy(maximum with { Groups = maximum.Groups.Append(new(Guid.NewGuid(), "last", [A])).ToArray() }));
    }

    [Fact]
    public async Task BackupPublicationFailureLeavesTheActiveSnapshotAndRevisionUntouched()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease);
        ReaderGroupsSnapshot first = await store.SaveAsync(Snapshot(A), 0);
        string path = Path.Combine(directory.Path, "reader-groups.json");
        string original = File.ReadAllText(path);
        // A directory cannot be used as File.Replace's backup destination on any supported platform.
        Directory.CreateDirectory(path + ".bak");
        Exception? failure = await Record.ExceptionAsync(() => store.SaveAsync(first with
            { Groups = [first.Groups[0] with { Name = "cannot publish" }] }, first.Revision));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(first.Revision, (await store.LoadAsync()).Snapshot!.Revision);
        Assert.Empty(Directory.GetFiles(directory.Path, ".reader-groups.json.*.tmp"));
    }

    [Fact]
    public async Task OversizedAndInvalidIdsAreRejectedWithoutPartialImport()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ReaderGroupStore store = new(directory.Path, lease); string path = Path.Combine(directory.Path, "reader-groups.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"revision\":0,\"groups\":[{\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"bad\",\"members\":[]}]}");
        Assert.Equal(ReaderGroupsLoadState.Invalid, (await store.LoadAsync()).State);
        using (FileStream file = File.OpenWrite(path)) file.SetLength(ReaderGroupsValidation.MaximumBytes + 1L);
        Assert.Equal(ReaderGroupsLoadState.Invalid, (await store.LoadAsync()).State);
        Assert.Equal(ReaderGroupsValidation.MaximumBytes + 1L, new FileInfo(path).Length);
    }
}
