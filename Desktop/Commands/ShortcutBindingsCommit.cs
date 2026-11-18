using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Commands;

/// <summary>
/// Where a shortcut draft lives and how it is saved: the Settings editor owns the one <see cref="ShortcutDraftSession"/> of
/// its window and commits it; the shortcuts window edits that same session (modals plan KS2), so the Settings card and the
/// window never disagree.
/// </summary>
internal interface IShortcutDraftHost
{
    ShortcutDraftSession ShortcutSession { get; }
    /// <summary>Saving would be refused now (the settings are being saved or are unavailable).</summary>
    bool IsShortcutCommitBlocked { get; }
    /// <summary>Validates, persists and publishes <paramref name="draft"/> as the profile's shortcuts, nothing else.</summary>
    Task CommitShortcutsAsync(ShortcutBindingsSection draft);
}

/// <summary>
/// The one commit path of shortcut bindings (modals plan KS2), used by the Settings save and by the shortcuts window: validate,
/// put the bindings into the candidate profile, persist it through the window's settings path (or save and publish directly
/// without one) and copy the result back.
/// </summary>
internal static class ShortcutBindingsCommit
{
    /// <summary>Puts a validated copy of <paramref name="draft"/> into <paramref name="candidate"/>; throws for a blocking issue.</summary>
    internal static void ApplyTo(Config candidate, ShortcutBindingsSection draft)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(draft);
        ShortcutService.ValidateForCommit(draft);
        candidate.ShortcutBindings = draft.Clone();
    }

    /// <summary>
    /// Persists the bindings of <paramref name="candidate"/>. With <paramref name="apply"/> (the main window's settings path) the
    /// session saves and publishes; without it the profile is written here and the bindings are published.
    /// </summary>
    internal static async Task PersistAsync(Config candidate, Func<Config, Task>? apply)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (apply != null) { await apply(candidate); return; }
        candidate.SaveConfig();
        ShortcutService.Publish(candidate.ShortcutBindings);
    }

    /// <summary>Commits only the shortcuts: the rest of <paramref name="committed"/> stays as saved, other Settings drafts untouched.</summary>
    internal static async Task CommitAsync(Config committed, ShortcutBindingsSection draft, Func<Config, Task>? apply)
    {
        ArgumentNullException.ThrowIfNull(committed);
        Config candidate = committed.Clone();
        ApplyTo(candidate, draft);
        await PersistAsync(candidate, apply);
        if (apply == null) committed.CopyFrom(candidate);
        else committed.ShortcutBindings = candidate.ShortcutBindings.Clone();
    }
}
