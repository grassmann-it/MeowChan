using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Archive;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// The details of one downloaded Danbooru medium, shared by the media page and the gallery. Everything shown comes
/// from the local catalog and the saved file: it never contacts the provider. The provider's rating and tags are
/// shown as provider data, clearly apart from the user's own state; source tags are read-only and are never saved as
/// private MeowChan tags.
/// </summary>
internal sealed class BooruMediaDetailsDialog : Window
{
    internal const string DialogName = "BooruMediaDetailsDialog";
    private readonly TextBlock _status = new() { Name = "BooruDetailsStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 0 };
    private readonly Func<Uri, Task> _open;
    private readonly Func<Uri, Task> _copy;

    /// <param name="fileLines">File name, size and checksum of the saved original, already formatted by the caller.</param>
    /// <param name="details">The catalog's data for the post, or null when the catalog cannot be read right now.</param>
    /// <param name="open">Opens the post page in the browser; injected so tests never start a browser.</param>
    internal BooruMediaDetailsDialog(string postId, IReadOnlyList<string> fileLines, BooruMediaDetails? details,
        Func<Uri, Task> open, Func<Uri, Task> copy)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        Name = DialogName;
        Title = Texts.T("booru.media.detail.title", postId);
        Width = 640; Height = 620; MinWidth = 380; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        TextBlock heading = new() { Name = "BooruDetailsHeading", Text = Title, TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("section-title");
        StackPanel body = new() { Spacing = 10, Margin = new Thickness(16, 4, 16, 16) };
        body.Children.Add(Badges(details));
        foreach (string line in fileLines)
            body.Children.Add(new SelectableTextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        if (details == null) body.Children.Add(Note("BooruDetailsUnavailable", Texts.T("booru.media.details.unavailable")));
        else
        {
            body.Children.Add(PostRow(details));
            body.Children.Add(Section("BooruVersions", Texts.T("booru.media.variants.heading"), VariantLines(details),
                Texts.T("booru.media.variants.note")));
            body.Children.Add(Section("BooruProvider", Texts.T("booru.media.provider.heading"), ProviderLines(details),
                Texts.T("booru.media.provider.note")));
            body.Children.Add(Section("BooruFindings", Texts.T("booru.media.findings.heading"), FindingLines(details),
                Texts.T("booru.media.findings.note")));
            body.Children.Add(SourceTags(details));
        }
        body.Children.Add(OwnTags());
        body.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        Button close = new() { Name = "BooruDetailsClose", Content = Texts.T("action.close"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = DialogFrame.Build(heading, body, close);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    internal TextBlock Status => _status;

    private static Control Badges(BooruMediaDetails? details)
    {
        WrapPanel row = new() { Name = "BooruBadges", ItemSpacing = 6, LineSpacing = 6 };
        row.Children.Add(Chip("BooruBadgeSource", Texts.T("booru.media.badge")));
        if (details != null)
        {
            row.Children.Add(Chip("BooruBadgeRating", Texts.T("booru.rating.label") + ": "
                + (details.RatingKnown ? BooruMessages.Rating(details.Rating) : Texts.T("booru.field.absent"))));
            row.Children.Add(Chip("BooruBadgeOriginal", VariantText(details.Original)));
            if (details.IsDeleted) row.Children.Add(Chip("BooruBadgeDeleted", Texts.T("booru.post.deleted")));
            if (details.IsBanned) row.Children.Add(Chip("BooruBadgeBanned", Texts.T("booru.post.banned")));
        }
        return row;
    }

    private static Border Chip(string name, string text)
    {
        TextBlock label = new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        Border chip = new()
        {
            Name = name, Child = label, Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        chip[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.AccentSurface");
        chip[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Border");
        AutomationProperties.SetName(chip, text);
        return chip;
    }

    private Control PostRow(BooruMediaDetails details)
    {
        TextBlock where = new() { Name = "BooruPostLine", Text = Texts.T("booru.media.detail.post", details.PostId), TextWrapping = TextWrapping.Wrap };
        Button open = new() { Name = "BooruOpenPost", Content = Texts.T("booru.media.openPost") };
        Button copy = new() { Name = "BooruCopyPost", Content = Texts.T("booru.media.copyPost") };
        ToolTip.SetTip(open, details.PostUrl.AbsoluteUri);
        ToolTip.SetTip(copy, details.PostUrl.AbsoluteUri);
        open.Click += async (_, _) => await RunAsync(() => _open(details.PostUrl), null, "booru.media.openFailed");
        copy.Click += async (_, _) => await RunAsync(() => _copy(details.PostUrl), "booru.media.copied", "booru.media.copyFailed");
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 6, Children = { open, copy } };
        return new StackPanel { Name = "BooruPostRow", Spacing = 6, Children = { where, actions } };
    }

    private async Task RunAsync(Func<Task> action, string? successKey, string failureKey)
    {
        try
        {
            await action();
            _status.Text = successKey == null ? string.Empty : Texts.T(successKey);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _status.Text = Texts.T(failureKey);
        }
    }

    private static string VariantText(BooruMediaVariant variant) =>
        BooruMessages.MediaRole(variant.Role) + ": " + Texts.T("booru.media.variant." + variant.State);

    private static IEnumerable<string> VariantLines(BooruMediaDetails details) => details.Variants.Select(variant =>
    {
        List<string> parts = new() { VariantText(variant) };
        if (variant.Width is int width && variant.Height is int height) parts.Add(width + "×" + height);
        if (variant.ExpectedSize is long size) parts.Add((size / 1_000_000d).ToString("0.###", Texts.Culture) + " MB");
        return string.Join(" · ", parts);
    });

    private static IEnumerable<string> ProviderLines(BooruMediaDetails details)
    {
        yield return Texts.T("booru.rating.label") + ": " + (details.RatingKnown
            ? BooruMessages.Rating(details.Rating) : Texts.T("booru.field.absent"));
        yield return details.Score is int score ? Texts.T("booru.media.provider.score", score) : Texts.T("booru.media.provider.score", Texts.T("booru.field.absent"));
        if (details.FavoriteCount is int favorites) yield return Texts.T("booru.media.provider.favorites", favorites);
        if (details.CreatedAtUtc is { } created) yield return Texts.T("booru.media.provider.created", created.ToLocalTime());
        if (!string.IsNullOrWhiteSpace(details.SourceClaim)) yield return Texts.T("booru.media.provider.source", details.SourceClaim!);
        yield return Texts.T("booru.media.provider.seen", details.FirstSeenAtUtc.ToLocalTime(), details.LastSeenAtUtc.ToLocalTime());
    }

    internal static string FindingText(BooruMediaFinding finding)
    {
        List<string> parts = new() { BooruMessages.Finding(finding.Kind) };
        if (!string.IsNullOrEmpty(finding.Query)) parts.Add("“" + finding.Query + "”");
        parts.Add(finding.FoundAtUtc.ToLocalTime().ToString("g", Texts.Culture));
        if (finding.Watcher == BooruMediaWatcherStatus.Removed) parts.Add(Texts.T("booru.media.finding.watcherRemoved"));
        else if (finding.Watcher == BooruMediaWatcherStatus.Observing && finding.WatcherState != null)
            parts.Add(Texts.T("booru.media.finding.watcherState", WatcherStateText(finding.WatcherState)));
        return string.Join(" · ", parts);
    }

    private static string WatcherStateText(string state) =>
        Texts.T(BuiltInCatalog.English.ContainsKey("booru.watcher.state." + state) ? "booru.watcher.state." + state : "booru.watcher.state.Unknown");

    private static IEnumerable<string> FindingLines(BooruMediaDetails details) => details.Findings.Count == 0
        ? new[] { Texts.T("booru.media.findings.none") }
        : details.Findings.Select(FindingText);

    private static Control SourceTags(BooruMediaDetails details)
    {
        StackPanel panel = new() { Name = "BooruSourceTags", Spacing = 4 };
        TextBlock heading = new() { Text = Texts.T("booru.tags.sourceLabel"), TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("section-title");
        panel.Children.Add(heading);
        if (details.Tags.Count == 0) panel.Children.Add(Note("BooruSourceTagsNone", Texts.T("booru.media.tags.none")));
        foreach (IGrouping<BooruPostClaimTagCategory, BooruPostClaimTag> group in details.Tags.GroupBy(tag => tag.Category))
        {
            // The category names the group; the tag keeps the provider's exact spelling and is selectable text, not a tag chip.
            string category = BooruMessages.TagCategory(group.Key);
            SelectableTextBlock line = new() { Name = "BooruSourceTagGroup", TextWrapping = TextWrapping.Wrap,
                Text = category + ": " + string.Join(", ", group.Select(tag => tag.Name)) };
            AutomationProperties.SetName(line, line.Text);
            panel.Children.Add(line);
        }
        panel.Children.Add(Note("BooruSourceTagsNote", Texts.T("booru.media.sourceTags.note")));
        return panel;
    }

    private static Control OwnTags()
    {
        StackPanel panel = new() { Name = "BooruOwnTags", Spacing = 4 };
        TextBlock heading = new() { Text = Texts.T("booru.media.ownTags.heading"), TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("section-title");
        panel.Children.Add(heading);
        panel.Children.Add(Note("BooruOwnTagsNote", Texts.T("booru.media.ownTags.unavailable")));
        return panel;
    }

    private static Control Section(string name, string heading, IEnumerable<string> lines, string note)
    {
        StackPanel panel = new() { Name = name, Spacing = 4 };
        TextBlock title = new() { Text = heading, TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("section-title");
        panel.Children.Add(title);
        foreach (string line in lines)
            panel.Children.Add(new SelectableTextBlock { Name = name + "Line", Text = line, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Note(name + "Note", note));
        return panel;
    }

    private static TextBlock Note(string name, string text)
    {
        TextBlock note = new() { Name = name, Text = text, TextWrapping = TextWrapping.Wrap };
        note.Classes.Add("workspace-meta");
        return note;
    }
}
