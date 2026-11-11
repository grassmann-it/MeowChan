using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Input;

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
        string key = Key.StartsWith("D", StringComparison.Ordinal) && Key.Length == 2 && char.IsDigit(Key[1]) ? Key[1..] : Key;
        key = key switch { "OemComma" => ",", "OemPlus" => "+", "OemMinus" => "−", _ => key };
        string prefix = string.Join("+", Modifiers.Select(m => m == "Primary" ? platform == ShortcutPlatform.MacOS ? "⌘" : "Ctrl" : m == "Control" ? "Ctrl" : m));
        return prefix.Length == 0 ? Symbol ?? key : prefix + "+" + (Symbol ?? key);
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
    private static readonly string[] AdditiveDefaultCommandIds =
    {
        "video.volume-up", "video.volume-down", "favorites.add", "favorites.remove"
    };
    public long Version { get; } = System.Threading.Interlocked.Increment(ref _nextVersion);
    public ShortcutPlatform Platform { get; }
    public IReadOnlyList<ShortcutIssue> Issues { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>> Gestures { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>> RequestedGestures { get; }
    private EffectiveShortcutBindings(ShortcutPlatform platform, Dictionary<string, IReadOnlyList<ShortcutGesture>> gestures, List<ShortcutIssue> issues, Dictionary<string, IReadOnlyList<ShortcutGesture>> requested)
    { Platform = platform; Gestures = new ReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>>(gestures); Issues = issues.AsReadOnly(); RequestedGestures = new ReadOnlyDictionary<string, IReadOnlyList<ShortcutGesture>>(requested); }
    public static ShortcutPlatform CurrentPlatform => OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : OperatingSystem.IsWindows() ? ShortcutPlatform.Windows : ShortcutPlatform.Linux;
    public IReadOnlyList<ShortcutGesture> For(string id) => Gestures.TryGetValue(id, out var value) ? value : Array.Empty<ShortcutGesture>();
    public string? Display(string id) => For(id).Count == 0 ? null : string.Join(" / ", For(id).Select(g => g.Display(Platform)));
    public bool Matches(KeyEventArgs e, string id) => For(id).Any(g =>
    {
        var resolved = g.Resolve(Platform);
        return resolved.KeyModifiers == e.KeyModifiers && (resolved.Key == e.Key || g.Symbol != null && g.Symbol == e.KeySymbol);
    });
    public static IReadOnlyList<ShortcutGesture> Defaults(CommandDefinition command)
    {
        var list = new List<ShortcutGesture>();
        if (command.Shortcut is not null && command.Shortcut != "Escape") list.Add(ShortcutGesture.Parse(command.Shortcut));
        switch (command.Id)
        {
            case "gallery.fit": list.Add(ShortcutGesture.Parse("NumPad0")); break;
            case "gallery.actual-size": list.Add(ShortcutGesture.Parse("NumPad1")); break;
            case "gallery.zoom-in": list.Add(ShortcutGesture.Parse("Add")); list.Add(ShortcutGesture.Parse("Shift+OemPlus")); break;
            case "gallery.zoom-out": list.Add(ShortcutGesture.Parse("Subtract")); break;
            case "viewer.shortcut-help": list.Add(new("OemQuestion", new[] { "Shift" }, "?")); break;
        }
        return list.AsReadOnly();
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
            if ((g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt)) return new("altGrText", "Ctrl+Alt can produce AltGr text / Ctrl+Alt kann AltGr-Text erzeugen");
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
        var values = CommandCatalog.All.ToDictionary(c => c.Id, Defaults, StringComparer.Ordinal);
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
        // These new defaults did not exist in older profiles. They must not disable an
        // existing explicit effective gesture merely by being introduced on upgrade.
        // This is deliberately NOT a general priority rule: two explicit bindings (or
        // existing conflicts) still receive the ordinary symmetric conflict rejection.
        if (section.IsEditable)
        foreach (string id in AdditiveDefaultCommandIds)
        {
            if (section.HasOverride(id)) continue;
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
        return new(os, values, issues, requested);
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
        if (id == "media.rating.manage") return new[] { "diagnostics" };
        if (id == "media.rating.remove-selected") return new[] { "dialog:media-ratings-manager" };
        if (id == "media.rating.cleanup-retry") return new[] { "dialog:archive-deletion" };
        if (id == "media.rating.edit") return new[] { "media", "reader", "gallery" };
        if (id == "media.rating.clear" || id.StartsWith("media.rating.set-", StringComparison.Ordinal))
            return new[] { "media", "reader", "gallery", "dialog:media-rating" };
        if (id.StartsWith("navigation.") || id is "session.pause" or "window.fullscreen" or "input.focus-url" or "viewer.shortcut-help") return new[] { "shell" };
        // The large reader owns thread find; the archive search box is not reachable from it.
        if (id == "reader.find") return new[] { "large-reader" };
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
