using System;
using System.Collections.Generic;
using System.Globalization;

namespace MeowChan.Desktop.Controls;

/// <summary>How much of each stat a <see cref="StatFooterRow"/> shows: the label under the icon and value, or the icon and value only.</summary>
internal enum StatFooterMode { Full, IconsOnly }

/// <summary>
/// The pure rules of the stat footer (no controls, so they run in a unit test): how a number is written and how the row adapts to its
/// width. The row's look never depends on the numbers themselves, only on the width the card gives it, so a value that grows from 0 to
/// 10 to 1000 cannot move a neighbour or make the row wrap.
/// </summary>
internal static class StatFooterLayout
{
    /// <summary>
    /// A count below this is written in full ("9999"); from here on it is abbreviated ("12.3k", "1.2M"). Not a setting: it only keeps the
    /// value inside the fixed width every segment reserves, which is five characters wide ("88.8k"), so no user choice depends on it.
    /// </summary>
    internal const long AbbreviateFrom = 10_000;

    /// <summary>The widest value text a segment ever shows; the segment reserves the width of this text (tabular figures) in advance.</summary>
    internal const string ReservedValueText = "88.8k";

    private static readonly string[] Units = ["k", "M", "G", "T"];

    /// <summary>The count as shown in the footer: whole numbers below <see cref="AbbreviateFrom"/>, then one decimal under 100 and none above.</summary>
    internal static string Format(long value, CultureInfo culture)
    {
        // No watch list holds a thousand billion items; the clamp only keeps the text inside the reserved width for any long.
        value = Math.Clamp(value, 0, 999_000_000_000_000);
        if (value < AbbreviateFrom) return value.ToString("0", culture);
        double scaled = value;
        int unit = -1;
        while (scaled >= 1000 && unit < Units.Length - 1) { scaled /= 1000; unit++; }
        double rounded = Round(scaled);
        // 999 950 rounds to "1000k": carry into the next unit instead of showing six characters.
        if (rounded >= 1000 && unit < Units.Length - 1) { rounded = Round(rounded / 1000); unit++; }
        return rounded.ToString(rounded < 100 ? "0.#" : "0", culture) + Units[unit];
    }

    /// <summary>The full number with digit grouping, for the tooltip and the accessible name of a segment.</summary>
    internal static string FormatFull(long value, CultureInfo culture) => Math.Max(0, value).ToString("N0", culture);

    private static double Round(double scaled) => scaled < 100
        ? Math.Round(scaled, 1, MidpointRounding.AwayFromZero)
        : Math.Round(scaled, 0, MidpointRounding.AwayFromZero);

    /// <summary>The outcome for one width: the mode and how many segments (the most important ones) are shown.</summary>
    internal readonly record struct Plan(StatFooterMode Mode, int VisibleCount);

    /// <summary>
    /// Picks the richest layout that fits. Every shown segment gets the same slot width, so the plan needs only the widest natural width of
    /// a segment with and without its label. First all segments with labels; else all segments as icon and value; else fewer segments as
    /// icon and value, always at least one. Which segments stay is the caller's priority order, not a property of the numbers.
    /// </summary>
    internal static Plan Resolve(double available, int count, double fullWidth, double iconsOnlyWidth, double gap)
    {
        if (count <= 0) return new Plan(StatFooterMode.Full, 0);
        if (!double.IsFinite(available) || FitsAll(available, count, fullWidth, gap)) return new Plan(StatFooterMode.Full, count);
        if (FitsAll(available, count, iconsOnlyWidth, gap)) return new Plan(StatFooterMode.IconsOnly, count);
        int fitting = (int)Math.Floor((available + gap) / (iconsOnlyWidth + gap));
        return new Plan(StatFooterMode.IconsOnly, Math.Clamp(fitting, 1, count));
    }

    private static bool FitsAll(double available, int count, double width, double gap) =>
        count * width + (count - 1) * gap <= available + 0.01;

    /// <summary>The indexes of the segments that stay visible: the <paramref name="visibleCount"/> best by priority (lower is more important), ties in item order.</summary>
    internal static bool[] VisibleSet(IReadOnlyList<int> priorities, int visibleCount)
    {
        bool[] visible = new bool[priorities.Count];
        int[] order = new int[priorities.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => priorities[a] != priorities[b] ? priorities[a].CompareTo(priorities[b]) : a.CompareTo(b));
        for (int i = 0; i < Math.Min(visibleCount, order.Length); i++) visible[order[i]] = true;
        return visible;
    }
}
