using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Command ids of quick tagging (gallery tag panel and tag popover). The catalog registers them and the shortcut editor
/// can rebind them; the packages that build the surfaces wire the handlers (panel: next/previous/commit-next/mark/thread-section,
/// both surfaces: undo and the nine suggestion toggles) and then add the id to <c>Wired</c>.</summary>
public static class QuickTagCommands
{
    /// <summary>Gallery panel: show the next medium, keep the panel and the field.</summary>
    public const string Next = "tags.quick.next";
    public const string Previous = "tags.quick.previous";
    /// <summary>Gallery panel: take over the field like Enter, then show the next medium.</summary>
    public const string CommitNext = "tags.quick.commit-next";
    /// <summary>Gallery panel: mark the shown medium for the batch and go on / go back and unmark.</summary>
    public const string MarkNext = "tags.quick.mark-next";
    public const string MarkPrevious = "tags.quick.mark-previous";
    /// <summary>Gallery panel: open and focus the thread tags section.</summary>
    public const string ThreadSection = "tags.quick.thread-section";
    /// <summary>Panel and popover: undo the last change of the surface's session.</summary>
    public const string Undo = "tags.quick.undo";
    public const int SuggestionCount = 9;

    /// <summary>Panel and popover: toggle suggestion <paramref name="number"/> (1 to 9).</summary>
    public static string Suggestion(int number) => number is >= 1 and <= SuggestionCount
        ? "tags.quick.suggestion-" + number : throw new ArgumentOutOfRangeException(nameof(number));

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        new[] { Next, Previous, CommitNext, MarkNext, MarkPrevious, ThreadSection, Undo }
            .Concat(Enumerable.Range(1, SuggestionCount).Select(Suggestion)).ToArray());

    /// <summary>Ids whose handler is wired. The package that wires one (TP2 panel, TP4 marking, TP3 popover) adds it here: the command
    /// then counts as implemented, its default gesture becomes active and the gallery help lists it.</summary>
    // TP2 wired the gallery panel: paging, commit-and-next, the thread section, undo and the nine toggles (GalleryTagPanelCommands.Handled).
    // TP4 wired the marking (mark-next, mark-previous): GalleryViewer.OnTagNavigationRequested marks or unmarks and moves.
    private static readonly string[] Wired = new[] { Next, Previous, CommitNext, MarkNext, MarkPrevious, ThreadSection, Undo }
        .Concat(Enumerable.Range(1, SuggestionCount).Select(Suggestion)).ToArray();

    /// <summary>Ids of <paramref name="all"/> that are not in <paramref name="wired"/>: registered, but without a handler.</summary>
    internal static IReadOnlySet<string> Unwired(IEnumerable<string> all, IEnumerable<string> wired) =>
        new HashSet<string>(all.Except(wired, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The ids that count as wired.</summary>
    internal static IReadOnlyList<string> WiredIds => Array.AsReadOnly(Wired);

    /// <summary>Registered commands whose handler is not wired yet.</summary>
    public static IReadOnlySet<string> AwaitingHandler { get; } = Unwired(All, Wired);
}
