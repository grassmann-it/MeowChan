using System;

namespace MeowChan.Desktop;

/// <summary>How the tagging page shows its queue (TG1): the table of rows, or a grid of thumbnails.</summary>
internal enum TaggingViewMode { List, Grid }

/// <summary>The saved spelling of <see cref="TaggingViewMode"/> (<c>Config.TaggingPageView</c>): unknown or empty text means the list.</summary>
internal static class TaggingViewModes
{
    internal static TaggingViewMode Parse(string? text) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out TaggingViewMode mode) && Enum.IsDefined(mode) ? mode : TaggingViewMode.List;

    /// <summary>The other mode (what the switch's other half stands for).</summary>
    internal static TaggingViewMode Other(TaggingViewMode mode) => mode == TaggingViewMode.List ? TaggingViewMode.Grid : TaggingViewMode.List;
}

/// <summary>
/// The saved state of the page's stamp block: "Auto" (never chosen) follows the work view, "Open" and "Closed" are the person's choice
/// for this profile.
/// </summary>
internal enum TaggingStampBlockState { Auto, Open, Closed }

internal static class TaggingStampBlocks
{
    internal static TaggingStampBlockState Parse(string? text) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out TaggingStampBlockState state) && Enum.IsDefined(state) ? state : TaggingStampBlockState.Auto;

    /// <summary>Whether the block is open: the choice when there is one, else open everywhere but in Barebones (<paramref name="compactView"/>).</summary>
    internal static bool IsOpen(TaggingStampBlockState state, bool compactView) => state switch
    {
        TaggingStampBlockState.Open => true,
        TaggingStampBlockState.Closed => false,
        _ => !compactView
    };
}
