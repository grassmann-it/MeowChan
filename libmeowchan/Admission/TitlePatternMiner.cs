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
    private sealed record Candidate(string RuleText, List<Title> Members);

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
            while (code.Success && code.Value == ownCode) code = code.NextMatch();
            if (code.Success) Group(slash, code.Value, title);
            else
            {
                string key = Edges().Replace(Whitespace().Replace(Counters().Replace(title.Clean, "${thread} "), " "), string.Empty);
                if (key.Length > 0) Group(stripped, key, title);
            }
        }
        foreach (var group in stripped.Values.Where(group => group.Count == 1))
        {
            Title title = group[0];
            string[] words = Words().Matches(title.Clean).Take(3).Select(word => word.Value).ToArray();
            if (words.Length == 3) Group(prefixes, string.Join(' ', words), title);
        }

        Candidate[] candidates = slash.Where(group => group.Value.Count >= minThreads).Select(group => new Candidate(group.Key, group.Value))
            .Concat(stripped.Values.Concat(prefixes.Values).Where(members => members.Count >= minThreads)
                .Select(members => new Candidate(CommonPhrase(members, token), members)).Where(candidate => candidate.RuleText.Length > 0))
            .ToArray();
        if (candidates.Length == 0) return new(rows.Count, now, []);

        // Count literal matches in a shared pass, including overlaps between different rules.
        var index = new LiteralIndex(candidates.Select(candidate => candidate.RuleText));
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Title title in titles.Values)
        {
            token.ThrowIfCancellationRequested();
            foreach (string rule in index.Find(title.Row.Title)) totals[rule] = totals.GetValueOrDefault(rule) + 1;
        }

        var suggestions = new List<BoardTitleSuggestion>();
        foreach (Candidate candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            int hits = candidate.Members.Count(title => Matches(title.Row.Title, candidate.RuleText));
            int outside = totals.GetValueOrDefault(candidate.RuleText) - hits;
            if (hits * 10 < candidate.Members.Count * 9 || outside * 4 > titles.Count - candidate.Members.Count) continue;
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

    private static string CommonPhrase(List<Title> members, CancellationToken token)
    {
        // Every shared phrase occurs in this title. Try longer word runs first, with
        // stable tie breaks even when the archive returns its rows in a different order.
        Title shortest = members.OrderBy(title => title.Clean.Length).ThenBy(title => title.Clean, StringComparer.Ordinal).First();
        string text = shortest.Clean;
        MatchCollection words = Words().Matches(text);
        for (int count = words.Count; count > 0; count--)
        {
            token.ThrowIfCancellationRequested();
            string best = string.Empty;
            for (int first = 0; first <= words.Count - count; first++)
            {
                Match last = words[first + count - 1];
                string phrase = text[words[first].Index..(last.Index + last.Length)];
                if (phrase.Length < 4 || phrase.Length < best.Length || phrase.EnumerateRunes().Any(Rune.IsDigit)
                    || !members.All(title => title.Clean.Contains(phrase, StringComparison.Ordinal))) continue;
                if (phrase.Length > best.Length || string.CompareOrdinal(phrase, best) < 0) best = phrase;
            }
            if (best.Length > 0) return best;
        }
        return string.Empty;
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

        public HashSet<string> Find(string title)
        {
            var matches = new HashSet<string>(StringComparer.Ordinal);
            Node node = _root;
            foreach (Rune rune in title.EnumerateRunes())
            {
                node = Advance(node, Rune.ToUpperInvariant(rune));
                for (Node? suffix = node.Rules == null ? node.Output : node; suffix != null; suffix = suffix.Output)
                    foreach (string rule in suffix.Rules!)
                        if (!matches.Contains(rule) && Matches(title, rule)) matches.Add(rule);
            }
            return matches;
        }
    }
}
