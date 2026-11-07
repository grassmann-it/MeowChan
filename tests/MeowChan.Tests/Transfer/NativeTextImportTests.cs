using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTextImportTests
{
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private const string Url = "https://boards.4chan.org/b/thread/7001";
    private static readonly DateTimeOffset When = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static CapturedPost Post(int number, string? text = null) => new(number.ToString(), number,
        PostFieldPresence.Html | PostFieldPresence.Author, Author: "", Html: text ?? "synthetic-" + number);
    private static TransferThread Thread(IEnumerable<CapturedPost> posts) => new(Id, Url, "synthetic", ThreadSourceState.Active,
        When, When, 9, true, posts.Select(post => new TransferPost(post, [])).ToImmutableArray());
    private static async Task<MemoryStream> Package(TransferThread thread)
    {
        MemoryStream stream = new();
        var written = await NativeTransferPackageWriter.WriteAsync(stream, new(When, [thread]), [], Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        return stream;
    }
    private static async Task<NativeTextImportPlan> Prepare(ArchiveCatalog catalog, TransferThread thread)
    {
        using MemoryStream package = await Package(thread);
        var result = await catalog.PrepareNativeTextImportAsync(package);
        Assert.Equal(NativeTextImportError.None, result.Error); return Assert.IsType<NativeTextImportPlan>(result.Plan);
    }
    private static CapturedThreadSnapshot Snapshot(TempDirectory directory, params CapturedPost[] posts) => new(Id, Url, "synthetic",
        ThreadSourceState.NotFound, When, Path.Combine(directory.Path, "existing-media"), posts) { DecimalPostOrder = true };

    [Fact]
    public async Task NewTextThreadSurvivesRestartWithoutClaimingLocalCaptureOrActiveSource()
    {
        using TempDirectory directory = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            var plan = await Prepare(catalog, Thread([Post(1), Post(2, "newneedle <a href=\"#p1\">&gt;&gt;1</a>")]));
            lease.Admission.CompleteStartup();
            NativeTextImportCommitResult result;
            using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
            {
                var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                result = outcome.Result;
            }
            Assert.Equal(new(NativeTextImportCommitStatus.Committed, 1, 2), result);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchivedThreadView view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id));
        Assert.Equal(2, view.PostCount); Assert.Null(view.Summary.LastCapturedAt); Assert.False(view.Summary.CaptureEnabled);
        Assert.Equal(ThreadSourceState.Archived, view.Summary.SourceState); Assert.NotEqual(When, view.Summary.FirstSeenAt);
        Assert.False(Directory.Exists(view.Summary.MediaDirectory));
        Assert.Single((await reader.SearchThreadsAsync(new("newneedle"))).Items);
        Assert.Single((await reader.GetBacklinksAsync(new(Id, "1"))).Items);
    }

    [Fact]
    public async Task OverlapAddsMissingPostsAndKeeps404CapturePreferenceAndObservationDates()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory, Enumerable.Range(15, 16).Select(i => Post(i)).ToArray()));
        await catalog.SetCaptureEnabledAsync(Id, false);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        var before = (await reader.ReadThreadAsync(Id))!.Summary;
        var plan = await Prepare(catalog, Thread(Enumerable.Range(1, 20).Select(i => Post(i))));
        lease.Admission.CompleteStartup();
        NativeTextImportCommitResult result;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            result = outcome.Result;
        }
        Assert.Equal(14, result.NewPosts); Assert.Equal(0, result.UpdatedPosts);
        var after = (await reader.ReadThreadAsync(Id))!;
        Assert.Equal(Enumerable.Range(1, 30).Select(i => i.ToString()), after.GetPosts().Select(p => p.PostId));
        Assert.Equal(before with { PostCount = 30, Revision = before.Revision + 1 }, after.Summary);
        using var sameBytes = await Package(Thread(Enumerable.Range(1, 20).Select(i => Post(i))));
        var again = (await catalog.PrepareNativeTextImportAsync(sameBytes)).Plan!;
        NativeTextImportCommitResult repeated;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, again);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            repeated = outcome.Result;
        }
        Assert.Equal(0, repeated.NewPosts);
        Assert.Equal(after.Summary, (await reader.ReadThreadAsync(Id))!.Summary);
        Assert.Single(await catalog.ReadNativeTextImportCandidatesAsync(Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local author")]
    public async Task ExplicitAuthorWinsWhileMissingHtmlIsFilledAndConflictIsDurable(string? author)
    {
        using TempDirectory directory = new(); Guid operation; string request;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            await catalog.CaptureAsync(Snapshot(directory, new CapturedPost("1", 1, PostFieldPresence.Author, Author: author)));
            var plan = await Prepare(catalog, Thread([Post(1, "added-html")])); operation = plan.OperationId; request = plan.RequestSha256;
            lease.Admission.CompleteStartup();
            NativeTextImportCommitResult result;
            using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
            {
                var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                result = outcome.Result;
            }
            Assert.Equal(1, result.UpdatedPosts); Assert.Equal(author == "" ? 0 : 1, result.ConflictingThreads);
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
            var post = Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts());
            Assert.Equal(author, post.Author); Assert.Equal("added-html", post.Html);
            var candidate = Assert.Single(await catalog.ReadNativeTextImportCandidatesAsync(Id));
            Assert.Equal(author != "", candidate.HasConflicts); Assert.NotEmpty(candidate.CatalogBytes);
            var receipt = await catalog.ReadNativeTextImportReceiptAsync(operation, request);
            Assert.True(receipt!.IsReplay); Assert.Equal(1, receipt.UpdatedPosts);
        }
    }

    [Fact]
    public async Task ConflictingTextAndDeletedStayWhileNewPostAndMissingSubjectAreAdded()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory, Post(1, "local-wins") with { Fields = PostFieldPresence.Html | PostFieldPresence.IsDeleted, Author = null, IsDeleted = true }));
        var incoming = Thread([Post(1, "incoming-loses") with { Fields = PostFieldPresence.All & ~PostFieldPresence.Attachments, Subject = "fill", IsDeleted = false }, Post(2)]);
        var plan = await Prepare(catalog, incoming);
        lease.Admission.CompleteStartup();
        NativeTextImportCommitResult result;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            result = outcome.Result;
        }
        Assert.Equal(1, result.NewPosts); Assert.Equal(1, result.UpdatedPosts); Assert.Equal(1, result.ConflictingThreads);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        var post = (await reader.ReadThreadAsync(Id))!.GetPosts().Single(p => p.PostId == "1");
        Assert.Equal("local-wins", post.Html); Assert.True(post.IsDeleted); Assert.Equal("fill", post.Subject);
        Assert.Single((await reader.SearchThreadsAsync(new("local-wins"))).Items);
        Assert.Empty((await reader.SearchThreadsAsync(new("incoming-loses"))).Items);
        var variant = Assert.Single(await catalog.ReadNativeTextImportCandidatesAsync(Id));
        Assert.Contains("incoming-loses", System.Text.Encoding.UTF8.GetString(variant.CatalogBytes.AsSpan()));
    }

    [Fact]
    public async Task ExistingAttachmentRowsAreNotReinsertedOrRekeyedForTextAdditions()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        CapturedAttachment attachment = new(0, "111", Url: "https://i.4cdn.org/b/111.jpg", PlannedPath: Path.Combine(directory.Path, "existing-media", "111.jpg"));
        await catalog.CaptureAsync(Snapshot(directory, new CapturedPost("1", 1, PostFieldPresence.Attachments, Attachments: [attachment])));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        var before = Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts()).Attachments;
        // A trigger makes any destructive replacement observable, including byte-identical reinsertion.
        using (var connection = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        { command.CommandText = "CREATE TRIGGER protect_test_attachment BEFORE DELETE ON archive_attachments BEGIN SELECT RAISE(ABORT,'attachment deletion'); END"; command.ExecuteNonQuery(); }
        var plan = await Prepare(catalog, Thread([Post(1)]));
        lease.Admission.CompleteStartup();
        NativeTextImportCommitResult result;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            result = outcome.Result;
        }
        Assert.Equal(1, result.UpdatedPosts);
        Assert.Equal(before, Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts()).Attachments);
    }

    [Fact]
    public async Task DeletedThreadDropsItsCandidateBytesButKeepsCountOnlyReplayReceipt()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        var plan = await Prepare(catalog, Thread([Post(1)]));
        lease.Admission.CompleteStartup();
        NativeTextImportCommitResult result;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            result = outcome.Result;
        }
        Assert.Equal(NativeTextImportCommitStatus.Committed, result.Status);
        await catalog.DeleteThreadsAsync([Id]);
        Assert.Empty(await catalog.ReadNativeTextImportCandidatesAsync(Id));
        NativeTextImportCommitResult replay;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            replay = outcome.Result;
        }
        Assert.True(replay.IsReplay);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Null(await reader.ReadThreadAsync(Id));
    }

    [Fact]
    public async Task InjectedFailureRollsBackPostsCandidateAndReceiptTogether()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        var plan = await Prepare(catalog, Thread([Post(1)]));
        using (var connection = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        { command.CommandText = "CREATE TRIGGER fail_test_receipt BEFORE INSERT ON archive_import_receipts BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END"; command.ExecuteNonQuery(); }
        lease.Admission.CompleteStartup();
        NativeTextImportCommitResult failed;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            failed = outcome.Result;
        }
        Assert.Equal(NativeTextImportCommitStatus.Inactive, failed.Status);
        Assert.Equal(nameof(SqliteException), failed.Diagnostic);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Null(await reader.ReadThreadAsync(Id)); Assert.Empty(await catalog.ReadNativeTextImportCandidatesAsync(Id));
        Assert.Null(await catalog.ReadNativeTextImportReceiptAsync(plan.OperationId, plan.RequestSha256));
        using (var connection = ArchiveSql.OpenConnection(catalog.DatabasePath, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        { command.CommandText = "DROP TRIGGER fail_test_receipt"; command.ExecuteNonQuery(); }
        NativeTextImportCommitResult retried;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            retried = outcome.Result;
        }
        Assert.Equal(NativeTextImportCommitStatus.Committed, retried.Status);
    }
    [Fact]
    public async Task InventedDeltaCannotUseAnIssuedPlansOwnerAndFenceAsCommitAuthority()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        var valid = await Prepare(catalog, Thread([Post(1)]));
        var original = valid.Deltas[0];
        var invented = original with { Added = [Post(2, "<script>invented</script>")], CatalogBytes = [1, 2, 3] };
        var forged = new NativeTextImportPlan(catalog, valid.Instance, valid.Generation, valid.ExportedAtUtc, [invented], valid.RequestSha256);
        lease.Admission.CompleteStartup();
        NativeTextImportCommitResult rejected;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, forged);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            rejected = outcome.Result;
        }
        Assert.Equal(NativeTextImportCommitStatus.StalePlan, rejected.Status);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        Assert.Null(await reader.ReadThreadAsync(Id));
        Assert.Empty(await catalog.ReadNativeTextImportCandidatesAsync(Id));
        NativeTextImportCommitResult accepted;
        using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
        {
            var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, valid);
            Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
            accepted = outcome.Result;
        }
        Assert.Equal(1, accepted.NewPosts);
    }

    [Fact]
    public async Task SameCandidateIsPromotedWhenLaterImportEncountersALocalConflict()
    {
        using TempDirectory directory = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            var firstPlan = await Prepare(catalog, Thread([Post(1, "first")]));
            lease.Admission.CompleteStartup();
            NativeTextImportCommitResult initial;
            using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
            {
                var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, firstPlan);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                initial = outcome.Result;
            }
            Assert.Equal(NativeTextImportCommitStatus.Committed, initial.Status);
            var first = Assert.Single(await catalog.ReadNativeTextImportCandidatesAsync(Id)); Assert.False(first.HasConflicts);
            await catalog.CaptureAsync(Snapshot(directory, Post(1, "local-later")));
            var laterPlan = await Prepare(catalog, Thread([Post(1, "first")]));
            NativeTextImportCommitResult result;
            using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
            {
                var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, laterPlan);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                result = outcome.Result;
            }
            Assert.Equal(1, result.ConflictingThreads); Assert.Equal(0, result.UpdatedPosts);
            var promoted = Assert.Single(await catalog.ReadNativeTextImportCandidatesAsync(Id));
            Assert.True(promoted.HasConflicts); Assert.Equal(first.ImportedAtUtc, promoted.ImportedAtUtc);
            Assert.Equal(first.Sha256, promoted.Sha256); Assert.True(first.CatalogBytes.AsSpan().SequenceEqual(promoted.CatalogBytes.AsSpan()));
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            Assert.True(Assert.Single(await catalog.ReadNativeTextImportCandidatesAsync(Id)).HasConflicts);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
            Assert.Equal("local-later", Assert.Single((await reader.ReadThreadAsync(Id))!.GetPosts()).Html);
        }
    }

    [Fact]
    public async Task CandidateThirtyThreeRemainsRecoverableThroughExplicitPages()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        lease.Admission.CompleteStartup();
        for (int i = 0; i < 33; i++)
        {
            var plan = await Prepare(catalog, Thread([Post(1, "variant-" + i)]));
            NativeTextImportCommitResult result;
            using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
            {
                var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                result = outcome.Result;
            }
            Assert.Equal(NativeTextImportCommitStatus.Committed, result.Status);
            if (i == 31) Assert.Equal(32, (await catalog.ReadNativeTextImportCandidatesAsync(Id)).Length);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ReadNativeTextImportCandidatesAsync(Id));
        var page = await catalog.ReadNativeTextImportCandidatesPageAsync(Id);
        Assert.Equal(32, page.Items.Length); Assert.NotNull(page.NextSha256);
        var last = await catalog.ReadNativeTextImportCandidatesPageAsync(Id, page.NextSha256);
        Assert.Single(last.Items); Assert.Null(last.NextSha256);
        Assert.Equal(33, page.Items.Concat(last.Items).Select(c => c.Sha256).Distinct().Count());
    }

    [Fact]
    public async Task AggregateByteBoundaryUsesAdditionalPagesWithoutDroppingEvidence()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string text = new('x', 500_000);
        lease.Admission.CompleteStartup();
        for (int variant = 0; variant < 3; variant++)
        {
            var plan = await Prepare(catalog, Thread(Enumerable.Range(1, 23).Select(i => Post(i, text + variant))));
            NativeTextImportCommitResult result;
            using (ProfileAdmissionToken owner = await lease.Admission.EnterExclusiveAsync())
            {
                var outcome = await catalog.CommitNativeTextImportAdmittedAsync(owner, plan);
                Assert.Equal(ProfileExclusiveDisposition.SafeToResume, outcome.Disposition);
                result = outcome.Result;
            }
            Assert.Equal(NativeTextImportCommitStatus.Committed, result.Status);
        }
        var first = await catalog.ReadNativeTextImportCandidatesPageAsync(Id);
        Assert.Equal(2, first.Items.Length); Assert.NotNull(first.NextSha256);
        Assert.InRange(first.Items.Sum(i => (long)i.CatalogBytes.Length), 22L * 1024 * 1024 - 1024 * 1024, 32L * 1024 * 1024);
        var second = await catalog.ReadNativeTextImportCandidatesPageAsync(Id, first.NextSha256);
        Assert.Single(second.Items); Assert.Null(second.NextSha256);
        Assert.Equal(3, first.Items.Concat(second.Items).Select(i => i.Sha256).Distinct().Count());
    }

}
