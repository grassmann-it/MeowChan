using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>
/// The fixed letter of every main page (Tom's decision of 15.11.2026, defaults revision 15). Ctrl/Cmd+1 to 9 keep counting the visible footer
/// buttons from the left (<see cref="MainPageOrder.Numbering"/>), so a page's digit moves when the footer is reordered. The letter does not:
/// <c>Ctrl+Shift+letter</c> (<c>⇧⌘letter</c> on a Mac) opens the same page whatever the footer looks like. Both are defaults of the one page
/// command, so both show next to each other ("Ctrl+3 / Ctrl+Shift+E") in the footer tooltips, the shortcuts window and the footer settings, and
/// both can be rebound; a binding the person saved for another command always wins over a new default (yielding defaults, revision 15).
/// </summary>
/// <remarks>
/// <para><b>Why Ctrl+Shift+letter.</b> Ctrl (⌘) is the application-command modifier of the scheme (Ctrl+digit already opens pages), Shift marks the
/// letter family, and nothing here is a trap: Ctrl+Alt is AltGr text on German, French and Polish layouts; bare Alt+letter and Alt+Shift+letter
/// either clash with menu access keys of buttons or with the layout switch of Windows (Alt+Shift), and on a Mac ⌥letter types characters. Meta/Win
/// is reserved by Windows and Linux. A "G then letter" sequence would need a new chord state machine in the router, the editor and the conflict
/// check, which this package does not build: <see cref="CommandRouter"/> and <see cref="ShortcutGesture"/> know single gestures only.</para>
/// <para><b>Which letters.</b> The initial where Ctrl+Shift+initial is free (D Downloads, A Archive, B Booru, S Status); where it is taken the
/// letter that is still in the name or tells the story: E for m<b>E</b>dia (Ctrl+Shift+M is the reader's media sidebar and ⌘M minimizes on a
/// Mac), U for r<b>U</b>les, X for e<b>X</b>tensions like in VS Code (Plugins), K for the tagging mode's <b>K</b>arten and its K key. Taken are
/// Ctrl+Shift+T (media tags), R (continue reading), I (import), P (pause), F (filters), O (open source), L (follow live), N (new posts),
/// M (the media sidebar and the Media page's panel), G (find previous), and a Mac reserves ⌘⇧Q (log out) and ⌘⇧W; H is left alone because ⌘H hides. The invariants are in
/// <c>PageKeySchemeTests</c>: unique, free on every platform and in every context, never Ctrl+Alt, never a reserved Mac letter.</para>
/// <para><b>Settings</b> is not in the table: its fixed key is <c>Ctrl/Cmd+,</c> on every platform (the platform's own convention, also in the
/// ⋯ menu), so it already has one and needs no letter. Diagnostics and Statistics are segments of Status and have none.</para>
/// <para>The keys act where the digits act: outside text fields (a letter typed in a field is text), while no modal overlay or dialog owns the
/// keyboard, from every main page, the large reader and the gallery. The routing is the page command's own (MainWindow.OnShellKeyDown).</para>
/// </remarks>
public static class PageKeyScheme
{
    /// <summary>The modifiers of every page letter, as stored in a gesture: Primary (Ctrl, ⌘ on a Mac) plus Shift.</summary>
    public static IReadOnlyList<string> Modifiers { get; } = Array.AsReadOnly(new[] { "Primary", "Shift" });

    /// <summary>The page command to its letter (upper case), one entry per main page.</summary>
    public static IReadOnlyDictionary<string, string> Letters { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["navigation.downloads"] = "D",
            ["navigation.archive"] = "A",
            ["navigation.media"] = "E",
            ["navigation.tagging"] = "K",
            ["navigation.booru"] = "B",
            ["navigation.rules"] = "U",
            ["navigation.sources"] = "X",
            ["navigation.status"] = "S",
        });

    /// <summary>
    /// Letters no page may take, whatever else is free: on a Mac ⌘⇧Q logs out and ⌘⇧W closes windows, ⌘H hides (⇧⌘H is the Finder's Home, a habit kept
    /// clear) and ⌘M minimizes. ⇧⌘M itself is not a system key and is not left unused: it is the panel switch of the reader's media sidebar and the Media
    /// page's media panel (<c>reader.sidebar.toggle</c>, <c>media.panel.toggle</c>), so M is no page letter. Ctrl+Shift+C/V/Z are the copy and paste
    /// variants and redo that text fields of the platform answer.
    /// </summary>
    public static IReadOnlyList<string> ReservedLetters { get; } = Array.AsReadOnly(new[] { "Q", "W", "H", "M", "C", "V", "Z" });

    /// <summary>The letter of a page command, or null for a command that has none (Settings, the Status segments, everything else).</summary>
    public static string? Letter(string commandId) => Letters.TryGetValue(commandId, out string? letter) ? letter : null;

    /// <summary>The fixed gesture of a page ("Primary+Shift+E"), or null for a command without one.</summary>
    public static ShortcutGesture? Gesture(string commandId) =>
        Letter(commandId) is { } letter ? new ShortcutGesture(letter, Modifiers) : null;
}
