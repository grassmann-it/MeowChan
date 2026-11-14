using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MeowChan;

/// <summary>A span of characters in the text as it is shown (not in the folded text), for highlighting.</summary>
internal readonly record struct SettingsTextRange(int Start, int Length);

/// <summary>One searchable settings entry. Texts are the ones shown in the current UI language;
/// <see cref="EnglishTexts"/> are their English originals (label first), so English words find a setting in any language.</summary>
internal sealed record SettingsSearchDocument(string Id, string Label, string Hint, string Group, string? Key,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> EnglishTexts);

internal sealed record SettingsSearchHit(SettingsSearchDocument Document, double Score,
    IReadOnlyList<SettingsTextRange> LabelRanges, IReadOnlyList<SettingsTextRange> HintRanges);

/// <summary>Documents prepared for matching: folded once, searched many times.</summary>
internal sealed class SettingsSearchIndex
{
    internal readonly List<SettingsSearchMatcher.Entry> Entries = new();
    public int Count => Entries.Count;
    public IEnumerable<SettingsSearchDocument> Documents => Entries.Select(entry => entry.Document);
}

/// <summary>
/// The matcher behind the settings search. Pure and offline: no UI types, no network, no package.
/// Matching works on words. A query word matches a word of a field as, best first: the whole word; its start;
/// a part inside it; its letters in order from the same first letter ("dwnld" for "downloads"); or with one or two
/// typos ("bandwith"). Accents and case never matter ("grosse" finds "Größe"). Every query word must match somewhere
/// in an entry: label, English label, key, alias (synonym), group or hint. Label matches rank above the rest.
/// </summary>
internal static class SettingsSearchMatcher
{
    // Quality knobs of the ranking, not user limits.
    private const double MinimumWordScore = 20;
    private const int MaximumQueryWords = 8;
    private const int MaximumQueryLength = 100;
    private const int MaximumTypoWordLength = 40;

    internal enum Kind { Label, EnglishLabel, Key, Alias, Group, Hint, EnglishHint }

    internal readonly record struct Word(int Start, int Length);

    internal sealed class Field
    {
        public Kind Kind;
        public double Weight;
        public bool Fuzzy;     // subsequence and typo matching: only for short, name-like fields
        public string Folded = string.Empty;
        public int[]? Map;     // folded index -> original index; kept for the fields that are highlighted
        public Word[] Words = Array.Empty<Word>();
    }

    internal sealed class Entry
    {
        public SettingsSearchDocument Document = null!;
        public int Order;
        public string FoldedLabel = string.Empty;
        public Field[] Fields = Array.Empty<Field>();
    }

    public static SettingsSearchIndex Build(IEnumerable<SettingsSearchDocument> documents)
    {
        var index = new SettingsSearchIndex();
        foreach (SettingsSearchDocument document in documents)
        {
            var fields = new List<Field>();
            Field label = MakeField(Kind.Label, 1.0, true, document.Label, keepMap: true);
            fields.Add(label);
            if (document.EnglishTexts.Count > 0 && !string.IsNullOrWhiteSpace(document.EnglishTexts[0]))
                fields.Add(MakeField(Kind.EnglishLabel, 0.9, true, document.EnglishTexts[0], keepMap: false));
            if (!string.IsNullOrWhiteSpace(document.Key))
                fields.Add(MakeKeyField(document.Key!));
            if (document.Aliases.Count > 0)
                fields.Add(MakeField(Kind.Alias, 0.7, true, string.Join(' ', document.Aliases), keepMap: false));
            if (!string.IsNullOrWhiteSpace(document.Group))
                fields.Add(MakeField(Kind.Group, 0.45, false, document.Group, keepMap: false));
            if (!string.IsNullOrWhiteSpace(document.Hint))
                fields.Add(MakeField(Kind.Hint, 0.4, false, document.Hint, keepMap: true));
            if (document.EnglishTexts.Count > 1)
                fields.Add(MakeField(Kind.EnglishHint, 0.35, false, string.Join(' ', document.EnglishTexts.Skip(1)), keepMap: false));
            index.Entries.Add(new Entry
            {
                Document = document,
                Order = index.Entries.Count,
                FoldedLabel = label.Folded,
                Fields = fields.ToArray()
            });
        }
        return index;
    }

    public static IReadOnlyList<SettingsSearchHit> Search(SettingsSearchIndex index, string? query)
    {
        string[] tokens = Tokenize(query);
        if (tokens.Length == 0) return Array.Empty<SettingsSearchHit>();
        string phrase = string.Join(' ', tokens);
        var hits = new List<(SettingsSearchHit Hit, int Order)>();
        foreach (Entry entry in index.Entries)
            if (Evaluate(entry, tokens, phrase) is { } hit) hits.Add((hit, entry.Order));
        return hits.OrderByDescending(item => item.Hit.Score).ThenBy(item => item.Order).Select(item => item.Hit).ToArray();
    }

    /// <summary>Lower case without accents: "Größe" becomes "grosse", "Café" becomes "cafe". A map to the original index is optional.</summary>
    internal static string Fold(string? text, List<int>? map = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 128) { builder.Append(char.ToLowerInvariant(c)); map?.Add(i); continue; }
            char lower = char.ToLowerInvariant(c);
            if (Accents.TryGetValue(lower, out string? plain))
            {
                builder.Append(plain);
                if (map != null) foreach (char _ in plain) map.Add(i);
                continue;
            }
            // Combining marks (a decomposed "ä") vanish; everything else stays as written.
            if (char.GetUnicodeCategory(lower) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(lower);
            map?.Add(i);
        }
        return builder.ToString();
    }

    /// <summary>"ClipboardUrlLimit" to "clipboard url limit clipboardurllimit": the key as words and as one word.</summary>
    internal static string FoldKey(string key)
    {
        var spaced = new StringBuilder(key.Length + 8);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i > 0 && (char.IsUpper(c) && !char.IsUpper(key[i - 1]) || char.IsDigit(c) != char.IsDigit(key[i - 1]) && char.IsLetterOrDigit(key[i - 1])))
                spaced.Append(' ');
            spaced.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        string compact = Fold(new string(key.Where(char.IsLetterOrDigit).ToArray()));
        return Fold(spaced.ToString()) + " " + compact;
    }

    private static Field MakeField(Kind kind, double weight, bool fuzzy, string text, bool keepMap)
    {
        List<int>? map = keepMap ? new List<int>(text.Length + 1) : null;
        string folded = Fold(text, map);
        return new Field { Kind = kind, Weight = weight, Fuzzy = fuzzy, Folded = folded, Map = map?.ToArray(), Words = Split(folded) };
    }

    private static Field MakeKeyField(string key)
    {
        string folded = FoldKey(key);
        return new Field { Kind = Kind.Key, Weight = 0.75, Fuzzy = true, Folded = folded, Words = Split(folded).Where(word => word.Length > 1).ToArray() };
    }

    private static Word[] Split(string folded)
    {
        var words = new List<Word>();
        int start = -1;
        for (int i = 0; i <= folded.Length; i++)
        {
            bool letter = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0) { words.Add(new Word(start, i - start)); start = -1; }
        }
        return words.ToArray();
    }

    private static string[] Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        if (query.Length > MaximumQueryLength) query = query[..MaximumQueryLength];
        string folded = Fold(query);
        return Split(folded).Take(MaximumQueryWords).Select(word => folded.Substring(word.Start, word.Length)).ToArray();
    }

    private static SettingsSearchHit? Evaluate(Entry entry, string[] tokens, string phrase)
    {
        double total = 0;
        List<(int Start, int Length)>? labelRanges = null, hintRanges = null;
        foreach (string token in tokens)
        {
            double best = 0;
            foreach (Field field in entry.Fields)
            {
                bool highlighted = field.Kind is Kind.Label or Kind.Hint;
                foreach (Word word in field.Words)
                {
                    double score = ScoreWord(token, field.Folded.AsSpan(word.Start, word.Length), field.Fuzzy, out int offset, out int length, out int[]? positions);
                    if (score <= 0) continue;
                    double weighted = score * field.Weight;
                    if (weighted > best) best = weighted;
                    if (!highlighted) continue;
                    List<(int Start, int Length)> ranges = field.Kind == Kind.Label
                        ? labelRanges ??= new List<(int Start, int Length)>()
                        : hintRanges ??= new List<(int Start, int Length)>();
                    if (positions != null) foreach (int position in positions) ranges.Add((word.Start + position, 1));
                    else ranges.Add((word.Start + offset, length));
                }
            }
            if (best < MinimumWordScore) return null;
            total += best;
        }
        // A label that contains the whole query as typed ranks above one that only has its words somewhere.
        int at = entry.FoldedLabel.IndexOf(phrase, StringComparison.Ordinal);
        if (at == 0) total += 30;
        else if (at > 0) total += 15;
        Field labelField = entry.Fields[0];
        return new SettingsSearchHit(entry.Document, total,
            ToOriginal(labelRanges, labelField), ToOriginal(hintRanges, entry.Fields.FirstOrDefault(f => f.Kind == Kind.Hint)));
    }

    private static IReadOnlyList<SettingsTextRange> ToOriginal(List<(int Start, int Length)>? ranges, Field? field)
    {
        if (ranges == null || field?.Map == null || ranges.Count == 0) return Array.Empty<SettingsTextRange>();
        var spans = new List<(int Start, int End)>();
        foreach ((int start, int length) in ranges)
        {
            if (start < 0 || length <= 0 || start + length > field.Map.Length) continue;
            spans.Add((field.Map[start], field.Map[start + length - 1] + 1));
        }
        spans.Sort();
        var merged = new List<SettingsTextRange>();
        int currentStart = -1, currentEnd = -1;
        foreach ((int start, int end) in spans)
        {
            if (currentStart >= 0 && start <= currentEnd) { currentEnd = Math.Max(currentEnd, end); continue; }
            if (currentStart >= 0) merged.Add(new SettingsTextRange(currentStart, currentEnd - currentStart));
            currentStart = start; currentEnd = end;
        }
        if (currentStart >= 0) merged.Add(new SettingsTextRange(currentStart, currentEnd - currentStart));
        return merged;
    }

    /// <summary>0 when the token does not match the word; otherwise up to 100, higher is better.</summary>
    internal static double ScoreWord(ReadOnlySpan<char> token, ReadOnlySpan<char> word, bool fuzzy,
        out int offset, out int length, out int[]? positions)
    {
        offset = 0; length = word.Length; positions = null;
        if (token.Length == 0 || word.Length == 0) return 0;
        if (token.SequenceEqual(word)) return 100;
        if (word.StartsWith(token, StringComparison.Ordinal))
        {
            length = token.Length;
            return 80 + 15.0 * token.Length / word.Length;
        }
        // One letter only counts as the start of a word; very short parts inside words are noise (except in CJK text).
        if (token.Length < 2) return 0;
        bool wide = HasWide(token);
        if (token.Length >= 3 || wide)
        {
            int inside = word.IndexOf(token, StringComparison.Ordinal);
            if (inside > 0)
            {
                offset = inside; length = token.Length;
                return 55 + 10.0 * token.Length / word.Length;
            }
        }
        if (!fuzzy || wide) return 0;
        double best = 0;
        int[]? found = null;
        if (token.Length >= 3 && token[0] == word[0] && token.Length * 10 >= word.Length * 4 && token.Length < word.Length
            && Subsequence(token, word, out found))
            best = 40 + 15.0 * token.Length / word.Length;
        // A typo reads as the whole word and beats an accidental run of letters in order ("bandwith" is "bandwidth", not b-a-n-d-w-i, t-h).
        double typo = TypoScore(token, word, out int typoLength);
        if (typo > best)
        {
            positions = null; offset = 0; length = typoLength;
            return typo;
        }
        positions = best > 0 ? found : null;
        return best;
    }

    private static double TypoScore(ReadOnlySpan<char> token, ReadOnlySpan<char> word, out int length)
    {
        length = word.Length;
        if (token.Length < 4 || word.Length > MaximumTypoWordLength) return 0;
        int allowed = token.Length >= 9 ? 2 : 1;
        // A typo in the first letter of a short word is rare and would match too much ("list" for "last").
        if (token.Length <= 5 && token[0] != word[0]) return 0;
        if (Math.Abs(token.Length - word.Length) <= allowed)
        {
            int whole = Distance(token, word, allowed);
            if (whole <= allowed) return 70 - 12 * (whole - 1);
        }
        // The start of a longer word with a typo: "dowload" for "downloads".
        for (int cut = token.Length; cut <= token.Length + 1 && cut < word.Length; cut++)
            if (Distance(token, word[..cut], allowed) <= allowed)
            {
                length = cut;
                return 58;
            }
        return 0;
    }

    private static bool HasWide(ReadOnlySpan<char> text)
    {
        foreach (char c in text) if (c > 0x2E7F) return true;
        return false;
    }

    private static bool Subsequence(ReadOnlySpan<char> token, ReadOnlySpan<char> word, out int[]? positions)
    {
        var found = new int[token.Length];
        int at = 0;
        for (int i = 0; i < token.Length; i++)
        {
            while (at < word.Length && word[at] != token[i]) at++;
            if (at == word.Length) { positions = null; return false; }
            found[i] = at++;
        }
        positions = found;
        return true;
    }

    /// <summary>Optimal string alignment distance (insert, delete, replace, swap of neighbours); stops above <paramref name="limit"/>.</summary>
    internal static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        int columns = b.Length + 1;
        Span<int> previousPrevious = stackalloc int[columns];
        Span<int> previous = stackalloc int[columns];
        Span<int> current = stackalloc int[columns];
        for (int j = 0; j < columns; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMinimum = current[0];
            for (int j = 1; j < columns; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                current[j] = value;
                if (value < rowMinimum) rowMinimum = value;
            }
            if (rowMinimum > limit) return limit + 1;
            Span<int> recycled = previousPrevious;
            previousPrevious = previous; previous = current; current = recycled;
        }
        return previous[b.Length];
    }

    // Letters that carry an accent or are written with a variant, mapped to what a keyboard without them types.
    private static readonly Dictionary<char, string> Accents = BuildAccents();

    private static Dictionary<char, string> BuildAccents()
    {
        var map = new Dictionary<char, string>();
        void Add(string letters, string plain) { foreach (char c in letters) map[c] = plain; }
        Add("àáâãäåāăąǎ", "a"); Add("çćĉċč", "c"); Add("ďđ", "d"); Add("èéêëēĕėęě", "e"); Add("ĝğġģ", "g");
        Add("ĥħ", "h"); Add("ìíîïĩīĭįıǐ", "i"); Add("ĵ", "j"); Add("ķ", "k"); Add("ĺļľŀł", "l"); Add("ñńņňŉ", "n");
        Add("òóôõöøōŏőǒ", "o"); Add("ŕŗř", "r"); Add("śŝşš", "s"); Add("ţťŧ", "t"); Add("ùúûüũūŭůűųǔ", "u");
        Add("ŵ", "w"); Add("ýÿŷ", "y"); Add("źżž", "z");
        map['ß'] = "ss"; map['æ'] = "ae"; map['œ'] = "oe"; map['ё'] = "е"; map['й'] = "и";
        return map;
    }
}
