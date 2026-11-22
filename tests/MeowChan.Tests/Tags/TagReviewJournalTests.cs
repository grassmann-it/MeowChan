using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

public sealed class TagReviewJournalTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly TagSuggestionProvenance Model = new("reference", "tagger", "1.0", "fixture", "r1");
    private static readonly TagRuleSet Rules = new([
        new(TagRuleKind.Alias, "raw", "canonical"),
        new(TagRuleKind.Parent, "raw", "existing"),
        new(TagRuleKind.Parent, "raw", "reassigned"),
        new(TagRuleKind.Parent, "raw", "claimed")
    ]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartUsesThePrivateReceiptRatherThanThePresenceOfTheTag(bool privateWriteCommitted)
    {
        using TempDirectory temp = new();
        Guid operation = Guid.NewGuid();
        MediaTagSubject.Bound subject = Bound("100");
        TagRulePreview preview;
        long revision;
        var crashAt = privateWriteCommitted ? PrivateTagWritePhase.AfterReplace : PrivateTagWritePhase.BeforeReplace;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var tags = new PrivateTagStore(temp.Path, lease) { WritePhaseForTests = InterruptAt(crashAt) };
            var suggestions = new TagSuggestionStore(lease, privateTags: tags);
            var pending = RunDouble(suggestions, "raw");
            revision = pending.Revision;
            preview = Preview(tags, pending, subject);

            Assert.Throws<IOException>(() => suggestions.AcceptReview(operation, preview));
            var interrupted = suggestions.Load();
            Assert.Equal(operation, interrupted.PendingReview!.OperationId);
            Assert.Equal(revision, interrupted.Revision);
            Assert.Equal(TagSuggestionState.Pending, Assert.Single(interrupted.Suggestions).State);
            Assert.Empty(interrupted.Events);
            Assert.Equal(TagSuggestionStoreError.ReviewRecoveryRequired,
                Assert.Throws<TagSuggestionStoreException>(() => RunDouble(suggestions, "next")).Error);
        }

        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var tags = new PrivateTagStore(temp.Path, lease);
            // In both cases the tag is present when recovery runs. An unrelated manual write keeps a
            // real receipt, while a manual assignment cannot stand in for an absent receipt.
            tags.DefineAndSetMediaSubjects(privateWriteCommitted ? "manual" : "canonical", [subject]);
            var suggestions = new TagSuggestionStore(lease, privateTags: tags);
            var recovered = suggestions.RecoverReview();
            Assert.Null(recovered.PendingReview);
            Assert.Equal(revision + 1, recovered.Revision);
            var review = Assert.Single(recovered.Reviews);
            Assert.Equal(operation, review.OperationId);
            Assert.Equal(privateWriteCommitted ? TagReviewStatus.Completed : TagReviewStatus.Interrupted, review.Status);
            Assert.Equal(privateWriteCommitted ? TagSuggestionState.Accepted : TagSuggestionState.Pending,
                Assert.Single(recovered.Suggestions).State);
            if (privateWriteCommitted)
            {
                Assert.Equal(operation, Assert.Single(recovered.Events).OperationId);
                Assert.Equal(new PrivateTagReviewReceipt(operation, review.PrivateChange!.WrittenRevision), tags.LastReviewReceipt());
            }
            else
            {
                Assert.Empty(recovered.Events);
                Assert.Null(tags.LastReviewReceipt());
            }

            Assert.Equal(review.Status, suggestions.AcceptReview(operation, preview).Operation.Status);
            Assert.Equal(recovered.Revision, suggestions.RecoverReview().Revision);
            Assert.Equal(TagSuggestionStoreError.OperationConflict, Assert.Throws<TagSuggestionStoreException>(() =>
                suggestions.RejectReview(operation, review.SuggestionId, revision)).Error);
            if (!privateWriteCommitted)
            {
                var fresh = suggestions.AcceptReview(Guid.NewGuid(), Preview(tags, recovered, subject));
                Assert.True(Assert.Single(fresh.Operation.PrivateChange!.Tags).WasAssigned);
                suggestions.UndoReview(Guid.NewGuid(), fresh.Operation.OperationId, fresh.Snapshot.Revision);
                Assert.Equal(["canonical"], Names(tags.ReadSnapshot(), subject));
            }
        }
    }

    [Fact]
    public void RestartedUndoRemovesOnlySurvivingOwnedAssignments()
    {
        using TempDirectory temp = new();
        Guid accept = Guid.NewGuid(), undo = Guid.NewGuid();
        MediaTagSubject.Bound subject = Bound("100"), other = Bound("101"), handedOver = Bound("100", 'b');
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var tags = new PrivateTagStore(temp.Path, lease);
            var suggestions = new TagSuggestionStore(lease, privateTags: tags);
            tags.DefineAndSetMediaSubjects("existing", [subject]);
            var pending = RunDouble(suggestions, "raw");
            var preview = Preview(tags, pending, subject, Rules);
            var accepted = suggestions.AcceptReview(accept, preview);
            Assert.Equal(accepted.Operation.Status, suggestions.AcceptReview(accept, preview).Operation.Status);
            Assert.Single(accepted.Snapshot.Events);
            var before = accepted.Operation.PrivateChange!;
            Assert.Equal(1, before.Revision);
            Assert.True(before.Tags.Single(tag => tag.Name == "existing").WasAssigned);
            Assert.False(before.Tags.Single(tag => tag.Name == "canonical").WasAssigned);
            Assert.Empty(Names(tags.ReadSnapshot(), other));

            var reassigned = tags.ReadSnapshot().Tags.Single(tag => tag.Name.Display == "reassigned").Id;
            tags.SetMedia(reassigned, subject.Target, assigned: false);
            tags.SetMedia(reassigned, subject.Target, assigned: true);
            tags.DefineAndSetMediaSubjects("claimed", [subject]);
            tags.DefineAndSetMediaSubjects("canonical", [other, handedOver]);
            tags.DefineAndSetMediaSubjects("manual", [subject]);
        }

        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var tags = new PrivateTagStore(temp.Path, lease);
            var suggestions = new TagSuggestionStore(lease, privateTags: tags);
            var accepted = suggestions.Load();
            Assert.Equal(TagSuggestionStoreError.InvalidTransition, Assert.Throws<TagSuggestionStoreException>(() =>
                suggestions.Decide([accepted.Suggestions[0].Id], TagSuggestionState.Pending, accepted.Revision)).Error);
            var undone = suggestions.UndoReview(undo, accept, accepted.Revision);
            Assert.Equal(accept, undone.Operation.Undoes);
            Assert.Equal("canonical", Assert.Single(undone.Operation.PrivateChange!.Tags).Name);
            Assert.Equal(undo, undone.Snapshot.Events[^1].OperationId);
            Assert.Equal(TagSuggestionState.Pending, Assert.Single(undone.Snapshot.Suggestions).State);
            Assert.Equal(["claimed", "existing", "manual", "reassigned"], Names(tags.ReadSnapshot(), subject));
            Assert.Equal(["canonical"], Names(tags.ReadSnapshot(), other));
            Assert.Equal(["canonical"], Names(tags.ReadSnapshot(), handedOver));
            long revision = tags.ReadSnapshot().Revision;
            Assert.Equal(TagReviewStatus.Completed, suggestions.UndoReview(undo, accept, accepted.Revision).Operation.Status);
            Assert.Equal(revision, tags.ReadSnapshot().Revision);
            Assert.Equal(2, suggestions.Load().Events.Count);
        }
    }

    [Fact]
    public void RestartFinalizesAnInterruptedLocalUndoWithoutTouchingATagSetSince()
    {
        using TempDirectory temp = new();
        Guid accept = Guid.NewGuid(), undo = Guid.NewGuid();
        Assert.True(LocalMediaObservation.TryCreate(Thread, "media", "image.png", new string('c', 64), 100, Hash, out var observation));
        var subject = new MediaTagSubject.Local(observation!, null);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var tags = new PrivateTagStore(temp.Path, lease);
            var suggestions = new TagSuggestionStore(lease, privateTags: tags);
            var pending = RunDouble(suggestions, "raw");
            var accepted = suggestions.AcceptReview(accept, Preview(tags, pending, subject));
            Assert.NotNull(Assert.Single(accepted.Snapshot.Reviews).PrivateChange!.Target.Local);
            Assert.Single(tags.ReadSnapshot().LocalAssignments);
            tags = new PrivateTagStore(temp.Path, lease) { WritePhaseForTests = InterruptAt(PrivateTagWritePhase.AfterReplace) };
            suggestions = new TagSuggestionStore(lease, privateTags: tags);
            Assert.Throws<IOException>(() => suggestions.UndoReview(undo, accept, accepted.Snapshot.Revision));
            Assert.Empty(tags.ReadSnapshot().LocalAssignments);
        }

        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            var tags = new PrivateTagStore(temp.Path, lease);
            tags.DefineAndSetMediaSubjects("canonical", [subject]);
            var suggestions = new TagSuggestionStore(lease, privateTags: tags);
            var recovered = suggestions.RecoverReview();
            Assert.Equal(TagSuggestionState.Pending, Assert.Single(recovered.Suggestions).State);
            Assert.Equal([accept, undo], recovered.Events.Select(item => item.OperationId!.Value));
            Assert.Single(tags.ReadSnapshot().LocalAssignments);
            Assert.Equal(recovered.Revision, suggestions.RecoverReview().Revision);
        }
    }

    [Fact]
    public void RejectionAndItsUndoAreJournalledWithoutPrivateWrites()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var suggestions = new TagSuggestionStore(lease);
        var pending = RunDouble(suggestions, "raw");
        long id = Assert.Single(pending.Suggestions).Id;
        Guid reject = Guid.NewGuid();
        Assert.Equal(TagSuggestionStoreError.StaleRevision, Assert.Throws<TagSuggestionStoreException>(() =>
            suggestions.RejectReview(reject, id, pending.Revision - 1)).Error);
        var rejected = suggestions.RejectReview(reject, id, pending.Revision);
        Assert.Equal(1, rejected.Snapshot.Events.Count);
        var rerun = RunDouble(suggestions, "raw", Model with { PluginId = "other" });
        Assert.Empty(rerun.PendingFor(Hash, 0.35));
        var undone = suggestions.UndoReview(Guid.NewGuid(), reject, rerun.Revision);
        Assert.Equal(id, Assert.Single(undone.Snapshot.PendingFor(Hash, 0.35)).Id);
        Assert.Equal(TagReviewStatus.Completed, suggestions.RejectReview(reject, id, pending.Revision).Operation.Status);
        Assert.False(File.Exists(Path.Combine(temp.Path, PrivateTagStore.FileName)));
    }

    [Fact]
    public void ASecondReviewOfTheSameMappedTagKeepsItWhenEitherReviewIsUndone()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        var tags = new PrivateTagStore(temp.Path, lease);
        var suggestions = new TagSuggestionStore(lease, privateTags: tags);
        var subject = Bound("100");
        var firstPending = RunDouble(suggestions, "raw");
        var first = suggestions.AcceptReview(Guid.NewGuid(), Preview(tags, firstPending, subject));
        var secondPending = RunDouble(suggestions, "canonical");
        var second = suggestions.AcceptReview(Guid.NewGuid(), Preview(tags, secondPending, subject));
        Assert.True(Assert.Single(second.Operation.PrivateChange!.Tags).WasAssigned);
        var undone = suggestions.UndoReview(Guid.NewGuid(), first.Operation.OperationId, second.Snapshot.Revision);
        suggestions.UndoReview(Guid.NewGuid(), second.Operation.OperationId, undone.Snapshot.Revision);
        Assert.Equal(["canonical"], Names(tags.ReadSnapshot(), subject));
    }

    private static TagSuggestionSnapshot RunDouble(TagSuggestionStore store, string name, TagSuggestionProvenance? model = null)
        => store.CommitRuns([new(Hash, 100, model ?? Model, TagSuggestionStore.TaggedOutcome, [new(name, "General", 0.8)])]);

    private static TagRulePreview Preview(PrivateTagStore tags, TagSuggestionSnapshot pending, MediaTagSubject subject,
        TagRuleSet? rules = null)
        => tags.PreviewTagRules(rules ?? new([new(TagRuleKind.Alias, "raw", "canonical")]),
            pending.PendingFor(subject, 100, 0.35), tags.ReadSnapshot().Revision);

    private static Action<PrivateTagWritePhase> InterruptAt(PrivateTagWritePhase crashAt) => phase =>
    {
        if (phase == crashAt) throw new IOException("Simulated process interruption.");
    };

    private static string[] Names(PrivateTagStoreSnapshot snapshot, MediaTagSubject.Bound subject)
        => snapshot.TagsOf(subject.Target).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();

    private static MediaTagSubject.Bound Bound(string post, char key = 'a')
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread, post, 0, "att-v2:" + new string(key, 64)),
            "media-" + post, "original", TransferMediaRole.Original, Hash, out var target, out _));
        return new(target!);
    }
}
