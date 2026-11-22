using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Tags;

internal sealed record PrivateTagReviewReceipt(Guid OperationId, long Revision);

internal sealed partial class PrivateTagStore
{
    internal bool UsesLease(ProfileLease lease) => ReferenceEquals(_lease, lease);

    internal PrivateTagReviewReceipt? LastReviewReceipt() => _lease.RunExclusive(() => Read().Model.LastReview);

    internal TagReviewPrivateChange PreviewReviewUndo(Guid operationId, TagReviewTarget target) => _lease.RunExclusive(() =>
    {
        (Model model, byte[] bytes) = Read();
        var ownedTags = model.BoundReviewOwners.Where(item => item.Value == operationId).Select(item => (TagId)item.Key.TagId)
            .Concat(model.LocalReviewOwners.Where(item => item.Value == operationId).Select(item => item.Key.TagId)).ToHashSet();
        return new TagReviewPrivateChange(model.Revision, Fingerprint(bytes), target, model.Catalog.Definitions
            .Where(tag => ownedTags.Contains(tag.Id)).Select(tag => new TagReviewTag(tag.Name.Display, WasAssigned: true)).ToArray());
    });

    internal void CommitReview(TagReviewOperation review, TagRulePreview? preview, ProfileAdmissionToken admission,
        Action prepareJournal, CancellationToken token)
    {
        TagReviewPrivateChange before = review.PrivateChange!;
        Mutate(admission, before.Revision, token, (model, bytes) =>
        {
            RequireRuleBase(preview?.Lease ?? _lease, before.Fingerprint, bytes);
            if (preview is not null)
            {
                CheckRuleSuggestions(preview.Projections.Select(row => row.Input), token);
                ApplyRuleChanges(model, preview.Changes, token);
                SetReviewOwnership(model, review.OperationId, preview.Projections[0].Input.Subject, before.Tags, token);
            }
            else
            {
                RemoveReviewAssignments(model, review.Undoes!.Value, token);
            }
            if (model.Revision == long.MaxValue) throw new PrivateTagStoreException(PrivateTagStoreError.RevisionOverflow);
            model.LastReview = new(review.OperationId, model.Revision + 1);
            return (true, default);
        }, beforePublish: prepareJournal);
    }

    private static void SetReviewOwnership(Model model, Guid operationId, MediaTagSubject subject,
        TagReviewTag[] tags, CancellationToken token)
    {
        // ApplyRuleChanges has committed the identity to this model, including any new loose occurrence.
        foreach (TagReviewTag tag in tags)
        {
            token.ThrowIfCancellationRequested();
            TagId id = model.Catalog.Find(RequireName(tag.Name))!.Id;
            if (subject is MediaTagSubject.Bound bound)
            {
                var assignment = new PrivateMediaTagAssignment(id, bound.Target);
                if (tag.WasAssigned) model.BoundReviewOwners.Remove(assignment);
                else model.BoundReviewOwners.Add(assignment, operationId);
            }
            else if (subject is MediaTagSubject.Local local)
            {
                var assignment = new PrivateLocalMediaTagAssignment(id, model.Resolve(local.Observation).Target!.Value);
                if (tag.WasAssigned) model.LocalReviewOwners.Remove(assignment);
                else model.LocalReviewOwners.Add(assignment, operationId);
            }
        }
    }

    private static void RemoveReviewAssignments(Model model, Guid operationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Owners name the exact assignment, even when a handover has given the same bytes another key.
        model.Media.RemoveAll(item => model.BoundReviewOwners.TryGetValue(item, out Guid owner) && owner == operationId);
        model.Local.RemoveAll(item => model.LocalReviewOwners.TryGetValue(item, out Guid owner) && owner == operationId);
    }

    private static Guid? CheckReviewAssignment(JsonElement item, int version, string[] properties)
    {
        if (version >= ReviewVersion && item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("ReviewOwner", out JsonElement owner))
        {
            CheckObject(item, [.. properties, "ReviewOwner"]);
            return ReadGuid(owner);
        }
        CheckObject(item, properties);
        return null;
    }

    private static PrivateTagReviewReceipt ReadReviewReceipt(JsonElement root, long documentRevision)
    {
        JsonElement receipt = root.GetProperty("LastReview");
        CheckObject(receipt, ["OperationId", "Revision"]);
        long revision = receipt.GetProperty("Revision").GetInt64();
        if (revision <= 0 || revision > documentRevision) throw new InvalidDataException(Invalid);
        return new(ReadGuid(receipt.GetProperty("OperationId")), revision);
    }
}
