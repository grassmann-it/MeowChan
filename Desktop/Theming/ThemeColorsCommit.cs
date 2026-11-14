using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Theming;

/// <summary>
/// TH3: the one commit path of the colour dialog, the twin of <c>ShortcutBindingsCommit</c>: put the draft into a copy of the
/// committed profile, persist it through the window's settings path (which saves and applies it live, <c>ApplyAppearance</c>) or,
/// without one, save the profile and apply the colours here, and copy the result back. Only the colours are committed; every other
/// Settings draft stays as it is. The caller has checked the gate (<see cref="ThemeConfiguratorModel.CanApply"/>); the runtime
/// checks it again and keeps a refused variant on its built-in palette.
/// </summary>
internal static class ThemeColorsCommit
{
    internal static async Task CommitAsync(Config committed, ThemeColorOverrides draft, Func<Config, Task>? apply)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(draft);
        Config candidate = committed.Clone();
        candidate.ThemeOverrides = draft;
        if (apply != null) await apply(candidate);
        else
        {
            candidate.SaveConfig();
            ThemeRuntime.ApplyOverrides(candidate.ThemeOverrides);
        }
        committed.ThemeOverrides = candidate.ThemeOverrides;
    }
}
