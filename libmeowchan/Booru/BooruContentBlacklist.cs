using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Booru;

/// <summary>Compiles one blacklist scope. Blank lines are ignored and a rule contains whitespace-separated terms.</summary>
internal sealed record BooruContentBlacklist(IReadOnlyList<BooruContentBlacklist.Rule> Rules, BooruContentRuleLocation? Error)
{
    internal enum TermKind { Tag, Rating, SourceRatingS, ScoreBelow }

    internal readonly record struct Term(TermKind Kind, bool Negated, string Text, int Value)
    {
        internal bool? Matches(BooruPostClaim post, HashSet<string>? tags)
        {
            bool? result = Kind switch
            {
                TermKind.Tag => tags?.Contains(Text),
                TermKind.Rating => post.Rating == (BooruPostClaimRating)Value,
                TermKind.SourceRatingS => post.SourceId switch
                {
                    "danbooru" => post.Rating == BooruPostClaimRating.Sensitive,
                    "e621" or "e926" => post.Rating == BooruPostClaimRating.General,
                    _ => null
                },
                TermKind.ScoreBelow => post.Fields.HasFlag(BooruPostClaimPresence.Score) && post.Score is int score
                    ? score < Value : null,
                _ => null
            };
            return Negated ? !result : result;
        }
    }

    internal sealed record Rule(BooruContentRuleLocation Location, IReadOnlyList<Term> Terms)
    {
        internal bool? Matches(BooruPostClaim post, HashSet<string>? tags)
        {
            bool? result = true;
            foreach (Term term in Terms)
            {
                bool? matches = term.Matches(post, tags);
                if (matches == false) return false;
                if (matches is null) result = null;
            }
            return result;
        }
    }

    internal static BooruContentBlacklist Parse(string text, BooruContentSourceBlacklist? source = null)
    {
        List<Rule> rules = new();
        using StringReader reader = new(text);
        for (int lineNumber = 1; reader.ReadLine() is string line; lineNumber++)
        {
            string[] words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            BooruContentRuleLocation location = new(lineNumber, source?.SourceId, source?.InstanceId);
            List<Term> terms = new(words.Length);
            foreach (string word in words)
            {
                Term? term = ParseTerm(word);
                if (term is null) return new([], location);
                terms.Add(term.Value);
            }
            rules.Add(new(location, terms));
        }
        return new(rules, null);
    }

    private static Term? ParseTerm(string word)
    {
        bool negated = word[0] == '-';
        string operand = negated ? word[1..] : word;
        if (operand.StartsWith("rating:", StringComparison.OrdinalIgnoreCase))
            return ParseRating(operand[7..], negated);
        if (operand.StartsWith("score:<", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(operand.AsSpan(7), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int score))
            return new(TermKind.ScoreBelow, negated, string.Empty, score);

        if (operand.Length == 0 || operand[0] == '-' || operand.AsSpan().ContainsAny(":+~*?<>|=\"")
            || operand.Any(char.IsControl))
            return null;
        return new(TermKind.Tag, negated, operand, 0);
    }

    private static Term? ParseRating(string operand, bool negated)
    {
        string value = operand.ToLowerInvariant();
        if (value == "s") return new(TermKind.SourceRatingS, negated, string.Empty, 0);
        BooruPostClaimRating rating = value switch
        {
            "g" or "general" or "safe" => BooruPostClaimRating.General,
            "sensitive" => BooruPostClaimRating.Sensitive,
            "q" or "questionable" => BooruPostClaimRating.Questionable,
            "e" or "explicit" => BooruPostClaimRating.Explicit,
            _ => BooruPostClaimRating.Unknown
        };
        return rating == BooruPostClaimRating.Unknown ? null : new(TermKind.Rating, negated, string.Empty, (int)rating);
    }
}
