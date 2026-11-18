using Avalonia.Controls;
using Avalonia.Media;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Expected palette colours for smoke checks. They are read from the active theme's resource
/// dictionary, resolved for the owner's actual theme variant, so a check follows the palette
/// instead of duplicating a hex value that goes stale with every palette change.
/// </summary>
internal static class SmokeThemeResources
{
    /// <summary>
    /// Resolves a solid-colour brush resource such as <c>Workspace.Text</c> for the owner's
    /// <see cref="Control.ActualThemeVariant"/>. A missing or non-solid resource fails loudly;
    /// a check must never pass because its expectation could not be determined.
    /// </summary>
    internal static Color Color(Control owner, string key)
    {
        if (!owner.TryFindResource(key, owner.ActualThemeVariant, out object? value))
            throw new InvalidOperationException($"Theme resource '{key}' was not found for theme variant {owner.ActualThemeVariant}; the expected colour cannot be determined.");
        if (value is not ISolidColorBrush brush)
            throw new InvalidOperationException($"Theme resource '{key}' is {value?.GetType().Name ?? "null"}, not a solid colour brush, for theme variant {owner.ActualThemeVariant}.");
        return brush.Color;
    }
}
