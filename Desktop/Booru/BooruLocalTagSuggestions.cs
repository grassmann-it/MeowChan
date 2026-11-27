using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>Provider tag names and distinct local post counts. A workbench issues one lookup at a time.</summary>
internal sealed class BooruLocalTagSuggestions
{
    internal readonly record struct Scope(string SourceId, string InstanceId, BooruSourceProfile? Profile, BooruContentFilter? Filter);
    internal readonly record struct Suggestion(string Name, BooruPostClaimTagCategory Category, int Posts);

    private IReadOnlyList<BooruPostRecord>? _posts;
    private readonly Dictionary<(string Source, string Instance), (Scope Scope, Suggestion[] Index)> _indexes = new();

    internal Task<Suggestion[]> FindAsync(Func<BooruCatalogSnapshot?> read, Scope scope, string prefix, int limit) =>
        limit <= Config.BooruLocalTagSuggestionMinimum
            ? Task.FromResult(Array.Empty<Suggestion>())
            : Task.Run(() => Find(read()?.Posts, scope, prefix, limit));

    private Suggestion[] Find(IReadOnlyList<BooruPostRecord>? posts, Scope scope, string prefix, int limit)
    {
        if (!ReferenceEquals(_posts, posts))
        {
            _indexes.Clear();
            _posts = posts;
        }
        if (posts is null) return [];
        var key = (scope.SourceId, scope.InstanceId);
        if (!_indexes.TryGetValue(key, out var entry) || entry.Scope != scope)
        {
            entry = (scope, Build(posts, scope));
            _indexes[key] = entry;
        }
        return Match(entry.Index, prefix, limit);
    }

    private readonly record struct CountedTag(BooruPostClaimTagCategory Category, int Posts, int LastPost);

    internal static Suggestion[] Build(IReadOnlyList<BooruPostRecord> posts, Scope scope)
    {
        Dictionary<string, CountedTag> counts = new(StringComparer.Ordinal);
        for (int i = 0; i < posts.Count; i++)
        {
            BooruPostRecord record = posts[i];
            BooruPostKey identity = record.Identity;
            if (identity.SourceId != scope.SourceId || identity.InstanceId != scope.InstanceId
                || !record.Post.Fields.HasFlag(BooruPostClaimPresence.Tags)
                || scope.Filter?.Evaluate(record.Post).IsAllowed == false) continue;

            foreach (BooruPostClaimTag tag in record.Post.Tags)
            {
                if (!counts.TryGetValue(tag.Name, out CountedTag count))
                {
                    bool searchable = BooruSourceTagSearch.Plan([tag.Name], scope.SourceId, scope.Profile).IsValid;
                    counts.Add(tag.Name, new(tag.Category, searchable ? 1 : 0, i));
                }
                else if (count.Posts != 0 && count.LastPost != i)
                {
                    BooruPostClaimTagCategory category = count.Category == BooruPostClaimTagCategory.Unknown ? tag.Category : count.Category;
                    counts[tag.Name] = new(category, count.Posts + 1, i);
                }
            }
        }
        return counts.Where(pair => pair.Value.Posts > 0)
            .Select(pair => new Suggestion(pair.Key, pair.Value.Category, pair.Value.Posts))
            .OrderByDescending(tag => tag.Posts).ThenBy(tag => tag.Name, StringComparer.Ordinal).ToArray();
    }

    internal static Suggestion[] Match(Suggestion[] index, string prefix, int limit) =>
        index.Where(tag => tag.Name.Length > prefix.Length && tag.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, Config.BooruLocalTagSuggestionMinimum, Config.BooruLocalTagSuggestionMaximum)).ToArray();

    /// <summary>The final, non-empty token. Preserve whitespace and the exclusion sign in the draft.</summary>
    internal static int PrefixStart(string draft)
    {
        int start = draft.Length;
        while (start > 0 && !char.IsWhiteSpace(draft[start - 1])) start--;
        if (start < draft.Length && draft[start] == '-') start++;
        return start;
    }
}
