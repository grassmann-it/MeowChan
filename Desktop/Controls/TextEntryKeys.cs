using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Which keys type text. Every platform turns a key into text only when its KeyDown comes back UNHANDLED (X11 raises the TextInput of a key press after
/// the KeyDown route returned unhandled, Win32 drops the WM_CHAR of a handled WM_KEYDOWN, macOS runs interpretKeyEvents only for an unhandled keyDown).
/// A handler that marks such a KeyDown handled above a text field therefore swallows the character, although the field shows the focus and the caret
/// (Tom 17.11.2026: the quick tag field took no typing; the popover frame consumed every key nothing inside had handled). Tests that raise KeyDown and
/// then a TextInput of their own never see this; the quick tag native typing smoke types with OS input.
/// </summary>
internal static class TextEntryKeys
{
    /// <summary>
    /// The text an unhandled <paramref name="e"/> types, or null for a key that types nothing (Enter, Esc, arrows, a command chord). Ctrl and ⌘ chords
    /// type nothing; Alt types on macOS only (Option makes characters there, as @ on a German layout), Ctrl+Alt on Windows only (AltGr).
    /// </summary>
    internal static string? TextOf(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        KeyModifiers modifiers = e.KeyModifiers;
        bool control = (modifiers & KeyModifiers.Control) != 0, alt = (modifiers & KeyModifiers.Alt) != 0;
        if ((modifiers & KeyModifiers.Meta) != 0) return null;
        if (control && !(alt && OperatingSystem.IsWindows())) return null;
        if (alt && !control && !OperatingSystem.IsMacOS()) return null;
        string? symbol = e.KeySymbol;
        return string.IsNullOrEmpty(symbol) || symbol.Any(char.IsControl) ? null : symbol;
    }

    /// <summary>True for a key that becomes text or feeds a composition when it stays unhandled (a printable key, an IME or dead key).</summary>
    internal static bool TypesText(KeyEventArgs e) => e.Key is Key.ImeProcessed or Key.DeadCharProcessed || TextOf(e) != null
        // X11 can omit KeySymbol for shifted number-row keys even though the unhandled key produces text.
        // Do not invent a layout-dependent character for redirection; just leave native text generation alone in an editor.
        || (e.Key is >= Key.D0 and <= Key.D9 && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift);

    /// <summary>A digit of the main row or the number pad with no modifier: in a tag field that is the start of a tag name ("1girl"), never a rating key.</summary>
    internal static bool IsBareDigit(KeyEventArgs e) =>
        e.KeyModifiers == KeyModifiers.None && (e.Key is >= Key.D0 and <= Key.D9 || e.Key is >= Key.NumPad0 and <= Key.NumPad9);

    /// <summary>True when <paramref name="source"/> is an editable, enabled text box or inside one (the box of an autocomplete field).</summary>
    internal static bool IsTextEditor(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().OfType<TextBox>().FirstOrDefault() is { IsReadOnly: false, IsEffectivelyEnabled: true };
}
