using Avalonia.Controls.Shapes;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// DS-P3: stroke icons that code-built controls need without the command catalog. The look (16 dip, 1.5 stroke, the owning
/// button's foreground) lives in WorkspaceStyles.axaml (<c>Path.workspace-icon</c>); a glyph character is never an icon.
/// </summary>
internal static class WorkspaceIcons
{
    /// <summary>The close mark for an icon-square button (<c>Button.icon-square</c>): content only, the button keeps its name and tooltip.</summary>
    internal static Path Close()
    {
        Path path = new();
        path.Classes.Add("workspace-icon");
        path.Classes.Add("icon-close");
        return path;
    }
}
