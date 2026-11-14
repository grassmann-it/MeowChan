using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// Registers a control under its <see cref="WorkViewPolicy"/> id.
/// XAML: <c>xmlns:wv="using:MeowChan.Desktop.WorkViews"</c> … <c>wv:WorkViewElement.Id="downloads.toolbar.columns"</c>;
/// code: <c>WorkViewElement.SetId(control, "downloads.toolbar.columns")</c>. Setting the id records the control in a weak
/// registry, so applying a view never walks the visual tree. The id must exist in the policy (a lint test checks every use).
/// </summary>
public sealed class WorkViewElement
{
    private WorkViewElement() { }

    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<WorkViewElement, Control, string?>("Id");

    /// <summary>The visibility the page's own state asks for, kept by <see cref="WorkViewGate.Apply"/> (the view may still hide it).</summary>
    internal static readonly AttachedProperty<bool> StateVisibleProperty =
        AvaloniaProperty.RegisterAttached<WorkViewElement, Control, bool>("StateVisible", true);

    private static readonly List<WeakReference<Control>> Registered = new();

    static WorkViewElement()
    {
        IdProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is string { Length: > 0 }) Track(control);
        });
    }

    public static string? GetId(Control control) => control.GetValue(IdProperty);

    public static void SetId(Control control, string? id) => control.SetValue(IdProperty, id);

    internal static bool GetStateVisible(Control control) => control.GetValue(StateVisibleProperty);

    internal static void SetStateVisible(Control control, bool visible) => control.SetValue(StateVisibleProperty, visible);

    private static void Track(Control control)
    {
        lock (Registered)
        {
            if (Registered.Count > 256) Registered.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (Registered.Any(reference => reference.TryGetTarget(out Control? known) && ReferenceEquals(known, control))) return;
            Registered.Add(new(control));
        }
    }

    /// <summary>Every live control with an id (any window). Callers filter by owner or page.</summary>
    internal static IReadOnlyList<Control> All()
    {
        lock (Registered)
            return Registered.Select(reference => reference.TryGetTarget(out Control? control) ? control : null)
                .OfType<Control>().Where(control => GetId(control) != null).ToArray();
    }

    /// <summary>Registered controls whose id belongs to <paramref name="page"/>.</summary>
    internal static IReadOnlyList<Control> For(WorkViewPage page) =>
        All().Where(control => WorkViewPolicy.Find(GetId(control)!)?.Page == page).ToArray();
}
