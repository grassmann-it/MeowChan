using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>
/// How full the favorite, thread favorite and rating lists are against their configured limits. From 75 percent on the status
/// bar warns, naming the setting that raises the limit, so a full list is never a surprise (the stores refuse growth only at
/// 100 percent, and lowering a limit never removes anything).
/// </summary>
internal static class StoreLimitUsage
{
    /// <summary>The share of a limit from which the status bar warns.</summary>
    internal const int WarningPercent = 75;

    /// <summary>Which of the three private lists a measurement belongs to.</summary>
    internal enum StoreList { Favorites, ThreadFavorites, Ratings }

    /// <summary>One measurement; <paramref name="List"/> names the list, or is null when the caller did not say which.</summary>
    internal readonly record struct Item(PrivateStoreLimitKind Kind, long Used, long Limit, StoreList? List = null)
    {
        /// <summary>Whole percent of the limit in use (never rounded up to a limit that is not reached).</summary>
        internal int Percent => Limit <= 0 ? 0 : (int)Math.Min(999, Used * 100 / Limit);
        internal bool NearLimit => Limit > 0 && Used * 100 >= Limit * WarningPercent;
    }

    /// <summary>The most used list and the most used file, against their limits; only the ones from 75 percent on.</summary>
    internal static IReadOnlyList<Item> Evaluate(IEnumerable<long> entryCounts, long entryLimit, IEnumerable<long> fileBytes, long byteLimit) =>
        Evaluate(entryCounts.Select(count => ((StoreList?)null, count)), entryLimit, fileBytes.Select(size => ((StoreList?)null, size)), byteLimit);

    /// <summary>The same, with each count and size tied to its list so the warning can name the one that is nearly full.</summary>
    internal static IReadOnlyList<Item> Evaluate(IEnumerable<(StoreList? List, long Value)> entryCounts, long entryLimit,
        IEnumerable<(StoreList? List, long Value)> fileBytes, long byteLimit)
    {
        List<Item> near = new(2);
        (StoreList? List, long Value) entries = Fullest(entryCounts), bytes = Fullest(fileBytes);
        Item list = new(PrivateStoreLimitKind.FavoriteEntries, entries.Value, entryLimit, entries.List);
        Item file = new(PrivateStoreLimitKind.FavoriteFile, bytes.Value, byteLimit, bytes.List);
        if (list.NearLimit) near.Add(list);
        if (file.NearLimit) near.Add(file);
        return near.OrderByDescending(item => item.Percent).ToArray();
    }

    private static (StoreList? List, long Value) Fullest(IEnumerable<(StoreList? List, long Value)> values)
    {
        (StoreList? List, long Value) best = (null, 0);
        foreach ((StoreList? List, long Value) value in values)
            if (value.Value > best.Value) best = value;
        return best;
    }

    /// <summary>The sentence for one warning, in the user's language: how full, which setting, which page.</summary>
    internal static string Describe(Item item)
    {
        string page = Texts.T("settings.storageLimits");
        bool file = item.Kind == PrivateStoreLimitKind.FavoriteFile;
        string name = Texts.T("limit.warn." + (file ? "file." : "list.") + item.List switch
        {
            StoreList.Favorites => "favorites",
            StoreList.ThreadFavorites => "threadFavorites",
            StoreList.Ratings => "ratings",
            _ => "any"
        });
        return file
            ? Texts.T("limit.warn.favoriteFile", item.Percent, Size(item.Used), Size(item.Limit), Texts.T("budgets.favoriteFile"), page, name)
            : Texts.Count("limit.warn.favoriteEntries", item.Limit, item.Percent, item.Used, item.Limit, Texts.T("budgets.favoriteEntries"), page, name);
    }

    /// <summary>The status-bar text (the fullest warning) and the tooltip (all warnings), or null when nothing is near a limit.</summary>
    internal static (string Text, string Tooltip)? Present(IReadOnlyList<Item> near) => near.Count == 0 ? null
        : (Describe(near[0]), string.Join(Environment.NewLine, near.Select(Describe)));

    private static string Size(long bytes) => SizeText.Format(bytes);
}
