using System.Globalization;
using Avalonia.Controls;

namespace MeowChan.Desktop;

/// <summary>Existing workspace table sizes, shared by XAML and preference application.</summary>
public static class WorkspaceMetrics
{
    public const double NormalRowHeight = 44;
    public const double CompactRowHeight = 32;

    /// <summary>Height of the app-owned title bar in every window. Code mirror of the Workspace.Size.TitleBar
    /// resource; a test keeps both equal.</summary>
    public const double TitleBarHeight = 48;

    /// <summary>The title bar as a grid row, for <c>RowDefinition Height="{x:Static desktop:WorkspaceMetrics.TitleBarRow}"</c>.</summary>
    public static readonly GridLength TitleBarRow = new(TitleBarHeight);

    /// <summary>Row definitions for a window grid: the title bar row, then the given rows (for example "*" or "*,Auto").</summary>
    public static RowDefinitions TitleBarRows(string followingRows) =>
        new(TitleBarHeight.ToString(CultureInfo.InvariantCulture) + "," + followingRows);
}
