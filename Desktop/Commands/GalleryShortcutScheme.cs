using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>
/// The key scheme of the gallery (Tom's decision of 14.11.2026, defaults revision 13). One rule keeps the keys guessable:
/// <list type="bullet">
/// <item><b>Shift + letter</b> shows or hides a panel or block: <c>Shift+H</c> tools, <c>Shift+I</c> info line, <c>Shift+K</c> cards of the tagging mode, <c>Shift+N</c> hint row,
/// <c>Shift+L</c> filmstrip, <c>Shift+O</c> controls overlay, <c>Shift+P</c> post panel, <c>Shift+T</c> view / tag mode.</item>
/// <item><b>Plain letters</b> act on what plays: <c>L</c> repeat and <c>A</c> the A–B loop. The older plain letters of the gallery (<c>F</c> toggles a favourite, <c>I</c> details, <c>Q</c> queue, <c>R</c> rating,
/// <c>T</c> show in thread) stay.</item>
/// <item><b>Favourites, uniformly everywhere</b> (Tom's decision of 15.11.2026, revision 20): <c>M</c> adds, <c>U</c> removes and <c>F</c> toggles, in the gallery as in
/// the reader, its media sidebar, the archive's media and the Media page's grid and panel. <c>B</c> (revision 13's "add") is gone since revision 21 (Tom's decision #132 of 16.11.2026); a binding saved with B keeps it.</item>
/// <item><b>Exceptions to the Shift rule</b>: <c>Shift+M</c> mutes (revision 20; <c>M</c> is "add favourite") and <c>Shift+R</c> turns the medium. Snapshot uses <c>Shift+S</c> and slideshow <c>Shift+D</c> (revision 22; S skips and D marks tagging items done). These act on the
/// shown medium rather than showing or hiding a block (<see cref="ShiftLetterActionIds"/>).</item>
/// <item><b>Ctrl (⌘)</b> stays for commands of the whole application; inside the gallery ⌘ is never used for panels (macOS owns ⌘H, ⌘M, ⌘Q, ⌘W). The one panel switch with
/// Primary+Shift is outside the gallery: <c>Primary+Shift+M</c> (⇧⌘M) turns the reader's media sidebar and the Media page's media panel on or off (revisions 9 and 18). ⇧⌘M is no system key
/// (⌘M minimizes, ⇧⌘M is free), and the gallery has no such switch.</item>
/// <item><b>The rule is a test</b> (<c>ShiftLetterSchemeTests</c>): every default of the form Shift + one letter is a panel switch, unless the command is listed in
/// <see cref="ShiftLetterActionIds"/> (acts on the medium) or <see cref="ShiftLetterOtherPanelIds"/> (a panel or mode switch of another page that uses the same form).
/// A new Shift+letter default must land in one of the lists on purpose.</item>
/// </list>
/// The defaults themselves live in <see cref="CommandCatalog"/> (and <see cref="EffectiveShortcutBindings.Defaults(CommandDefinition, ShortcutPlatform)"/>);
/// this class holds what the rule tests and the layout commands of the work still on their branch need: which commands are show / hide
/// switches, which are playback letters, and the defaults of layout commands that the catalog of this branch does not know yet.
/// Every default of the scheme is a yielding default (revision 13): a binding the person saved is never taken away.
/// </summary>
public static class GalleryShortcutScheme
{
    /// <summary>Every show / hide switch of the gallery that may have a default key. A default, when there is one, is Shift plus one letter.
    /// The layout rows (<c>gallery.layout.*</c>) belong to the Layout menu of GL3; they appear in the catalog when that branch is merged.</summary>
    public static IReadOnlyList<string> PanelToggleIds { get; } = Array.AsReadOnly(new[]
    {
        "gallery.filmstrip-toggle", "gallery.context-toggle", "gallery.mode-toggle", "gallery.tools-toggle",
        "gallery.overlay-toggle", "gallery.overlay-autohide-toggle", "gallery.post-overlay-toggle",
        "gallery.layout.tools", "gallery.layout.info", "gallery.layout.hints", "tags.queue.cards"
    });

    /// <summary>The plain-letter defaults that act on the shown medium's playback.</summary>
    public static IReadOnlyList<string> PlaybackLetterIds { get; } = Array.AsReadOnly(new[]
    {
        "video.loop", "video.ab-loop"
    });

    /// <summary>The Shift-letter defaults that act on the shown medium instead of showing or hiding a block: mute (Shift+M), rotate (Shift+R), snapshot (Shift+S) and slideshow (Shift+D, revision 22).</summary>
    public static IReadOnlyList<string> ShiftLetterActionIds { get; } = Array.AsReadOnly(new[] { "video.mute", "gallery.rotate", "video.snapshot", "gallery.slideshow-toggle" });

    /// <summary>The Shift-letter defaults of another page that switch what that page shows (the Media page's focus mode, Shift+F next to the favourite's F). They are
    /// panel-like switches, not actions on a medium, and are no gallery commands, so they sit in neither <see cref="PanelToggleIds"/> nor <see cref="ShiftLetterActionIds"/>.
    /// <c>ShiftLetterSchemeTests</c> accepts exactly these three lists for a default of the form Shift + one letter.</summary>
    public static IReadOnlyList<string> ShiftLetterOtherPanelIds { get; } = Array.AsReadOnly(new[] { "media.focus-mode" });

    /// <summary>The favourite keys (revision 20), the same on every surface that shows or selects a medium: M adds, U removes, F toggles.</summary>
    public static IReadOnlyList<string> FavoriteKeyIds { get; } = Array.AsReadOnly(new[] { "favorites.add", "favorites.remove", "favorites.toggle" });

    /// <summary>
    /// Defaults of show / hide commands that exist only on the Layout menu branch (GL3) and have no shortcut in their catalog line there:
    /// the info line and the hint row. They apply as soon as the commands are registered; a command that is not in the catalog never
    /// asks for them. The tools row stays without a key of its own (Shift+H is the hide-tools switch of the same block); the filmstrip
    /// positions, the reset and the menu itself are actions, not switches, and stay unbound.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LayoutDefaults { get; } = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gallery.layout.info"] = "Shift+I",
            ["gallery.layout.hints"] = "Shift+N"
        });

    /// <summary>Whether <paramref name="gesture"/> is the form the scheme gives show / hide switches: Shift and exactly one letter.</summary>
    public static bool IsShiftLetter(ShortcutGesture gesture) =>
        gesture.Symbol == null && gesture.Modifiers.Count == 1 && gesture.Modifiers[0] == "Shift"
        && gesture.Key.Length == 1 && char.IsAsciiLetterUpper(gesture.Key[0]);

    /// <summary>Whether <paramref name="gesture"/> is one plain letter without any modifier.</summary>
    public static bool IsPlainLetter(ShortcutGesture gesture) =>
        gesture.Symbol == null && gesture.Modifiers.Count == 0 && gesture.Key.Length == 1 && char.IsAsciiLetterUpper(gesture.Key[0]);

    /// <summary>The default gestures of a layout command that the catalog does not carry yet (empty for every other command).</summary>
    internal static IReadOnlyList<ShortcutGesture> LayoutDefault(string commandId) =>
        LayoutDefaults.TryGetValue(commandId, out string? text) ? new[] { ShortcutGesture.Parse(text) } : Array.Empty<ShortcutGesture>();
}
