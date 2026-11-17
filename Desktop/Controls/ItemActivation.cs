using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one rule of list, grid and card surfaces (interaction audit 1.1.0): a double click runs the same primary action as Enter, a
/// right-click opens the same menu as the Menu key and Shift+F10. A double click on an interactive part of an item (a button, a chip,
/// a switch, a text field, a slider, selectable text) keeps that part's own meaning and never runs the primary action, and a double
/// click on empty space between the items runs nothing (no hidden "the old selection opens").
/// </summary>
internal static class ItemActivation
{
    /// <summary>
    /// Whether <paramref name="source"/> (the pointer's or the key's source) lies in a part of an item that has a meaning of its own,
    /// up to <paramref name="surface"/> (the list or card the primary action belongs to; null: up to the root). Selectable text counts:
    /// a double click there selects a word.
    /// </summary>
    internal static bool IsOwnedByInteractivePart(object? source, Visual? surface = null)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (surface != null && ReferenceEquals(node, surface)) return false;
            if (PointerFocusReclaim.IsTextInput(node) || node is Button or SelectableTextBlock or Slider or ScrollBar or MenuItem
                or ToggleSwitch or CalendarDatePicker or DataGridColumnHeader or GridSplitter
                or Thumb) return true;
        }
        return false;
    }

    /// <summary>
    /// The item under <paramref name="source"/>: the data of the nearest list item, table row or card that carries a
    /// <typeparamref name="T"/>, up to <paramref name="surface"/>. Null on empty space and on a header.
    /// </summary>
    internal static T? ItemAt<T>(object? source, Visual? surface = null) where T : class
    {
        if (source is not Visual visual) return null;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (surface != null && ReferenceEquals(node, surface)) return null;
            if (node is ListBoxItem or DataGridRow && node is Control { DataContext: T item }) return item;
        }
        return null;
    }

    /// <summary>The container (list item or table row) under <paramref name="source"/>, up to <paramref name="surface"/>.</summary>
    internal static Control? ContainerAt(object? source, Visual? surface = null)
    {
        if (source is not Visual visual) return null;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (surface != null && ReferenceEquals(node, surface)) return null;
            if (node is ListBoxItem or DataGridRow) return (Control)node;
        }
        return null;
    }

    /// <summary>
    /// The guard every double click of a surface runs first: true when the double click is meant for an item's primary action
    /// (on an item, not on one of its interactive parts, not already handled).
    /// </summary>
    internal static bool IsItemDoubleClick(TappedEventArgs e, Visual surface) =>
        !e.Handled && ContainerAt(e.Source, surface) != null && !IsOwnedByInteractivePart(e.Source, surface);

    /// <summary>The same guard for a card that is not a list item: anywhere on the card except its interactive parts.</summary>
    internal static bool IsCardDoubleClick(TappedEventArgs e, Visual card) => !e.Handled && !IsOwnedByInteractivePart(e.Source, card);

    /// <summary>Enter (or Return) without modifiers: the keyboard form of the primary action.</summary>
    internal static bool IsPrimaryKey(KeyEventArgs e) => !e.Handled && e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.None;

    /// <summary>Whether a wheel turn asks for zoom: Ctrl held (also ⌘ on macOS), nothing else.</summary>
    internal static bool IsZoomWheel(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.Control || OperatingSystem.IsMacOS() && modifiers == KeyModifiers.Meta;
}

/// <summary>
/// Turns wheel deltas into whole steps: a mouse gives one step per notch, a touchpad many small deltas that add up to one. The rest
/// is kept until it reaches a step or the direction changes; nothing is kept between gestures longer than <see cref="Pause"/>.
/// </summary>
internal sealed class WheelSteps
{
    /// <summary>A pause this long starts a new gesture (the rest of the previous one is dropped).</summary>
    internal static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(400);
    private double _rest;
    private DateTime _last = DateTime.MinValue;

    /// <summary>The whole steps of this delta (positive: wheel up, away from the user), at <paramref name="now"/>.</summary>
    internal int Add(double delta, DateTime now)
    {
        if (!double.IsFinite(delta) || delta == 0) return 0;
        if (now - _last > Pause || Math.Sign(delta) != Math.Sign(_rest)) _rest = 0;
        _last = now;
        _rest += delta;
        int steps = (int)Math.Truncate(_rest);
        _rest -= steps;
        return steps;
    }
}
