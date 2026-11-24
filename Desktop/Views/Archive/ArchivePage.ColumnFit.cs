using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// A1 and A3 of the archive table: default fit widths per language, and "Fit column" / "Fit all columns".
public partial class ArchivePage
{
    private const string ThreadTableName = "ArchiveThreadTable";
    // Upper layout bounds of a fit width (header and widest value stay whole well below them); not user data limits.
    private static readonly Dictionary<string, double> ThreadFitMax = new(StringComparer.Ordinal)
    {
        ["posts"] = 140, ["media"] = 260, ["source"] = 200, ["work"] = 200, ["lastCapture"] = 260
    };
    private const double FitMaxContent = 480;
    private readonly Dictionary<string, IReadOnlyDictionary<string, double>> _columnFitCache = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, double> NoFitWidths = new Dictionary<string, double>();

    private void InitializeArchiveColumnMenus()
    {
        // Tunnel: the table's own selection menu would otherwise take the request first. Column headers are not focusable, so the
        // keyboard reaches the same commands through the "Columns…" button.
        foreach (DataGrid grid in new[] { ArchiveThreadTable, ArchiveMediaGrid })
            grid.AddHandler(ContextRequestedEvent, OnArchiveHeaderContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The fonts the fit is measured with are the inherited ones, which are only known once the table is in the window.
        ArchiveThreadTable.AttachedToVisualTree += (_, _) => ApplyArchiveColumns();
    }

    private void OnArchiveHeaderContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not DataGrid grid || e.Source is not Visual source) return;
        DataGridColumnHeader? header = source.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault();
        if (header == null) return;
        e.Handled = true;
        string[] keys = ColumnKeys(grid);
        string? target = null;
        for (int index = 0; index < keys.Length && target == null; index++)
            if (grid.Columns[index].IsVisible && Equals(grid.Columns[index].Header, header.Content)) target = keys[index];
        ContextMenu menu = BuildArchiveColumnsMenu(grid, target);
        ShowColumnMenu(menu, header);
    }

    /// <summary>How a column menu (a header's or the Columns button's) is shown (a test records it instead of opening a popup).</summary>
    internal Action<ContextMenu, Control> ShowColumnMenu { get; set; } = static (menu, header) => menu.Open(header);

    /// <summary>
    /// Header of a thread column. Numbers (posts, saved versions) get a right-aligned header over their right-aligned values; the
    /// favourite column has the star as its header, named and explained by tooltip and accessible name. The control of a header is
    /// kept across language changes and only renamed.
    /// </summary>
    private void SetThreadColumnHeader(DataGridColumn column, string key)
    {
        string text = Texts.T("archive.columns." + key);
        switch (key)
        {
            case PinnedColumnKey:
                if (column.Header is not Border border)
                {
                    Avalonia.Controls.Shapes.Path star = WorkspaceIcons.Create(WorkspaceIcons.Star);
                    star.Stretch = Stretch.Uniform; star.Width = 14; star.Height = 14;
                    border = new Border
                    {
                        Background = Brushes.Transparent, Child = star,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                    column.Header = border;
                }
                ToolTip.SetTip(border, text);
                AutomationProperties.SetName(border, text);
                break;
            case "posts" or "media":
                if (column.Header is not TextBlock label)
                {
                    label = new TextBlock
                    {
                        TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    column.Header = label;
                }
                label.Text = text;
                break;
            default:
                column.Header = text;
                break;
        }
    }

    private static double HeaderFontSize(Control scope) =>
        scope.TryFindResource("Workspace.Text.Meta", scope.ActualThemeVariant, out object? meta) && meta is double size ? size : 12;

    /// <summary>
    /// Fit widths of the thread table's columns that have a content pattern, by column key. Measured once per language, culture and
    /// font, then served from the cache; empty for the media grid and when the text engine cannot measure (the fixed defaults apply).
    /// </summary>
    private IReadOnlyDictionary<string, double> FitDefaultWidths(DataGrid grid)
    {
        if (grid.Name != ThreadTableName) return NoFitWidths;
        double body = grid.FontSize, header = HeaderFontSize(grid);
        string cacheKey = ColumnFit.CacheKey(grid.Name, Texts.Language, Texts.Culture.Name, grid.FontFamily.ToString(), body, header);
        if (_columnFitCache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, double>? cached)) return cached;
        Dictionary<string, double> widths = new(StringComparer.Ordinal);
        ColumnFit.Measurer measurer = new(grid.FontFamily);
        string[] keys = ColumnKeys(grid);
        string[] none = ["—"];
        Dictionary<string, string[]> samples = new(StringComparer.Ordinal)
        {
            ["posts"] = [ColumnFit.NumberPattern(0)],
            ["media"] = [ColumnFit.NumberPattern(0), .. none],
            ["source"] = new[] { "sourceActive", "sourceArchived", "sourceMissing" }.Select(k => Texts.T("archive.columns." + k)).ToArray(),
            ["work"] = new[] { "workStopped", "workDone", "workRepair", "workAttention", "workWatching" }
                .Select(k => Texts.T("archive.columns." + k)).Concat(none).ToArray(),
            ["lastCapture"] = [.. ColumnFit.DatePatterns(Texts.Culture), Texts.T("gallery.unknown")]
        };
        foreach ((string key, string[] sample) in samples)
        {
            int index = Array.IndexOf(keys, key);
            if (index < 0) continue;
            double headerWidth = measurer.Measure(Texts.T("archive.columns." + key), header, FontWeight.SemiBold);
            double contentWidth = ColumnFit.Widest(sample, text => measurer.Measure(text, body));
            // No measurement at all (no text engine): leave the column on its fixed default.
            if (headerWidth <= 0 && contentWidth <= 0) continue;
            widths[key] = ColumnFit.Width(headerWidth, contentWidth, grid.Columns[index].MinWidth, ThreadFitMax[key]);
        }
        if (_columnFitCache.Count > 8) _columnFitCache.Clear();
        _columnFitCache[cacheKey] = widths;
        return widths;
    }

    /// <summary>A column can be fitted when it is shown and not the pinned star or a column that takes the rest of the width.</summary>
    private bool CanFitArchiveColumn(DataGrid grid, string key)
    {
        int index = Array.IndexOf(ColumnKeys(grid), key);
        if (index < 0 || key == PinnedColumnKey) return false;
        DataGridColumn column = grid.Columns[index];
        return column.IsVisible && DefaultColumnWidth(grid, index, FitDefaultWidths(grid)) != 0;
    }

    /// <summary>
    /// The width "Fit column" gives: the fit width of a pattern column, otherwise the header or the widest value of the rows
    /// that are loaded. The rows are measured once, on this command, not while scrolling.
    /// </summary>
    private double? ExplicitFitWidth(DataGrid grid, int index, ColumnFit.Measurer measurer, IReadOnlyDictionary<string, double> fit)
    {
        string key = ColumnKeys(grid)[index];
        if (fit.TryGetValue(key, out double known)) return known;
        double body = grid.FontSize, header = HeaderFontSize(grid);
        IEnumerable<string> texts = grid.Name == ThreadTableName
            ? key switch
            {
                "identity" => ArchiveVisibleRows.Select(row => row.Identity),
                "tags" => ArchiveVisibleRows.Select(row => row.TagColumnText),
                _ => []
            }
            : key switch
            {
                "size" => MediaRows().Select(row => row.Size),
                "version" => MediaRows().Select(row => row.Version),
                "rating" => MediaRows().Select(row => row.RatingLabel),
                _ => []
            };
        double headerWidth = measurer.Measure(key == "rating" && grid.Name != ThreadTableName
            ? MeowChan.Desktop.MediaRatingText.Label : Texts.T("archive.columns." + key), header, FontWeight.SemiBold);
        double contentWidth = ColumnFit.Widest(texts, text => measurer.Measure(text, body));
        if (headerWidth <= 0 && contentWidth <= 0) return null;
        double padding = grid.Columns[index] is DataGridTextColumn
            ? ColumnFit.CellPadding + ColumnFit.TextColumnExtraPadding : ColumnFit.CellPadding;
        return ColumnFit.Width(headerWidth, contentWidth, grid.Columns[index].MinWidth, FitMaxContent, padding,
            sortable: grid.CanUserSortColumns);
    }

    private IEnumerable<ArchiveAssetRow> MediaRows() => ArchiveMediaGrid.ItemsSource?.OfType<ArchiveAssetRow>() ?? [];

    /// <summary>
    /// "Fit column" (<paramref name="target"/>) or "Fit all columns" (null): stores the fit widths like a drag does, so they survive
    /// restarts and belong to the profile and work view. "Reset" removes them again and the language-fit default returns.
    /// </summary>
    private async Task FitArchiveColumnsAsync(DataGrid grid, string? target)
    {
        string[] keys = ColumnKeys(grid);
        IReadOnlyDictionary<string, double> fit = FitDefaultWidths(grid);
        ColumnFit.Measurer measurer = new(grid.FontFamily);
        Dictionary<string, double> next = new(_archiveColumnWidths, StringComparer.Ordinal);
        bool changed = false;
        for (int index = 0; index < keys.Length; index++)
        {
            if (target != null && keys[index] != target) continue;
            if (!CanFitArchiveColumn(grid, keys[index])) continue;
            if (ExplicitFitWidth(grid, index, measurer, fit) is not double width) continue;
            width = Math.Clamp(width, 55, 2000);
            string key = ColumnPreferenceKey(grid, keys[index]);
            if (next.TryGetValue(key, out double prior) && Math.Abs(prior - width) < 0.5) continue;
            next[key] = width;
            changed = true;
        }
        if (!changed) return;
        var saveWidths = SaveArchiveColumnWidthsAsync;
        _archiveColumnWidths = next;
        ApplyArchiveColumns();
        if (saveWidths == null) return;
        try { await saveWidths(next); }
        catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }
}
