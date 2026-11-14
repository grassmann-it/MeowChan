using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Puts the stamp set in front of any suggestion source (PLAN-POWER-TAGGEN PT5): the stamp tags the target does not carry yet come
/// first, in stamp order, with the reason <see cref="QuickTagReason.Stamp"/>; then the inner source fills the rest of the strip.
/// The total never exceeds <see cref="QuickTagSuggestionRequest.Limit"/> (the suggestion count), so a long stamp can push every
/// other suggestion out but never makes the strip longer. A stamp tag the target already carries is a chip, not a suggestion.
/// </summary>
internal sealed class QuickTagStampSource(IQuickTagSuggestionSource inner, QuickTagStamp stamp) : IQuickTagSuggestionSource
{
    /// <summary>The score the stamp entries carry: far above any additive context score, so the entries read as pinned.</summary>
    internal const double StampScore = 1000;

    private readonly IQuickTagSuggestionSource _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly QuickTagStamp _stamp = stamp ?? throw new ArgumentNullException(nameof(stamp));

    internal IQuickTagSuggestionSource Inner => _inner;

    public IReadOnlyList<QuickTagSuggestion> Suggest(QuickTagSuggestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit <= 0) return Array.Empty<QuickTagSuggestion>();
        IReadOnlyList<PrivateTagDefinition> tags = _stamp.Resolve(request.Snapshot);
        List<QuickTagSuggestion> result = new();
        HashSet<string> keys = new(StringComparer.Ordinal);
        HashSet<TagId> excluded = new(request.Excluded);
        if (tags.Count > 0)
        {
            IReadOnlyDictionary<TagId, int> uses = TagSuggestionIndex.UsesOf(request.Snapshot);
            foreach (PrivateTagDefinition tag in tags)
            {
                if (result.Count >= request.Limit) break;
                // Carried: it is an assigned chip now. Still excluded from the inner source below, so it does not come back as "often used".
                excluded.Add(tag.Id);
                if (request.Excluded.Contains(tag.Id) || !keys.Add(tag.Name.Key)) continue;
                result.Add(new QuickTagSuggestion(tag.Name, tag.Id, StampScore, QuickTagReason.Stamp, uses.GetValueOrDefault(tag.Id)));
            }
        }
        if (result.Count >= request.Limit) return result;
        foreach (QuickTagSuggestion item in _inner.Suggest(request with { Excluded = excluded }) ?? Array.Empty<QuickTagSuggestion>())
        {
            if (item == null || !keys.Add(item.Name.Key)) continue;
            result.Add(item);
            if (result.Count >= request.Limit) break;
        }
        return result;
    }
}
