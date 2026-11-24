using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MeowChan.Admission;

internal sealed record BoardTitleHistory(string Thread, string Title, DateTimeOffset FirstSeenAt, DateTimeOffset? LastCapturedAt);
internal sealed record BoardTitleSuggestion(string RuleText, int Count, DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt, TimeSpan Rhythm, string ExampleTitle);
internal sealed record BoardTitleSuggestions(int Scanned, DateTimeOffset AsOf, IReadOnlyList<BoardTitleSuggestion> Items);

internal static partial class TitlePatternMiner
{
    private sealed record Title(BoardTitleHistory Row, string Clean);
    private sealed record Candidate(string RuleText, List<Title> Members, bool IsSingleWord = false);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"/[a-z0-9+]{1,12}/")]
    private static partial Regex SlashCode();
    // Keep the word "thread" in titles such as "Recent Photo Thread #123".
    [GeneratedRegex(@"(?<!\d)\d{1,4}[-./]\d{1,2}(?:[-./]\d{1,4})?(?!\d)|#\s*\d+|\b(?:no|nr|vol|part|pt|ep|episode|edition|week|day)\.?\s*#?\d+|\b(?<thread>thread)\.?\s*#?\d+|\b\d+\b")]
    private static partial Regex Counters();
    [GeneratedRegex(@"^[\p{P}\p{S}\s]+|[\p{P}\p{S}\s]+$")]
    private static partial Regex Edges();
    [GeneratedRegex(@"[\p{L}\p{M}\p{Nd}]+")]
    private static partial Regex Words();

    internal static BoardTitleSuggestions Mine(IReadOnlyList<BoardTitleHistory> rows, string board, DateTimeOffset now,
        int minThreads, int limit, CancellationToken token = default)
    {
        var titles = new Dictionary<string, Title>(StringComparer.Ordinal);
        var slash = new Dictionary<string, List<Title>>(StringComparer.Ordinal);
        var stripped = new Dictionary<string, List<Title>>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, List<Title>>(StringComparer.Ordinal);
        string ownCode = "/" + board.ToLowerInvariant() + "/";
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            string clean = Whitespace().Replace(row.Title.Normalize(NormalizationForm.FormKC), " ").Trim().ToLowerInvariant();
            if (clean.Length < 3 || clean.Equals(row.Thread, StringComparison.OrdinalIgnoreCase)) continue;
            if (!titles.TryGetValue(row.Thread, out var old) || row.FirstSeenAt > old.Row.FirstSeenAt
                || row.FirstSeenAt == old.Row.FirstSeenAt && string.CompareOrdinal(row.Title, old.Row.Title) < 0)
                titles[row.Thread] = new(row, clean);
        }
        foreach (Title title in titles.Values)
        {
            token.ThrowIfCancellationRequested();
            Match code = SlashCode().Match(title.Clean);
            bool hasOwnCode = code.Success && code.Value == ownCode;
            while (code.Success && code.Value == ownCode) code = code.NextMatch();
            if (code.Success) Group(slash, code.Value, title);
            else
            {
                string clean = hasOwnCode
                    ? Whitespace().Replace(title.Clean.Replace(ownCode, " ", StringComparison.Ordinal), " ").Trim()
                    : title.Clean;
                string key = Edges().Replace(Whitespace().Replace(Counters().Replace(clean, "${thread} "), " "), string.Empty);
                if (key.Length > 0) Group(stripped, key, title with { Clean = clean });
            }
        }
        foreach ((string key, List<Title> group) in stripped)
        {
            token.ThrowIfCancellationRequested();
            if (group.Count != 1) continue;
            string[] words = Words().Matches(key).Take(3).Select(word => word.Value).ToArray();
            if (words.Length == 3) Group(prefixes, string.Join(' ', words), group[0]);
        }

        Candidate[] candidates = slash.Where(group => group.Value.Count >= minThreads).Select(group => new Candidate(group.Key, group.Value))
            .Concat(stripped.Values.Concat(prefixes.Values).Where(members => members.Count >= minThreads)
                .Select(members => CommonPhrase(members, token)).OfType<Candidate>())
            .ToArray();
        if (candidates.Length == 0) return new(rows.Count, now, []);

        // Count literal matches in a shared pass, including overlaps between different rules.
        var index = new LiteralIndex(candidates.Select(candidate => candidate.RuleText));
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Title title in titles.Values)
        {
            token.ThrowIfCancellationRequested();
            foreach (string rule in index.Find(title.Row.Title, token)) totals[rule] = totals.GetValueOrDefault(rule) + 1;
        }

        var suggestions = new List<BoardTitleSuggestion>();
        foreach (Candidate candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            int hits = candidate.Members.Count(title => Matches(title.Row.Title, candidate.RuleText));
            int total = totals.GetValueOrDefault(candidate.RuleText);
            int outside = total - hits;
            if (hits * 10 < candidate.Members.Count * 9 || outside * 4 > titles.Count - candidate.Members.Count
                || candidate.IsSingleWord && total * 4 > titles.Count) continue;
            suggestions.Add(Summarize(candidate));
        }
        return new(rows.Count, now, suggestions.OrderByDescending(item => item.LastSeenAt).ThenByDescending(item => item.Count)
            .ThenBy(item => item.RuleText, StringComparer.Ordinal).DistinctBy(item => item.RuleText, StringComparer.Ordinal)
            .Take(limit).ToArray());
    }

    private static void Group(Dictionary<string, List<Title>> groups, string key, Title title)
    {
        if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
        members.Add(title);
    }

    private static Candidate? CommonPhrase(List<Title> members, CancellationToken token)
    {
        Title shortest = members.OrderBy(title => title.Clean.Length).ThenBy(title => title.Clean, StringComparer.Ordinal).First();
        ReadOnlySpan<char> text = shortest.Clean.AsSpan();
        var words = new List<(int Start, int End)>();
        foreach (var word in Words().EnumerateMatches(text))
        {
            token.ThrowIfCancellationRequested();
            words.Add((word.Index, word.Index + word.Length));
        }
        if (words.Count == 0) return null;

        // For each start position, find the longest literal prefix shared by every title.
        int[] shared = new int[text.Length];
        for (int index = 0; index < shared.Length; index++) shared[index] = shared.Length - index;
        foreach (Title member in members)
        {
            token.ThrowIfCancellationRequested();
            if (!member.Clean.AsSpan().Contains(text, StringComparison.Ordinal))
                new ReversedSubstrings(member.Clean, token).Intersect(text, shared, token);
        }

        ReadOnlySpan<char> best = [];
        int bestWordCount = 0;
        int nextDigit = 0;
        int endWord = -1;
        for (int first = 0; first < words.Count; first++)
        {
            token.ThrowIfCancellationRequested();
            int start = words[first].Start;
            if (nextDigit <= start)
            {
                nextDigit = start;
                while (nextDigit < text.Length && !char.IsDigit(shortest.Clean, nextDigit))
                {
                    token.ThrowIfCancellationRequested();
                    nextDigit++;
                }
            }

            // Both end bounds are nondecreasing as the start advances.
            int endLimit = Math.Min(start + shared[start], nextDigit);
            while (endWord + 1 < words.Count && words[endWord + 1].End <= endLimit) endWord++;
            if (endWord < first) continue;
            int wordCount = endWord - first + 1;
            ReadOnlySpan<char> phrase = text[start..words[endWord].End];
            if (phrase.Length < 4 || wordCount < bestWordCount) continue;
            if (wordCount > bestWordCount || phrase.Length > best.Length
                || phrase.Length == best.Length && phrase.SequenceCompareTo(best) < 0)
            {
                best = phrase;
                bestWordCount = wordCount;
            }
        }
        return best.IsEmpty ? null : new(best.ToString(), members, bestWordCount == 1);
    }

    // A suffix automaton over a reversed title turns longest-prefix comparisons into
    // one right-to-left scan. Transitions compare the original UTF-16 code units.
    private sealed class ReversedSubstrings
    {
        private sealed class State(int length, State? suffix)
        {
            public int Length { get; } = length;
            public State? Suffix { get; set; } = suffix;
            public Dictionary<char, State> Next { get; } = new();
        }

        private readonly State _root = new(0, null);

        public ReversedSubstrings(string title, CancellationToken token)
        {
            State tail = _root;
            for (int index = title.Length - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested();
                char key = title[index];
                State next = new(tail.Length + 1, _root);
                State? prefix = tail;
                while (prefix != null && prefix.Next.TryAdd(key, next))
                {
                    token.ThrowIfCancellationRequested();
                    prefix = prefix.Suffix;
                }
                if (prefix != null)
                {
                    State existing = prefix.Next[key];
                    next.Suffix = prefix.Length + 1 == existing.Length
                        ? existing : Split(prefix, key, existing, token);
                }
                tail = next;
            }
        }

        private static State Split(State prefix, char key, State existing, CancellationToken token)
        {
            State clone = new(prefix.Length + 1, existing.Suffix);
            foreach ((char value, State target) in existing.Next)
            {
                token.ThrowIfCancellationRequested();
                clone.Next.Add(value, target);
            }
            for (State? state = prefix; state != null && state.Next.GetValueOrDefault(key) == existing; state = state.Suffix)
            {
                token.ThrowIfCancellationRequested();
                state.Next[key] = clone;
            }
            existing.Suffix = clone;
            return clone;
        }

        public void Intersect(ReadOnlySpan<char> text, int[] shared, CancellationToken token)
        {
            State state = _root;
            int length = 0;
            for (int index = text.Length - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested();
                char key = text[index];
                State? next;
                while (!state.Next.TryGetValue(key, out next) && state.Suffix is { } suffix)
                {
                    token.ThrowIfCancellationRequested();
                    state = suffix;
                    length = state.Length;
                }
                if (next != null)
                {
                    state = next;
                    length++;
                }
                else length = 0;
                shared[index] = Math.Min(shared[index], length);
            }
        }
    }

    private static bool Matches(string title, string rule) => title.Contains(rule, StringComparison.OrdinalIgnoreCase);

    private static BoardTitleSuggestion Summarize(Candidate candidate)
    {
        BoardTitleHistory[] rows = candidate.Members.Select(title => title.Row).OrderBy(row => row.FirstSeenAt)
            .ThenByDescending(row => row.Thread, StringComparer.Ordinal).ToArray();
        long[] gaps = Enumerable.Range(1, rows.Length - 1)
            .Select(i => (rows[i].FirstSeenAt - rows[i - 1].FirstSeenAt).Ticks).Order().ToArray();
        long median = gaps.Length == 0 ? 0 : (gaps[(gaps.Length - 1) / 2] + gaps[gaps.Length / 2]) / 2;
        return new(candidate.RuleText, rows.Length, rows[0].FirstSeenAt, rows[^1].FirstSeenAt, TimeSpan.FromTicks(median), rows[^1].Title);
    }

    // Failure links share work between patterns. A final ordinal comparison makes the
    // count use the same semantics as BoardAdmissionPolicy, even for non-ASCII case folds.
    private sealed class LiteralIndex
    {
        private sealed class Node
        {
            public Dictionary<Rune, Node> Next { get; } = new();
            public Node? Failure { get; set; }
            public Node? Output { get; set; }
            public List<string>? Rules { get; set; }
        }

        private readonly Node _root = new();

        public LiteralIndex(IEnumerable<string> rules)
        {
            foreach (string rule in rules.Distinct(StringComparer.Ordinal))
            {
                Node node = _root;
                foreach (Rune rune in rule.EnumerateRunes())
                {
                    Rune key = Rune.ToUpperInvariant(rune);
                    if (!node.Next.TryGetValue(key, out Node? child)) node.Next[key] = child = new();
                    node = child;
                }
                (node.Rules ??= []).Add(rule);
            }
            var pending = new Queue<Node>();
            foreach (Node child in _root.Next.Values)
            {
                child.Failure = _root;
                pending.Enqueue(child);
            }
            while (pending.TryDequeue(out Node? node))
            {
                foreach ((Rune key, Node child) in node.Next)
                {
                    child.Failure = Advance(node.Failure!, key);
                    child.Output = child.Failure.Rules == null ? child.Failure.Output : child.Failure;
                    pending.Enqueue(child);
                }
            }
        }

        private Node Advance(Node node, Rune key)
        {
            while (true)
            {
                if (node.Next.TryGetValue(key, out Node? next)) return next;
                if (node.Failure == null) return _root;
                node = node.Failure;
            }
        }

        public HashSet<string> Find(string title, CancellationToken token)
        {
            var matches = new HashSet<string>(StringComparer.Ordinal);
            Node node = _root;
            foreach (Rune rune in title.EnumerateRunes())
            {
                token.ThrowIfCancellationRequested();
                node = Advance(node, Rune.ToUpperInvariant(rune));
                for (Node? suffix = node.Rules == null ? node.Output : node; suffix != null; suffix = suffix.Output)
                    foreach (string rule in suffix.Rules!)
                        if (!matches.Contains(rule) && Matches(title, rule)) matches.Add(rule);
            }
            return matches;
        }
    }
}
