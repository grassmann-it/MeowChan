using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>
/// The post filters (plan P2, stage 1 without regular expressions) as a reader classifier. Rules compare without regard
/// to case: text against the plain post text, subject, every original file name, and MD5 by its 16 bytes (hex or base64
/// on either side). Hide beats placeholder beats highlight. "Also hide replies" spreads hide and placeholder
/// transitively over the quote graph (never highlight). Individually hidden posts follow the store's hidden-posts
/// action. Disabled and unsupported rules (stage 2 <c>regex</c>) never apply. Only a ready store applies: a damaged
/// file waiting for its explicit recovery, a newer or failed store filters nothing.
/// </summary>
internal sealed class PostFilterMatcher : IReaderPostClassifier
{
    private CompiledPostFilters _compiled = CompiledPostFilters.Empty;
    private long _revision;
    private ClassifiedThread? _last;

    private sealed record ClassifiedThread(ResourceId Thread, long ThreadRevision, int PostCount, CompiledPostFilters Filters,
        ReaderPostVerdict[] Verdicts);

    /// <summary>Raised on the calling thread after <see cref="Update"/> changed the rules.</summary>
    internal event EventHandler? Changed;

    /// <summary>The rules now in force. A snapshot that is not ready applies nothing.</summary>
    internal void Update(PostFilterSnapshot? snapshot)
    {
        CompiledPostFilters next = CompiledPostFilters.Compile(snapshot);
        Volatile.Write(ref _compiled, next);
        Interlocked.Increment(ref _revision);
        Volatile.Write(ref _last, null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal CompiledPostFilters Filters => Volatile.Read(ref _compiled);

    public bool IsActive(ResourceId thread) => Filters.AppliesTo(thread);
    public bool UsesQuoteGraph => Filters.SpreadsToReplies;
    public long Revision => Interlocked.Read(ref _revision);

    public void Classify(ArchivedThreadView thread, ReaderQuoteGraph? graph, ReaderPostVerdict[] verdicts, CancellationToken token)
    {
        CompiledPostFilters filters = Filters;
        filters.Classify(thread, graph, verdicts, token);
        // Kept for the notes in reply cards and quote previews (the projection removes hidden posts entirely).
        Volatile.Write(ref _last, new ClassifiedThread(thread.Summary.Id, thread.Summary.Revision, thread.PostCount, filters,
            (ReaderPostVerdict[])verdicts.Clone()));
    }

    /// <summary>The filter's own verdict of a post of the last classified read, or null when that read is not this one
    /// or the rules changed since.</summary>
    internal ReaderPostVerdict? VerdictOf(ArchivedThreadView thread, string postId)
    {
        ClassifiedThread? last = Volatile.Read(ref _last);
        if (last == null || !ReferenceEquals(last.Filters, Filters) || last.Thread != thread.Summary.Id
            || last.ThreadRevision != thread.Summary.Revision || last.PostCount != thread.PostCount) return null;
        int index = thread.FindPostIndex(postId);
        return index < 0 || index >= last.Verdicts.Length ? null : last.Verdicts[index];
    }
}

/// <summary>The rules of one store revision, prepared once for every classification of every thread.</summary>
internal sealed class CompiledPostFilters
{
    internal static readonly CompiledPostFilters Empty = new(Array.Empty<CompiledRule>(),
        new Dictionary<ResourceId, Dictionary<string, HiddenPost>>(), HiddenPostsAction.Stub);

    private readonly CompiledRule[] _rules;
    private readonly Dictionary<ResourceId, Dictionary<string, HiddenPost>> _hidden;
    private readonly HiddenPostsAction _hiddenAction;

    internal sealed record CompiledRule(PostFilterRule Rule, UInt128 Md5, ReaderPostVerdict Verdict, ReaderPostVisibility Spread)
    {
        internal bool InScope(ResourceId thread) => Rule.Source == null
            || string.Equals(Rule.Source, thread.Source, StringComparison.Ordinal)
                && (Rule.Board == null || string.Equals(Rule.Board, thread.Board, StringComparison.Ordinal));
    }

    private CompiledPostFilters(CompiledRule[] rules, Dictionary<ResourceId, Dictionary<string, HiddenPost>> hidden, HiddenPostsAction action)
    {
        _rules = rules;
        _hidden = hidden;
        _hiddenAction = action;
        SpreadsToReplies = rules.Any(rule => rule.Spread != ReaderPostVisibility.Shown)
            || hidden.Values.Any(posts => posts.Values.Any(post => post.Replies));
    }

    internal int RuleCount => _rules.Length;
    internal bool SpreadsToReplies { get; }

    internal static CompiledPostFilters Compile(PostFilterSnapshot? snapshot)
    {
        if (snapshot == null || snapshot.State != PostFilterStoreState.Ready) return Empty;
        List<CompiledRule> rules = new();
        foreach (PostFilterRule rule in snapshot.Rules)
        {
            if (!rule.CanApply || !rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) continue;
            UInt128 md5 = default;
            if (rule.Field == PostFilterField.Md5 && !TryMd5(rule.Pattern, out md5)) continue;
            ReaderPostVerdict verdict = rule.Action switch
            {
                PostFilterAction.Hide => new(ReaderPostVisibility.Hidden, ReaderPostMarks.None, null),
                PostFilterAction.Stub => new(ReaderPostVisibility.Stub, ReaderPostMarks.None, null),
                PostFilterAction.Highlight => new(ReaderPostVisibility.Shown, ReaderPostMarks.Highlight, null),
                _ => default
            };
            if (verdict == default) continue;
            ReaderPostVisibility spread = rule.Replies && rule.Action != PostFilterAction.Highlight ? verdict.Visibility : ReaderPostVisibility.Shown;
            rules.Add(new CompiledRule(rule, md5, verdict, spread));
        }
        Dictionary<ResourceId, Dictionary<string, HiddenPost>> hidden = new();
        foreach (HiddenPost post in snapshot.HiddenPosts)
        {
            if (!hidden.TryGetValue(post.Thread, out Dictionary<string, HiddenPost>? posts))
                hidden[post.Thread] = posts = new(StringComparer.Ordinal);
            posts[post.Post] = post;
        }
        return rules.Count == 0 && hidden.Count == 0 ? Empty : new CompiledPostFilters(rules.ToArray(), hidden, snapshot.HiddenPostsAction);
    }

    /// <summary>Some rule or hidden post can change this thread (UI thread, cheap).</summary>
    internal bool AppliesTo(ResourceId thread)
    {
        if (_hidden.ContainsKey(thread)) return true;
        foreach (CompiledRule rule in _rules) if (rule.InScope(thread)) return true;
        return false;
    }

    internal static bool TryMd5(string? text, out UInt128 value)
    {
        value = default;
        if (!PostFilterRules.TryParseMd5(text, out byte[] bytes) || bytes.Length != 16) return false;
        value = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        return true;
    }

    /// <summary>Background thread, pure apart from the localized reasons. <paramref name="verdicts"/> has one slot per
    /// thread index and is prefilled with Shown.</summary>
    internal void Classify(ArchivedThreadView thread, ReaderQuoteGraph? graph, ReaderPostVerdict[] verdicts, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(verdicts);
        ResourceId id = thread.Summary.Id;
        CompiledRule[] scoped = _rules.Where(rule => rule.InScope(id)).ToArray();
        _hidden.TryGetValue(id, out Dictionary<string, HiddenPost>? hiddenPosts);
        if (scoped.Length == 0 && hiddenPosts == null) return;
        FieldRules text = new(scoped, PostFilterField.Text);
        FieldRules subject = new(scoped, PostFilterField.Subject);
        FieldRules filename = new(scoped, PostFilterField.Filename);
        Dictionary<UInt128, List<CompiledRule>> md5 = new();
        foreach (CompiledRule rule in scoped.Where(rule => rule.Rule.Field == PostFilterField.Md5))
        {
            if (!md5.TryGetValue(rule.Md5, out List<CompiledRule>? list)) md5[rule.Md5] = list = new();
            list.Add(rule);
        }
        ReaderPostVisibility hiddenVisibility = _hiddenAction == HiddenPostsAction.Hide ? ReaderPostVisibility.Hidden : ReaderPostVisibility.Stub;
        string? singleReason = hiddenPosts != null ? Texts.T("postFilter.reason.single") : null;
        IReadOnlyList<CapturedPost> posts = thread.Posts;
        int count = Math.Min(posts.Count, verdicts.Length);
        ReaderPostVisibility[] spread = new ReaderPostVisibility[count];
        bool anySpread = false;
        List<CompiledRule> hits = new();
        for (int index = 0; index < count; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            CapturedPost post = posts[index];
            hits.Clear();
            if (!text.IsEmpty) text.Match(ArchiveText.PlainText(post.Html), hits);
            if (!subject.IsEmpty && !string.IsNullOrEmpty(post.Subject)) subject.Match(post.Subject, hits);
            if (post.Attachments is { Count: > 0 } attachments && (!filename.IsEmpty || md5.Count > 0))
                foreach (CapturedAttachment attachment in attachments)
                {
                    if (!filename.IsEmpty && !string.IsNullOrEmpty(attachment.OriginalFileName)) filename.Match(attachment.OriginalFileName, hits);
                    if (md5.Count > 0 && TryMd5(attachment.ExpectedMd5, out UInt128 hash) && md5.TryGetValue(hash, out List<CompiledRule>? found))
                        hits.AddRange(found);
                }
            ReaderPostVerdict verdict = verdicts[index];
            ReaderPostVisibility spreads = ReaderPostVisibility.Shown;
            foreach (CompiledRule rule in hits)
            {
                verdict = ReaderPostProjection.Combine(verdict, rule.Verdict with { Reason = RuleReason(rule.Rule) });
                if (rule.Spread > spreads) spreads = rule.Spread;
            }
            if (hiddenPosts != null && hiddenPosts.TryGetValue(post.PostId, out HiddenPost? single))
            {
                verdict = ReaderPostProjection.Combine(verdict, new ReaderPostVerdict(hiddenVisibility, ReaderPostMarks.None, singleReason));
                if (single.Replies && hiddenVisibility > spreads) spreads = hiddenVisibility;
            }
            verdicts[index] = verdict;
            spread[index] = spreads;
            anySpread |= spreads != ReaderPostVisibility.Shown;
        }
        if (anySpread && graph != null && graph.PostCount == count) SpreadToReplies(posts, graph, verdicts, spread, token);
    }

    // Breadth first over "who replies to whom". A reply takes the hiding of the post it quotes and passes it on; a post is
    // visited again only when a stronger hiding reaches it, so every post is queued at most twice.
    private static void SpreadToReplies(IReadOnlyList<CapturedPost> posts, ReaderQuoteGraph graph, ReaderPostVerdict[] verdicts,
        ReaderPostVisibility[] spread, CancellationToken token)
    {
        Queue<int> queue = new();
        for (int index = 0; index < spread.Length; index++) if (spread[index] != ReaderPostVisibility.Shown) queue.Enqueue(index);
        int steps = 0;
        while (queue.Count > 0)
        {
            if ((++steps & 255) == 0) token.ThrowIfCancellationRequested();
            int source = queue.Dequeue();
            ReaderPostVisibility visibility = spread[source];
            string? reason = null;
            foreach (int reply in graph.RepliesTo(source))
            {
                if ((uint)reply >= (uint)spread.Length || spread[reply] >= visibility) continue;
                reason ??= Texts.T("postFilter.reason.reply", posts[source].PostId);
                verdicts[reply] = ReaderPostProjection.Combine(verdicts[reply], new ReaderPostVerdict(visibility, ReaderPostMarks.None, reason));
                spread[reply] = visibility;
                queue.Enqueue(reply);
            }
        }
    }

    internal static string RuleReason(PostFilterRule rule)
    {
        string pattern = rule.Pattern.Length > 60 ? rule.Pattern[..59].TrimEnd() + "…" : rule.Pattern;
        return Texts.T("postFilter.reason.rule", pattern);
    }

    // The text rules of one field: a single SearchValues pass finds whether any "contains" pattern occurs at all; only then
    // (and for "exact") the single rules are checked, so a post that matches nothing costs one scan per field.
    private readonly struct FieldRules
    {
        private readonly CompiledRule[] _contains;
        private readonly CompiledRule[] _exact;
        private readonly SearchValues<string>? _any;

        internal FieldRules(CompiledRule[] rules, PostFilterField field)
        {
            _contains = rules.Where(rule => rule.Rule.Field == field && rule.Rule.Match == PostFilterMatch.Contains).ToArray();
            _exact = rules.Where(rule => rule.Rule.Field == field && rule.Rule.Match == PostFilterMatch.Exact).ToArray();
            _any = _contains.Length > 1
                ? SearchValues.Create(_contains.Select(rule => rule.Rule.Pattern).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparison.OrdinalIgnoreCase)
                : null;
        }

        internal bool IsEmpty => _contains.Length == 0 && _exact.Length == 0;

        internal void Match(string value, List<CompiledRule> hits)
        {
            if (_contains.Length > 0 && (_any == null || value.AsSpan().IndexOfAny(_any) >= 0))
                foreach (CompiledRule rule in _contains)
                    if (value.Contains(rule.Rule.Pattern, StringComparison.OrdinalIgnoreCase)) hits.Add(rule);
            if (_exact.Length > 0)
            {
                ReadOnlySpan<char> trimmed = value.AsSpan().Trim();
                foreach (CompiledRule rule in _exact)
                    if (trimmed.Equals(rule.Rule.Pattern.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase)) hits.Add(rule);
            }
        }
    }
}
