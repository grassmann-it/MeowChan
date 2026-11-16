using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop;

/// <summary>The two reader-owned media-step commands, not a general exception to native list navigation.</summary>
internal static class ReaderSidebarStepKeys
{
    internal static int Direction(KeyEventArgs e, ListBox posts, ListBox? strip, Func<string, bool> matches)
    {
        if (e.Handled || e.Key is Key.Escape or Key.Tab || !SourceAllowsStep(e, posts, strip)) return 0;
        return matches("reader.sidebar.previous-media") ? -1 : matches("reader.sidebar.next-media") ? 1 : 0;
    }

    private static bool SourceAllowsStep(KeyEventArgs e, ListBox posts, ListBox? strip)
    {
        if (e.Source is not Visual source) return false;
        // Enter and Space belong to the focused selecting control (and to buttons/video
        // activators). Never let a saved media-step binding turn either activation key
        // into sidebar navigation, including when Avalonia reports the list itself as
        // the source rather than its realized item.
        if (e.Key is Key.Enter or Key.Space) return false;
        if (!CommandRouter.IsNativeInput(source, e.Key)) return true;

        // The advertised Alt+Shift+Left/Right is not ListBox selection/typeahead. Only the reader's own
        // post list and thumbnail strip yield it; plain/Shift/Ctrl arrows and all other lists stay native.
        if (e.Key is not (Key.Left or Key.Right) || e.KeyModifiers != (KeyModifiers.Alt | KeyModifiers.Shift)) return false;
        foreach (Visual node in source.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, posts) || ReferenceEquals(node, strip)) return true;
            if (node is TextBox or ComboBox or NumericUpDown or Slider or ScrollBar or MenuItem
                or Ursa.Controls.AutoCompleteBox or SelectingItemsControl or DataGrid or TabItem
                || node is SelectableTextBlock text && text.SelectionStart != text.SelectionEnd) return false;
        }
        return false;
    }
}
