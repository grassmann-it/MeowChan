using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// MV3 (F04): the side panel switches show the half of the frame their panel sits in; the viewer focus mode has an icon of its own.
// Plain catalog objects; no application, no GUI.
public sealed class SidebarIconAssignmentTests
{
    private static CommandDefinition Command(string id) => CommandCatalog.All.Single(command => command.Id == id);

    [Fact]
    public void ThePanelSwitchesNameTheirSide()
    {
        Assert.Equal("panel-right", Command("media.panel.toggle").Icon);
        Assert.Equal("panel-left", Command("reader.sidebar.toggle").Icon);
        foreach (string id in new[] { "media.panel.toggle", "reader.sidebar.toggle" })
        {
            Assert.Equal("panel-left", CommandCatalog.IconForState(id, "left"));
            Assert.Equal("panel-right", CommandCatalog.IconForState(id, "right"));
        }
    }

    [Fact]
    public void TheFocusModeIconIsNoPanelIconAndChangesWithItsState()
    {
        string focus = Command("viewer.focus-mode").Icon;
        Assert.DoesNotContain(focus, new[] { "sidebar", "panel-left", "panel-right" });
        Assert.NotEqual(CommandCatalog.IconForState("viewer.focus-mode", "embedded"), CommandCatalog.IconForState("viewer.focus-mode", "expanded"));
    }

    [Fact]
    public void NoCommandUsesTheSidelessSidebarIconAndPanelIconsBelongToPanelSwitchesOnly()
    {
        Assert.DoesNotContain(CommandCatalog.All, command => command.Icon == "sidebar");
        string[] owners = CommandCatalog.All.Where(command => command.Icon is "panel-left" or "panel-right").Select(command => command.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "media.panel.toggle", "reader.sidebar.toggle" }, owners);
    }

    [Theory]
    [InlineData("panel-left")]
    [InlineData("panel-right")]
    [InlineData("workspace-expand")]
    [InlineData("workspace-shrink")]
    public void TheNewIconsHaveSvgAndMask(string icon, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));
        Assert.True(File.Exists(Path.Combine(root, "Assets/icons/feather/" + icon + ".svg")));
        Assert.True(File.Exists(Path.Combine(root, "Assets/icons/feather/masks/" + icon + ".png")));
    }
}
