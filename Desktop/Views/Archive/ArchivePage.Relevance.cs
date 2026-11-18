using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Relevance (Tom, 16.11.): a text search lists the threads whose title matches first, then those whose opening post matches,
// then the ones that match only in other posts. The list shows one small group header above the first row of each tier; the
// table shows a "Found in" column instead. "Search in" cuts the search off at the title or the opening post.
public partial class ArchivePage
{
    private IReadOnlyList<int>? _matchTierCounts;

    // ---- Default sort: the one place that decides it ----

    /// <summary>
    /// The order a search starts with for this text: Relevance with a text, else the latest capture. To make Relevance opt-in
    /// again, return <see cref="ArchiveThreadSort.LatestCapture"/> here; nothing else decides the default.
    /// </summary>
    internal static ArchiveThreadSort DefaultSortFor(string? text) =>
        string.IsNullOrWhiteSpace(text) ? ArchiveThreadSort.LatestCapture : ArchiveThreadSort.Relevance;

    /// <summary>
    /// The order after the text changes from <paramref name="previousText"/> to <paramref name="text"/>: a sort the user left at
    /// the default follows the default for the new text (typing a term switches to Relevance, clearing it switches back); a sort
    /// the user chose stays.
    /// </summary>
    internal static ArchiveThreadSort SortAfterTextChange(ArchiveThreadSort current, string? previousText, string? text) =>
        current == DefaultSortFor(previousText) ? DefaultSortFor(text) : current;

    private void InitializeRelevanceSort()
    {
        ArchiveSearchText.PropertyChanged += (_, e) =>
        {
            // A written draft (applied or saved search, restore) carries its own sort.
            if (e.Property != TextBox.TextProperty || _writingDraft) return;
            ArchiveThreadSort current = SortFromIndex(ArchiveSort.SelectedIndex);
            ArchiveThreadSort next = SortAfterTextChange(current, e.OldValue as string, e.NewValue as string);
            if (next != current) ArchiveSort.SelectedIndex = (int)next;
        };
    }

    private static ArchiveThreadSort SortFromIndex(int index) =>
        index >= 0 && Enum.IsDefined((ArchiveThreadSort)index) ? (ArchiveThreadSort)index : ArchiveThreadSort.LatestCapture;

    // ---- Search in (text scope): Title · Title and opening post · All posts, narrowest first ----

    internal static MeowChan.Archive.ArchiveSearchScope TextScopeFromIndex(int index) => index switch
    {
        0 => MeowChan.Archive.ArchiveSearchScope.Title, 1 => MeowChan.Archive.ArchiveSearchScope.TitleAndOpeningPost, _ => MeowChan.Archive.ArchiveSearchScope.AllPosts
    };

    internal static int TextScopeToIndex(MeowChan.Archive.ArchiveSearchScope scope) => scope switch
    {
        MeowChan.Archive.ArchiveSearchScope.Title => 0, MeowChan.Archive.ArchiveSearchScope.TitleAndOpeningPost => 1, _ => 2
    };

    internal static string TextScopeLabel(MeowChan.Archive.ArchiveSearchScope scope) => Texts.T(scope switch
    {
        MeowChan.Archive.ArchiveSearchScope.Title => "archive.textScope.title",
        MeowChan.Archive.ArchiveSearchScope.TitleAndOpeningPost => "archive.textScope.titleAndOpening",
        _ => "archive.textScope.allPosts"
    });

    private MeowChan.Archive.ArchiveSearchScope DraftTextScope => TextScopeFromIndex(ArchiveTextScope.SelectedIndex);

    private void LocalizeTextScope()
    {
        ArchiveTextScopeLabel.Text = Texts.T("archive.filter.textScope");
        Avalonia.Automation.AutomationProperties.SetName(ArchiveTextScope, Texts.T("archive.filter.textScope"));
        ToolTip.SetTip(ArchiveTextScope, Texts.T("archive.filter.textScopeHint"));
        bool first = ArchiveTextScope.ItemsSource == null;
        SetOptions("ArchiveTextScope", "archive.textScope.title", "archive.textScope.titleAndOpening", "archive.textScope.allPosts");
        if (!first) return;
        // Before its first options the list may not hold the XAML index: it starts at the default, All posts.
        _localizingSearchOptions = true;
        try { ArchiveTextScope.SelectedIndex = TextScopeToIndex(MeowChan.Archive.ArchiveSearchScope.AllPosts); }
        finally { _localizingSearchOptions = false; }
    }

    // ---- Group headers (list) and "Found in" (table) ----

    /// <summary>The "Found in" cell of a tier.</summary>
    internal static string MatchTierLabel(ArchiveSearchMatchTier tier) => Texts.T(tier switch
    {
        ArchiveSearchMatchTier.Title => "archive.textScope.title",
        ArchiveSearchMatchTier.OpeningPost => "archive.foundIn.opening",
        _ => "archive.foundIn.other"
    });

    /// <summary>The list's group header of a tier, with the tier's size over the whole result ("In title · 3").</summary>
    internal static string MatchGroupHeader(ArchiveSearchMatchTier tier, int count) => Texts.T(tier switch
    {
        ArchiveSearchMatchTier.Title => "archive.matchGroup.title",
        ArchiveSearchMatchTier.OpeningPost => "archive.matchGroup.opening",
        _ => "archive.matchGroup.other"
    }, count);

    /// <summary>
    /// The header each row of one page shows: the first row of every tier on the page (also a page that starts inside a tier)
    /// carries the tier's label with its count over the whole result; every other row, and every row of an unranked result,
    /// none. Headers are part of a row's template, never items: selection, keyboard steps and marking only see threads.
    /// </summary>
    internal static string?[] MatchGroupHeaders(IReadOnlyList<ArchiveSearchMatchTier?> tiers, IReadOnlyList<int>? counts)
    {
        string?[] headers = new string?[tiers.Count];
        ArchiveSearchMatchTier? previous = null;
        for (int i = 0; i < tiers.Count; i++)
        {
            if (tiers[i] is not { } tier) { previous = null; continue; }
            if (tier != previous)
            {
                int count = counts != null && (int)tier < counts.Count ? counts[(int)tier] : tiers.Count(item => item == tier);
                headers[i] = MatchGroupHeader(tier, count);
            }
            previous = tier;
        }
        return headers;
    }

    /// <summary>Gives the rows of a committed page their tier, group header and "Found in" text (none for an unranked page).</summary>
    private void ApplyMatchGroups(ArchiveThreadRow[] rows, IReadOnlyList<ArchiveSearchMatchTier>? tiers, IReadOnlyList<int>? counts)
    {
        _matchTierCounts = tiers == null ? null : counts;
        ApplyMatchGroups(rows, tiers?.Select(tier => (ArchiveSearchMatchTier?)tier).ToArray());
    }

    /// <summary>Re-labels rows that keep their tiers (a language change re-creates the rows from their summaries).</summary>
    private void ApplyMatchGroups(ArchiveThreadRow[] rows, IReadOnlyList<ArchiveSearchMatchTier?>? tiers)
    {
        ArchiveSearchMatchTier?[] values = tiers != null && tiers.Count == rows.Length ? tiers.ToArray() : new ArchiveSearchMatchTier?[rows.Length];
        string?[] headers = MatchGroupHeaders(values, _matchTierCounts);
        for (int i = 0; i < rows.Length; i++)
            rows[i].SetMatchGroup(values[i], headers[i], values[i] is { } tier ? MatchTierLabel(tier) : string.Empty);
        UpdateFoundInColumn();
    }

    /// <summary>The "Found in" column is part of the table only while the shown result is ranked by tier.</summary>
    private void UpdateFoundInColumn()
    {
        int index = Array.IndexOf(ThreadColumnKeys, FoundInColumnKey);
        if (index < 0 || index >= ArchiveThreadTable.Columns.Count) return;
        ArchiveThreadTable.Columns[index].IsVisible = _matchTierCounts != null && ColumnVisible(ArchiveThreadTable, FoundInColumnKey);
    }

    internal const string FoundInColumnKey = "foundIn";

    /// <summary>A click on a group header neither selects nor opens the row below it: the header is a separator.</summary>
    private void OnArchiveMatchGroupHeaderPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    private void OnArchiveMatchGroupHeaderReleased(object? sender, PointerReleasedEventArgs e) => e.Handled = true;
    private void OnArchiveMatchGroupHeaderTapped(object? sender, Avalonia.Input.TappedEventArgs e) => e.Handled = true;
}
