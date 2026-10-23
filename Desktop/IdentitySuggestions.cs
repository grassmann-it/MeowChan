using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal sealed record IdentitySuggestion(string Value, string? Source, string Label);

/// <summary>Immutable bounded data shared by scalar and set-valued editor adapters. No query state.</summary>
internal sealed class IdentitySuggestions
{
    internal const int SourceLimit = 256, BoardLimit = 2048, VisibleLimit = 20;
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
