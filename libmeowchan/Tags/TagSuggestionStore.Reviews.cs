using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Tags;

internal enum TagReviewKind { Accept, Reject, Undo }
internal enum TagReviewStatus { Prepared, Completed, Interrupted }

internal sealed record TagReviewTag(string Name, bool WasAssigned);
internal sealed record TagReviewPrivateChange(long Revision, string Fingerprint, TagReviewTarget Target, TagReviewTag[] Tags)
{
    internal long WrittenRevision => checked(Revision + 1);
}

internal sealed record TagReviewOperation(Guid OperationId, long SuggestionId, TagReviewKind Kind, TagReviewStatus Status,
    long ReviewRevision, DateTimeOffset At, Guid? Undoes = null, TagReviewPrivateChange? PrivateChange = null);

internal sealed record TagReviewResult(TagReviewOperation Operation, TagSuggestionSnapshot Snapshot);

internal sealed partial class TagSuggestionStore
{
    /// <summary>Accepts one visible proposal using the exact AP06 preview confirmed by the caller.</summary>
    internal TagReviewResult AcceptReview(Guid operationId, TagRulePreview preview, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Projections.Count != 1 || preview.Projections[0] is not { Canonical: not null,
                Input.Origin: TagRuleOrigin.Model origin } projection)
            throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);

        var added = preview.Changes.Select(change => change.Name).ToHashSet();
        var before = new TagReviewPrivateChange(preview.Revision, preview.Fingerprint, TagReviewTarget.From(projection.Input.Subject),
            projection.MappedNames().Select(name => new TagReviewTag(name.Display, !added.Contains(name))).ToArray());
        return Review(new(operationId, origin.Suggestion.Id, TagReviewKind.Accept, TagReviewStatus.Prepared,
            origin.Revision, _clock(), PrivateChange: before), preview, token);
    }

    internal TagReviewResult RejectReview(Guid operationId, long suggestionId, long expectedRevision, CancellationToken token = default)
        => Review(new(operationId, suggestionId, TagReviewKind.Reject, TagReviewStatus.Prepared,
            expectedRevision, _clock()), preview: null, token);

    internal TagReviewResult UndoReview(Guid operationId, Guid decisionId, long expectedRevision, CancellationToken token = default)
        => Review(new(operationId, 0, TagReviewKind.Undo, TagReviewStatus.Prepared,
            expectedRevision, _clock(), Undoes: decisionId), preview: null, token);

    /// <summary>Reconciles a prepared operation against the private document's atomic receipt.</summary>
    internal TagSuggestionSnapshot RecoverReview(CancellationToken token = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return RecoverReviewAdmitted(admission, token);
    }

    internal TagSuggestionSnapshot RecoverReviewAdmitted(ProfileAdmissionToken admission, CancellationToken token)
        => _lease.RunStoreWrite(admission, () => RecoverReviewCore(token));

    // An occurrence review must use the profile's configured write limits.
    private PrivateTagStore PrivateTags() => _privateTags
        ?? throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);

    private TagSuggestionSnapshot RecoverReviewCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TagSuggestionSnapshot disk = Writable(allowPending: true);
        if (disk.PendingReview is not { PrivateChange: { } before } pending) return disk;

        PrivateTagReviewReceipt? receipt = PrivateTags().LastReviewReceipt();
        bool committed = receipt == new PrivateTagReviewReceipt(pending.OperationId, before.WrittenRevision);
        if (!committed && receipt is { } other
            && (other.OperationId == pending.OperationId || other.Revision > before.Revision))
            throw new InvalidDataException("The private tag review receipt is inconsistent with the journal.");
        // An uncommitted intent is terminal. A fresh confirmation must revalidate the preview and occurrence.
        return FinishReview(disk, pending, committed, token);
    }

    private TagReviewResult Review(TagReviewOperation request, TagRulePreview? preview, CancellationToken token)
    {
        if (request.OperationId == Guid.Empty || request.Undoes == Guid.Empty)
            throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidInput);

        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
        return _lease.RunStoreWrite(admission, () =>
        {
            TagSuggestionSnapshot disk = RecoverReviewCore(token);
            if (disk.Reviews.FirstOrDefault(review => review.OperationId == request.OperationId) is { } existing)
            {
                if (!SameRequest(existing, request))
                    throw new TagSuggestionStoreException(TagSuggestionStoreError.OperationConflict);
                return new TagReviewResult(existing, disk);
            }
            if (request.ReviewRevision != disk.Revision)
                throw new TagSuggestionStoreException(TagSuggestionStoreError.StaleRevision);

            TagReviewOperation review = ResolveReview(disk, request);
            Document completed = WithReview(disk, review, TagReviewStatus.Completed);
            if (review.PrivateChange is null)
            {
                TagSuggestionSnapshot saved = Publish(completed, PrepareWrite(completed, forReview: true), checkAuthority: null, token);
                return new TagReviewResult(saved.Reviews[^1], saved);
            }

            // Both possible terminal documents must fit before we change private tags. A setting change
            // after prepare cannot strand a committed operation behind a newly lowered budget.
            PrepareWrite(completed, forReview: true);
            PrepareWrite(WithReview(disk, review, TagReviewStatus.Interrupted), forReview: true);
            Document prepared = WithReview(disk, review, TagReviewStatus.Prepared);
            PrivateTags().CommitReview(review, preview, admission, () =>
                Publish(prepared, PrepareWrite(prepared, forReview: true), checkAuthority: null, token), token);
            TagSuggestionSnapshot final = FinishReview(disk, review, committed: true, token: CancellationToken.None);
            return new TagReviewResult(final.Reviews[^1], final);
        });
    }

    private TagReviewOperation ResolveReview(TagSuggestionSnapshot disk, TagReviewOperation request)
    {
        if (request.Undoes is { } undo)
        {
            TagReviewOperation? original = disk.Reviews.FirstOrDefault(review => review.OperationId == undo
                && review.Status == TagReviewStatus.Completed && review.Kind is TagReviewKind.Accept or TagReviewKind.Reject);
            if (original is null || disk.Events.LastOrDefault(review => review.SuggestionId == original.SuggestionId)?.OperationId != undo)
                throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidTransition);

            TagReviewPrivateChange? change = null;
            if (original.PrivateChange is { } before)
            {
                change = PrivateTags().PreviewReviewUndo(undo, before.Target);
                StoredTagSuggestion originalSuggestion = disk.Suggestions.First(value => value.Id == original.SuggestionId);
                if (!ValidPrivateChange(TagReviewKind.Undo, change, originalSuggestion, before))
                    throw new InvalidDataException("The private tag assignments are inconsistent with the journal.");
            }
            return request with { SuggestionId = original.SuggestionId, PrivateChange = change };
        }

        StoredTagSuggestion? suggestion = disk.Suggestions.FirstOrDefault(value => value.Id == request.SuggestionId);
        if (suggestion is null) throw new TagSuggestionStoreException(TagSuggestionStoreError.UnknownSuggestion);
        if (suggestion.State != TagSuggestionState.Pending
            || disk.Suggestions.Any(value => value.IsDecided && value.TagKey == suggestion.TagKey))
            throw new TagSuggestionStoreException(TagSuggestionStoreError.InvalidTransition);
        return request;
    }

    private static bool SameRequest(TagReviewOperation existing, TagReviewOperation request)
    {
        if (existing.Kind != request.Kind || existing.ReviewRevision != request.ReviewRevision)
            return false;
        if (request.Undoes is { } undo) return existing.Undoes == undo;
        if (existing.SuggestionId != request.SuggestionId) return false;
        if (request.PrivateChange is not { } before) return existing.PrivateChange is null;
        return existing.PrivateChange is { } saved && saved.Revision == before.Revision && saved.Fingerprint == before.Fingerprint
            && saved.Target == before.Target && saved.Tags.SequenceEqual(before.Tags);
    }

    private TagSuggestionSnapshot FinishReview(TagSuggestionSnapshot disk, TagReviewOperation review, bool committed, CancellationToken token)
    {
        Document terminal = WithReview(disk, review, committed ? TagReviewStatus.Completed : TagReviewStatus.Interrupted);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(terminal, JsonOptions);
        if (bytes.LongLength > MaximumBytes) throw new TagSuggestionStoreException(TagSuggestionStoreError.LimitReached);
        return Publish(terminal, bytes, checkAuthority: null, token);
    }

    private static Document WithReview(TagSuggestionSnapshot disk, TagReviewOperation review, TagReviewStatus status)
    {
        Document document = Document.From(disk);
        document.Version = JournalVersion;
        document.Reviews ??= [];
        if (disk.PendingReview is not null) document.Reviews.RemoveAt(document.Reviews.Count - 1);
        document.Reviews.Add(review with { Status = status });
        if (status == TagReviewStatus.Prepared) return document;

        document.Revision = checked(disk.Revision + 1);
        if (status == TagReviewStatus.Completed)
        {
            int index = document.Suggestions.FindIndex(value => value.Id == review.SuggestionId);
            StoredTagSuggestion suggestion = document.Suggestions[index];
            TagSuggestionState target = ReviewTargetState(review.Kind);
            document.Suggestions[index] = suggestion with { State = target, DecidedAt = target == TagSuggestionState.Pending ? null : review.At };
            document.AddEvent(suggestion.Id, suggestion.State, target, review.At, review.OperationId);
        }
        return document;
    }

    private static TagSuggestionState ReviewTargetState(TagReviewKind kind) => kind switch
    {
        TagReviewKind.Accept => TagSuggestionState.Accepted,
        TagReviewKind.Reject => TagSuggestionState.Rejected,
        _ => TagSuggestionState.Pending
    };

    private static void CheckReviewJournal(Document document)
    {
        const string invalid = "The tag review journal is inconsistent.";
        if (document.Version == FormatVersion)
        {
            if (document.Reviews is not null || document.Events.Any(review => review.OperationId is not null))
                throw new InvalidDataException(invalid);
            return;
        }
        if (document.Reviews is not { Count: > 0 } reviews) throw new InvalidDataException(invalid);

        var suggestions = document.Suggestions.ToDictionary(value => value.Id);
        Dictionary<Guid, TagReviewOperation> operations = new();
        long previousRevision = -1;
        foreach (TagReviewOperation review in reviews)
        {
            if (review is null || review.OperationId == Guid.Empty || !Enum.IsDefined(review.Kind) || !Enum.IsDefined(review.Status)
                || review.ReviewRevision <= previousRevision || review.ReviewRevision > document.Revision
                || !suggestions.TryGetValue(review.SuggestionId, out StoredTagSuggestion? suggestion))
                throw new InvalidDataException(invalid);
            if (review.Status == TagReviewStatus.Prepared
                ? review.ReviewRevision != document.Revision || !ReferenceEquals(review, reviews[^1])
                : review.ReviewRevision >= document.Revision)
                throw new InvalidDataException(invalid);

            TagReviewOperation? original = null;
            if (review.Kind == TagReviewKind.Undo)
            {
                if (review.Undoes is not { } undo || !operations.TryGetValue(undo, out original)
                    || original.Kind == TagReviewKind.Undo || original.Status != TagReviewStatus.Completed
                    || original.SuggestionId != review.SuggestionId)
                    throw new InvalidDataException(invalid);
            }
            else if (review.Undoes is not null) throw new InvalidDataException(invalid);

            bool needsPrivate = review.Kind == TagReviewKind.Accept || original?.PrivateChange is not null;
            if (needsPrivate != (review.PrivateChange is not null)
                || (!needsPrivate && review.Status != TagReviewStatus.Completed)
                || (review.PrivateChange is { } before && !ValidPrivateChange(review.Kind, before, suggestion, original?.PrivateChange)))
                throw new InvalidDataException(invalid);
            if (!operations.TryAdd(review.OperationId, review)) throw new InvalidDataException(invalid);
            previousRevision = review.ReviewRevision;
        }

        HashSet<Guid> events = new();
        Dictionary<long, TagSuggestionReviewEvent> latest = new();
        foreach (TagSuggestionReviewEvent item in document.Events)
        {
            latest.TryGetValue(item.SuggestionId, out TagSuggestionReviewEvent? prior);
            if (item.OperationId is { } id)
            {
                if (!events.Add(id) || !operations.TryGetValue(id, out TagReviewOperation? review)
                    || review.Status != TagReviewStatus.Completed || item.SuggestionId != review.SuggestionId
                    || item.At != review.At || item.To != ReviewTargetState(review.Kind)
                    || !MatchesPriorDecision(review, item.From, prior, operations))
                    throw new InvalidDataException(invalid);
            }
            latest[item.SuggestionId] = item;
        }
        if (events.Count != reviews.Count(review => review.Status == TagReviewStatus.Completed))
            throw new InvalidDataException(invalid);

        foreach (long id in reviews.Select(review => review.SuggestionId).Distinct())
            if (latest.TryGetValue(id, out TagSuggestionReviewEvent? last) && last.To != suggestions[id].State)
                throw new InvalidDataException(invalid);
        TagReviewOperation tail = reviews[^1];
        if (tail.Status == TagReviewStatus.Prepared
            && !MatchesPriorDecision(tail, suggestions[tail.SuggestionId].State, latest.GetValueOrDefault(tail.SuggestionId), operations))
            throw new InvalidDataException(invalid);
    }

    private static bool MatchesPriorDecision(TagReviewOperation review, TagSuggestionState from, TagSuggestionReviewEvent? prior,
        Dictionary<Guid, TagReviewOperation> operations)
    {
        if (prior is not null && prior.To != from) return false;
        return review.Undoes is { } undo
            ? prior?.OperationId == undo && from == ReviewTargetState(operations[undo].Kind)
            : from == TagSuggestionState.Pending;
    }

    private static bool ValidPrivateChange(TagReviewKind kind, TagReviewPrivateChange before, StoredTagSuggestion suggestion,
        TagReviewPrivateChange? original)
    {
        if (before.Revision is < 0 or long.MaxValue || before.Fingerprint is not { Length: 64 } fingerprint
            || !fingerprint.All(Uri.IsHexDigit) || before.Target is null || !before.Target.Matches(suggestion) || before.Tags is null)
            return false;
        HashSet<PrivateTagName> names = new();
        if (before.Tags.Any(tag => tag is null || !PrivateTagName.TryCreate(tag.Name, out PrivateTagName? name, out _)
            || name!.Display != tag.Name || !names.Add(name))) return false;
        if (kind == TagReviewKind.Accept) return before.Tags.Length > 0;
        if (original is null || before.Target != original.Target || before.Revision < original.WrittenRevision) return false;
        var introduced = original.Tags.Where(tag => !tag.WasAssigned).Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
        return before.Tags.All(tag => tag.WasAssigned && introduced.Contains(tag.Name));
    }
}
