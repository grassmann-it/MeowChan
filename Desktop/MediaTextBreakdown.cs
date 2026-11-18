using System;
using System.Collections.Generic;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// How many media (tiles, as the grid counts them) the words of the search text hit, in total and per field. Every number is a tile
/// count over the media that pass all the other filters (including the page's queue and source tag terms), whatever the chosen scope is:
/// the parts of the row therefore stay the same while the user narrows or widens the scope. They do not add up: one medium can hit in
/// several fields, and words can be spread over fields (then <see cref="All"/> is above zero while every field is zero).
/// </summary>
/// <param name="Scope">The scope the presentation that made these counts used.</param>
/// <param name="InScope">The tiles that scope let through: what the grid shows.</param>
internal sealed record MediaTextCounts(int All, int Name, int Title, int Board, int Source, int Tag, MediaTextFields Scope, int InScope)
{
    /// <summary>The tiles whose occurrences hit with every word in this one field (<see cref="MediaTextFields.All"/>: <see cref="All"/>).</summary>
    internal int Of(MediaTextFields field) => field switch
    {
        MediaTextFields.Name => Name,
        MediaTextFields.Title => Title,
        MediaTextFields.Board => Board,
        MediaTextFields.Source => Source,
        MediaTextFields.Tag => Tag,
        MediaTextFields.All => All,
        _ => 0
    };

    /// <summary>The words only match across several fields: media were found, but not one field holds every word.</summary>
    internal bool OnlyAcrossFields => All > 0 && Name == 0 && Title == 0 && Board == 0 && Source == 0 && Tag == 0;

    /// <summary>A narrowed scope that finds nothing while the text is found elsewhere (the row then offers to search everywhere).</summary>
    internal bool NothingInScope => Scope != MediaTextFields.All && InScope == 0 && All > 0;

    /// <summary>
    /// Counts the tiles of <paramref name="tally"/> (the occurrences that pass every other filter, in the order of the index). A tile
    /// of equal bytes counts once per field however many of its occurrences hit there; a missing occurrence counts only when the page
    /// shows missing files, exactly as the grid does.
    /// </summary>
    internal static MediaTextCounts Of(IEnumerable<MediaTextTally> tally, bool includeMissing, MediaTextFields scope, int inScope)
    {
        ArgumentNullException.ThrowIfNull(tally);
        TileSet all = new();
        TileSet[] fields = new TileSet[MediaTextBreakdown.Fields.Length];
        for (int i = 0; i < fields.Length; i++) fields[i] = new TileSet();
        foreach (MediaTextTally item in tally)
        {
            if (item.Entry.Presence != MediaLibraryPresence.Present && !includeMissing) continue;
            all.Add(item.Entry);
            for (int i = 0; i < fields.Length; i++)
                if ((item.Fields & MediaTextBreakdown.Fields[i]) != 0) fields[i].Add(item.Entry);
        }
        return new MediaTextCounts(all.Count, fields[0].Count, fields[1].Count, fields[2].Count, fields[3].Count, fields[4].Count, scope, inScope);
    }

    // The tiles of a set of occurrences, as MediaLibraryGrouping.CountTiles counts them: one per byte identity, one per occurrence without.
    private sealed class TileSet
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private int _loose;
        internal int Count => _keys.Count + _loose;
        internal void Add(MediaLibraryEntry entry)
        {
            if (MediaLibraryGrouping.ByteKey(entry) is { } key) _keys.Add(key);
            else _loose++;
        }
    }
}

/// <summary>One part of the row: "All", or one field; <see cref="Selected"/> when the scope includes it.</summary>
internal readonly record struct MediaBreakdownPart(MediaTextFields Field, int Count, bool Selected);

/// <summary>
/// The rules of the "where was it found" row under the Media search field (MediaPage.Breakdown.cs): which parts it shows, what a click
/// on a part does to the scope, and when the row is there at all. Pure, so the rules are tested without the page.
/// </summary>
internal static class MediaTextBreakdown
{
    /// <summary>The five fields, in the order of the row, and of the scope's canonical spelling.</summary>
    internal static readonly MediaTextFields[] Fields =
    {
        MediaTextFields.Name, MediaTextFields.Title, MediaTextFields.Board, MediaTextFields.Source, MediaTextFields.Tag
    };

    /// <summary>
    /// The longest text the row's name and the screen reader summary repeat; a longer search text is cut with an ellipsis. It only
    /// shortens a label (the search itself reads the whole text), so it is a fixed display ceiling, not a setting.
    /// </summary>
    internal const int TextLabelLength = 40;

    /// <summary>The row is there while the text has a word and at least one medium has every word in some field.</summary>
    internal static bool IsShown(MediaTextCounts? counts, bool hasWords) => hasWords && counts is { All: > 0 };

    /// <summary>
    /// The parts to show: "All" first, then the fields in row order. A field without a hit is left out unless it is selected (then it shows 0,
    /// so the user sees what is chosen and can undo it). Only a narrowed scope selects fields; under "All" nothing but "All" is on.
    /// </summary>
    internal static IReadOnlyList<MediaBreakdownPart> Parts(MediaTextCounts counts, MediaTextFields scope)
    {
        ArgumentNullException.ThrowIfNull(counts);
        List<MediaBreakdownPart> parts = new(Fields.Length + 1) { new MediaBreakdownPart(MediaTextFields.All, counts.All, scope == MediaTextFields.All) };
        foreach (MediaTextFields field in Fields)
        {
            bool selected = scope != MediaTextFields.All && (scope & field) != 0;
            int count = counts.Of(field);
            if (count > 0 || selected) parts.Add(new MediaBreakdownPart(field, count, selected));
        }
        return parts;
    }

    /// <summary>
    /// The scope after a click on <paramref name="clicked"/>. "All" always means the default. A field clicked while "All" is on selects only
    /// that field; further fields toggle; taking away the last one, or choosing all five, is "All" again.
    /// </summary>
    internal static MediaTextFields Toggle(MediaTextFields scope, MediaTextFields clicked)
    {
        if (clicked == MediaTextFields.All || (clicked & MediaTextFields.All) == 0) return MediaTextFields.All;
        MediaTextFields next = scope == MediaTextFields.All ? clicked & MediaTextFields.All : (scope ^ clicked) & MediaTextFields.All;
        return next == MediaTextFields.None || next == MediaTextFields.All ? MediaTextFields.All : next;
    }

    /// <summary>The selected fields in row order (empty for "All").</summary>
    internal static IReadOnlyList<MediaTextFields> Selected(MediaTextFields scope)
    {
        List<MediaTextFields> selected = new();
        if (scope == MediaTextFields.All) return selected;
        foreach (MediaTextFields field in Fields)
            if ((scope & field) != 0) selected.Add(field);
        return selected;
    }

    /// <summary>The key of one field in the catalog (<c>media.scope.name</c>, <c>media.breakdown.field.name</c>, …).</summary>
    internal static string Key(MediaTextFields field) => field switch
    {
        MediaTextFields.Name => "name",
        MediaTextFields.Title => "title",
        MediaTextFields.Board => "board",
        MediaTextFields.Source => "source",
        MediaTextFields.Tag => "tag",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "The part is one field.")
    };

    /// <summary>The search text as the row repeats it: trimmed, and cut with an ellipsis at <see cref="TextLabelLength"/> characters.</summary>
    internal static string TextLabel(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length <= TextLabelLength) return trimmed;
        int cut = TextLabelLength;
        if (char.IsHighSurrogate(trimmed[cut - 1])) cut--; // never leave half of a surrogate pair
        return trimmed[..cut].TrimEnd() + "…";
    }
}
