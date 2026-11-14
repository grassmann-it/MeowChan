using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Why a tag is suggested, for the tooltip and the screen-reader name. The text is localized by the surface (<see cref="TagSuggestionReasons.LocalizationKey"/>).</summary>
internal enum TagSuggestionReason
{
    /// <summary>Q5: a file with the same bytes carries the tag.</summary>
    SameFile,
    /// <summary>Q1: the thread itself carries the tag.</summary>
    ThreadTag,
    /// <summary>Q1: other files of the thread carry the tag.</summary>
    ThreadMedia,
    /// <summary>Q8: the provider's source tags of this medium contain the name.</summary>
    SourceTag,
    /// <summary>Q3: used recently in this session.</summary>
    Recent,
    /// <summary>Q2: a neighbouring file of the current list carries the tag.</summary>
    Neighbour,
    /// <summary>Q6: a word of the file name matches the tag name.</summary>
    FileName,
    /// <summary>Q7: a word of the thread title or subject matches the tag name.</summary>
    ThreadTitle,
    /// <summary>Q7: a /board/ marker in the title or subject (the tag may not exist yet).</summary>
    SlashMarker,
    /// <summary>Q4: used often overall.</summary>
    Frequent
}

internal static class TagSuggestionReasons
{
    /// <summary>The language-pack key TP1 defines for the reason text (≤ 24 characters in English), e.g. <c>tags.quick.reason.sameFile</c>.</summary>
    internal static string LocalizationKey(TagSuggestionReason reason) => "tags.quick.reason." + reason switch
    {
        TagSuggestionReason.SameFile => "sameFile",
        TagSuggestionReason.ThreadTag => "threadTag",
        TagSuggestionReason.ThreadMedia => "threadMedia",
        TagSuggestionReason.SourceTag => "sourceTag",
        TagSuggestionReason.Recent => "recent",
        TagSuggestionReason.Neighbour => "neighbour",
        TagSuggestionReason.FileName => "fileName",
        TagSuggestionReason.ThreadTitle => "threadTitle",
        TagSuggestionReason.SlashMarker => "slashMarker",
        TagSuggestionReason.Frequent => "frequent",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}

/// <summary>What choosing the suggestion does to the vocabulary.</summary>
internal enum TagSuggestionOrigin
{
    /// <summary>An existing own tag (<see cref="RankedTagSuggestion.Id"/> is set).</summary>
    Own,
    /// <summary>A provider source tag without an own tag of that name: choosing it creates an own tag.</summary>
    Source,
    /// <summary>A /board/ marker from the title without an own tag: choosing it creates an own tag.</summary>
    New
}

/// <summary>Points one source added to a candidate.</summary>
internal readonly record struct TagSuggestionContribution(TagSuggestionReason Reason, double Points);

/// <summary>
/// One ranked suggestion. <see cref="Score"/> is the additive context score; <see cref="Contributions"/> lists the
/// non-zero sources, best first (the first one is the reason to show). <see cref="TypedMatch"/> says how typed text
/// matched the name and is null when nothing was typed. Id is null for a name that is not an own tag yet.
/// </summary>
internal sealed record RankedTagSuggestion(PrivateTagName Name, TagId? Id, TagSuggestionOrigin Origin, int Uses, double Score,
    IReadOnlyList<TagSuggestionContribution> Contributions, TagMatchKind? TypedMatch = null)
{
    /// <summary>The reasons in contribution order, strongest first.</summary>
    internal IEnumerable<TagSuggestionReason> Reasons => Contributions.Select(item => item.Reason);

    /// <summary>The reason with the highest contribution (the tooltip text); the frequency reason when nothing else applies.</summary>
    internal TagSuggestionReason PrimaryReason => Contributions.Count > 0 ? Contributions[0].Reason : TagSuggestionReason.Frequent;
}

/// <summary>Own-tag suggestions and provider source-tag suggestions for typed text, each in display order.</summary>
internal sealed record TypedTagSuggestions(IReadOnlyList<RankedTagSuggestion> Own, IReadOnlyList<RankedTagSuggestion> Source)
{
    internal static TypedTagSuggestions Empty { get; } = new(Array.Empty<RankedTagSuggestion>(), Array.Empty<RankedTagSuggestion>());
}

/// <summary>
/// Limits of the context suggestions, each clamped into its documented range (the settings of the tag surface bind to
/// these; out-of-range input never throws, it is clamped). Ranges are (default, min, max).
/// </summary>
internal sealed class TagContextOptions
{
    /// <summary>Suggestions shown for an empty field.</summary>
    internal const int DefaultSuggestionCount = 9, MinimumSuggestionCount = 3, MaximumSuggestionCount = 18;
    /// <summary>Recently used tags considered; 0 switches the "recent" source off.</summary>
    internal const int DefaultRecentCount = 50, MinimumRecentCount = 0, MaximumRecentCount = 500;
    /// <summary>Neighbour media examined around the current one; the scan alternates outwards and stops at this many.</summary>
    internal const int DefaultNeighbourScan = 2000, MinimumNeighbourScan = 100, MaximumNeighbourScan = 20_000;
    /// <summary>Words taken from a file name (and from a title); 0 switches word matching off (/board/ markers stay).</summary>
    internal const int DefaultFilenameTokens = 8, MinimumFilenameTokens = 0, MaximumFilenameTokens = 32;
    /// <summary>Provider source tags of one medium considered; 0 switches the source off.</summary>
    internal const int DefaultSourceTagsPerMedium = 24, MinimumSourceTagsPerMedium = 0, MaximumSourceTagsPerMedium = 200;

    internal static TagContextOptions Default { get; } = new();

    internal TagContextOptions(int suggestionCount = DefaultSuggestionCount, int recentCount = DefaultRecentCount,
        int neighbourScan = DefaultNeighbourScan, int filenameTokens = DefaultFilenameTokens, int sourceTagsPerMedium = DefaultSourceTagsPerMedium,
        int slashSuggestionCount = SlashTagSuggestions.DefaultSuggestionCount)
    {
        SlashSuggestionCount = Math.Clamp(slashSuggestionCount, SlashTagSuggestions.MinimumSuggestionCount, SlashTagSuggestions.MaximumSuggestionCount);
        SuggestionCount = Math.Clamp(suggestionCount, MinimumSuggestionCount, MaximumSuggestionCount);
        RecentCount = Math.Clamp(recentCount, MinimumRecentCount, MaximumRecentCount);
        NeighbourScan = Math.Clamp(neighbourScan, MinimumNeighbourScan, MaximumNeighbourScan);
        FilenameTokens = Math.Clamp(filenameTokens, MinimumFilenameTokens, MaximumFilenameTokens);
        SourceTagsPerMedium = Math.Clamp(sourceTagsPerMedium, MinimumSourceTagsPerMedium, MaximumSourceTagsPerMedium);
    }

    // Independent of the overall quick-tag display/ranking count.
    internal int SlashSuggestionCount { get; }
    internal int SuggestionCount { get; }
    internal int RecentCount { get; }
    internal int NeighbourScan { get; }
    internal int FilenameTokens { get; }
    internal int SourceTagsPerMedium { get; }
}

/// <summary>
/// The list the current medium sits in (gallery entries, visible media tiles): how many, where the current one is, and
/// the tag subject of each by index (null for a file that cannot carry tags). The delegate may be called from a worker
/// thread and must only read data that no longer changes.
/// </summary>
internal readonly record struct TagNeighbourhood(int Count, int Index, Func<int, MediaTagSubject?>? SubjectAt)
{
    internal static TagNeighbourhood None => default;

    internal static TagNeighbourhood From(IReadOnlyList<MediaTagSubject?> subjects, int index)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        return new TagNeighbourhood(subjects.Count, index, at => subjects[at]);
    }
}

/// <summary>
/// What is known about the medium the tags are for. Every part is optional; a missing part just contributes nothing.
/// Immutable input: build it on the UI thread, rank on any thread.
/// </summary>
internal sealed record TagContext
{
    internal static TagContext Empty { get; } = new();

    /// <summary>The thread of the medium (Q1); null for a loose file outside any thread.</summary>
    internal ResourceId? Thread { get; init; }
    /// <summary>SHA-256 of the file bytes (Q5), hex.</summary>
    internal string? Sha256 { get; init; }
    /// <summary>The original file name (Q6).</summary>
    internal string? FileName { get; init; }
    /// <summary>Thread title, first-post subject and the like (Q7).</summary>
    internal IReadOnlyList<string?> TitleTexts { get; init; } = Array.Empty<string?>();
    /// <summary>Neighbours in the current list (Q2).</summary>
    internal TagNeighbourhood Neighbours { get; init; }
    /// <summary>Provider source tag names of this medium (Q8), most relevant first; only a Danbooru medium has any.</summary>
    internal IReadOnlyList<string> SourceTags { get; init; } = Array.Empty<string>();
    /// <summary>Recently used tags in this session, most recent first (Q3); see <see cref="TagRecentList"/>.</summary>
    internal IReadOnlyList<TagId> Recent { get; init; } = Array.Empty<TagId>();
}

/// <summary>
/// Session list of recently used tags, most recent first, for the "recent" source. Thread-safe; a snapshot is an
/// immutable array, so ranking on a worker never sees a change. Keeps at most <see cref="Capacity"/> tags
/// (<see cref="TagContextOptions.MinimumRecentCount"/>…<see cref="TagContextOptions.MaximumRecentCount"/>; 0 keeps nothing).
/// Tags that no longer exist are simply ignored by the ranking.
/// </summary>
internal sealed class TagRecentList
{
    private readonly object _gate = new();
    private TagId[] _items = Array.Empty<TagId>();
    private int _capacity;

    internal TagRecentList(int capacity = TagContextOptions.DefaultRecentCount) => _capacity = Clamp(capacity);

    private static int Clamp(int capacity) => Math.Clamp(capacity, TagContextOptions.MinimumRecentCount, TagContextOptions.MaximumRecentCount);

    internal int Capacity
    {
        get { lock (_gate) return _capacity; }
        set
        {
            lock (_gate)
            {
                _capacity = Clamp(value);
                if (_items.Length > _capacity) _items = _items.AsSpan(0, _capacity).ToArray();
            }
        }
    }

    /// <summary>Marks the tags as used now; the last one passed becomes the most recent. Repeats move to the front.</summary>
    internal void Touch(IEnumerable<TagId> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        lock (_gate)
        {
            if (_capacity == 0) return;
            List<TagId> next = new(_items);
            foreach (TagId tag in tags)
            {
                next.Remove(tag);
                next.Insert(0, tag);
            }
            if (next.Count > _capacity) next.RemoveRange(_capacity, next.Count - _capacity);
            _items = next.ToArray();
        }
    }

    internal void Touch(TagId tag) => Touch(new[] { tag });

    internal void Remove(TagId tag)
    {
        lock (_gate) _items = _items.Where(item => item != tag).ToArray();
    }

    internal void Clear()
    {
        lock (_gate) _items = Array.Empty<TagId>();
    }

    /// <summary>The current list, most recent first. The array is never modified afterwards.</summary>
    internal IReadOnlyList<TagId> Snapshot()
    {
        lock (_gate) return _items;
    }
}

/// <summary>
/// The offline context score for tag suggestions (plan "Schnell-Taggen" §2.2): for each candidate every source adds its
/// best contribution, sources add up, ties break by uses and then name. Sources: same bytes elsewhere (6), thread (5,
/// or 4 plus one per three files, at most 6), source tags of the medium (4 with an own tag of that name, else 2), recent
/// use (4 for ranks 1–3, 3 up to 10, then 1), neighbours (3 within ±5, else 2), word of the file name and word of the
/// title (3 exact, 2 prefix; a /board/ marker 2 when new), and overall use as base noise (log2(uses + 1), at most 3).
/// Assigned tags of the target are never suggested. Nothing here assigns or creates a tag, makes a network request or
/// touches the UI; inputs are immutable snapshots, so any thread may call it, and it honours the cancellation token.
/// </summary>
internal static class TagContextSuggestions
{
    /// <summary>Neighbours at most this far from the current medium score <see cref="NearNeighbourPoints"/>, farther ones <see cref="FarNeighbourPoints"/>.</summary>
    internal const int NearNeighbourRadius = 5;
    internal const double SameFilePoints = 6, ThreadTagPoints = 5, ThreadMediaBasePoints = 4, ThreadMediaBonusMaximum = 2,
        ThreadMediaFilesPerBonus = 3, SourceOwnPoints = 4, SourceNewPoints = 2, NearNeighbourPoints = 3, FarNeighbourPoints = 2,
        TokenExactPoints = 3, TokenPrefixPoints = 2, SlashNewPoints = 2, FrequencyMaximum = 3;
    /// <summary>Technical caps of the word matching (documented, not settings): prefix names looked at and kept per word.</summary>
    internal const int PrefixScanPerWord = 256, PrefixHitsPerWord = 4, ContainedMaximumScalars = 16;

    private enum Slot { SameFile, Thread, Source, Recent, Neighbour, FileName, Title, Frequent }
    private const int SlotCount = 8;

    /// <summary>
    /// The best <paramref name="limit"/> (default <see cref="TagContextOptions.SuggestionCount"/>) suggestions for an
    /// empty field, strongest first. Only candidates with a score above zero appear.
    /// </summary>
    /// <param name="snapshot">The published tag snapshot the field shows.</param>
    /// <param name="assigned">Tags the target already carries; never suggested.</param>
    /// <param name="sources">Provider source tags of the catalog (Q9); only used to match words of the file name or title.</param>
    internal static IReadOnlyList<RankedTagSuggestion> Rank(PrivateTagStoreSnapshot snapshot, TagContext? context = null,
        IReadOnlySet<TagId>? assigned = null, TagContextOptions? options = null, TagSuggestionIndex? sources = null,
        int? limit = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= TagContextOptions.Default;
        int take = Math.Max(0, limit ?? options.SuggestionCount);
        if (take == 0) return Array.Empty<RankedTagSuggestion>();
        TagContextIndex index = TagContextIndex.For(snapshot, cancellation);
        IReadOnlyDictionary<TagId, int> uses = TagSuggestionIndex.UsesOf(snapshot);
        Dictionary<string, Candidate> found = Gather(index, uses, context ?? TagContext.Empty, assigned, options, sources, cancellation);
        // Overall use is base noise for every tag, so the most used unassigned tags join the field even without context.
        // Enough of them are fetched that the context candidates among them cannot crowd the free ones out.
        foreach (TagSuggestion item in TagSuggestionIndex.For(snapshot).MostUsed(assigned, take + found.Count))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!found.ContainsKey(item.Name.Key))
                found[item.Name.Key] = new Candidate(item.Name, item.Id, TagSuggestionOrigin.Own, item.Uses);
        }
        TopK<RankedTagSuggestion> top = new(take, Compare);
        foreach (Candidate candidate in found.Values)
        {
            if (candidate.Origin == TagSuggestionOrigin.Own) AddFrequency(candidate);
            if (candidate.Total > 0) top.Offer(candidate.ToRanked(null));
        }
        return top.ToList();
    }

    /// <summary>
    /// Matches for typed text in the existing group order (exact, prefix, word start, anywhere); inside a group the
    /// context score decides before the number of uses. Own tags and provider source tags come as separate lists
    /// (<paramref name="ownLimit"/>, <paramref name="sourceLimit"/>); the caller adds its "new tag" row. A tag
    /// without context is ranked exactly like <see cref="TagSuggestionIndex.Match"/> ranks it.
    /// </summary>
    internal static TypedTagSuggestions RankTyped(PrivateTagStoreSnapshot snapshot, string? text, TagContext? context = null,
        IReadOnlySet<TagId>? assigned = null, TagContextOptions? options = null, TagSuggestionIndex? sources = null,
        int ownLimit = TagSuggestionIndex.OwnLimit, int sourceLimit = TagSuggestionIndex.SourceLimit,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (TagSuggestionIndex.QueryKey(text) == null) return TypedTagSuggestions.Empty;
        options ??= TagContextOptions.Default;
        TagContextIndex index = TagContextIndex.For(snapshot, cancellation);
        TagSuggestionIndex own = TagSuggestionIndex.For(snapshot);
        Dictionary<string, Candidate> found = Gather(index, TagSuggestionIndex.UsesOf(snapshot), context ?? TagContext.Empty,
            assigned, options, sources, cancellation);

        TopK<RankedTagSuggestion> mine = new(Math.Max(0, ownLimit), CompareTyped);
        foreach (TagSuggestion item in own.Match(text, assigned))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!found.TryGetValue(item.Name.Key, out Candidate? candidate)) candidate = new Candidate(item.Name, item.Id, TagSuggestionOrigin.Own, item.Uses);
            AddFrequency(candidate);
            mine.Offer(candidate.ToRanked(item.Kind));
        }
        TopK<RankedTagSuggestion> provided = new(Math.Max(0, sourceLimit), CompareTyped);
        if (sources != null && sourceLimit > 0)
        {
            // A source tag without context scores 0, so matches without context keep the index's order (kind, then uses,
            // then name: the tie-break of CompareTyped). The best sourceLimit of them plus one slot per context candidate
            // are enough; the context candidates themselves are looked up by name below. So a keystroke never scans and
            // copies the whole catalog (the early stop of Match), and the result is the same as an unbounded match.
            int matchLimit = sourceLimit > int.MaxValue - found.Count ? int.MaxValue : sourceLimit + found.Count;
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (TagSuggestion item in sources.Match(text, null, matchLimit, own.Keys))
            {
                cancellation.ThrowIfCancellationRequested();
                seen.Add(item.Name.Key);
                if (!found.TryGetValue(item.Name.Key, out Candidate? candidate))
                    candidate = new Candidate(item.Name, null, TagSuggestionOrigin.Source, item.Uses);
                provided.Offer(candidate.ToRanked(item.Kind));
            }
            foreach (Candidate candidate in found.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                if (seen.Contains(candidate.Name.Key) || own.Keys.Contains(candidate.Name.Key)) continue;
                if (sources.MatchKey(text, candidate.Name.Key) is { } item) provided.Offer(candidate.ToRanked(item.Kind));
            }
        }
        return new TypedTagSuggestions(mine.ToList(), provided.ToList());
    }

    /// <summary>Score descending, then uses descending, then name (case-insensitive, then ordinal key): the plan's tie-break.</summary>
    internal static int Compare(RankedTagSuggestion left, RankedTagSuggestion right)
    {
        int byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0) return byScore;
        int byUses = right.Uses.CompareTo(left.Uses);
        if (byUses != 0) return byUses;
        int byName = string.Compare(left.Name.Display, right.Name.Display, StringComparison.InvariantCultureIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(left.Name.Key, right.Name.Key);
    }

    private static int CompareTyped(RankedTagSuggestion left, RankedTagSuggestion right)
    {
        int byKind = (left.TypedMatch ?? TagMatchKind.Substring).CompareTo(right.TypedMatch ?? TagMatchKind.Substring);
        return byKind != 0 ? byKind : Compare(left, right);
    }

    private static void AddFrequency(Candidate candidate)
    {
        if (candidate.Uses > 0 && candidate.Origin == TagSuggestionOrigin.Own)
            candidate.Offer(Slot.Frequent, Math.Min(FrequencyMaximum, Math.Log2(candidate.Uses + 1)), TagSuggestionReason.Frequent);
    }

    private static Dictionary<string, Candidate> Gather(TagContextIndex index, IReadOnlyDictionary<TagId, int> uses, TagContext context,
        IReadOnlySet<TagId>? assigned, TagContextOptions options, TagSuggestionIndex? sources, CancellationToken cancellation)
    {
        Dictionary<string, Candidate> found = new(StringComparer.Ordinal);
        Candidate? OwnCandidate(TagId id)
        {
            if (assigned?.Contains(id) == true || index.Find(id) is not { } tag) return null;
            if (!found.TryGetValue(tag.Name.Key, out Candidate? candidate))
                found[tag.Name.Key] = candidate = new Candidate(tag.Name, tag.Id, TagSuggestionOrigin.Own, uses.GetValueOrDefault(tag.Id));
            return candidate;
        }

        // Q5: the same bytes carry the tag somewhere else.
        foreach (TagId id in index.TagsOfHash(context.Sha256))
            OwnCandidate(id)?.Offer(Slot.SameFile, SameFilePoints, TagSuggestionReason.SameFile);

        // Q1: the thread, and other files of the thread.
        foreach (TagId id in index.ThreadTags(context.Thread))
            OwnCandidate(id)?.Offer(Slot.Thread, ThreadTagPoints, TagSuggestionReason.ThreadTag);
        foreach ((TagId id, int files) in index.ThreadMediaTags(context.Thread))
            OwnCandidate(id)?.Offer(Slot.Thread, ThreadMediaBasePoints + Math.Min(ThreadMediaBonusMaximum, files / (int)ThreadMediaFilesPerBonus),
                TagSuggestionReason.ThreadMedia);
        cancellation.ThrowIfCancellationRequested();

        // Q8: source tags of this medium; an own tag of the name scores higher, a missing one is a source candidate.
        if (options.SourceTagsPerMedium > 0)
        {
            int taken = 0;
            foreach (string raw in context.SourceTags)
            {
                if (taken >= options.SourceTagsPerMedium) break;
                taken++;
                if (!PrivateTagName.TryCreate(raw, out PrivateTagName? name, out _) || name == null) continue;
                if (index.FindByKey(name.Key) is { } tag) OwnCandidate(tag.Id)?.Offer(Slot.Source, SourceOwnPoints, TagSuggestionReason.SourceTag);
                else GetOrAdd(found, name, TagSuggestionOrigin.Source).Offer(Slot.Source, SourceNewPoints, TagSuggestionReason.SourceTag);
            }
        }

        // Q3: recent use by rank.
        int rank = 0;
        foreach (TagId id in context.Recent)
        {
            if (rank >= options.RecentCount) break;
            rank++;
            OwnCandidate(id)?.Offer(Slot.Recent, rank <= 3 ? 4 : rank <= 10 ? 3 : 1, TagSuggestionReason.Recent);
        }

        // Q2: neighbours, alternating outwards from the current medium.
        GatherNeighbours(index, context.Neighbours, options.NeighbourScan, OwnCandidate, cancellation);

        // Q6 and Q7: words of the file name and of the title against existing names only.
        if (options.FilenameTokens > 0)
        {
            TagTokens name = TagContextTokenizer.Tokenize(context.FileName, options.FilenameTokens, isFileName: true, cancellation);
            MatchTokens(index, sources, name, Slot.FileName, TagSuggestionReason.FileName, found, assigned, uses, cancellation);
            foreach (string? title in context.TitleTexts)
            {
                TagTokens words = TagContextTokenizer.Tokenize(title, options.FilenameTokens, isFileName: false, cancellation);
                MatchTokens(index, sources, words, Slot.Title, TagSuggestionReason.ThreadTitle, found, assigned, uses, cancellation);
            }
        }
        // /board/ markers do not need words; an existing tag of the name scores like an exact word, a new name less.
        if (context.TitleTexts.Count > 0)
        {
            foreach (PrivateTagName marker in SlashTagSuggestions.Detect(options.SlashSuggestionCount, context.TitleTexts.ToArray()))
            {
                if (index.FindByKey(marker.Key) is { } tag) OwnCandidate(tag.Id)?.Offer(Slot.Title, TokenExactPoints, TagSuggestionReason.SlashMarker);
                else GetOrAdd(found, marker, TagSuggestionOrigin.New).Offer(Slot.Title, SlashNewPoints, TagSuggestionReason.SlashMarker);
            }
        }
        return found;
    }

    private static Candidate GetOrAdd(Dictionary<string, Candidate> found, PrivateTagName name, TagSuggestionOrigin origin, int uses = 0)
    {
        if (!found.TryGetValue(name.Key, out Candidate? candidate)) found[name.Key] = candidate = new Candidate(name, null, origin, uses);
        return candidate;
    }

    private static void GatherNeighbours(TagContextIndex index, TagNeighbourhood neighbours, int scan,
        Func<TagId, Candidate?> ownCandidate, CancellationToken cancellation)
    {
        if (neighbours.SubjectAt is not { } at || neighbours.Count <= 1 || scan <= 0) return;
        int count = neighbours.Count, center = Math.Clamp(neighbours.Index, 0, count - 1), examined = 0;
        for (int distance = 1; examined < scan && (center - distance >= 0 || center + distance < count); distance++)
        {
            double points = distance <= NearNeighbourRadius ? NearNeighbourPoints : FarNeighbourPoints;
            for (int side = 0; side < 2 && examined < scan; side++)
            {
                int position = side == 0 ? center - distance : center + distance;
                if (position < 0 || position >= count) continue;
                if ((++examined & 255) == 0) cancellation.ThrowIfCancellationRequested();
                MediaTagSubject? subject = at(position);
                if (subject == null) continue;
                foreach (TagId id in index.TagsOf(subject)) ownCandidate(id)?.Offer(Slot.Neighbour, points, TagSuggestionReason.Neighbour);
            }
        }
    }

    private static void MatchTokens(TagContextIndex index, TagSuggestionIndex? sources, TagTokens tokens, Slot slot,
        TagSuggestionReason reason, Dictionary<string, Candidate> found, IReadOnlySet<TagId>? assigned,
        IReadOnlyDictionary<TagId, int> uses, CancellationToken cancellation)
    {
        if (tokens.Words.Count == 0 && tokens.Phrases.Count == 0) return;
        TagNameLookup? provided = sources == null ? null : SourceLookups.GetValue(sources, BuildSourceLookup);
        List<TagNameEntry> hits = new();

        void Offer(TagNameLookup lookup, TagNameEntry entry, double points)
        {
            if (lookup == index.Names)
            {
                if (entry.Id is not { } id || assigned?.Contains(id) == true) return;
                GetOrAdd(found, entry.Name, TagSuggestionOrigin.Own, uses.GetValueOrDefault(id)).WithId(id).Offer(slot, points, reason);
            }
            // A source name that is an own tag already is that own tag; it must not show up twice.
            else if (index.FindByKey(entry.Key) == null) GetOrAdd(found, entry.Name, TagSuggestionOrigin.Source, entry.Weight).Offer(slot, points, reason);
        }

        void Exact(string key, double points)
        {
            if (index.Names.TryExact(key, out TagNameEntry own)) Offer(index.Names, own, points);
            else if (provided != null && provided.TryExact(key, out TagNameEntry source)) Offer(provided, source, points);
        }

        void Prefix(TagNameLookup lookup, string key)
        {
            hits.Clear();
            lookup.Prefix(key, PrefixScanPerWord, hits);
            // Of the names scanned the best used few count; the scan order is by name, so the cut is deterministic.
            foreach (TagNameEntry hit in hits.OrderByDescending(item => item.Weight).ThenBy(item => item.Key, StringComparer.Ordinal).Take(PrefixHitsPerWord))
                Offer(lookup, hit, TokenPrefixPoints);
        }

        foreach (string word in tokens.Words)
        {
            cancellation.ThrowIfCancellationRequested();
            string key = TagContextTokenizer.Key(word);
            Exact(key, TokenExactPoints);
            Prefix(index.Names, key);
            if (provided != null) Prefix(provided, key);
            if (TagContextTokenizer.IsCjk(word)) Contained(word, Exact);
        }
        foreach (string phrase in tokens.Phrases)
        {
            cancellation.ThrowIfCancellationRequested();
            string key = TagContextTokenizer.Key(phrase);
            Exact(key, TokenExactPoints);
            // Tags written with a space ("blue sky") match the same two words.
            Exact(TagContextTokenizer.Key(phrase.Replace('_', ' ')), TokenExactPoints);
        }
    }

    /// <summary>
    /// A written-together run (Japanese, Chinese, Korean) contains tag names without a separator, so every part of at
    /// least two characters (up to <see cref="ContainedMaximumScalars"/>) is looked up as a name. A run that is one tag counts as exact elsewhere.
    /// </summary>
    private static void Contained(string word, Action<string, double> exact)
    {
        // Offsets of every scalar start, then the end.
        int[] starts = new int[word.Length + 1];
        int scalars = 0;
        for (int offset = 0; offset < word.Length; scalars++)
        {
            starts[scalars] = offset;
            offset += char.IsHighSurrogate(word[offset]) && offset + 1 < word.Length ? 2 : 1;
        }
        starts[scalars] = word.Length;
        for (int from = 0; from < scalars; from++)
            for (int length = 2; length <= ContainedMaximumScalars && from + length <= scalars; length++)
            {
                if (from == 0 && length == scalars) continue; // the whole run was looked up as an exact word
                exact(TagContextTokenizer.Key(word.Substring(starts[from], starts[from + length] - starts[from])), TokenPrefixPoints);
            }
    }

    private static readonly ConditionalWeakTable<TagSuggestionIndex, TagNameLookup> SourceLookups = new();

    private static TagNameLookup BuildSourceLookup(TagSuggestionIndex index) =>
        new(index.MostUsed(null, int.MaxValue).Select(item => new TagNameEntry(item.Name.Key, item.Name, null, item.Uses)));

    /// <summary>One suggestion being scored: the best points per source slot, summed.</summary>
    private sealed class Candidate
    {
        private readonly double[] _points = new double[SlotCount];
        private readonly TagSuggestionReason[] _reasons = new TagSuggestionReason[SlotCount];

        internal Candidate(PrivateTagName name, TagId? id, TagSuggestionOrigin origin, int uses)
        {
            Name = name;
            Id = id;
            Origin = origin;
            Uses = uses;
        }

        internal PrivateTagName Name { get; }
        internal TagId? Id { get; private set; }
        internal TagSuggestionOrigin Origin { get; }
        internal int Uses { get; }

        internal double Total => _points.Sum();

        /// <summary>Only the best contribution of a source counts; a later, smaller one is dropped.</summary>
        internal void Offer(Slot slot, double points, TagSuggestionReason reason)
        {
            if (points <= _points[(int)slot]) return;
            _points[(int)slot] = points;
            _reasons[(int)slot] = reason;
        }

        internal Candidate WithId(TagId id)
        {
            Id ??= id;
            return this;
        }

        internal RankedTagSuggestion ToRanked(TagMatchKind? typed)
        {
            List<TagSuggestionContribution> contributions = new();
            for (int slot = 0; slot < SlotCount; slot++)
                if (_points[slot] > 0) contributions.Add(new TagSuggestionContribution(_reasons[slot], _points[slot]));
            // Strongest contribution first; equal points follow the reason order, so the tooltip is stable.
            contributions.Sort(static (left, right) =>
            {
                int byPoints = right.Points.CompareTo(left.Points);
                return byPoints != 0 ? byPoints : left.Reason.CompareTo(right.Reason);
            });
            return new RankedTagSuggestion(Name, Id, Origin, Uses, Total, contributions.AsReadOnly(), typed);
        }
    }

    /// <summary>Keeps the best <c>capacity</c> items in order with one insertion per offer; no sort of the whole input.</summary>
    private sealed class TopK<T>
    {
        private readonly int _capacity;
        private readonly Comparison<T> _compare;
        private readonly List<T> _items;

        internal TopK(int capacity, Comparison<T> compare)
        {
            _capacity = capacity;
            _compare = compare;
            _items = new List<T>(Math.Min(capacity, 64));
        }

        internal void Offer(T item)
        {
            if (_capacity <= 0) return;
            if (_items.Count >= _capacity && _compare(item, _items[^1]) >= 0) return;
            int low = 0, high = _items.Count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (_compare(_items[middle], item) <= 0) low = middle + 1; else high = middle;
            }
            _items.Insert(low, item);
            if (_items.Count > _capacity) _items.RemoveAt(_items.Count - 1);
        }

        internal IReadOnlyList<T> ToList() => _items.AsReadOnly();
    }
}
