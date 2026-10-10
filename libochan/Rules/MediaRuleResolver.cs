using System;
using System.Collections.Generic;
using System.Linq;

namespace oChan.Rules;

/// <summary>Pure field-by-field resolution; this class does not persist first-candidate decisions.</summary>
public sealed class MediaRuleResolver
{
    private readonly MediaRuleSet _ruleSet;
    private readonly Dictionary<MediaRuleScope, MediaRule> _rules;

    public MediaRuleResolver(MediaRuleSet ruleSet)
    {
        _ruleSet = ruleSet ?? throw new ArgumentNullException(nameof(ruleSet));
        _rules = ruleSet.Rules.ToDictionary(rule => rule.Scope);
    }

    public EffectiveMediaRule Resolve(ResourceId resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Resolve(resource.Thread == null
            ? MediaRuleScope.ForBoard(resource.Source, resource.Board)
            : MediaRuleScope.ForThread(resource.Source, resource.Board, resource.Thread));
    }

    /// <summary>Also resolves a global or source editor preview without inventing a board identity.</summary>
    public EffectiveMediaRule Resolve(MediaRuleScope target)
    {
        ArgumentNullException.ThrowIfNull(target);
        MediaTypeSelection types = MediaTypeSelection.All;
        MediaSizeLimit size = MediaSizeLimit.Unlimited;
        MediaRuleOrigin typesOrigin = new MediaRuleOrigin(null, _ruleSet.Revision);
        MediaRuleOrigin sizeOrigin = new MediaRuleOrigin(null, _ruleSet.Revision);
        List<MediaRuleScope> scopes = new List<MediaRuleScope>
        {
            MediaRuleScope.Global
        };
        if (target.Kind >= MediaRuleScopeKind.Source) scopes.Add(MediaRuleScope.ForSource(target.Source!));
        if (target.Kind >= MediaRuleScopeKind.Board) scopes.Add(MediaRuleScope.ForBoard(target.Source!, target.Board!));
        if (target.Kind == MediaRuleScopeKind.Thread) scopes.Add(target);
        foreach (MediaRuleScope scope in scopes)
        {
            if (!_rules.TryGetValue(scope, out MediaRule? rule) || !rule.Enabled) continue;
            if (rule.Types.Mode != MediaTypeSelectionMode.Inherit)
            {
                types = rule.Types;
                typesOrigin = new MediaRuleOrigin(scope, _ruleSet.Revision);
            }
            if (rule.SizeLimit.Mode != MediaSizeLimitMode.Inherit)
            {
                size = rule.SizeLimit;
                sizeOrigin = new MediaRuleOrigin(scope, _ruleSet.Revision);
            }
        }
        return new EffectiveMediaRule(_ruleSet.Revision, types, size, typesOrigin, sizeOrigin);
    }

    public MediaSelectionResult Evaluate(ResourceId resource, string? extension, long? sizeBytes)
    {
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        EffectiveMediaRule effective = Resolve(resource);
        string? normalized = string.IsNullOrWhiteSpace(extension) ? null : MediaTypeSelection.NormalizeExtension(extension);
        if (effective.Types.Mode == MediaTypeSelectionMode.Selected &&
            (normalized == null || !effective.Types.Extensions.Contains(normalized, StringComparer.Ordinal)))
            return new MediaSelectionResult(MediaSelectionOutcome.Excluded, MediaSelectionReason.TypeExcluded,
                effective, normalized, sizeBytes);
        if (effective.SizeLimit.Mode == MediaSizeLimitMode.Limited && sizeBytes > effective.SizeLimit.Bytes)
            return new MediaSelectionResult(MediaSelectionOutcome.Excluded, MediaSelectionReason.SizeExceeded,
                effective, normalized, sizeBytes);
        return new MediaSelectionResult(MediaSelectionOutcome.Selected,
            sizeBytes.HasValue ? MediaSelectionReason.Allowed : MediaSelectionReason.AllowedUnknownSize,
            effective, normalized, sizeBytes);
    }
}
