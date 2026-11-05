using System.Linq;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Localization;

// Entries are included in both BuiltInCatalog dictionaries and bundled preview packs.
// Use the normal Texts fallback/reload chain; do not freeze or privately translate
// validation messages here. User IDs, gesture tokens and exception details stay raw.
internal static class ShortcutIssueCatalog
{
    internal const string Prefix = "shortcuts.issue.";
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("shortcuts.issue.unknownModifier", "Unknown modifier: {0}", "Unbekannter Modifikator: {0}"),
        ("shortcuts.issue.imeKey", "IME keys cannot be shortcuts", "IME-Tasten sind keine Kürzel"),
        ("shortcuts.issue.unknownKey", "Unknown key or modifier-only gesture: {0}", "Unbekannte Taste oder Kürzel nur aus Modifikatoren: {0}"),
        ("shortcuts.issue.duplicateModifiers", "Duplicate modifiers", "Doppelte Modifikatoren"),
        ("shortcuts.issue.repeatedPlatformModifier", "Repeated platform modifier", "Doppelte Plattformtaste"),
        ("shortcuts.issue.fixedNavigation", "Tab and Escape are fixed navigation", "Tab und Escape bleiben feste Navigation"),
        ("shortcuts.issue.altGrText", "Ctrl+Alt can produce AltGr text", "Ctrl+Alt kann AltGr-Text erzeugen"),
        ("shortcuts.issue.reservedSystem", "Reserved system shortcut", "Reserviertes Systemkürzel"),
        ("shortcuts.issue.unsupportedSymbol", "Unsupported symbol", "Nicht unterstütztes Symbol"),
        ("shortcuts.issue.unsupportedSchema", "Unsupported shortcut schema; defaults active", "Unbekanntes Kürzelschema; Standards aktiv"),
        ("shortcuts.issue.unknownAction", "Unknown action retained", "Unbekannte Aktion erhalten"),
        ("shortcuts.issue.limitsExceeded", "Shortcut limits exceeded", "Kürzelgrenzen überschritten"),
        ("shortcuts.issue.invalidMode", "Invalid binding mode", "Ungültiger Belegungsmodus"),
        ("shortcuts.issue.defaultYields", "New default yields to your saved binding", "Neuer Standard lässt deine gespeicherte Belegung unverändert"),
        ("shortcuts.issue.notImplemented", "Action not implemented; binding retained but inactive", "Aktion nicht implementiert; Belegung erhalten, aber inaktiv"),
        ("shortcuts.issue.conflictingActions", "Conflicting actions", "Kollidierende Aktionen"),
    };

    internal static string Render(ShortcutIssue issue)
        // A caller may use the public record's with-expression to replace Message.
        // Never let retained provenance override an explicitly replaced diagnostic.
        => issue.Reason is { } reason && reason.LegacyMessage == issue.Message ? Render(reason) : issue.Message;

    internal static string Render(ShortcutIssueReason reason)
        => reason.Code is { } code ? Texts.T(Prefix + code, reason.Arguments.Cast<object?>().ToArray()) : reason.LegacyMessage;
}
