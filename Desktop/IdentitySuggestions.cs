using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Desktop;

/// <summary><paramref name="Description"/> is the screen reader name and tooltip of the row when it says more than the
/// visible label. <paramref name="GroupHeader"/> labels a group that starts at this row; it is shown above it, not a row.</summary>
internal sealed record IdentitySuggestion(string Value, string? Source, string Label, string? Description = null, string? GroupHeader = null)
{
    /// <summary>Where the caret goes once <see cref="Value"/> is written into the editor; null: the end.</summary>
    internal int? Caret { get; init; }
    /// <summary>Accepting the row only edits the draft (another tag follows the completed one): nothing is added yet.</summary>
    internal bool InPlace { get; init; }
    /// <summary>The row is highlighted as soon as it is shown (the one real match of the typed word), so Enter takes it.</summary>
    internal bool Preselect { get; init; }
}

/// <summary>Immutable bounded data shared by scalar and set-valued editor adapters. No query state.</summary>
internal sealed class IdentitySuggestions
{
    internal const int SourceLimit = 256, BoardLimit = 2048, VisibleLimit = 20;
    // A selected stored identity is authoritative only while its exact text remains.
    // Free input retains the UI's existing whitespace normalization.
    internal static IdentitySuggestion? CurrentSelection(string? text, IdentitySuggestion? candidate) =>
        candidate != null && StringComparer.Ordinal.Equals(candidate.Value, text) ? candidate : null;
    internal static string? InputIdentity(string? text, IdentitySuggestion? candidate) =>
        CurrentSelection(text, candidate)?.Value ?? text?.Trim();
    // Enter or Add without a click takes the highlighted suggestion, else the one matching the text exactly
    // (ignoring case, only if unique), else the only suggestion shown. Null keeps the typed text as a free value.
    // Tab completes the highlighted suggestion, else the first one shown. A highlight that equals the draft is the
    // control's automatic exact match (or a "new" row), not a choice, so the first suggestion is used instead; one
    // that already equals the draft completes nothing, so Tab keeps moving focus. Null: Tab is left to focus navigation.
    internal static IdentitySuggestion? TabCompletion(string? text, IdentitySuggestion? highlighted, IReadOnlyList<IdentitySuggestion> shown)
    {
        bool Same(IdentitySuggestion item) => StringComparer.Ordinal.Equals(item.Value, text);
        IdentitySuggestion? candidate = highlighted is { } chosen && !Same(chosen) ? chosen : shown.Count > 0 ? shown[0] : null;
        return candidate == null || Same(candidate) ? null : candidate;
    }
    internal static IdentitySuggestion? Resolve(string? text, IdentitySuggestion? highlighted, IReadOnlyList<IdentitySuggestion> candidates)
    {
        if (highlighted != null) return highlighted;
        string typed = text?.Trim() ?? "";
        if (typed.Length == 0) return null;
        IdentitySuggestion[] exact = candidates.Where(x => StringComparer.Ordinal.Equals(x.Value, text)).ToArray();
        if (exact.Length == 1) return exact[0];
        IdentitySuggestion[] folded = candidates.Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.Value.Trim(), typed)).ToArray();
        if (folded.Length == 1) return folded[0];
        return candidates.Count == 1 ? candidates[0] : null;
    }

    internal IdentitySuggestions(ArchiveIdentityPage<string> sources, ArchiveIdentityPage<ArchiveBoardIdentity> boards)
    { Sources = sources; Boards = boards; }
    internal ArchiveIdentityPage<string> Sources { get; }
    internal ArchiveIdentityPage<ArchiveBoardIdentity> Boards { get; }
    internal IdentitySuggestion[] Match(bool boards, string text, string? source = null,
        ArchiveIdentityPage<ArchiveBoardIdentity>? scoped = null) => (boards
        ? (scoped ?? Boards).Items.Where(x => string.IsNullOrEmpty(source) || x.Source == source)
            .Where(x => x.Board.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Select(x => new IdentitySuggestion(x.Board, x.Source, x.Board + " · " + x.Source))
        : Sources.Items.Where(x => x.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Select(x => new IdentitySuggestion(x, null, x)))
        .Take(VisibleLimit).ToArray();

    internal static IdentitySuggestions FromContexts(IEnumerable<ArchiveBoardIdentity> preferred,
        IEnumerable<ArchiveBoardIdentity> context, IEnumerable<string> providerSources)
    {
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        var boards = new SortedSet<ArchiveBoardIdentity>(Comparer<ArchiveBoardIdentity>.Create((x, y) =>
        { int source = StringComparer.Ordinal.Compare(x.Source, y.Source); return source != 0 ? source : StringComparer.Ordinal.Compare(x.Board, y.Board); }));
        bool moreSources = false, moreBoards = false;
        // Keep preferred rule/draft identities before admitting session identities. Each
        // pass holds at most its limit, even if a session has millions of watches.
        void Add(IEnumerable<ArchiveBoardIdentity> input, bool _)
        {
            var passSources = new SortedSet<string>(StringComparer.Ordinal);
            var passBoards = new SortedSet<ArchiveBoardIdentity>(boards.Comparer);
            int sourceSpace = SourceLimit - sources.Count, boardSpace = BoardLimit - boards.Count;
            foreach (var item in input)
            {
                if (!string.IsNullOrWhiteSpace(item.Source) && !sources.Contains(item.Source))
                {
                    passSources.Add(item.Source);
                    if (passSources.Count > sourceSpace) { moreSources = true; passSources.Remove(passSources.Max!); }
                }
                if (!string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.Board) && !boards.Contains(item))
                {
                    passBoards.Add(item);
                    if (passBoards.Count > boardSpace) { moreBoards = true; passBoards.Remove(passBoards.Max!); }
                }
            }
            sources.UnionWith(passSources); boards.UnionWith(passBoards);
        }
        Add(preferred, true);
        Add(context.Concat(providerSources.Select(source => new ArchiveBoardIdentity(source, ""))), false);
        return new(new(sources, moreSources), new(boards, moreBoards));
    }
}
