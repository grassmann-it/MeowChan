using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// Set once <see cref="WorkViewGate.Apply"/> owns the element's visibility. A registered element that no gate drives (a
    /// control the page shows or hides by its own state only) is never "hidden by the view", so a shortcut cannot run it
    /// while its state keeps it away.
    /// </summary>
    internal static readonly AttachedProperty<bool> GatedProperty =
        AvaloniaProperty.RegisterAttached<WorkViewElement, Control, bool>("Gated", false);

    // Membership uses control identity without keeping controls alive. The list is only for weak iteration;
    // every access to either collection (and the prune countdown) holds Registered's lock.
    private static readonly ConditionalWeakTable<Control, object> Membership = new();
    private static readonly object MembershipMarker = new();
    private static readonly List<WeakReference<Control>> Registered = new();
    private const int MinimumRegistrationsBetweenPrunes = 256;
    private static int _registrationsUntilPrune = MinimumRegistrationsBetweenPrunes;

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

    internal static bool GetGated(Control control) => control.GetValue(GatedProperty);

    internal static void SetGated(Control control, bool gated) => control.SetValue(GatedProperty, gated);

    private static void Track(Control control)
    {
        lock (Registered)
        {
            if (Membership.TryGetValue(control, out _)) return;
            Membership.Add(control, MembershipMarker);
            Registered.Add(new(control));
            if (--_registrationsUntilPrune == 0)
            {
                Registered.RemoveAll(reference => !reference.TryGetTarget(out _));
                ResetPruneCountdown();
            }
        }
    }

    // A registration scan is paid for by as many new registrations as the preceding live population (with a small floor).
    // A fixed interval would still be quadratic when many controls stay alive. Snapshots already visit every slot,
    // so they also compact it and restart the countdown. This is housekeeping, not a limit on registered controls.
    private static void ResetPruneCountdown() =>
        _registrationsUntilPrune = Math.Max(MinimumRegistrationsBetweenPrunes, Registered.Count);

    /// <summary>Every live control with a nonempty id that the calling thread may read. Callers filter by owner or page.
    /// Other dispatcher threads are skipped; in production every control belongs to the one UI thread.</summary>
    internal static IReadOnlyList<Control> All()
    {
        lock (Registered)
        {
            List<Control> controls = new(Registered.Count);
            int retained = 0;
            for (int read = 0; read < Registered.Count; read++)
            {
                WeakReference<Control> reference = Registered[read];
                if (!reference.TryGetTarget(out Control? control)) continue;
                Registered[retained++] = reference;
                // Keep membership even while an id is cleared: reassigning it must not add a second weak slot.
                if (control.CheckAccess() && !string.IsNullOrEmpty(GetId(control))) controls.Add(control);
            }
            Registered.RemoveRange(retained, Registered.Count - retained);
            ResetPruneCountdown();
            return controls.ToArray();
        }
    }

    /// <summary>Registered controls whose id belongs to <paramref name="page"/>.</summary>
    internal static IReadOnlyList<Control> For(WorkViewPage page) =>
        All().Where(control => WorkViewPolicy.Find(GetId(control)!)?.Page == page).ToArray();
}
