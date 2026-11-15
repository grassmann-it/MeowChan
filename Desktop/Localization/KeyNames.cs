using System;

namespace MeowChan.Desktop.Localization;

/// <summary>
/// The names of the keys in a shortcut display on Windows and Linux (Tom's review item 14, second half, 15.11.2026): "Entf", "Pfeil hoch", "Num 0"
/// in German, the name the keyboard or the operating system gives the key in the other languages that have one. A language whose keyboards
/// print the English word (Delete, Home, End) keeps it. English stays exactly what the stored key is called (Delete, PageUp, NumPad0), so
/// nothing that reads an English display changes. Letters, digits, the F keys and punctuation are the same everywhere and are not named here.
/// A Mac keeps its symbols (⌫ ⌦ ↩ ⎋ ← ⇞, <see cref="MeowChan.Desktop.Commands.ShortcutGesture.DisplayParts"/>); the stored gesture never changes.
/// Modifiers have their own keys (<c>key.ctrl</c>, <c>key.shift</c>), "Alt" is "Alt" on every keyboard, and Tab is "Tab" everywhere.
/// </summary>
internal static class KeyNames
{
    /// <summary>Every text key of this class, for tests: the catalog and all bundled packs carry each of them.</summary>
    internal static readonly string[] Keys =
    {
        "key.enter", "key.space", "key.escape", "key.delete", "key.insert", "key.home", "key.end", "key.pageUp", "key.pageDown",
        "key.left", "key.right", "key.up", "key.down", "key.menu",
        "key.numpad", "key.numAdd", "key.numSubtract", "key.numMultiply", "key.numDivide", "key.numDecimal",
    };

    /// <summary>The localized name of the stored key <paramref name="key"/> (an Avalonia key name), or null when the key has no name of its own.</summary>
    internal static string? Localized(string key) => key switch
    {
        "Enter" or "Return" => Texts.T("key.enter"),
        "Space" => Texts.T("key.space"),
        "Escape" => Texts.T("key.escape"),
        "Back" => Texts.T("key.backspace"),
        "Delete" => Texts.T("key.delete"),
        "Insert" => Texts.T("key.insert"),
        "Home" => Texts.T("key.home"),
        "End" => Texts.T("key.end"),
        "PageUp" or "Prior" => Texts.T("key.pageUp"),
        "PageDown" or "Next" => Texts.T("key.pageDown"),
        "Left" => Texts.T("key.left"),
        "Right" => Texts.T("key.right"),
        "Up" => Texts.T("key.up"),
        "Down" => Texts.T("key.down"),
        "Apps" => Texts.T("key.menu"),
        "Add" => Texts.T("key.numAdd"),
        "Subtract" => Texts.T("key.numSubtract"),
        "Multiply" => Texts.T("key.numMultiply"),
        "Divide" => Texts.T("key.numDivide"),
        "Decimal" => Texts.T("key.numDecimal"),
        _ when key.Length == 7 && key.StartsWith("NumPad", StringComparison.Ordinal) && char.IsAsciiDigit(key[6]) => Texts.T("key.numpad", key[6].ToString()),
        _ => null
    };
}
