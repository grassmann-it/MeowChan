using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MeowChan.Archive;

namespace MeowChan.Booru;

internal sealed record BooruContentSourceBlacklist(
    [property: JsonRequired] string SourceId,
    [property: JsonRequired] string InstanceId,
    [property: JsonRequired] string Blacklist);

internal sealed record BooruContentSettings(
    [property: JsonRequired] BooruPostClaimRating? MaximumRating,
    [property: JsonRequired] string GlobalBlacklist,
    [property: JsonRequired] IReadOnlyList<BooruContentSourceBlacklist> SourceBlacklists);

internal enum BooruContentReason
{
    Allowed,
    PageBlacklistException,
    ChoiceRequired,
    ProfileUnavailable,
    InvalidPostIdentity,
    UnknownRating,
    AboveRatingCeiling,
    InvalidRules,
    InsufficientMetadata,
    Blacklisted
}

internal sealed record BooruContentRuleLocation(int Line, string? SourceId, string? InstanceId);

internal sealed record BooruContentDecision(long Revision, BooruContentReason Reason, BooruContentRuleLocation? Rule = null)
{
    internal bool IsAllowed => Reason is BooruContentReason.Allowed or BooruContentReason.PageBlacklistException;
}

/// <summary>Immutable decision for one profile revision, shared by all consumers of provider claims.</summary>
internal sealed class BooruContentFilter
{
    private readonly BooruPostClaimRating? _maximumRating;
    private readonly bool _profileAvailable;
    private readonly BooruContentBlacklist _global;
    private readonly Dictionary<(string SourceId, string InstanceId), BooruContentBlacklist> _sources = new();

    internal BooruContentFilter(long revision, BooruContentSettings settings, bool profileAvailable = true)
    {
        Revision = revision;
        _profileAvailable = profileAvailable;
        _maximumRating = settings.MaximumRating;
        _global = BooruContentBlacklist.Parse(settings.GlobalBlacklist);
        foreach (BooruContentSourceBlacklist rules in settings.SourceBlacklists)
            _sources.Add((rules.SourceId, rules.InstanceId), BooruContentBlacklist.Parse(rules.Blacklist, rules));
    }

    internal long Revision { get; }

    internal BooruContentRuleLocation? FirstInvalidRule =>
        _global.Error ?? _sources.Values.Select(rules => rules.Error).FirstOrDefault(error => error is not null);

    internal BooruContentDecision Evaluate(BooruPostClaim post) => EvaluateCore(post, bypassBlacklist: false);

    /// <summary>The caller binds the exception to its current page and discards it when that page changes.</summary>
    internal BooruContentDecision EvaluateForPage(BooruPostClaim post, long blacklistExceptionRevision) =>
        EvaluateCore(post, bypassBlacklist: blacklistExceptionRevision == Revision);

    private BooruContentDecision EvaluateCore(BooruPostClaim post, bool bypassBlacklist)
    {
        if (!_profileAvailable) return new(Revision, BooruContentReason.ProfileUnavailable);
        if (!_maximumRating.HasValue) return new(Revision, BooruContentReason.ChoiceRequired);
        BooruPostKey identity = post.Identity;
        if (!identity.IsValid) return new(Revision, BooruContentReason.InvalidPostIdentity);

        if (!post.Fields.HasFlag(BooruPostClaimPresence.Rating) || !IsKnownRating(post.Rating) ||
            (post.RawRating is not null && ReadRawRating(post.SourceId, post.RawRating) != post.Rating))
            return new(Revision, BooruContentReason.UnknownRating);

        if (!IsKnownRating(_maximumRating.Value) || post.Rating > _maximumRating.Value)
            return new(Revision, BooruContentReason.AboveRatingCeiling);

        _sources.TryGetValue((identity.SourceId, identity.InstanceId), out BooruContentBlacklist? source);
        BooruContentRuleLocation? invalidRule = _global.Error ?? source?.Error;
        if (invalidRule is not null) return new(Revision, BooruContentReason.InvalidRules, invalidRule);

        if (_global.Rules.Count == 0 && (source is null || source.Rules.Count == 0))
            return new(Revision, BooruContentReason.Allowed);

        HashSet<string>? tags = post.Fields.HasFlag(BooruPostClaimPresence.Tags) && post.Tags is not null
            ? new(post.Tags.Select(tag => tag.Name), StringComparer.OrdinalIgnoreCase) : null;
        BooruContentRuleLocation? matched = null;
        BooruContentRuleLocation? uncertain = null;
        IEnumerable<BooruContentBlacklist.Rule> rules = source is null ? _global.Rules : _global.Rules.Concat(source.Rules);
        foreach (BooruContentBlacklist.Rule rule in rules)
        {
            switch (rule.Matches(post, tags))
            {
                case true when !bypassBlacklist:
                    return new(Revision, BooruContentReason.Blacklisted, rule.Location);
                case true:
                    matched ??= rule.Location;
                    break;
                case null:
                    uncertain ??= rule.Location;
                    break;
            }
        }

        if (uncertain is not null) return new(Revision, BooruContentReason.InsufficientMetadata, uncertain);
        return matched is null ? new(Revision, BooruContentReason.Allowed)
            : new(Revision, BooruContentReason.PageBlacklistException, matched);
    }

    internal static bool IsKnownRating(BooruPostClaimRating rating) =>
        rating is >= BooruPostClaimRating.General and <= BooruPostClaimRating.Explicit;

    private static BooruPostClaimRating ReadRawRating(string sourceId, string raw) => (sourceId, raw) switch
    {
        ("danbooru", "g") or ("e621" or "e926", "s" or "safe") => BooruPostClaimRating.General,
        ("danbooru", "s") => BooruPostClaimRating.Sensitive,
        ("danbooru" or "e621" or "e926", "q") => BooruPostClaimRating.Questionable,
        ("danbooru" or "e621" or "e926", "e") => BooruPostClaimRating.Explicit,
        _ => BooruPostClaimRating.Unknown
    };
}
