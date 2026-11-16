using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Input;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Commands;

public enum ShortcutPlatform { Windows, Linux, MacOS }
public sealed record ShortcutGesture(string Key, IReadOnlyList<string> Modifiers, string? Symbol = null)
{
    public static ShortcutGesture Parse(string text)
    {
        var parts = text.Split('+');
        string key = parts[^1];
        key = key switch { "," => "OemComma", "Plus" => "OemPlus", "Minus" => "OemMinus", _ => key };
        if (key.Length == 1 && char.IsAsciiDigit(key[0])) key = "D" + key;
        return new(key, Array.AsReadOnly(parts[..^1]));
    }
    public KeyGesture Resolve(ShortcutPlatform platform)
    {
        KeyModifiers mods = KeyModifiers.None;
        foreach (var m in Modifiers) mods |= m switch
        { "Primary" => platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control,
          "Control" or "Ctrl" => KeyModifiers.Control, "Meta" => KeyModifiers.Meta,
          "Alt" => KeyModifiers.Alt, "Shift" => KeyModifiers.Shift, _ => throw new ShortcutIssueReason("unknownModifier", "Unknown modifier: " + m, m).ToException() };
        if (Key is "ImeProcessed" or "DeadCharProcessed" or "System") throw new ShortcutIssueReason("imeKey", "IME keys cannot be shortcuts / IME-Tasten sind keine Kürzel").ToException();
        if (string.IsNullOrEmpty(Key) || !Enum.TryParse<Avalonia.Input.Key>(Key, out var key) || !Enum.IsDefined(key) || char.IsDigit(Key[0]) || key is Avalonia.Input.Key.None or Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl or Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt or Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift or Avalonia.Input.Key.LWin or Avalonia.Input.Key.RWin)
            throw new ShortcutIssueReason("unknownKey", "Unknown key or modifier-only gesture: " + Key, Key).ToException();
        return new(key, mods);
    }
    public string Display(ShortcutPlatform platform) =>
        string.Join(platform == ShortcutPlatform.MacOS ? "" : "+", DisplayParts(platform));

    /// <summary>The keys of <see cref="Display"/> one by one (modifiers, then the key): the keycaps of the shortcuts window.
    /// Joined with "+" (Apple: without separator) they give exactly <see cref="Display"/>.</summary>
    public IReadOnlyList<string> DisplayParts(ShortcutPlatform platform)
    {
        var resolved = Resolve(platform);
        if (platform == ShortcutPlatform.MacOS) return DisplayMac(resolved);
        string key = Key.StartsWith("D", StringComparison.Ordinal) && Key.Length == 2 && char.IsDigit(Key[1]) ? Key[1..] : Key;
        key = key switch
        {
            "OemComma" => ",", "OemPeriod" => ".", "OemPlus" => "+", "OemMinus" => "−",
            "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", _ => KeyNames.Localized(key) ?? key
        };
        // Key names follow the interface language (German: Strg, Umschalt, Entf, Pfeil hoch, Num 0; KeyNames); the stored gesture stays unchanged.
        return Array.AsReadOnly(Modifiers.Select(m => m is "Primary" or "Control" or "Ctrl" ? Texts.T("key.ctrl")
            : m == "Shift" ? Texts.T("key.shift") : m).Append(Symbol ?? key).ToArray());
    }

    // Apple's order and glyphs: ⌃ ⌥ ⇧ ⌘ directly before the key, no "+". A typed symbol such as "?" already carries
    // its Shift (⌘? and not ⇧⌘/). Function keys are marked because an Apple keyboard needs Fn for them by default.
    private IReadOnlyList<string> DisplayMac(KeyGesture resolved)
    {
        KeyModifiers mods = resolved.KeyModifiers;
        List<string> parts = new();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("⌃");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("⌥");
        if (mods.HasFlag(KeyModifiers.Shift) && Symbol == null) parts.Add("⇧");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("⌘");
        string key = Symbol ?? Key switch
        {
            "Back" => "⌫", "Delete" => "⌦", "Enter" or "Return" => "↩", "Escape" => "⎋", "Space" => "␣", "Tab" => "⇥",
            "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "PageUp" or "Prior" => "⇞", "PageDown" or "Next" => "⇟",
            "Home" => "↖", "End" => "↘", "OemComma" => ",", "OemPeriod" => ".", "OemPlus" => "+", "OemMinus" => "−", "Apps" => "Menu",
            "OemOpenBrackets" => "[", "OemCloseBrackets" => "]",
            _ when Key.Length == 2 && Key[0] == 'D' && char.IsDigit(Key[1]) => Key[1..],
            _ when Key.Length >= 2 && Key[0] == 'F' && int.TryParse(Key.AsSpan(1), out _) => Key + " (Fn)",
            _ => Key.ToUpperInvariant()
        };
        parts.Add(key);
        return parts.AsReadOnly();
    }
}
public sealed record ShortcutIssue(string CommandId, string Message, string? OtherCommandId = null, bool BlocksSave = true)
{
    // Message remains the legacy public diagnostic; presentation uses this retained
    // reason instead. Caller-created issues without a reason stay raw diagnostics.
    internal ShortcutIssueReason? Reason { get; init; }
    // Presentation provenance must not change the public record's value identity.
    public bool Equals(ShortcutIssue? other) => other is not null && CommandId == other.CommandId
        && Message == other.Message && OtherCommandId == other.OtherCommandId && BlocksSave == other.BlocksSave;
    public override int GetHashCode() => HashCode.Combine(CommandId, Message, OtherCommandId, BlocksSave);
    internal static ShortcutIssue FromReason(string commandId, ShortcutIssueReason reason,
        string? otherCommandId = null, bool blocksSave = true)
        => new(commandId, reason.LegacyMessage, otherCommandId, blocksSave) { Reason = reason };
}

// No translated strings or language state in validation. Preserve the original
// FormatException type/message and arbitrary framework diagnostics; never infer a
// host reason by parsing exception prose. Arguments are copied, read-only raw data.
internal sealed class ShortcutIssueReason
{
    private static readonly object ExceptionKey = new();
    internal ShortcutIssueReason(string? code, string legacyMessage, params string?[] arguments)
    { Code = code; LegacyMessage = legacyMessage; Arguments = Array.AsReadOnly(arguments.ToArray()); }
    internal string? Code { get; }
    internal string LegacyMessage { get; }
    internal IReadOnlyList<string?> Arguments { get; }
    internal FormatException ToException()
    {
        var exception = new FormatException(LegacyMessage);
        exception.Data[ExceptionKey] = this;
        return exception;
    }
    internal static ShortcutIssueReason FromException(Exception exception)
        => exception.Data[ExceptionKey] as ShortcutIssueReason ?? new(null, exception.Message);
}

/// <summary>Immutable validated view. No conflicting gesture has an arbitrary winner.</summary>
public sealed class EffectiveShortcutBindings
{
    private static long _nextVersion;
    /// <summary>Revision of the default table. Raise it, and list the command in
    /// <see cref="DefaultChangedInRevision"/>, whenever a default gesture is added to or changed on a command.</summary>
    public const int DefaultsRevision = 21;
    /// <summary>Command ids that existed in an earlier build and were removed on purpose (the previous/next media-panel keys).
    /// A saved binding for one is dead data, not a typo: <see cref="Build"/> skips it without an issue and the next save drops it
    /// (<see cref="ShortcutBindingsSection.WithoutRetired"/>). A truly unknown id is still reported and kept. The defaults revision does not change.</summary>
    internal static readonly IReadOnlySet<string> RetiredCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "media.panel.previous", "media.panel.next",
    };
    /// <summary>Commands whose default gesture set was introduced or changed after profiles with saved
    /// bindings may already exist, with the revision that did it. A new default on such a command yields to
    /// a saved binding of another command instead of silencing both; entries are never removed because a
    /// saved binding can date from any earlier revision (including frozen "add another shortcut" lists).</summary>
    private static readonly IReadOnlyDictionary<string, int> DefaultChangedInRevision = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["video.volume-up"] = 1, ["video.volume-down"] = 1,
        // (favorites.add and favorites.remove: revision 1, now listed with revision 20 below.)
        // (navigation.media, rules and sources: revision 2, now listed with revision 15 below.)
        ["navigation.statistics"] = 2, ["input.focus-media-search"] = 2, ["input.focus-settings-search"] = 2, ["input.focus-diagnostics-search"] = 2,
        ["thread.favorite-toggle"] = 2, ["thread.tags"] = 2,
        ["archive.filters-toggle"] = 2, ["board.filters"] = 2, ["media.select-all"] = 2, ["menu.context-open"] = 2,
        // macOS standards of revision 2 (Defaults(command, MacOS)).
        ["window.fullscreen"] = 2, ["viewer.shortcut-help"] = 2, ["watch.remove"] = 2, ["archive.delete-permanently"] = 2,
        ["thread.details"] = 2, ["watch.check"] = 2, ["watch.retry-media"] = 2, ["archive.previous-page"] = 2,
        ["archive.next-page"] = 2, ["media.return"] = 2,
        // New in revision 2: find next / previous in the large reader.
        ["reader.find-next"] = 2, ["reader.find-previous"] = 2,
        // New single keys and chords of the gallery and the media page.
        ["gallery.slideshow-toggle"] = 2, ["media.details"] = 2, ["media.rating.edit"] = 2,
        ["media.export-original"] = 2, ["media.show-in-folder"] = 2,
        // One Back gesture and one page gesture for several commands that never apply at the same time.
        ["reader.history-back"] = 2, ["gallery.return"] = 2, ["pagination.previous"] = 2, ["pagination.next"] = 2,
        ["window.minimize"] = 2,
        // New in revision 3: shortcuts for actions that had none (UX flows AP13).
        ["navigation.continue-reading"] = 3, ["import.open"] = 3, ["reader.show-new-posts"] = 3,
        ["watch.follow-live"] = 3, ["diagnostics.retry-selection"] = 3,
        // New in revision 4: Primary+6 and Primary+7 swapped (Plugins is the 6th page, Diagnostics the 7th, MainPageOrder), and media tags moved from
        // Primary+T to Primary+Shift+T (thread tags keep Primary+T). Both yield to a saved binding of another command, like every changed default.
        ["navigation.plugins"] = 4, ["navigation.diagnostics"] = 4, ["media.tags.edit"] = 4,
        // New in revision 5: viewer quick wins (seek, mute, speed, frame snapshot).
        // (video.mute: revision 5, now listed with revision 20 below.)
        ["video.seek-back"] = 5, ["video.seek-forward"] = 5, ["video.seek-back-far"] = 5,
        ["video.seek-forward-far"] = 5, ["video.speed-reset"] = 5,
        ["video.snapshot"] = 5,
        // New in revision 6: quick tagging in the gallery tag panel and the tag popover (QuickTagCommands). Not yet wired
        // commands are inactive (CommandDefinition.IsAwaitingHandler), their defaults already yield like any new default.
        ["tags.quick.next"] = 6, ["tags.quick.previous"] = 6, ["tags.quick.commit-next"] = 6, ["tags.quick.mark-next"] = 6,
        ["tags.quick.mark-previous"] = 6, ["tags.quick.undo"] = 6, ["tags.quick.thread-section"] = 6, ["tags.quick.stamp-apply"] = 6,
        ["tags.quick.suggestion-1"] = 6, ["tags.quick.suggestion-2"] = 6, ["tags.quick.suggestion-3"] = 6,
        ["tags.quick.suggestion-4"] = 6, ["tags.quick.suggestion-5"] = 6, ["tags.quick.suggestion-6"] = 6,
        ["tags.quick.suggestion-7"] = 6, ["tags.quick.suggestion-8"] = 6, ["tags.quick.suggestion-9"] = 6,
        // New in revision 7: Diagnostics and Statistics are segments of one Status page (MainPageOrder). Status takes Primary+7 and
        // yields to a saved binding of another command; Diagnostics and Statistics lose their digits (Primary+8 is free) and
        // keep any binding the user saved for them.
        // (navigation.status: revision 7, now listed with revision 15 below.)
        // New in revision 8: the own Booru page takes the previously free Primary+6. A persisted
        // navigation.plugins=Primary+6 override therefore keeps winning over the new default.
        // (navigation.booru: revision 8, now listed with revision 15 below.)
        // New in revision 9 (wave3: V1, V3, PT6, R8, M4 and PT3 each raised the table in their own branch; Status took 7, the Booru page 8): the video transport row
        // (A–B loop, next frame).
        ["video.ab-loop"] = 9,
        // New in revision 9: the gallery filmstrip toggle (L until revision 12 gave L to the video repeat; see below).
        // New in revision 9 (PT6, decision E2): Shift+1 to Shift+5 rate and Shift+0 removes the rating, in the gallery, the large reader's
        // media rows and the rating dialog; R stays the dialog. They yield to a saved binding of another command like every new default.
        ["media.rating.set-1"] = 9, ["media.rating.set-2"] = 9, ["media.rating.set-3"] = 9, ["media.rating.set-4"] = 9,
        ["media.rating.set-5"] = 9, ["media.rating.clear"] = 9,
        // New in revision 9: the reader's media sidebar (R8).
        ["reader.sidebar.toggle"] = 9, ["reader.sidebar.previous-media"] = 9, ["reader.sidebar.next-media"] = 9,
        // New in revision 9: the media page's focus mode (M4), Shift+F next to the favourite's F.
        ["media.focus-mode"] = 9,
        // New in revision 9 (PT3): the keys of the tagging mode in the gallery. They live in their own context ("gallery-tagging"), so they
        // never compete with Space, Enter and Backspace of the plain gallery, and yield to a saved binding like every new default.
        ["tags.queue.done-next"] = 9, ["tags.queue.skip"] = 9, ["tags.queue.back"] = 9, ["tags.queue.play-pause"] = 9,
        // New in revision 9 (PT2): the list keys of the tagging page. They act only in its queue list (context "tagging") and
        // yield to a saved binding of another command like every new default.
        ["tagging.continue-here"] = 9, ["tagging.move-up"] = 9, ["tagging.move-down"] = 9,
        // PT4 (card mode) joins the same revision 9 of the tagging mode (neither is released yet): K and the card keys 1 to 5.
        ["tags.queue.cards"] = 9, ["tags.queue.card-1"] = 9, ["tags.queue.card-2"] = 9, ["tags.queue.card-3"] = 9,
        ["tags.queue.card-4"] = 9, ["tags.queue.card-5"] = 9,
        // New in revision 9 (V4): turning the shown picture or video (Shift+R).
        ["gallery.rotate"] = 9,
        // PT7 (decision on the default key): Q puts the shown medium, or the selected media of the media page, in the tagging queue. Q is free in
        // the contexts it is active in (media page and gallery); it yields to a saved binding of another command like every new default.
        ["media.queue.add"] = 9,
        // G1 (Tom's E3 addition): Shift+T switches the gallery between View and Tag (the tagging mode). Shift+T is free in the gallery
        // (T shows the post in the thread, Primary+Shift+T opens the tag panel); it yields to a saved binding like every new default. The
        // wave-5 integration gives it revision 10, the first one after the defaults already in goal.
        ["gallery.mode-toggle"] = 10,
        // New in revision 11 (F1, footer setting): Ctrl/Cmd+1, 2, 3 ... follow the footer buttons the person chose, from left to right, then the hidden
        // pages (MainPageOrder.Numbering). Every main page can therefore get another digit than before, so each default yields to a saved binding
        // of another command (media, booru, rules, sources and status are listed above from earlier revisions).
        // (navigation.downloads, archive and tagging: revision 11, now listed with revision 15 below.)
        // TG1: Alt+Home and Alt+End send the selected entry of the tagging page to the start or the end of the queue (context "tagging").
        // The wave-6 integration puts them into revision 11 (F1's), the first one after wave 5's 10; they yield like every new default.
        ["tagging.move-first"] = 11, ["tagging.move-last"] = 11,
        // Revision 12 (video keys, on top of the footer package's revision 11): "," and "." seek by the step setting, L repeats the video, the next frame moved to Shift+. and the filmstrip
        // toggle to Shift+L (L is the video repeat). All of them yield to a saved binding of another command like every changed default.
        ["video.step-back"] = 12, ["video.step-forward"] = 12, ["video.loop"] = 12, ["video.frame-step"] = 12, ["gallery.filmstrip-toggle"] = 12,
        // Revision 13 (the gallery key scheme, GalleryShortcutScheme): Shift plus a letter shows or hides a block, plain letters act on the playback.
        // Changed: the post panel P to Shift+P, the slideshow S to D, mute Shift+M to M, the frame snapshot Shift+S to S (video.mute, video.snapshot and
        // gallery.slideshow-toggle are in the table since revisions 2 and 5; their entries stay). New: Shift+H hides the tools, Shift+O switches the
        // controls overlay, and Shift+I / Shift+N show the info line and the hint row of the Layout menu (GL3, applied when those commands exist). The card mode of the tagging
        // mode moved from K to Shift+K (the cards are a block).
        // "Add favorite" moved from M (now mute) to B; a binding saved for any of them stays untouched. (Revision 20 gave M back to "Add favorite", below; revision 21 dropped B.)
        ["gallery.context-toggle"] = 13, ["gallery.tools-toggle"] = 13, ["gallery.overlay-toggle"] = 13,
        ["gallery.layout.info"] = 13, ["gallery.layout.hints"] = 13,
        // Revision 14 (Tom's decision of 15.11.2026): video speed gets the extra keys X (slower) and C (faster), because [ and ] sit behind AltGr on
        // German and French layouts and Ctrl+Alt is not a valid gesture. [ ] and Backspace (normal speed) stay. Both commands are listed since revision 5;
        // the entry now says that a saved binding of another command on X or C also wins over the new key (the key alone yields, [ or ] stay).
        ["video.speed-down"] = 14, ["video.speed-up"] = 14,
        // Revision 15 (Tom's decision of 15.11.2026): every main page gets a fixed Ctrl/Cmd+Shift+letter next to its footer digit (PageKeyScheme: Downloads D, Archive A,
        // Media E, Tagging K, Booru B, Rules U, Plugins X, Status S). The ids are listed since revision 11 (the digits); the entry now also says that a saved binding
        // of another command on the letter wins over the new default (the letter alone yields, the digit stays). A page whose own gestures the person saved keeps them.
        ["navigation.downloads"] = 15, ["navigation.archive"] = 15, ["navigation.media"] = 15, ["navigation.tagging"] = 15,
        ["navigation.booru"] = 15, ["navigation.rules"] = 15, ["navigation.sources"] = 15, ["navigation.status"] = 15,
        // Revision 16 (interaction audit 1.1.0; revision 15 belongs to the overlay keys of feature/overlay-close-page-keys): Ctrl/Cmd+Plus and
        // Ctrl/Cmd+Minus step the media page's tile size (Ctrl+wheel does the same), Ctrl/Cmd+C copies the selected row of the Diagnostics and the
        // Statistics table (the context menu's "Copy details" / "Copy row"). All four yield to a saved binding of another command like every new default.
        ["media.tile-size.larger"] = 16, ["media.tile-size.smaller"] = 16, ["diagnostics.copy"] = 16, ["statistics.copy-row"] = 16,
        // Revision 17 (reader video keys): the gallery's play / pause, mute, repeat, seek step, speed and next frame keys also run in the reader on the selected
        // post's video (scope Reader added, no new gesture). Their gestures now share the reader's context, so a saved binding of a reader command on one of
        // them wins over the video key (play / pause is new in this table; the others are listed from earlier revisions).
        ["video.play-pause"] = 17,
        // Revision 18 (the Media page's media panel): the same nine video keys also run on the panel's video (scope Media added, no new gesture; their entries
        // above keep the revision that introduced them). On the Media page they give way to its own commands (ConflictContexts, MediaPanelKeys.Route), so a
        // saved Media binding on one of them neither silences the key in the gallery and the reader nor conflicts with it. New: Primary+Shift+M turns the
        // panel on or off, like the reader's media sidebar; it yields to a saved binding of another command like every new default.
        ["media.panel.toggle"] = 18,
        // Revision 19 (Tom's decision #125 of 15.11.2026, from the menu audit): frequent menu actions get a key. The tagging list marks done with D and
        // skipped with S; the reader hides the selected post with H and copies its link with Primary+Shift+C; Primary+Shift+C also copies a medium's file path
        // (media page, gallery, tagging list); Alt+M shows a thread or a tagging entry in Media; Primary+Enter opens the embedded reader's thread in the large
        // reader; Primary+Shift+O opens the gallery's medium with the default program (next to Primary+O, the folder). All of them yield to a saved binding of
        // another command, like every new default.
        ["tagging.mark-done"] = 19, ["tagging.mark-skipped"] = 19, ["post.hide"] = 19, ["post.copy-link"] = 19, ["media.copy-path"] = 19,
        ["thread.show-in-media"] = 19, ["tagging.show-in-media"] = 19, ["reader.expand"] = 19, ["media.open-external"] = 19,
        // Revision 20 (one revision for the quick-win packages of 15.11.2026; each entry yields to a saved binding of another command like every new default):
        // - "menus, delete, Esc": Delete (and Primary+Back on macOS) also acts on the media page, where it removes the selected media that are in the
        //   tagging queue from it (the same command as the tagging list's, now also in the context "media"; introduced in revision 9). It never deletes a file.
        ["tagging.remove"] = 20,
        // - "mouse and keyboard" (audit V4, V13): Ctrl/Cmd+Plus and Ctrl/Cmd+Minus step the reader's font size, Ctrl/Cmd+0 sets it back to Normal
        //   (Ctrl+wheel does the first two); F5 refreshes the Plugins page (macOS: Cmd+Option+R first and F5 as an extra; not Cmd+R, which
        //   is rules.edit-scope, also on the Plugins page).
        ["reader.font-size.larger"] = 20, ["reader.font-size.smaller"] = 20, ["reader.font-size.reset"] = 20, ["plugins.refresh"] = 20,
        // - reader Space and Enter on the selected post's medium: Enter, until now a fixed content key, is the command "Open medium of the post" (what a click
        //   on the medium does by the choice "Clicking a medium"). Space stays the video's play / pause.
        ["reader.media.activate"] = 20,
        // - tile sizes: Ctrl/Cmd+0 sets the medium tile size where Ctrl/Cmd+Plus and Minus step it (media page and the tagging grid, one setting:
        //   media.tile-size.reset is new; media.tile-size.larger/.smaller, revision 16, now also act in the context "tagging"), and the archive's media
        //   tiles get the same three keys for a size of their own (archive.media-tile-size.*, context "archive").
        ["media.tile-size.reset"] = 20, ["archive.media-tile-size.larger"] = 20, ["archive.media-tile-size.smaller"] = 20, ["archive.media-tile-size.reset"] = 20,
        // - favourites everywhere (Tom's decision of 15.11.2026): M adds and U removes a favourite, F toggles, uniformly in the reader (the medium of the
        //   shared resolver), its media sidebar, the archive's media, the Media page's grid and panel and the gallery (scopes Reader, Media, Archive and
        //   Gallery). (B, a second key of "Add favorite" from revision 13, went in revision 21.) Mute moved from M to Shift+M in every scope (Shift+M was free in all of them).
        //   The revision is extended rather than raised (20 is not released): the three ids only move here, so a saved binding of another command on M,
        //   Shift+M, U or B still wins over the new key, and a saved binding of one of them (a mute on M, say) stays exactly as it was.
        //   F (favorites.toggle) reaches the archive's media for the first time, so it yields there too.
        ["favorites.add"] = 20, ["favorites.remove"] = 20, ["favorites.toggle"] = 20, ["video.mute"] = 20,
        // Revision 21 (Tom's decision #132 of 16.11.2026, sweep S3): B is no longer a second default key of "Add favorite" ("einheitlich M und U"; B only
        // survived from revision 13, when M was mute, and made every star tooltip say "F / M / B"). Taking a default away needs no entry here: nothing
        // new can collide with a saved binding. What a saved profile holds stays exactly as it is: a binding saved for "Add favorite" that lists B
        // (the editor freezes the whole list when a person adds a key) keeps B, and a saved binding of another command on B no longer has a default to
        // silence it (B is free in the gallery, the reader, the Media page and the archive).
    };
    public static IReadOnlyCollection<string> YieldingDefaultCommandIds => DefaultChangedInRevision.Keys.ToArray();
    // Commands whose default changed only in the macOS table: on Windows and Linux their gesture is the old stable one
    // and keeps the ordinary symmetric conflict rule.
    private static readonly HashSet<string> MacOnlyDefaultChanges = new(StringComparer.Ordinal)
    {
        "window.fullscreen", "viewer.shortcut-help", "watch.remove", "archive.delete-permanently", "thread.details",
        "watch.check", "watch.retry-media", "archive.previous-page", "archive.next-page", "media.return", "window.minimize"
    };
    /// <summary>Whether the default of <paramref name="id"/> yields to saved bindings on <paramref name="platform"/>.</summary>
    public static bool YieldsOn(string id, ShortcutPlatform platform) =>
        DefaultChangedInRevision.ContainsKey(id) && (platform == ShortcutPlatform.MacOS || !MacOnlyDefaultChanges.Contains(id));
    /// <summary>Whether an issue deserves the startup warning: only what silences or blocks a binding
    /// (never the informational yield, unknown or not-implemented entries).</summary>
    public static bool NeedsStartupWarning(ShortcutIssue issue)
        => issue.BlocksSave || issue.Reason?.Code == "unsupportedSchema";
    public long Version { get; } = System.Threading.Interlocked.Increment(ref _nextVersion);
    public ShortcutPlatform Platform { get; }
    /// <summary>False when the profile switched the standard single-character shortcuts off.</summary>
    public bool SingleKeyShortcuts { get; }
    public IReadOnlyList<ShortcutIssue> Issues { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>> Gestures { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>> RequestedGestures { get; }
    /// <summary>A default gesture that a saved binding took away in some contexts only: it stays active in the others.</summary>
    private readonly record struct ContextYield(ShortcutGesture Gesture, IReadOnlyList<string> Contexts);
    private readonly Dictionary<string, List<ContextYield>> _contextYields;
    private EffectiveShortcutBindings(ShortcutPlatform platform, Dictionary<string, IReadOnlyList<ShortcutGesture>> gestures, List<ShortcutIssue> issues, Dictionary<string, IReadOnlyList<ShortcutGesture>> requested, bool singleKeys,
        Dictionary<string, List<ContextYield>>? contextYields = null)
    { Platform = platform; SingleKeyShortcuts = singleKeys; _contextYields = contextYields ?? new(StringComparer.Ordinal); Gestures = new ReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>>(gestures); Issues = issues.AsReadOnly(); RequestedGestures = new ReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>>(requested); }
    public static ShortcutPlatform CurrentPlatform => OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : OperatingSystem.IsWindows() ? ShortcutPlatform.Windows : ShortcutPlatform.Linux;
    public IReadOnlyList<ShortcutGesture> For(string id) => Gestures.TryGetValue(id, out var value) ? value : Array.Empty<ShortcutGesture>();
    public string? Display(string id) => For(id).Count == 0 ? null : string.Join(" / ", For(id).Select(g => g.Display(Platform)));
    [ThreadStatic] private static string? _keyContext;
    /// <summary>Tells every <see cref="Matches(KeyEventArgs, string)"/> on this thread, until the scope is disposed, which execution context (see
    /// <see cref="ExecutionContexts"/>) the key is handled in. The gallery's key handler wraps itself in it, so a default that a saved binding of another
    /// page took away stays active there.</summary>
    public static KeyContextScope EnterKeyContext(string context) => new(context);
    /// <summary>The execution context of the key being handled on this thread, or null outside any <see cref="EnterKeyContext"/> scope.</summary>
    internal static string? CurrentKeyContext => _keyContext;
    public readonly struct KeyContextScope : IDisposable
    {
        private readonly string? _previous;
        internal KeyContextScope(string context) { _previous = _keyContext; _keyContext = context; }
        public void Dispose() => _keyContext = _previous;
    }
    /// <summary>Whether the key is a gesture of <paramref name="id"/>. A default that a saved binding took away in some contexts only (see
    /// <see cref="YieldedContexts"/>) counts as not matching when the caller does not say where it runs (<see cref="EnterKeyContext"/>): a saved
    /// binding is never overridden.</summary>
    public bool Matches(KeyEventArgs e, string id) => Matches(e, id, _keyContext);
    /// <summary><see cref="Matches(KeyEventArgs, string)"/> for a caller that knows its execution context (see <see cref="ExecutionContexts"/>): a default that
    /// a saved binding took away elsewhere still matches here, where nothing else has the gesture.</summary>
    public bool Matches(KeyEventArgs e, string id, string? context) => For(id).Any(g =>
    {
        var resolved = g.Resolve(Platform);
        return resolved.KeyModifiers == e.KeyModifiers && (resolved.Key == e.Key || g.Symbol != null && g.Symbol == e.KeySymbol)
            && !IsYielded(id, g, context);
    });
    /// <summary>The contexts in which the gesture of <paramref name="id"/> gives way to a saved binding of another command (empty when it does not;
    /// a gesture that gives way everywhere is not in <see cref="Gestures"/> at all). The default is per command, not per context, so it stays active
    /// in every other context of the command.</summary>
    public IReadOnlyList<string> YieldedContexts(string id, ShortcutGesture gesture)
    {
        if (!_contextYields.TryGetValue(id, out var yields)) return Array.Empty<string>();
        return yields.Where(y => Equivalent(y.Gesture, gesture, Platform)).SelectMany(y => y.Contexts).Distinct(StringComparer.Ordinal).ToArray();
    }
    private bool IsYielded(string id, ShortcutGesture gesture, string? context)
    {
        if (_contextYields.Count == 0 || !_contextYields.ContainsKey(id)) return false;
        IReadOnlyList<string> yielded = YieldedContexts(id, gesture);
        return yielded.Count > 0 && (context == null || yielded.Contains(context, StringComparer.Ordinal));
    }
    public static IReadOnlyList<ShortcutGesture> Defaults(CommandDefinition command) => Defaults(command, CurrentPlatform);
    public static IReadOnlyList<ShortcutGesture> Defaults(CommandDefinition command, ShortcutPlatform platform)
    {
        if (platform == ShortcutPlatform.MacOS && MacDefaults(command.Id) is { } mac) return mac;
        var list = new List<ShortcutGesture>();
        // A main page's digit follows the footer (MainPageOrder.Numbering), not a fixed slot of the catalog.
        // Revision 15 adds the page's fixed letter (PageKeyScheme: Ctrl/Cmd+Shift+letter) behind the digit, so the digit stays the first gesture.
        if (MainPageOrder.Contains(command.Id))
        {
            if (MainPageOrder.Digit(command.Id) is int digit) list.Add(ShortcutGesture.Parse("Primary+" + digit));
            if (PageKeyScheme.Gesture(command.Id) is { } letter) list.Add(letter);
        }
        else if (command.Shortcut is not null && command.Shortcut != "Escape") list.Add(ShortcutGesture.Parse(command.Shortcut));
        switch (command.Id)
        {
            case "gallery.fit": list.Add(ShortcutGesture.Parse("NumPad0")); break;
            case "gallery.actual-size": list.Add(ShortcutGesture.Parse("NumPad1")); break;
            case "gallery.zoom-in": list.Add(ShortcutGesture.Parse("Add")); list.Add(ShortcutGesture.Parse("Shift+OemPlus")); break;
            case "gallery.zoom-out": list.Add(ShortcutGesture.Parse("Subtract")); break;
            case "media.tile-size.larger": list.Add(ShortcutGesture.Parse("Primary+Add")); break;
            case "media.tile-size.smaller": list.Add(ShortcutGesture.Parse("Primary+Subtract")); break;
            // The reader's font size (revision 20): the main keyboard's Plus and Minus (the catalog's gesture) and the number pad's, and the
            // number pad's 0 next to Primary+0.
            case "reader.font-size.larger": list.Add(ShortcutGesture.Parse("Primary+Add")); break;
            case "reader.font-size.smaller": list.Add(ShortcutGesture.Parse("Primary+Subtract")); break;
            case "reader.font-size.reset": list.Add(ShortcutGesture.Parse("Primary+NumPad0")); break;
            case "archive.media-tile-size.larger": list.Add(ShortcutGesture.Parse("Primary+Add")); break;
            case "archive.media-tile-size.smaller": list.Add(ShortcutGesture.Parse("Primary+Subtract")); break;
            case "media.tile-size.reset":
            case "archive.media-tile-size.reset": list.Add(ShortcutGesture.Parse("Primary+NumPad0")); break;
            case "viewer.shortcut-help": list.Add(new("OemQuestion", new[] { "Shift" }, "?")); break;
            case "menu.context-open": list.Add(ShortcutGesture.Parse("Apps")); break;
            case "reader.find-next": list.Add(ShortcutGesture.Parse("Primary+G")); break;
            case "reader.find-previous": list.Add(ShortcutGesture.Parse("Primary+Shift+G")); break;
            // Speed: [ and ] (the catalog's key) plus X and C (revision 14), which need no AltGr on German and French layouts. Shift+, is free
            // (no previous frame) and Shift+. is the next frame, so no "<" / ">" alias.
            case "video.speed-down": list.Add(ShortcutGesture.Parse("X")); break;
            case "video.speed-up": list.Add(ShortcutGesture.Parse("C")); break;
            // Revision 20: M adds a favourite everywhere (the catalog's key). Revision 21 dropped B, the gallery's "add" key of revision 13: M is the only default.
            default: list.AddRange(GalleryShortcutScheme.LayoutDefault(command.Id)); break;
        }
        return list.AsReadOnly();
    }

    // macOS standards that differ from Windows/Linux (see the macOS shortcut note): never Fn as the only way (the F-key
    // and forward-Delete gestures stay as extras), ⌘ for the main modifier, the Apple delete key is Key.Back.
    // The whole list replaces the Windows/Linux one for that command; every entry here is also a yielding default.
    private static IReadOnlyList<ShortcutGesture>? MacDefaults(string id)
    {
        static IReadOnlyList<ShortcutGesture> Of(params string[] gestures) => Array.AsReadOnly(gestures.Select(ShortcutGesture.Parse).ToArray());
        return id switch
        {
            "window.fullscreen" => Of("Control+Primary+F", "F11"),
            "viewer.shortcut-help" => Array.AsReadOnly(new[]
            {
                new ShortcutGesture("OemQuestion", new[] { "Primary", "Shift" }, "?"),
                new ShortcutGesture("OemQuestion", new[] { "Shift" }, "?"), ShortcutGesture.Parse("F1")
            }),
            "watch.remove" => Of("Primary+Back", "Delete"),
            "archive.delete-permanently" => Of("Primary+Shift+Back", "Shift+Delete"),
            "thread.details" => Of("Primary+I", "Alt+Enter"),
            "watch.check" => Of("Primary+Alt+R", "F5"),
            "watch.retry-media" => Of("Primary+Alt+Shift+R", "Shift+F5"),
            "archive.previous-page" => Of("Primary+Alt+Left", "Alt+PageUp"),
            "archive.next-page" => Of("Primary+Alt+Right", "Alt+PageDown"),
            "media.return" or "reader.history-back" or "gallery.return" => Of("Primary+Left"),
            "window.minimize" => Of("Primary+M"),
            // Revision 20: F5 needs Fn on an Apple keyboard, so the Plugins page's refresh gets Cmd+Option+R first, like "Check now" (watch.check); F5 stays an
            // extra. Not Cmd+R: that is rules.edit-scope, also on the Plugins page.
            "plugins.refresh" => Of("Primary+Alt+R", "F5"),
            "pagination.previous" => Of("Primary+Alt+Left", "Alt+PageUp"),
            "pagination.next" => Of("Primary+Alt+Right", "Alt+PageDown"),
            "media.details" => Of("I", "Primary+I"),
            "reader.find-next" => Of("Primary+G", "F3"),
            "reader.find-previous" => Of("Primary+Shift+G", "Shift+F3"),
            // Fn is never the only way on a Mac: the Diagnostics retry shares the gesture of "Retry missing media"
            // (they never apply on the same page).
            "diagnostics.retry-selection" => Of("Primary+Alt+Shift+R"),
            // TG1 tagging page: Home/End need Fn on an Apple keyboard, so ⌥⌘↑ / ⌥⌘↓ (the Mac's "to the start / end" arrows next to ⌥↑/⌥↓
            // of one step) come first and Alt+Home/Alt+End stay extras.
            "tagging.move-first" => Of("Primary+Alt+Up", "Alt+Home"),
            "tagging.move-last" => Of("Primary+Alt+Down", "Alt+End"),
            // Quick tagging: an Apple keyboard has no PageUp/PageDown (Fn+arrow), so the ⌘-arrow is the first gesture there.
            "tags.quick.next" => Of("Primary+Down", "PageDown"),
            "tags.quick.previous" => Of("Primary+Up", "PageUp"),
            "tags.quick.mark-next" => Of("Primary+Shift+Down", "Shift+PageDown"),
            "tags.quick.mark-previous" => Of("Primary+Shift+Up", "Shift+PageUp"),
            // The Apple delete key is Key.Back; forward Delete needs Fn and stays an extra (like watch.remove).
            "tagging.remove" => Of("Primary+Back", "Delete"),
            _ => null
        };
    }

    /// <summary>A bare character key (letter, digit, +/−, typed symbol), alone or with Shift: what a switch for
    /// single-key shortcuts turns off. Enter, Space, arrows, Delete, F-keys and every gesture with Ctrl/Alt/⌘ stay.</summary>
    public static bool IsSingleCharacterKey(ShortcutGesture gesture)
    {
        if (gesture.Modifiers.Any(m => m != "Shift")) return false;
        string key = gesture.Key;
        return gesture.Symbol != null || key.Length == 1 && char.IsAsciiLetter(key[0])
            || key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1])
            || key.Length == 7 && key.StartsWith("NumPad", StringComparison.Ordinal) && char.IsAsciiDigit(key[6])
            || key is "Add" or "Subtract" or "Multiply" or "Divide" or "OemPlus" or "OemMinus" or "OemComma" or "OemPeriod" or "OemQuestion"
                or "OemOpenBrackets" or "OemCloseBrackets";
    }
    /// <summary>
    /// The gestures macOS takes for itself before any application sees them (Tom's decision list of sweep S3, 16.11.2026), so a binding on one could never
    /// fire and the editor refuses it: ⌘H hide and ⌥⌘H hide others, ⌘Q quit, ⇧⌘Q log out and ⌃⌘Q lock the screen, ⌥⌘D show or hide the Dock, ⇧⌘3 / ⇧⌘4 /
    /// ⇧⌘5 screenshots, ⌃ with an arrow (Spaces and Mission Control) and ⌃Space (the input source). ⌘Space, Tab, Escape and Alt+F4 are refused on their own
    /// (<see cref="InvalidIssueReason"/>). ⌘M, ⌘W, ⌘, and ⌃⌘F reach the application and stay allowed (the defaults use them).
    /// </summary>
    internal static bool IsMacSystemKey(Key key, KeyModifiers modifiers)
    {
        const KeyModifiers Cmd = KeyModifiers.Meta, Opt = KeyModifiers.Alt, Ctrl = KeyModifiers.Control, Shift = KeyModifiers.Shift;
        return (modifiers, key) switch
        {
            (Cmd, Key.H) or (Cmd | Opt, Key.H) => true,
            (Cmd, Key.Q) or (Cmd | Shift, Key.Q) or (Cmd | Ctrl, Key.Q) => true,
            (Cmd | Opt, Key.D) => true,
            (Cmd | Shift, Key.D3 or Key.D4 or Key.D5) => true,
            (Ctrl, Key.Left or Key.Right or Key.Up or Key.Down) => true,
            (Ctrl, Key.Space) => true,
            _ => false
        };
    }
    public static string? InvalidReason(ShortcutGesture gesture, ShortcutPlatform platform)
        => InvalidIssueReason(gesture, platform)?.LegacyMessage;
    internal static ShortcutIssueReason? InvalidIssueReason(ShortcutGesture gesture, ShortcutPlatform platform)
    {
        try
        {
            if (gesture.Modifiers.Count != gesture.Modifiers.Distinct(StringComparer.Ordinal).Count()) return new("duplicateModifiers", "Duplicate modifiers / Doppelte Modifikatoren");
            var g = gesture.Resolve(platform);
            if (gesture.Modifiers.Contains("Primary") && gesture.Modifiers.Contains(platform == ShortcutPlatform.MacOS ? "Meta" : "Control")) return new("repeatedPlatformModifier", "Repeated platform modifier / Doppelte Plattformtaste");
            if (g.Key is Key.Tab or Key.Escape) return new("fixedNavigation", "Tab and Escape are fixed navigation / Tab und Escape bleiben feste Navigation");
            if ((g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt)) return new("altGrText", "Ctrl+Alt can produce AltGr text / Strg+Alt kann AltGr-Text erzeugen");
            if (platform != ShortcutPlatform.MacOS && g.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
                platform == ShortcutPlatform.MacOS && g.KeyModifiers.HasFlag(KeyModifiers.Meta) && g.Key == Key.Space ||
                g.KeyModifiers.HasFlag(KeyModifiers.Alt) && g.Key == Key.F4)
                return new("reservedSystem", "Reserved system shortcut / Reserviertes Systemkürzel");
            if (platform == ShortcutPlatform.MacOS && IsMacSystemKey(g.Key, g.KeyModifiers))
                return new("reservedMacSystem", "Reserved macOS system shortcut / Reserviertes macOS-Systemkürzel");
            if (gesture.Symbol != null && gesture.Symbol is not "?" and not "+") return new("unsupportedSymbol", "Unsupported symbol / Nicht unterstütztes Symbol");
            return null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException) { return ShortcutIssueReason.FromException(ex); }
    }
    public static EffectiveShortcutBindings Build(ShortcutBindingsSection? section = null, ShortcutPlatform? platform = null)
    {
        var os = platform ?? CurrentPlatform;
        var values = CommandCatalog.All.ToDictionary(c => c.Id, c => Defaults(c, os), StringComparer.Ordinal);
        // Optional accessibility switch (WCAG 2.1.4): standard single-key gestures off. Saved bindings are the user's own and stay.
        bool singleKeys = section?.SingleKeyShortcuts ?? true;
        if (!singleKeys)
            foreach (string id in values.Keys.ToArray())
                values[id] = values[id].Where(g => !IsSingleCharacterKey(g)).ToList().AsReadOnly();
        var issues = new List<ShortcutIssue>();
        section ??= new();
        if (!section.IsEditable) issues.Add(ShortcutIssue.FromReason("", new("unsupportedSchema", "Unsupported shortcut schema; defaults active / Unbekanntes Kürzelschema; Standards aktiv"), blocksSave: false));
        else
        {
            var entries = section.Overrides.ToArray();
            foreach (var entry in entries)
            {
                if (!values.ContainsKey(entry.Name))
                {
                    if (RetiredCommands.Contains(entry.Name)) continue;
                    issues.Add(ShortcutIssue.FromReason(entry.Name, new("unknownAction", "Unknown action retained / Unbekannte Aktion erhalten"), blocksSave: false));
                    continue;
                }
                values[entry.Name] = Array.Empty<ShortcutGesture>();
                try
                {
                    if (entries.Length > 512 || entry.Name.Length > 128) throw new ShortcutIssueReason("limitsExceeded", "Shortcut limits exceeded / Kürzelgrenzen überschritten").ToException();
                    var raw = entry.Value;
                    var mode = raw.GetProperty("Mode").GetString();
                    var array = raw.GetProperty("Gestures");
                    if (array.GetArrayLength() > 8 || mode is not "Replace" and not "Unbound" || mode == "Unbound" && array.GetArrayLength() != 0 || mode == "Replace" && array.GetArrayLength() == 0) throw new ShortcutIssueReason("invalidMode", "Invalid binding mode / Ungültiger Belegungsmodus").ToException();
                    var gestures = new List<ShortcutGesture>();
                    foreach (var g in array.EnumerateArray())
                    {
                        var gesture = new ShortcutGesture(g.GetProperty("Key").GetString()!, Array.AsReadOnly(g.GetProperty("Modifiers").EnumerateArray().Select(m => m.GetString()!).ToArray()), g.TryGetProperty("Symbol", out var symbol) ? symbol.GetString() : null);
                        if (InvalidIssueReason(gesture, os) is { } reason) throw reason.ToException();
                        gestures.Add(gesture);
                    }
                    values[entry.Name] = gestures.AsReadOnly();
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or NullReferenceException)
                { issues.Add(ShortcutIssue.FromReason(entry.Name, ShortcutIssueReason.FromException(ex))); }
            }
        }
        // New or changed defaults did not exist in older profiles. They must not disable an
        // existing explicit effective gesture merely by being introduced on upgrade.
        // This is deliberately NOT a general priority rule: two explicit bindings (or
        // existing conflicts) still receive the ordinary symmetric conflict rejection.
        // A default is per command, not per context. When the saved binding collides in only some of the command's contexts (a saved archive command on F
        // against favorites.toggle, which also runs in the gallery), the default stays active in the others and records where it gives way.
        var contextYields = new Dictionary<string, List<ContextYield>>(StringComparer.Ordinal);
        if (section.IsEditable)
        foreach (string id in DefaultChangedInRevision.Keys)
        {
            if (section.HasOverride(id) || !values.ContainsKey(id) || !YieldsOn(id, os)) continue;
            var retained = new List<ShortcutGesture>();
            IReadOnlyList<string> own = ConflictContexts(id);
            foreach (ShortcutGesture gesture in values[id])
            {
                string[] owners = CommandCatalog.All.Where(c => c.IsImplemented && c.Id != id
                    && section.HasOverride(c.Id) && ContextsOverlap(id, c.Id)
                    && values[c.Id].Any(g => Equivalent(gesture, g, os))).Select(c => c.Id).ToArray();
                if (owners.Length == 0) { retained.Add(gesture); continue; }
                // Where the saved binding works: all execution contexts of its command, also those where a video key gives way to its page's commands
                // (ConflictContexts), because a default that kept the key there would take it from the saved binding.
                string[] yielded = own.Where(context => owners.Any(owner => ContextsOverlap(new[] { context }, ExecutionContexts(owner)))).ToArray();
                if (yielded.Length < own.Count)
                {
                    retained.Add(gesture);
                    if (!contextYields.TryGetValue(id, out var list)) contextYields[id] = list = new();
                    list.Add(new(gesture, Array.AsReadOnly(yielded)));
                }
                issues.Add(ShortcutIssue.FromReason(id,
                    new("defaultYields", "New default yields to your saved binding / Neuer Standard lässt deine gespeicherte Belegung unverändert"),
                    owners[0], blocksSave: false));
            }
            values[id] = retained.AsReadOnly();
        }
        foreach (string id in values.Keys.ToArray()) values[id] = Array.AsReadOnly(values[id].Select(g => new ShortcutGesture(g.Key, Array.AsReadOnly(g.Modifiers.ToArray()), g.Symbol)).ToArray());
        var requested = new Dictionary<string, IReadOnlyList<ShortcutGesture>>(values, StringComparer.Ordinal);
        foreach (var command in CommandCatalog.All.Where(command => !command.IsImplemented))
        {
            // A command that only waits for its handler keeps its default silently; a saved binding of the user is still reported.
            if (values[command.Id].Count > 0 && !(command.IsAwaitingHandler && !section.HasOverride(command.Id)))
                issues.Add(ShortcutIssue.FromReason(command.Id, new("notImplemented", "Action not implemented; binding retained but inactive / Aktion nicht implementiert; Belegung erhalten, aber inaktiv"), blocksSave: false));
            values[command.Id] = Array.Empty<ShortcutGesture>();
        }
        var rejected = new Dictionary<string, HashSet<ShortcutGesture>>(StringComparer.Ordinal);
        var ids = values.Keys.ToArray();
        for (int a = 0; a < ids.Length; a++) for (int b = a + 1; b < ids.Length; b++)
        {
            if (!ContextsOverlap(ids[a], ids[b])) continue;
            foreach (var first in values[ids[a]]) foreach (var second in values[ids[b]])
                if (Equivalent(first, second, os) && ContextsOverlap(ContextsOf(ids[a], first), ContextsOf(ids[b], second)))
                {
                    issues.Add(ShortcutIssue.FromReason(ids[a], new("conflictingActions", "Conflicting actions / Kollidierende Aktionen"), ids[b]));
                    if (!rejected.ContainsKey(ids[a])) rejected[ids[a]] = new();
                    if (!rejected.ContainsKey(ids[b])) rejected[ids[b]] = new();
                    rejected[ids[a]].Add(first); rejected[ids[b]].Add(second);
                }
        }
        foreach (var pair in rejected) values[pair.Key] = Array.AsReadOnly(values[pair.Key].Where(g => !pair.Value.Contains(g)).ToArray());
        return new(os, values, issues, requested, singleKeys, contextYields);

        // The contexts in which a gesture competes: those of its command, less the ones where it gave way to a saved binding.
        IReadOnlyList<string> ContextsOf(string id, ShortcutGesture gesture)
        {
            IReadOnlyList<string> contexts = ConflictContexts(id);
            if (!contextYields.TryGetValue(id, out var yields)) return contexts;
            var gone = yields.Where(y => Equivalent(y.Gesture, gesture, os)).SelectMany(y => y.Contexts).ToHashSet(StringComparer.Ordinal);
            return gone.Count == 0 ? contexts : contexts.Where(context => !gone.Contains(context)).ToArray();
        }
    }
    public static bool Equivalent(ShortcutGesture a, ShortcutGesture b, ShortcutPlatform platform)
    { var x = a.Resolve(platform); var y = b.Resolve(platform); return x.KeyModifiers == y.KeyModifiers && (x.Key == y.Key || a.Symbol != null && a.Symbol == b.Symbol); }
    public static bool ContextsOverlap(string first, string second) => ContextsOverlap(ConflictContexts(first), ConflictContexts(second));
    private static bool ContextsOverlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        static bool OnlyModal(IReadOnlyList<string> contexts) => contexts.All(c => c.StartsWith("dialog:", StringComparison.Ordinal));
        return a.Contains("shell") && !OnlyModal(b) || b.Contains("shell") && !OnlyModal(a) || a.Intersect(b).Any();
    }
    /// <summary>The presentation context of the video keys that the Media page routes to its media panel (CommandDefinition.Contexts).</summary>
    public const string MediaPanelVideoKeys = "media-video-keys";

    /// <summary>The presentation context of the video keys that the reader routes to the selected post's video or its media sidebar (CommandDefinition.Contexts).</summary>
    public const string ReaderPostVideoKeys = "reader-video-keys";

    /// <summary>
    /// The contexts in which a gesture of <paramref name="id"/> competes with other commands: its <see cref="ExecutionContexts"/>, except the Media page for
    /// the video keys of its media panel and the reader for the video keys of its selected post. Both pages route those keys only after their own commands
    /// (MediaPanelKeys.Route, ReaderVideoKeys.ClassifyUnclaimed), so there a Media or reader command on the same gesture simply wins: it neither conflicts
    /// with the video key nor makes it yield, which would silence it in the gallery (and on the other page) as well (the defaults are per command, not per context).
    /// </summary>
    public static IReadOnlyList<string> ConflictContexts(string id)
    {
        IReadOnlyList<string> contexts = ExecutionContexts(id);
        IReadOnlyList<string> marks = CommandCatalog.Get(id).Contexts;
        bool media = marks.Contains(MediaPanelVideoKeys, StringComparer.Ordinal), reader = marks.Contains(ReaderPostVideoKeys, StringComparer.Ordinal);
        return media || reader
            ? contexts.Where(context => !(media && context == "media" || reader && context == "reader")).ToArray() : contexts;
    }

    public static IReadOnlyList<string> ExecutionContexts(string id)
    {
        var c = CommandCatalog.Get(id);
        if (c.ActiveContexts != null) return c.ActiveContexts;
        // Execution surfaces, not presentation labels or scope bits. A command may
        // be available in both its owning page and a separate modal window.
        if (id.StartsWith("sections.", StringComparison.Ordinal)) return new[] { "settings", "plugins" };
        if (id == "reader.skip-unavailable-group-member") return new[] { "reader" };
        if (id == "reader-groups.open-manager") return new[] { "shell" };
        if (id == "archive.saved-searches.save-current") return new[] { "archive", "dialog:saved-searches" };
        if (id.StartsWith("rules.template-", StringComparison.Ordinal)) return new[] { "rules" };
        if (id == "media.rating.manage") return new[] { "media", "archive", "gallery" };
        if (id == "media.rating.remove-selected") return new[] { "dialog:media-ratings-manager" };
        if (id == "media.rating.cleanup-retry") return new[] { "dialog:archive-deletion" };
        if (id == "media.rating.edit") return new[] { "media", "reader", "gallery" };
        // Ctrl+O is one gesture with two readings: the folder of the selected thread (archive, downloads) and, in the media
        // page and the gallery, the folder of the shown medium (media.show-in-folder). Neither claims the other's page.
        if (id == "location.open-folder") return new[] { "archive", "dialog", "downloads" };
        if (id == "media.rating.clear" || id.StartsWith("media.rating.set-", StringComparison.Ordinal))
            return new[] { "media", "reader", "gallery" };
        if (id.StartsWith("navigation.") || id is "session.pause" or "window.fullscreen" or "input.focus-url" or "viewer.shortcut-help" or "workspace.view-cycle") return new[] { "shell" };
        // The large reader owns thread find; the archive search box is not reachable from it.
        if (id is "reader.find" or "reader.find-next" or "reader.find-previous") return new[] { "large-reader" };
        if (id == "input.focus-search") return new[] { "archive", "reader", "about" };
        if (id == "thread.read") return new[] { "archive", "downloads" };
        if (c.Contexts.Any(x => x.StartsWith("reader-groups"))) return new[] { "dialog:reader-groups" };
        if (c.Scope == CommandScope.Dialog)
        {
            string family = id.StartsWith("archive.saved-searches.", StringComparison.Ordinal) ? "saved-searches"
                : id.StartsWith("post-bookmarks.", StringComparison.Ordinal) ? "post-bookmarks"
                : id.Split('.')[0];
            return new[] { "dialog:" + family };
        }
        if (id.StartsWith("plugins.", StringComparison.Ordinal)) return new[] { "plugins" };
        if (id.StartsWith("settings.", StringComparison.Ordinal)) return new[] { "settings" };
        if (id.StartsWith("rules.", StringComparison.Ordinal) && id != "rules.edit-scope") return new[] { "rules" };
        return c.Scope.ToString().Split(", ").Where(x => x != "Application" && x != "None").Select(x => x.ToLowerInvariant()).DefaultIfEmpty("shell").ToArray();
    }
}
