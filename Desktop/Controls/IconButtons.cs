using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// An icon-only button of the toolbar family: a square of the input height (<c>icon-only</c> in WorkspaceStyles.axaml) with a
/// catalog icon (<see cref="CommandBindings.NamedIcon"/>, e.g. <c>refresh-cw</c>), the name as tooltip and accessible name. The icon is
/// never the only name. The icon is created when the button is shown, because it needs the platform's asset loader.
/// </summary>
internal static class IconButtons
{
    // The catalog icon each button was given (icon-only here, icon before the text in CommandBindings.ShowIconBeforeText), so a test
    // can tell which icon a button carries without the platform's asset loader.
    private static readonly ConditionalWeakTable<Button, string> Keys = new();

    /// <summary>Remembers the catalog icon <paramref name="icon"/> of <paramref name="button"/> (see <see cref="KeyOf"/>).</summary>
    internal static void Record(Button button, string icon) => Keys.AddOrUpdate(button, icon);

    /// <summary>The catalog icon file name a button was given by <see cref="Apply"/> or <c>CommandBindings.ShowIconBeforeText</c>; null when it has none.</summary>
    internal static string? KeyOf(Button button) => Keys.TryGetValue(button, out string? icon) ? icon : null;

    /// <summary>
    /// Draws the catalog icon <paramref name="icon"/> before the string label of <paramref name="button"/> (a tool button that keeps its text),
    /// without replacing the string: whatever sets or reads <c>Content</c> (the language refresh, a test, a smoke) is unchanged. Idempotent.
    /// </summary>
    internal static Button WithText(Button button, string icon)
    {
        CommandBindings.EnsureIconBeforeText(button, icon);
        return button;
    }

    /// <summary>Turns <paramref name="button"/> into an icon-only button; calling it again only renames it (language change).</summary>
    internal static void Apply(Button button, string icon, string name)
    {
        Record(button, icon);
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        if (button.Classes.Contains("icon-only")) return;
        button.Classes.Add("icon-only");
        button.Classes.Add("subtle");
        void Show() { if (button.Content is not Control) button.Content = CommandBindings.NamedIcon(icon); }
        if (button.IsAttachedToVisualTree()) Show();
        else button.AttachedToVisualTree += (_, _) => Show();
    }
}
