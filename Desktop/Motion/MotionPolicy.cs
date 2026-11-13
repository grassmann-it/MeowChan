using System;
using Avalonia;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MeowChan.Desktop.Motion;

/// <summary>
/// D1 (design language E5): the one answer to "may the interface move?". <c>Effective = Environment ?? Setting ?? OsSignal</c>.
/// <list type="bullet">
/// <item><b>Environment</b>: <c>MEOWCHAN_MOTION=off|on</c> or <c>MEOWCHAN_REDUCED_MOTION=1|0</c>. Read live, so tests and the harness
/// (which runs every capture and smoke with motion off) need no restart.</item>
/// <item><b>Setting</b>: Settings → Interface → Animations: System (default), On, Off.</item>
/// <item><b>OS signal</b>: the system's reduced-motion setting (<see cref="OsMotionSignal"/>); read at start and on change where the
/// platform reports it.</item>
/// </list>
/// "Off" puts the class <c>motion-off</c> on every TopLevel (windows at once, popups through <see cref="Apply"/>); the transition
/// styles in WorkspaceStyles.axaml only apply below a TopLevel without that class, so toggling at run time needs no restart.
/// Progress and spinners are state, not decoration, and are never switched off.
/// </summary>
internal static class MotionPolicy
{
    internal const string OffClass = "motion-off";
    internal const string System = "System", On = "On", Off = "Off";

    private static string _setting = System;
    private static bool _osReduced;
    private static bool _installed;
    private static int _themeChanges;
    private static readonly ConditionalWeakTable<TopLevel, object> Attached = new();

    /// <summary>Raised on the UI thread whenever the effective answer may have changed.</summary>
    internal static event EventHandler? Changed;

    /// <summary>True when nothing may animate right now (environment, then setting, then the operating system).</summary>
    internal static bool IsOff => _themeChanges > 0 || Resolve(FromEnvironment(Environment.GetEnvironmentVariable), _setting, _osReduced);

    /// <summary>The reader and every other caller ask this: should short fades be skipped?</summary>
    internal static bool Reduced => IsOff;

    internal static string Setting => _setting;
    internal static bool OsReduced => _osReduced;

    /// <summary><c>Environment ?? Setting ?? OsSignal</c>; <paramref name="environment"/> is true for off, false for on, null for no say.</summary>
    internal static bool Resolve(bool? environment, string setting, bool osReduced) =>
        environment ?? NormalizeSetting(setting) switch { Off => true, On => false, _ => osReduced };

    /// <summary>
    /// The environment's say: <c>MEOWCHAN_MOTION=off</c> and <c>MEOWCHAN_REDUCED_MOTION=1|true|yes|on</c> mean off (the reader's two
    /// original names keep working); <c>MEOWCHAN_MOTION=on</c> and <c>MEOWCHAN_REDUCED_MOTION=0|false|no|off</c> force motion on; null otherwise.
    /// Off wins when both are set.
    /// </summary>
    internal static bool? FromEnvironment(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? motion = environment("MEOWCHAN_MOTION")?.Trim().ToLowerInvariant();
        string? reduced = environment("MEOWCHAN_REDUCED_MOTION")?.Trim().ToLowerInvariant();
        if (motion == "off" || reduced is "1" or "true" or "yes" or "on") return true;
        if (motion == "on" || reduced is "0" or "false" or "no" or "off") return false;
        return null;
    }

    internal static string NormalizeSetting(string? value) =>
        string.Equals(value?.Trim(), Off, StringComparison.OrdinalIgnoreCase) ? Off
        : string.Equals(value?.Trim(), On, StringComparison.OrdinalIgnoreCase) ? On : System;

    /// <summary>Applies the saved "Animations" setting; a change re-evaluates every open window.</summary>
    internal static void SetSetting(string? value)
    {
        string normalized = NormalizeSetting(value);
        if (normalized == _setting) return;
        _setting = normalized;
        Raise();
    }

    /// <summary>Records what the operating system says; a change re-evaluates every open window.</summary>
    internal static void SetOsReduced(bool reduced)
    {
        if (reduced == _osReduced) return;
        _osReduced = reduced;
        Raise();
    }

    /// <summary>
    /// Who decides right now, for the start-up summary: <c>animations OFF, decided by the environment (MEOWCHAN_MOTION=off)</c>,
    /// <c>... by the setting (Off)</c> or <c>... by the OS signal (detected OFF)</c>.
    /// </summary>
    internal static string DescribeSource() => DescribeSource(Environment.GetEnvironmentVariable, _setting, _osReduced, OsMotionSignal.Status);

    internal static string DescribeSource(Func<string, string?> environment, string setting, bool osReduced, OsMotionStatus os)
    {
        bool? fromEnvironment = FromEnvironment(environment);
        string state = "animations " + (Resolve(fromEnvironment, setting, osReduced) ? "OFF" : "ON");
        if (fromEnvironment != null)
            return state + ", decided by the environment (" + (environment("MEOWCHAN_MOTION") is { Length: > 0 } motion
                ? "MEOWCHAN_MOTION=" + motion : "MEOWCHAN_REDUCED_MOTION=" + environment("MEOWCHAN_REDUCED_MOTION")) + ")";
        string normalized = NormalizeSetting(setting);
        if (normalized != System) return state + ", decided by the setting (" + normalized + ")";
        return state + ", decided by the OS signal (" + PlatformDiagnostics.MotionSummary(os) + ")";
    }

    /// <summary>Re-evaluates now, for example after the environment changed in a test.</summary>
    internal static void Refresh() => Raise();

    /// <summary>
    /// Puts or removes <c>motion-off</c> on the TopLevel that hosts <paramref name="element"/> (the element itself when it is one),
    /// so a popup's own root follows the policy too. Safe to call repeatedly.
    /// </summary>
    internal static void Apply(StyledElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        StyledElement target = element is Visual visual && TopLevel.GetTopLevel(visual) is { } top ? top : element;
        target.Classes.Set(OffClass, IsOff);
    }

    /// <summary>
    /// Runs a theme change (RequestedThemeVariant) with motion off, so the buttons and chips switch their brushes at once instead of
    /// cross-fading between the two themes (spec: no theme crossfade). Motion comes back after the next background pass, when the new
    /// values are in place; nothing is left to animate.
    /// </summary>
    internal static void WhileThemeChanges(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);
        BeginThemeChange();
        try { change(); }
        finally { EndThemeChange(); }
    }

    private static void BeginThemeChange()
    {
        _themeChanges++;
        Raise();
    }

    private static void EndThemeChange() =>
        Dispatcher.UIThread.Post(() => { _themeChanges--; Raise(); }, DispatcherPriority.Background);

    /// <summary>Follows the policy for the lifetime of <paramref name="top"/> (windows and popup roots).</summary>
    internal static void Attach(TopLevel top)
    {
        ArgumentNullException.ThrowIfNull(top);
        if (Attached.TryGetValue(top, out _)) return;
        Attached.Add(top, top);
        // Weak: a popup root that never raises Closed must not be kept alive by the static event.
        WeakReference<TopLevel> weak = new(top);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (weak.TryGetTarget(out TopLevel? alive)) Apply(alive);
            else Changed -= handler;
        };
        Changed += handler;
        top.Closed += (_, _) => Changed -= handler;
        Apply(top);
    }

    /// <summary>
    /// Once per process, on the UI thread: every window that opens follows the policy, and the OS signal is read and watched.
    /// Not called by unit tests, which never open windows.
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));
        // Popups (menus, flyouts, drop-downs, tooltips) live in their own native root on desktop and do not inherit the window's
        // class, so each popup root follows the policy as soon as it has its template.
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<PopupRoot>((root, _) => Attach(root));
        Control.LoadedEvent.AddClassHandler<PopupRoot>((root, _) => Attach(root));
        if (Application.Current is { } app)
        {
            // An operating-system theme change (theme Default) cannot be wrapped by the caller; best effort, same suppression.
            app.ActualThemeVariantChanged += (_, _) => { if (_themeChanges == 0) { BeginThemeChange(); EndThemeChange(); } };
        }
        OsMotionSignal.Start(SetOsReduced);
    }

    private static void Raise()
    {
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke(null, EventArgs.Empty);
        else Dispatcher.UIThread.Post(() => Changed?.Invoke(null, EventArgs.Empty));
    }
}
