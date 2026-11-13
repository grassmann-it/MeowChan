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
    public string Display(ShortcutPlatform platform)
    {
        var resolved = Resolve(platform);
        if (platform == ShortcutPlatform.MacOS) return DisplayMac(resolved);
        string key = Key.StartsWith("D", StringComparison.Ordinal) && Key.Length == 2 && char.IsDigit(Key[1]) ? Key[1..] : Key;
        key = key switch { "OemComma" => ",", "OemPlus" => "+", "OemMinus" => "−", "Apps" => "Menu", _ => key };
        // Key names follow the interface language (German: Strg, Umschalt); the stored gesture stays unchanged.
        string prefix = string.Join("+", Modifiers.Select(m => m is "Primary" or "Control" or "Ctrl" ? Texts.T("key.ctrl")
            : m == "Shift" ? Texts.T("key.shift") : m));
        return prefix.Length == 0 ? Symbol ?? key : prefix + "+" + (Symbol ?? key);
    }

    // Apple's order and glyphs: ⌃ ⌥ ⇧ ⌘ directly before the key, no "+". A typed symbol such as "?" already carries
    // its Shift (⌘? and not ⇧⌘/). Function keys are marked because an Apple keyboard needs Fn for them by default.
    private string DisplayMac(KeyGesture resolved)
    {
        KeyModifiers mods = resolved.KeyModifiers;
        string prefix = (mods.HasFlag(KeyModifiers.Control) ? "⌃" : "") + (mods.HasFlag(KeyModifiers.Alt) ? "⌥" : "")
            + (mods.HasFlag(KeyModifiers.Shift) && Symbol == null ? "⇧" : "") + (mods.HasFlag(KeyModifiers.Meta) ? "⌘" : "");
        string key = Symbol ?? Key switch
        {
            "Back" => "⌫", "Delete" => "⌦", "Enter" or "Return" => "↩", "Escape" => "⎋", "Space" => "␣", "Tab" => "⇥",
            "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "PageUp" or "Prior" => "⇞", "PageDown" or "Next" => "⇟",
            "Home" => "↖", "End" => "↘", "OemComma" => ",", "OemPlus" => "+", "OemMinus" => "−", "Apps" => "Menu",
            _ when Key.Length == 2 && Key[0] == 'D' && char.IsDigit(Key[1]) => Key[1..],
            _ when Key.Length >= 2 && Key[0] == 'F' && int.TryParse(Key.AsSpan(1), out _) => Key + " (Fn)",
            _ => Key.ToUpperInvariant()
        };
        return prefix + key;
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
    public const int DefaultsRevision = 4;
    /// <summary>Commands whose default gesture set was introduced or changed after profiles with saved
    /// bindings may already exist, with the revision that did it. A new default on such a command yields to
    /// a saved binding of another command instead of silencing both; entries are never removed because a
    /// saved binding can date from any earlier revision (including frozen "add another shortcut" lists).</summary>
    private static readonly IReadOnlyDictionary<string, int> DefaultChangedInRevision = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["video.volume-up"] = 1, ["video.volume-down"] = 1, ["favorites.add"] = 1, ["favorites.remove"] = 1,
        ["navigation.media"] = 2, ["navigation.rules"] = 2, ["navigation.sources"] = 2,
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
    private EffectiveShortcutBindings(ShortcutPlatform platform, Dictionary<string, IReadOnlyList<ShortcutGesture>> gestures, List<ShortcutIssue> issues, Dictionary<string, IReadOnlyList<ShortcutGesture>> requested, bool singleKeys)
    { Platform = platform; SingleKeyShortcuts = singleKeys; Gestures = new ReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>>(gestures); Issues = issues.AsReadOnly(); RequestedGestures = new ReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>>(requested); }
    public static ShortcutPlatform CurrentPlatform => OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : OperatingSystem.IsWindows() ? ShortcutPlatform.Windows : ShortcutPlatform.Linux;
    public IReadOnlyList<ShortcutGesture> For(string id) => Gestures.TryGetValue(id, out var value) ? value : Array.Empty<ShortcutGesture>();
    public string? Display(string id) => For(id).Count == 0 ? null : string.Join(" / ", For(id).Select(g => g.Display(Platform)));
    public bool Matches(KeyEventArgs e, string id) => For(id).Any(g =>
    {
        var resolved = g.Resolve(Platform);
        return resolved.KeyModifiers == e.KeyModifiers && (resolved.Key == e.Key || g.Symbol != null && g.Symbol == e.KeySymbol);
    });
    public static IReadOnlyList<ShortcutGesture> Defaults(CommandDefinition command) => Defaults(command, CurrentPlatform);
    public static IReadOnlyList<ShortcutGesture> Defaults(CommandDefinition command, ShortcutPlatform platform)
    {
        if (platform == ShortcutPlatform.MacOS && MacDefaults(command.Id) is { } mac) return mac;
        var list = new List<ShortcutGesture>();
        if (command.Shortcut is not null && command.Shortcut != "Escape") list.Add(ShortcutGesture.Parse(command.Shortcut));
        switch (command.Id)
        {
            case "gallery.fit": list.Add(ShortcutGesture.Parse("NumPad0")); break;
            case "gallery.actual-size": list.Add(ShortcutGesture.Parse("NumPad1")); break;
            case "gallery.zoom-in": list.Add(ShortcutGesture.Parse("Add")); list.Add(ShortcutGesture.Parse("Shift+OemPlus")); break;
            case "gallery.zoom-out": list.Add(ShortcutGesture.Parse("Subtract")); break;
            case "viewer.shortcut-help": list.Add(new("OemQuestion", new[] { "Shift" }, "?")); break;
            case "menu.context-open": list.Add(ShortcutGesture.Parse("Apps")); break;
            case "reader.find-next": list.Add(ShortcutGesture.Parse("Primary+G")); break;
            case "reader.find-previous": list.Add(ShortcutGesture.Parse("Primary+Shift+G")); break;
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
            "pagination.previous" => Of("Primary+Alt+Left", "Alt+PageUp"),
            "pagination.next" => Of("Primary+Alt+Right", "Alt+PageDown"),
            "media.details" => Of("I", "Primary+I"),
            "reader.find-next" => Of("Primary+G", "F3"),
            "reader.find-previous" => Of("Primary+Shift+G", "Shift+F3"),
            // Fn is never the only way on a Mac: the Diagnostics retry shares the gesture of "Retry missing media"
            // (they never apply on the same page).
            "diagnostics.retry-selection" => Of("Primary+Alt+Shift+R"),
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
            || key is "Add" or "Subtract" or "Multiply" or "Divide" or "OemPlus" or "OemMinus" or "OemComma" or "OemPeriod" or "OemQuestion";
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
                if (!values.ContainsKey(entry.Name)) { issues.Add(ShortcutIssue.FromReason(entry.Name, new("unknownAction", "Unknown action retained / Unbekannte Aktion erhalten"), blocksSave: false)); continue; }
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
        if (section.IsEditable)
        foreach (string id in DefaultChangedInRevision.Keys)
        {
            if (section.HasOverride(id) || !values.ContainsKey(id) || !YieldsOn(id, os)) continue;
            var retained = new List<ShortcutGesture>();
            foreach (ShortcutGesture gesture in values[id])
            {
                string? owner = CommandCatalog.All.Where(c => c.IsImplemented && c.Id != id
                    && section.HasOverride(c.Id) && ContextsOverlap(id, c.Id))
                    .Select(c => c.Id).FirstOrDefault(other => values[other].Any(g => Equivalent(gesture, g, os)));
                if (owner is null) retained.Add(gesture);
                else issues.Add(ShortcutIssue.FromReason(id,
                    new("defaultYields", "New default yields to your saved binding / Neuer Standard lässt deine gespeicherte Belegung unverändert"),
                    owner, blocksSave: false));
            }
            values[id] = retained.AsReadOnly();
        }
        foreach (string id in values.Keys.ToArray()) values[id] = Array.AsReadOnly(values[id].Select(g => new ShortcutGesture(g.Key, Array.AsReadOnly(g.Modifiers.ToArray()), g.Symbol)).ToArray());
        var requested = new Dictionary<string, IReadOnlyList<ShortcutGesture>>(values, StringComparer.Ordinal);
        foreach (var command in CommandCatalog.All.Where(command => !command.IsImplemented))
        {
            if (values[command.Id].Count > 0)
                issues.Add(ShortcutIssue.FromReason(command.Id, new("notImplemented", "Action not implemented; binding retained but inactive / Aktion nicht implementiert; Belegung erhalten, aber inaktiv"), blocksSave: false));
            values[command.Id] = Array.Empty<ShortcutGesture>();
        }
        var rejected = new Dictionary<string, HashSet<ShortcutGesture>>(StringComparer.Ordinal);
        var ids = values.Keys.ToArray();
        for (int a = 0; a < ids.Length; a++) for (int b = a + 1; b < ids.Length; b++)
        {
            if (!ContextsOverlap(ids[a], ids[b])) continue;
            foreach (var first in values[ids[a]]) foreach (var second in values[ids[b]])
                if (Equivalent(first, second, os))
                {
                    issues.Add(ShortcutIssue.FromReason(ids[a], new("conflictingActions", "Conflicting actions / Kollidierende Aktionen"), ids[b]));
                    if (!rejected.ContainsKey(ids[a])) rejected[ids[a]] = new();
                    if (!rejected.ContainsKey(ids[b])) rejected[ids[b]] = new();
                    rejected[ids[a]].Add(first); rejected[ids[b]].Add(second);
                }
        }
        foreach (var pair in rejected) values[pair.Key] = Array.AsReadOnly(values[pair.Key].Where(g => !pair.Value.Contains(g)).ToArray());
        return new(os, values, issues, requested, singleKeys);
    }
    public static bool Equivalent(ShortcutGesture a, ShortcutGesture b, ShortcutPlatform platform)
    { var x = a.Resolve(platform); var y = b.Resolve(platform); return x.KeyModifiers == y.KeyModifiers && (x.Key == y.Key || a.Symbol != null && a.Symbol == b.Symbol); }
    public static bool ContextsOverlap(string first, string second)
    {
        var a = ExecutionContexts(first); var b = ExecutionContexts(second);
        static bool OnlyModal(IReadOnlyList<string> contexts) => contexts.All(c => c.StartsWith("dialog:", StringComparison.Ordinal));
        return a.Contains("shell") && !OnlyModal(b) || b.Contains("shell") && !OnlyModal(a) || a.Intersect(b).Any();
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
            return new[] { "media", "reader", "gallery", "dialog:media-rating" };
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
                : id.StartsWith("media.rating.", StringComparison.Ordinal) ? "media-rating"
                : id.Split('.')[0];
            return new[] { "dialog:" + family };
        }
        if (id.StartsWith("plugins.", StringComparison.Ordinal)) return new[] { "plugins" };
        if (id.StartsWith("settings.", StringComparison.Ordinal)) return new[] { "settings" };
        if (id.StartsWith("rules.", StringComparison.Ordinal) && id != "rules.edit-scope") return new[] { "rules" };
        return c.Scope.ToString().Split(", ").Where(x => x != "Application" && x != "None").Select(x => x.ToLowerInvariant()).DefaultIfEmpty("shell").ToArray();
    }
}
