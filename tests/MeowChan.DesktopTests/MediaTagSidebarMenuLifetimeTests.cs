using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

public sealed class MediaTagSidebarMenuLifetimeTests
{
    [Fact]
    public void SidebarOwnsItsWorkspaceMenuHostForTheVisualTreeLifetime()
    {
        string source = Source("Desktop/Views/Media/MediaTagSidebar.cs");

        Assert.Contains("private ContextMenuHost? _menuHost;", source, StringComparison.Ordinal);
        Assert.Contains("AttachedToVisualTree += (_, _) => _menuHost ??= WorkspaceChips.AttachMenu(_list, ComposeMenu", source, StringComparison.Ordinal);
        Assert.Contains("DetachedFromVisualTree += (_, _) => { _menuHost?.Dispose(); _menuHost = null; };", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\n        WorkspaceChips.AttachMenu(_list, ComposeMenu, () => _list.Focus(NavigationMethod.Tab));", source, StringComparison.Ordinal);
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
