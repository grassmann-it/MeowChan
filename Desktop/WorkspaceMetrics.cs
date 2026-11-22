using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

/// <summary>Size steps of a dialog window (modals plan MD1). Every secondary window picks one step instead of its own literals.</summary>
public enum DialogSize
{
    /// <summary>Prompt or confirmation: 480 × 360, minimum 360 × 300.</summary>
    S,
    /// <summary>Form: 640 × 520, minimum 420 × 360.</summary>
    M,
    /// <summary>List with details: 760 × 640, minimum 480 × 400.</summary>
    L,
    /// <summary>Assistant or workbench: 980 × 720, minimum 640 × 560.</summary>
    XL
}

/// <summary>Existing workspace table sizes, shared by XAML and preference application.</summary>
public static class WorkspaceMetrics
{
    public const double NormalRowHeight = 44;
    public const double CompactRowHeight = 32;
    internal const double InputHeight = 32;
    internal const double FilterChipHeight = 28;
    /// <summary>Code mirror of Workspace.Inset.Section for code-built side panels whose host may override individual edges.</summary>
    public const double SectionInset = 16;
    /// <summary>A list row with a title and a meta line (16 dip padding above and below them together, on the 8 dip grid).</summary>
    public const double TwoLineRowHeight = 56;

    /// <summary>Height of the app-owned title bar in every window. Code mirror of the Workspace.Size.TitleBar
    /// resource; a test keeps both equal.</summary>
    public const double TitleBarHeight = 48;

    /// <summary>The title bar as a grid row, for <c>RowDefinition Height="{x:Static desktop:WorkspaceMetrics.TitleBarRow}"</c>.</summary>
    public static readonly GridLength TitleBarRow = new(TitleBarHeight);

    /// <summary>Row definitions for a window grid: the title bar row, then the given rows (for example "*" or "*,Auto").</summary>
    public static RowDefinitions TitleBarRows(string followingRows) =>
        new(TitleBarHeight.ToString(CultureInfo.InvariantCulture) + "," + followingRows);

    /// <summary>Opening client size of a dialog step (dip). <see cref="DialogFit"/> still shrinks it on a small or scaled screen.</summary>
    public static Size DialogPreferredSize(DialogSize size) => size switch
    {
        DialogSize.S => new(480, 360),
        DialogSize.M => new(640, 520),
        DialogSize.L => new(760, 640),
        DialogSize.XL => new(980, 720),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
    };

    /// <summary>Smallest client size a dialog step may be resized to (dip): room for title bar, a usable body and the footer.</summary>
    public static Size DialogMinimumSize(DialogSize size) => size switch
    {
        DialogSize.S => new(360, 300),
        DialogSize.M => new(420, 360),
        DialogSize.L => new(480, 400),
        DialogSize.XL => new(640, 560),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, null)
    };

    /// <summary>Minimum height of a dialog whose height follows its content (a short confirmation): title bar, one line, footer.</summary>
    public const double DialogContentMinimumHeight = 200;
}
