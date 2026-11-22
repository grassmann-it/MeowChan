using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Ratings;

namespace MeowChan.Desktop;

/// <summary>Words and colours of the four colour labels. A label is never shown as a colour alone: every surface also names it.</summary>
internal static class MediaLabelText
{
    /// <summary>"Red label", "Yellow label", ... in the language in force; empty for no label.</summary>
    internal static string Name(string? color) => MediaLabelColors.IsValid(color) ? Texts.T("media.badge.label." + color) : string.Empty;

    /// <summary>The colour of the mark (fixed, readable on the dark badge disc in both themes).</summary>
    internal static Color Of(Control surface, string color) =>
        MediaLabelColors.IsValid(color)
        && surface.TryFindResource("Workspace.MediaLabel." + color, out object? resource)
        && resource is Color tone ? tone : default;

    /// <summary>The command id for a colour, and back: <c>media.label.red</c>.</summary>
    internal static string CommandId(string color) => "media.label." + color;
}
