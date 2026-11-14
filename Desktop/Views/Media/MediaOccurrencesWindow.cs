using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Lists every occurrence of one tile's bytes: source, board, thread, file name, the occurrence's own
/// tags and a jump into the thread. Read-only; tags are shown per occurrence and never merged.
/// </summary>
internal sealed class MediaOccurrencesWindow : Window
{
    internal MediaOccurrencesWindow(MediaLibraryTile tile, Func<MediaLibraryEntry, string> tagsOf, Func<MediaLibraryEntry, bool> canJump,
        Action<MediaLibraryEntry> showInThread, Func<MediaLibraryEntry, string?>? providerOf = null)
    {
        Name = "MediaOccurrences";
        Title = Texts.T("media.occurrences.title", tile.Entry.Name ?? tile.Entry.MediaId ?? string.Empty);
        Width = 680; Height = 480; MinWidth = 340; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TextBlock summary = new() { Name = "MediaOccurrencesSummary", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = tile.ProviderPostCount == 0 ? Texts.T("media.occurrences.summary", tile.Occurrences.Count, tile.ThreadCount)
                : tile.ArchivedThreadCount == 0 ? Texts.T("booru.media.occurrences.summaryPosts", tile.Occurrences.Count, tile.ProviderPostCount)
                : Texts.T("booru.media.occurrences.summaryMixed", tile.Occurrences.Count, tile.ArchivedThreadCount, tile.ProviderPostCount) };
        summary.Classes.Add("workspace-meta");
        TextBlock favorite = new() { Name = "MediaOccurrencesFavorite", Text = MediaLibraryStateText.Favorite(tile.Favorite), IsVisible = tile.IsGroup };
        favorite.Classes.Add("workspace-meta");
        StackPanel rows = new() { Spacing = 8 };
        foreach (MediaLibraryEntry entry in tile.Occurrences)
        {
            string board = Texts.T("media.filter.boardOfSource", entry.Thread.Board, entry.Thread.Source);
            string thread = string.IsNullOrWhiteSpace(entry.ThreadTitle) ? "#" + entry.Thread.Thread : entry.ThreadTitle;
            // A provider medium is told apart by its source and post, and lists every finding that brought it here.
            string? provider = entry.BooruPostId == null ? null : providerOf?.Invoke(entry);
            TextBlock where = new() { Name = "MediaOccurrenceWhere", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = entry.BooruPostId == null ? board + " · " + thread : Texts.T("booru.media.badge") + " · " + Texts.T("booru.media.tile.post", entry.BooruPostId) };
            where.Classes.Add("section-title");
            TextBlock file = new() { Name = "MediaOccurrenceFile", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = string.Join(" · ", new[] { entry.Name ?? entry.MediaId ?? string.Empty,
                    entry.BooruPostId != null ? string.Empty : entry.PostId == null ? Texts.T("media.tile.unassigned") : "#" + entry.PostId }.Where(part => part.Length > 0)) };
            TextBlock tags = new() { Name = "MediaOccurrenceTags", Text = tagsOf(entry), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            TextBlock claims = new() { Name = "MediaOccurrenceFindings", Text = provider ?? string.Empty, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                IsVisible = !string.IsNullOrEmpty(provider) };
            foreach (TextBlock text in new[] { file, tags, claims }) text.Classes.Add("workspace-meta");
            Button show = new() { Name = "MediaOccurrenceShow", Content = Texts.T("media.showInThread"), IsEnabled = canJump(entry),
                HorizontalAlignment = HorizontalAlignment.Left };
            show.Click += (_, _) => showInThread(entry);
            string description = string.Join(Environment.NewLine, new[] { where.Text, file.Text, tags.Text, claims.Text }.Where(part => !string.IsNullOrEmpty(part)));
            StackPanel row = new() { Name = "MediaOccurrence", Spacing = 4, Children = { where, file, claims, tags, show } };
            AutomationProperties.SetName(row, description);
            rows.Children.Add(new Border { Child = row, Classes = { "workspace-card" } });
        }
        Button close = new() { Content = Texts.T("action.close"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        ScrollViewer list = new() { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid layout = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        layout.Children.Add(new StackPanel { Spacing = 2, Children = { summary, favorite } });
        layout.Children.Add(list); Grid.SetRow(list, 1);
        layout.Children.Add(close); Grid.SetRow(close, 2);
        Content = WindowChrome.Frame(this, layout);
        KeyDown += (_, e) => { if (e.Key != Key.Escape) return; e.Handled = true; Close(); };
    }
}
