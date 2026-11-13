using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MeowChan.Archive;

namespace MeowChan.Persistence;

/// <summary>What a post filter rule looks at. Stage 1 has no regular expressions.</summary>
internal enum PostFilterField { Text, Subject, Filename, Md5 }

/// <summary>How the pattern is compared (always without regard to case). <c>regex</c> is reserved for stage 2 and kept as an unsupported rule.</summary>
internal enum PostFilterMatch { Contains, Exact }

/// <summary>What a hit does. The precedence is Hide over Stub over Highlight (decided by the matcher, not by the store).</summary>
internal enum PostFilterAction { Hide, Stub, Highlight }

/// <summary>What an individually hidden post shows.</summary>
internal enum HiddenPostsAction { Stub, Hide }

/// <summary>
/// The user's input for one rule. <see cref="Source"/> and <see cref="Board"/> are both empty for a rule that applies
/// everywhere; a board needs its source. Nothing here is trusted: the store validates and normalizes it.
/// </summary>
internal sealed record PostFilterRuleDraft(PostFilterField Field, PostFilterMatch Match, string Pattern, PostFilterAction Action,
    bool Replies, string? Source = null, string? Board = null, bool Enabled = true);

/// <summary>
/// One stored rule. A rule this version cannot interpret (an unknown <c>match</c> such as <c>regex</c>, an unknown field or
/// action, or unknown members) has <see cref="UnsupportedReason"/> set and keeps its exact JSON in
/// <see cref="RawDefinition"/>: it is never applied, cannot be edited, and is written back unchanged.
/// </summary>
internal sealed record PostFilterRule(Guid Id, bool Enabled, PostFilterField Field, PostFilterMatch Match, string Pattern,
    PostFilterAction Action, bool Replies, string? Source, string? Board, string? RawDefinition = null, string? UnsupportedReason = null)
{
    public bool CanApply => UnsupportedReason == null;

    /// <summary>The duplicate identity: same field, match, scope and pattern (case-insensitive; MD5 compared by its 16 bytes).</summary>
    internal string DuplicateKey => PostFilterRules.DuplicateKey(Field, Match, Pattern, Source, Board);
}

/// <summary>A post the user hid by hand. <see cref="Replies"/> hides its replies as well.</summary>
internal sealed record HiddenPost(ResourceId Thread, string Post, bool Replies, DateTimeOffset Added)
{
    internal (ResourceId Thread, string Post) Key => (Thread, Post);
}

internal enum PostFilterStoreState { Ready, Unsupported, RecoveryAvailable, Failed }

/// <summary>An immutable view of the store. Only <see cref="PostFilterStoreState.Ready"/> may be written.</summary>
internal sealed record PostFilterSnapshot(long Revision, HiddenPostsAction HiddenPostsAction, IReadOnlyList<PostFilterRule> Rules,
    IReadOnlyList<HiddenPost> HiddenPosts, PostFilterStoreState State, string? Error = null)
{
    public bool CanWrite => State == PostFilterStoreState.Ready;
    internal static PostFilterSnapshot Empty(PostFilterStoreState state, string? error = null) =>
        new(0, HiddenPostsAction.Stub, Array.Empty<PostFilterRule>(), Array.Empty<HiddenPost>(), state, error);
}

internal sealed class PostFilterRevisionException(PostFilterSnapshot current)
    : IOException("Post filters changed. Review the current list and explicitly retry.")
{
    public PostFilterSnapshot Current { get; } = current;
}

/// <summary>A rule or a hidden post that is already stored; nothing was written. The desktop says "This rule already exists.".</summary>
internal sealed class PostFilterDuplicateException(bool rule, Guid? existingRuleId = null)
    : ArgumentException(rule ? "This post filter rule already exists." : "This post is already hidden.")
{
    public bool IsRule { get; } = rule;
    public Guid? ExistingRuleId { get; } = existingRuleId;
}

/// <summary>Validation and normalization shared by the store and its callers.</summary>
internal static class PostFilterRules
{
    /// <summary>A pattern holds at most this many Unicode code points (fixed, documented, not a setting).</summary>
    internal const int MaximumPatternCodepoints = 512;
    internal const int MaximumIdentityLength = 256;

    /// <summary>The 16 bytes of an MD5 given as 32 hex digits or as 24 base64 characters (with padding); false for anything else.</summary>
    internal static bool TryParseMd5(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text == null) return false;
        string trimmed = text.Trim();
        try
        {
            if (trimmed.Length == 32 && trimmed.All(Uri.IsHexDigit)) { bytes = Convert.FromHexString(trimmed); return true; }
            if (trimmed.Length == 24 && trimmed.EndsWith("==", StringComparison.Ordinal))
            {
                byte[] decoded = Convert.FromBase64String(trimmed);
                if (decoded.Length != 16) return false;
                bytes = decoded;
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }

    /// <summary>The rule as the store keeps it: trimmed scope, a validated pattern, MD5 as 32 lower-case hex digits with the exact comparison.</summary>
    internal static PostFilterRuleDraft Normalize(PostFilterRuleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Enum.IsDefined(draft.Field) || !Enum.IsDefined(draft.Match) || !Enum.IsDefined(draft.Action))
            throw new ArgumentException("The post filter rule has an unknown field, match or action.");
        string? source = Scope(draft.Source, "source");
        string? board = Scope(draft.Board, "board");
        if (board != null && source == null) throw new ArgumentException("A board scope needs its source.");
        string pattern = draft.Pattern ?? throw new ArgumentException("A post filter rule needs a pattern.");
        PostFilterMatch match = draft.Match;
        if (draft.Field == PostFilterField.Md5)
        {
            if (!TryParseMd5(pattern, out byte[] md5)) throw new ArgumentException("The MD5 must be 32 hex digits or 24 base64 characters.");
            pattern = Convert.ToHexString(md5).ToLowerInvariant();
            match = PostFilterMatch.Exact;
        }
        else ValidatePattern(pattern);
        return draft with { Pattern = pattern, Match = match, Source = source, Board = board };
    }

    internal static void ValidatePattern(string pattern)
    {
        int count = 0;
        foreach (Rune rune in pattern.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) throw new ArgumentException("The pattern contains a control character.");
            count++;
        }
        if (count < 1 || count > MaximumPatternCodepoints || string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException($"The pattern must contain 1–{MaximumPatternCodepoints} characters and not only white space.");
    }

    private static string? Scope(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdentityLength || value.Any(char.IsControl))
            throw new ArgumentException("The " + name + " scope must be non-empty and contain no control characters.");
        return value;
    }

    internal static string DuplicateKey(PostFilterField field, PostFilterMatch match, string pattern, string? source, string? board)
    {
        string text = pattern;
        if (field == PostFilterField.Md5 && TryParseMd5(pattern, out byte[] md5)) text = Convert.ToHexString(md5);
        return string.Join('\u0001', field.ToString(), match.ToString(), source ?? "", board ?? "",
            text.ToUpperInvariant());
    }

    internal static PostFilterRule Create(Guid id, PostFilterRuleDraft normalized) =>
        new(id, normalized.Enabled, normalized.Field, normalized.Match, normalized.Pattern, normalized.Action, normalized.Replies,
            normalized.Source, normalized.Board);

    internal static void ValidateHiddenPost(ResourceId thread, string post)
    {
        ArchiveValidation.ValidateId(thread);
        if (thread.Thread == null) throw new ArgumentException("A hidden post needs a thread.");
        if (string.IsNullOrWhiteSpace(post) || post.Length > MaximumIdentityLength || post.Any(char.IsControl))
            throw new ArgumentException("A hidden post needs a valid post identity.");
    }

    internal static string FormatAdded(DateTimeOffset added) => added.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
