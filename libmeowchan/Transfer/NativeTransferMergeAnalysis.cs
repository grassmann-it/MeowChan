using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Transfer;

internal enum TransferMergeAnalysisError { None, DifferentResource, InvalidInput, BudgetExceeded }
internal enum TransferMergeDisposition { TargetOnly, New, Identical, Conflict }
[Flags]
internal enum TransferPostDifference
{
    None = 0, Ordinal = 1, Presence = 2, PostedAt = 4, Subject = 8, Author = 16,
    Html = 32, IsDeleted = 64, Attachments = 128
}
[Flags]
internal enum TransferThreadDifference { None = 0, PageUrl = 1, Title = 2, OrderContract = 4 }
internal sealed record TransferMergePostAnalysis(string PostId, TransferMergeDisposition Disposition,
    TransferPostDifference Differences);
internal sealed record TransferMergeRetainedAnalysis(string PostId, string RevisionKey,
    TransferMergeDisposition Disposition);
internal sealed record TransferMergeAnalysisResult(TransferMergeAnalysisError Error,
    TransferThreadDifference ThreadDifferences, ImmutableArray<TransferMergePostAnalysis> Posts,
    ImmutableArray<TransferMergeRetainedAnalysis> RetainedBindings);
// Combined work across both snapshots; callers may lower, never raise these safety caps.
internal sealed record TransferMergeAnalysisLimits(int MaxPosts = 200_000,
    int MaxAttachments = 200_000, long MaxTextCharacters = 32L * 1024 * 1024);

/// <summary>
/// Passive, conservative comparison of two already admitted/frozen Chan projections.
/// New means a candidate identity addition, NOT an import plan or commit capability.
/// No fieldwise synthesis, byte reads, source observation, sorting, authority or store access.
/// Output contains only identities/classifications, never caller-owned attachment lists.
/// </summary>
internal static class NativeTransferMergeAnalysis
{
    internal static TransferMergeAnalysisResult Analyze(TransferThread target, TransferThread incoming,
        TransferMergeAnalysisLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(incoming);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new();
        if (limits.MaxPosts is < 0 or > 200_000 || limits.MaxAttachments is < 0 or > 200_000
            || limits.MaxTextCharacters is < 0 or > 32L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        try
        {
            Budget budget = new(limits, cancellationToken);
            Snapshot old = Inspect(target, budget), next = Inspect(incoming, budget);
            // ResourceId components are ordinal; never compare just Thread or a URL/hash.
            if (target.Id != incoming.Id) return Fail(TransferMergeAnalysisError.DifferentResource);
            TransferThreadDifference threadDifferences = TransferThreadDifference.None;
            if (target.PageUrl != incoming.PageUrl) threadDifferences |= TransferThreadDifference.PageUrl;
            if (target.Title != incoming.Title) threadDifferences |= TransferThreadDifference.Title;
            if (target.DecimalPostOrder != incoming.DecimalPostOrder) threadDifferences |= TransferThreadDifference.OrderContract;
            // FirstSeen/CapturedAt/CaptureRevision and SourceState are historical/local observations,
            // not comparable freshness tokens. In particular an incoming active claim cannot revive 404.
            ImmutableArray<TransferMergePostAnalysis>.Builder posts = ImmutableArray.CreateBuilder<TransferMergePostAnalysis>();
            foreach (TransferPost post in target.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!next.Posts.ContainsKey(post.Metadata.PostId))
                    posts.Add(new(post.Metadata.PostId, TransferMergeDisposition.TargetOnly, TransferPostDifference.None));
            }
            foreach (TransferPost post in incoming.Posts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransferPostDifference bindingDifference = TransferPostDifference.None;
                foreach (TransferAttachment attachment in post.Attachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attachment.Metadata.AttachmentRevisionKey is { } key
                        && old.Revisions.TryGetValue(key, out Binding? existing)
                        && (existing.PostId != post.Metadata.PostId || existing.Attachment != attachment))
                        bindingDifference = TransferPostDifference.Attachments;
                }
                if (!old.Posts.TryGetValue(post.Metadata.PostId, out TransferPost? previous))
                    posts.Add(new(post.Metadata.PostId, bindingDifference == TransferPostDifference.None
                        ? TransferMergeDisposition.New : TransferMergeDisposition.Conflict, bindingDifference));
                else
                {
                    TransferPostDifference difference = Compare(previous, post, cancellationToken) | bindingDifference;
                    posts.Add(new(post.Metadata.PostId, difference == TransferPostDifference.None
                        ? TransferMergeDisposition.Identical : TransferMergeDisposition.Conflict, difference));
                }
            }
            ImmutableArray<TransferMergeRetainedAnalysis>.Builder retained = ImmutableArray.CreateBuilder<TransferMergeRetainedAnalysis>();
            foreach (TransferRetainedBinding binding in target.RetainedBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = binding.Attachment.Metadata.AttachmentRevisionKey!;
                if (!next.Revisions.TryGetValue(key, out Binding? matching))
                    retained.Add(new(binding.PostId, key, TransferMergeDisposition.TargetOnly));
                else if (!next.RetainedKeys.Contains(key))
                    retained.Add(new(binding.PostId, key, matching.PostId == binding.PostId && matching.Attachment == binding.Attachment
                        ? TransferMergeDisposition.Identical : TransferMergeDisposition.Conflict));
            }
            foreach (TransferRetainedBinding binding in incoming.RetainedBindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = binding.Attachment.Metadata.AttachmentRevisionKey!;
                TransferMergeDisposition disposition = !old.Revisions.TryGetValue(key, out Binding? previous)
                    ? TransferMergeDisposition.New
                    : previous.PostId == binding.PostId && previous.Attachment == binding.Attachment
                        ? TransferMergeDisposition.Identical : TransferMergeDisposition.Conflict;
                retained.Add(new(binding.PostId, key, disposition));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(TransferMergeAnalysisError.None, threadDifferences, posts.ToImmutable(), retained.ToImmutable());
        }
        catch (AnalysisException e) { return Fail(e.Error); }
    }

    private static TransferPostDifference Compare(TransferPost first, TransferPost second, CancellationToken ct)
    {
        CapturedPost a = first.Metadata, b = second.Metadata;
        TransferPostDifference difference = TransferPostDifference.None;
        if (a.Ordinal != b.Ordinal) difference |= TransferPostDifference.Ordinal;
        if (a.Fields != b.Fields) difference |= TransferPostDifference.Presence;
        if (!Nullable.Equals(a.PostedAt, b.PostedAt)) difference |= TransferPostDifference.PostedAt;
        if (a.Subject != b.Subject) difference |= TransferPostDifference.Subject;
        if (a.Author != b.Author) difference |= TransferPostDifference.Author;
        if (a.Html != b.Html) difference |= TransferPostDifference.Html;
        if (a.IsDeleted != b.IsDeleted) difference |= TransferPostDifference.IsDeleted;
        // Null collection, observed empty and missing presence remain separate. Position is the
        // slot identity; array enumeration order does not change the observed attachment values.
        if (first.AttachmentsWereNull != second.AttachmentsWereNull || first.Attachments.Length != second.Attachments.Length)
            difference |= TransferPostDifference.Attachments;
        else
        {
            Dictionary<int, TransferAttachment> slots = new();
            foreach (TransferAttachment attachment in first.Attachments)
            {
                ct.ThrowIfCancellationRequested(); slots.Add(attachment.Metadata.Position, attachment);
            }
            foreach (TransferAttachment attachment in second.Attachments)
            {
                ct.ThrowIfCancellationRequested();
                // Record value equality includes ALL raw metadata (incl. legacy revision null,
                // SizeHint, SHA1), role, content status, payload and passive historical evidence.
                // Omitted/missing/failed media are differences, never deletion commands.
                if (!slots.TryGetValue(attachment.Metadata.Position, out TransferAttachment? other) || other != attachment)
                    difference |= TransferPostDifference.Attachments;
            }
        }
        ct.ThrowIfCancellationRequested();
        return difference;
    }

    private sealed record Binding(string PostId, TransferAttachment Attachment);
    private sealed record Snapshot(Dictionary<string, TransferPost> Posts, Dictionary<string, Binding> Revisions, HashSet<string> RetainedKeys);

    // Internal DTO constructors are not admission capabilities. Check comparison shape, duplicates
    // and counted work here; package hash/privacy/history validation still belongs to GP10.
    private static Snapshot Inspect(TransferThread thread, Budget budget)
    {
        if (thread.Id == null || thread.Posts.IsDefault || thread.RetainedBindings.IsDefault) throw Invalid();
        budget.Identifier(thread.Id.Source); budget.Identifier(thread.Id.Board); budget.Identifier(thread.Id.Thread);
        budget.Text(thread.PageUrl); budget.Text(thread.Title);
        budget.Posts(thread.Posts.Length);
        budget.Attachments(thread.RetainedBindings.Length);
        Dictionary<string, TransferPost> posts = new(StringComparer.Ordinal);
        Dictionary<string, Binding> revisions = new(StringComparer.Ordinal);
        foreach (TransferPost post in thread.Posts)
        {
            budget.Check();
            if (post?.Metadata == null || post.Attachments.IsDefault) throw Invalid();
            CapturedPost metadata = post.Metadata;
            budget.Identifier(metadata.PostId);
            if (!posts.TryAdd(metadata.PostId, post) || metadata.Ordinal < 0
                || (metadata.Fields & ~PostFieldPresence.All) != 0) throw Invalid();
            budget.Text(metadata.Subject); budget.Text(metadata.Author); budget.Text(metadata.Html);
            bool Has(PostFieldPresence flag) => (metadata.Fields & flag) != 0;
            if (!Has(PostFieldPresence.PostedAt) && metadata.PostedAt != null
                || !Has(PostFieldPresence.Subject) && metadata.Subject != null
                || !Has(PostFieldPresence.Author) && metadata.Author != null
                || !Has(PostFieldPresence.Html) && metadata.Html != null
                || !Has(PostFieldPresence.IsDeleted) && metadata.IsDeleted
                || !Has(PostFieldPresence.Attachments) && (post.AttachmentsWereNull || post.Attachments.Length != 0)) throw Invalid();
            if (post.AttachmentsWereNull != (metadata.Attachments == null)
                || (metadata.Attachments?.Count ?? 0) != post.Attachments.Length) throw Invalid();
            budget.Attachments(post.Attachments.Length);
            HashSet<int> positions = new();
            for (int i = 0; i < post.Attachments.Length; i++)
            {
                TransferAttachment attachment = post.Attachments[i];
                InspectAttachment(attachment, budget);
                if (!positions.Add(attachment.Metadata.Position) || metadata.Attachments![i] != attachment.Metadata) throw Invalid();
                AddRevision(metadata.PostId, attachment, revisions);
            }
        }
        HashSet<string> retainedKeys = new(StringComparer.Ordinal);
        foreach (TransferRetainedBinding binding in thread.RetainedBindings)
        {
            budget.Check();
            if (binding == null) throw Invalid();
            budget.Identifier(binding.PostId);
            InspectAttachment(binding.Attachment, budget);
            CapturedAttachment metadata = binding.Attachment.Metadata;
            if (metadata.AttachmentRevisionKey == null || metadata.MediaId == null || metadata.Url == null
                || metadata.IsDeleted || metadata.Variant != "original" || binding.Attachment.Role != TransferMediaRole.Original
                || binding.Attachment.Status is not (TransferContentStatus.Contained or TransferContentStatus.Unavailable or TransferContentStatus.Omitted)) throw Invalid();
            AddRevision(binding.PostId, binding.Attachment, revisions);
            retainedKeys.Add(metadata.AttachmentRevisionKey);
        }
        return new(posts, revisions, retainedKeys);
    }

    private static void AddRevision(string postId, TransferAttachment attachment, Dictionary<string, Binding> revisions)
    {
        if (attachment.Metadata.AttachmentRevisionKey is { } key && !revisions.TryAdd(key, new(postId, attachment))) throw Invalid();
    }

    private static void InspectAttachment(TransferAttachment attachment, Budget budget)
    {
        budget.Check();
        if (attachment?.Metadata == null) throw Invalid();
        CapturedAttachment a = attachment.Metadata;
        if (a.Position < 0 || a.ExpectedSize < 0 || a.PlannedPath != null
            || !Enum.IsDefined(attachment.Role) || !Enum.IsDefined(attachment.Status)) throw Invalid();
        budget.Text(a.MediaId); budget.Identifier(a.Variant); budget.Text(a.Url); budget.Text(a.OriginalFileName);
        budget.Text(a.ExpectedMd5); budget.Text(a.ExpectedSha1); budget.Text(a.ExpectedSha256);
        budget.Text(a.AttachmentRevisionKey); budget.Text(attachment.PayloadSha256);
        if (a.ProviderSizeHint is { } hint)
        { budget.Text(hint.RawValue); budget.Text(hint.UnitLabel); }
        if (attachment.History is { } history) budget.Text(history.Kind);
    }

    private sealed class Budget(TransferMergeAnalysisLimits limits, CancellationToken ct)
    {
        private int posts, attachments;
        private long characters;
        internal void Check() => ct.ThrowIfCancellationRequested();
        internal void Posts(int count)
        { Check(); if (count > limits.MaxPosts - posts) throw Exceeded(); posts += count; }
        internal void Attachments(int count)
        { Check(); if (count > limits.MaxAttachments - attachments) throw Exceeded(); attachments += count; }
        internal void Identifier(string? value)
        {
            Text(value);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw Invalid();
        }
        internal void Text(string? value)
        {
            Check();
            int length = value?.Length ?? 0;
            if (length > 1_048_576 || length > limits.MaxTextCharacters - characters) throw Exceeded();
            characters += length;
        }
    }
    private static AnalysisException Invalid() => new(TransferMergeAnalysisError.InvalidInput);
    private static AnalysisException Exceeded() => new(TransferMergeAnalysisError.BudgetExceeded);
    private sealed class AnalysisException(TransferMergeAnalysisError error) : Exception
    { internal TransferMergeAnalysisError Error => error; }
    private static TransferMergeAnalysisResult Fail(TransferMergeAnalysisError error)
        => new(error, TransferThreadDifference.None, [], []);
}
