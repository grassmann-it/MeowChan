using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Lists every occurrence of one tile's bytes: source, board, thread, file name, the occurrence's own
/// tags and a jump into the thread. Read-only; tags are shown per occurrence and never merged.
/// </summary>
internal sealed class MediaOccurrencesWindow : Window
{
    private readonly Func<bool, CancellationToken, Task<Dictionary<MediaLibraryEntry, IReadOnlyList<BooruPostClaimTag>>?>>? _readSourceTags;
    private sealed record SourceTagRow(StackPanel Row, string Description, Button Show, TextBlock Text, Button Copy, string? Source);
    private readonly Dictionary<MediaLibraryEntry, SourceTagRow> _sourceTagRows = new();
    private readonly TextBlock _copyStatus = new()
    {
        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "workspace-meta" }, IsVisible = false
    };
    private CancellationTokenSource? _sourceTagRead;

    internal MediaOccurrencesWindow(MediaLibraryTile tile, Func<MediaLibraryEntry, string> tagsOf, Func<MediaLibraryEntry, bool> canJump,
        Action<MediaLibraryEntry> showInThread, Func<MediaLibraryEntry, string?>? providerOf = null,
        Func<MediaLibraryEntry, Action, Control?>? extraOf = null,
        Func<bool, CancellationToken, Task<Dictionary<MediaLibraryEntry, IReadOnlyList<BooruPostClaimTag>>?>>? readSourceTags = null)
    {
        _readSourceTags = readSourceTags;
        Name = "MediaOccurrences";
        Title = Texts.T("media.occurrences.title", tile.Entry.Name ?? tile.Entry.MediaId ?? string.Empty);
        // Modals plan MD1: modeless (minimize and taskbar stay), the list step, fitted to the screen.
        DialogWindow.Configure(this, DialogSize.M, modal: false);
        TextBlock summary = new() { Name = "MediaOccurrencesSummary", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = tile.ProviderPostCount == 0 ? Texts.T("media.occurrences.summary", tile.Occurrences.Count, tile.ThreadCount)
                : tile.ArchivedThreadCount == 0 ? Texts.T("booru.media.occurrences.summaryPosts", tile.Occurrences.Count, tile.ProviderPostCount)
                : Texts.T("booru.media.occurrences.summaryMixed", tile.Occurrences.Count, tile.ArchivedThreadCount, tile.ProviderPostCount) };
        summary.Classes.Add("workspace-meta");
        TextBlock favorite = new() { Name = "MediaOccurrencesFavorite", Text = MediaLibraryStateText.Favorite(tile.Favorite), IsVisible = tile.IsGroup };
        favorite.Classes.Add("workspace-meta");
        StackPanel rows = new() { Spacing = 8, Margin = new Thickness(16, 0, 16, 16) };
        foreach (MediaLibraryEntry entry in tile.Occurrences)
        {
            // A folder file is told apart by its folder's name; it has no board or thread.
            bool folderFile = entry.FolderItemId != null;
            string board = Texts.T("media.filter.boardOfSource", entry.Thread.Board, entry.Thread.Source);
            string thread = string.IsNullOrWhiteSpace(entry.ThreadTitle) ? "#" + entry.Thread.Thread : entry.ThreadTitle;
            // A provider medium is told apart by its source and post, and lists every finding that brought it here.
            string? provider = entry.BooruPostId == null ? null : providerOf?.Invoke(entry);
            TextBlock where = new() { Name = "MediaOccurrenceWhere", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = folderFile ? Texts.T("folderSource.media.board", string.IsNullOrWhiteSpace(entry.ThreadTitle) ? Texts.T("folderSource.media.unknown") : entry.ThreadTitle)
                    : entry.BooruIdentity is { } identity
                        ? string.Join(" · ", identity.SourceId == BooruMediaIdentity.Source ? Texts.T("booru.media.source") : identity.SourceId,
                            identity.InstanceId, Texts.T("booru.ui.post.title", identity.PostId))
                        : board + " · " + thread };
            where.Classes.Add("section-title");
            TextBlock file = new() { Name = "MediaOccurrenceFile", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = string.Join(" · ", new[] { entry.Name ?? entry.MediaId ?? string.Empty,
                    entry.BooruPostId != null || folderFile ? string.Empty : entry.PostId == null ? Texts.T("media.tile.unassigned") : "#" + entry.PostId }.Where(part => part.Length > 0)) };
            TextBlock tags = new() { Name = "MediaOccurrenceTags", Text = tagsOf(entry), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            TextBlock claims = new() { Name = "MediaOccurrenceFindings", Text = provider ?? string.Empty, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                IsVisible = !string.IsNullOrEmpty(provider) };
            foreach (TextBlock text in new[] { file, tags, claims }) text.Classes.Add("workspace-meta");
            Button show = new() { Name = "MediaOccurrenceShow", Content = Texts.T("media.showInThread"), IsEnabled = canJump(entry),
                HorizontalAlignment = HorizontalAlignment.Left };
            show.Click += (_, _) => showInThread(entry);
            // Every row has its own Show in thread: the name says which occurrence it opens.
            AutomationProperties.SetName(show, ShowName((string)show.Content, where.Text));
            string description = string.Join(Environment.NewLine, new[] { where.Text, file.Text, tags.Text, claims.Text }.Where(part => !string.IsNullOrEmpty(part)));
            StackPanel row = new() { Name = "MediaOccurrence", Spacing = 4, Children = { where, file, claims, tags, show } };
            // Extra content of a row (the hint about a changed folder file) can ask for the row's own tags to be shown again.
            if (extraOf?.Invoke(entry, () => tags.Text = tagsOf(entry)) is { } extra) row.Children.Add(extra);
            AutomationProperties.SetName(row, description);
            if (_readSourceTags != null && entry.BooruIdentity != null)
                AddSourceTagControls(row, description, show, entry, where.Text);
            rows.Children.Add(new Border { Child = row, Classes = { "workspace-card" } });
        }
        // The title (the file) stands in the title bar; the summary is the header. Read-only with Close as its only way
        // out, so there is no footer: × at the top right and Escape close it.
        // The list carries the window title (the file), which otherwise stands in the title bar only.
        AutomationProperties.SetName(rows, Title);
        StackPanel header = new() { Spacing = 2, Children = { summary, favorite } };
        AutomationProperties.SetAutomationId(_copyStatus, "MediaOccurrenceCopyStatus");
        AutomationProperties.SetLiveSetting(_copyStatus, AutomationLiveSetting.Polite);
        header.Children.Add(_copyStatus);
        DialogWindow.Frame(this, DialogFrame.Build(header, rows, footer: null), modal: false);
        DialogEscape.Attach(this);

        if (_sourceTagRows.Count != 0)
        {
            // The local rules and files can change while this modeless window stays open.
            DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => RefreshSourceTags();
            Opened += (_, _) => { timer.Start(); RefreshSourceTags(); };
            Closed += (_, _) => { timer.Stop(); _sourceTagRead?.Cancel(); };
        }
    }

    private void AddSourceTagControls(StackPanel row, string description, Button show, MediaLibraryEntry entry, string? source)
    {
        TextBlock text = new() { IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "workspace-meta" } };
        AutomationProperties.SetAutomationId(text, "MediaOccurrenceSourceTags");
        string label = Texts.T("booru.media.tags.copyAll");
        Button copy = new() { Content = label, IsVisible = false, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(copy, "MediaOccurrenceCopySourceTags");
        AutomationProperties.SetName(copy, ShowName(label, source));
        copy.Click += (_, _) => RefreshSourceTags(entry);
        row.Children.Add(text);
        row.Children.Add(copy);
        row.Focusable = true;
        row.IsTabStop = false;
        _sourceTagRows.Add(entry, new(row, description, show, text, copy, source));
    }

    private void RefreshSourceTags(MediaLibraryEntry? copy = null) =>
        OpenActions.Start(() => RefreshSourceTagsAsync(copy), "media.occurrences.sourceTags");

    private async Task RefreshSourceTagsAsync(MediaLibraryEntry? copy)
    {
        if (_readSourceTags == null || !IsVisible || (copy == null && _sourceTagRead != null)) return;
        // Copy starts a new local read even if a periodic refresh is already in flight.
        _sourceTagRead?.Cancel();
        using CancellationTokenSource reading = new();
        _sourceTagRead = reading;
        try
        {
            if (copy != null) _copyStatus.IsVisible = false;
            Dictionary<MediaLibraryEntry, IReadOnlyList<BooruPostClaimTag>>? tags = await _readSourceTags(copy != null, reading.Token);
            reading.Token.ThrowIfCancellationRequested();
            if (tags == null) return;
            foreach (var (entry, (row, description, show, text, button, source)) in _sourceTagRows)
            {
                bool available = tags.TryGetValue(entry, out IReadOnlyList<BooruPostClaimTag>? names);
                text.Text = names == null ? null : SourceTagText(source, names);
                AutomationProperties.SetName(text, text.Text);
                AutomationProperties.SetName(row, text.Text == null ? description : description + Environment.NewLine + text.Text);
                if (!available && button.IsFocused && (!show.IsEffectivelyEnabled || !show.Focus())) row.Focus();
                text.IsVisible = button.IsVisible = button.IsEnabled = available;
            }
            if (copy == null) return;
            bool success = tags.TryGetValue(copy, out IReadOnlyList<BooruPostClaimTag>? copied)
                && await CopyTextAsync(BooruSourceTagSearch.Written(copied.Select(tag => tag.Name)));
            if (!reading.IsCancellationRequested)
            {
                _copyStatus.IsVisible = true;
                _copyStatus.Text = success ? Texts.Count("booru.media.tags.copied", copied!.Count) : Texts.T("booru.media.tags.copyFailed");
            }
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
            // A newer read (or closing the window) superseded this one; it owns the result.
        }
        finally
        {
            if (ReferenceEquals(_sourceTagRead, reading)) _sourceTagRead = null;
        }
    }

    // A refused or failing clipboard is told in words (copyFailed); it never throws into the caller.
    private async Task<bool> CopyTextAsync(string text)
    {
        try
        {
            if (Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string SourceTagText(string? source, IReadOnlyList<BooruPostClaimTag> tags) => string.Join(Environment.NewLine,
        tags.GroupBy(tag => tag.Category).Select(group => BooruMessages.TagCategory(group.Key) + " · " + string.Join(", ", group.Select(tag => tag.Name)))
            .Prepend(Texts.T("booru.media.tags.listFor", source ?? string.Empty)));

    /// <summary>The accessible name of a row's action: the action, then the occurrence it applies to.</summary>
    internal static string ShowName(string action, string? where) =>
        string.IsNullOrEmpty(where) ? action : action + ", " + where;
}
