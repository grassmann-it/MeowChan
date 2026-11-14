using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MeowChan.Desktop;

/// <summary>
/// Where the media of the focused (selected) post is shown enlarged. Today that is the card itself (<see cref="Inline"/>): the
/// card's pictures are drawn at the "Enlarge focused post's media" percentage. The full-height media sidebar (a later package) shows
/// the same media in its own column instead. Both use this one seam, so they look alike and never show the media twice:
/// <list type="bullet">
/// <item>the target is the selected post's attachments (<c>ArchivePostRow.Attachments</c>), whichever surface shows them;</item>
/// <item>the frame look is <see cref="CreateFrameEdge"/> (accent edge, control radius, theme tokens only), also on the sidebar picture;</item>
/// <item>a surface that presents the focused media itself reports it through <c>ArchivePage.FocusedMediaPresentedElsewhere</c>,
/// which turns the inline enlargement off for as long as it does;</item>
/// <item>decoding asks for <see cref="ReaderDisplayPreferences.DecodeBoundsForWidth"/> of the frame it fills.</item>
/// </list>
/// </summary>
internal static class ReaderFocusedMedia
{
    /// <summary>Which surface presents the focused post's media enlarged.</summary>
    internal enum Presentation { None, Inline, Sidebar }

    internal static Presentation Choose(ReaderMediaPreferences media, bool presentedElsewhere) =>
        presentedElsewhere ? Presentation.Sidebar : media.FocusedEnlarge && media.FocusedScalePercent > 100 ? Presentation.Inline : Presentation.None;

    /// <summary>
    /// The edge drawn around the enlarged picture: the accent colour and the control radius of the theme, nothing else. It never takes
    /// pointer input. The inline card and the sidebar both place one over their picture frame.
    /// </summary>
    internal static Border CreateFrameEdge()
    {
        Border edge = new() { BorderThickness = new Avalonia.Thickness(1), IsHitTestVisible = false, IsVisible = false };
        edge[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Accent");
        edge[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        return edge;
    }
}
