using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>One button of the footer for <see cref="FooterFitPlanner"/>: its width with icon and label, its width as an icon only, and where it stands now.</summary>
/// <param name="Id">The command id of the page (Settings and More have their own).</param>
/// <param name="LabelledWidth">The measured width with icon and text.</param>
/// <param name="IconWidth">The width as an icon only (icon, padding and border).</param>
/// <param name="Direct">True when the layout and the work view put the page in the footer itself.</param>
/// <param name="Available">False for a page that does not exist now (Booru without its ports): it is neither in the footer nor in More.</param>
internal sealed record FooterFitButton(string Id, double LabelledWidth, double IconWidth, bool Direct = true, bool Available = true);

/// <summary>What the footer shows: which buttons are icon only, and which pages moved to More.</summary>
internal sealed record FooterFitResult(bool PagesIconOnly, bool SettingsIconOnly, bool DownloadsIconOnly, bool ToolsIconOnly, IReadOnlySet<string> MovedToMore)
{
    /// <summary>True when more than the fixed buttons left the footer or lost their text.</summary>
    internal bool Compact => PagesIconOnly || SettingsIconOnly || DownloadsIconOnly || ToolsIconOnly || MovedToMore.Count > 0;
}

/// <summary>
/// Review of 15.11.2026, item 6: every footer button shows its icon and its text; when the text does not fit, the page buttons become icons
/// (with the tooltip and the accessible name of the command) before a page moves to More, so a narrow window keeps every page one click
/// away. Pure: the window measures the widths and applies the result. The stages, each taken only while the footer still does not fit:
/// 1. every page button as an icon; 2. pages move to More, the rightmost first (Downloads never leaves); 3. Settings as an icon;
/// 4. Downloads as an icon; 5. More as an icon (its label stays longest, it is the way to everything that left).
/// </summary>
internal static class FooterFitPlanner
{
    /// <summary>
    /// Plans the footer.
    /// </summary>
    /// <param name="available">The width the buttons may take (the window's width less the padding and the work view selector).</param>
    /// <param name="spacing">The spacing between two items of the footer row.</param>
    /// <param name="separatorWidth">The width of one separator with its margins.</param>
    /// <param name="pages">The pages in the person's footer order, Downloads included (it is the one that never moves).</param>
    /// <param name="downloadsId">The id of Downloads in <paramref name="pages"/>.</param>
    /// <param name="settings">The Settings button.</param>
    /// <param name="more">The More button (needed only while a page is in it).</param>
    /// <param name="separators">How many separators stand for the pages shown directly (the flags per page in the order of <paramref name="pages"/>).</param>
    internal static FooterFitResult Plan(double available, double spacing, double separatorWidth, IReadOnlyList<FooterFitButton> pages, string downloadsId,
        FooterFitButton settings, FooterFitButton more, Func<bool[], int> separators)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(more);
        ArgumentNullException.ThrowIfNull(separators);
        bool[] direct = pages.Select(page => page.Available && page.Direct).ToArray();
        bool pagesIcons = false, settingsIcon = false, downloadsIcon = false, moreIcon = false;

        double Width(FooterFitButton button, bool icon) => icon ? button.IconWidth : button.LabelledWidth;
        double Required()
        {
            bool overflow = pages.Where((page, index) => page.Available && !direct[index]).Any();
            int separatorCount = separators((bool[])direct.Clone());
            int count = 1 + direct.Count(shown => shown) + (overflow ? 1 : 0) + separatorCount;
            double buttons = 0;
            for (int index = 0; index < pages.Count; index++)
                if (direct[index]) buttons += Width(pages[index], pages[index].Id == downloadsId ? downloadsIcon : pagesIcons);
            return Width(settings, settingsIcon) + buttons + (overflow ? Width(more, moreIcon) : 0)
                + separatorCount * separatorWidth + Math.Max(0, count - 1) * spacing;
        }

        if (Required() > available) pagesIcons = true;
        // The rightmost page leaves first, so the pages the person put at the front stay longest.
        for (int index = pages.Count - 1; index >= 0 && Required() > available; index--)
            if (pages[index].Id != downloadsId) direct[index] = false;
        if (Required() > available) settingsIcon = true;
        if (Required() > available) downloadsIcon = true;
        if (Required() > available) moreIcon = true;
        HashSet<string> moved = pages.Where((page, index) => page.Available && !direct[index] && page.Direct).Select(page => page.Id).ToHashSet();
        return new FooterFitResult(pagesIcons, settingsIcon, downloadsIcon, moreIcon, moved);
    }
}
