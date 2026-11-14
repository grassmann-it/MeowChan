using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Controls.Templates;
using MeowChan.Archive;
using MeowChan.Desktop.Booru;
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
    private readonly Func<string, Task> _copyText;
    private readonly Func<string, bool>? _useAsSearch;
    private readonly List<ListBox> _tagLists = new();
    private readonly TextBlock _tagStatus = new() { Name = "BooruSourceTagStatus", TextWrapping = TextWrapping.Wrap, MinHeight = 0 };
    private readonly TextBlock _tagSelection = new() { Name = "BooruSourceTagSelection", TextWrapping = TextWrapping.Wrap };
    private readonly Button _copySelectedTags = new() { Name = "BooruCopySelectedTags", IsEnabled = false };
    private readonly Button _useAsSearchButton = new() { Name = "BooruUseAsSearch", IsEnabled = false };

    /// <param name="fileLines">File name, size and checksum of the saved original, already formatted by the caller.</param>
    /// <param name="details">The catalog's data for the post, or null when the catalog cannot be read right now.</param>
    /// <param name="open">Opens the post page in the browser; injected so tests never start a browser.</param>
    /// <param name="copyText">Puts source tags (or a search query) on the clipboard.</param>
    /// <param name="useAsSearch">Puts a checked query into the Danbooru window's search field and returns true, or returns
    /// false (or is null) when that window cannot be reached; the query is then copied instead.</param>
    internal BooruMediaDetailsDialog(string postId, IReadOnlyList<string> fileLines, BooruMediaDetails? details,
        Func<Uri, Task> open, Func<Uri, Task> copy, Func<string, Task> copyText, Func<string, bool>? useAsSearch)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _copyText = copyText ?? throw new ArgumentNullException(nameof(copyText));
        _useAsSearch = useAsSearch;
        Name = DialogName;
        Title = Texts.T("booru.media.detail.title", postId);
        // Modals plan MD2: a read-only modal details dialog on the form step (fitted to the screen). The title stands in
        // the title bar only, and a lone Close button is left out: × at the top right and Escape close it.
        DialogWindow.Configure(this, DialogSize.M, modal: true);

        StackPanel body = new() { Spacing = 10, Margin = new Thickness(16, 0, 16, 16) };
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
            AutomationProperties.SetLiveSetting(_tagStatus, AutomationLiveSetting.Polite);
        }
        body.Children.Add(OwnTags());
        body.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        DialogWindow.Frame(this, DialogFrame.Build(header: null, body, footer: null), modal: true);
        DialogEscape.Attach(this);
        // Like the other dialogs, start on the first action: the post page when the catalog has the post, otherwise the window.
        Opened += (_, _) => (_initialFocus ?? this).Focus();
    }

    private Control? _initialFocus;

    internal TextBlock Status => _status;
    internal TextBlock TagStatus => _tagStatus;

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

    // C1: static facts are chip badges (filled accent surface, chip outline), not tags.
    private static Border Chip(string name, string text) => WorkspaceChips.CreateBadge(name, text);

    private Control PostRow(BooruMediaDetails details)
    {
        TextBlock where = new() { Name = "BooruPostLine", Text = Texts.T("booru.media.detail.post", details.PostId), TextWrapping = TextWrapping.Wrap };
        Button open = new() { Name = "BooruOpenPost", Content = Texts.T("booru.media.openPost") };
        Button copy = new() { Name = "BooruCopyPost", Content = Texts.T("booru.media.copyPost") };
        ToolTip.SetTip(open, details.PostUrl.AbsoluteUri);
        ToolTip.SetTip(copy, details.PostUrl.AbsoluteUri);
        AutomationProperties.SetHelpText(open, details.PostUrl.AbsoluteUri);
        AutomationProperties.SetHelpText(copy, details.PostUrl.AbsoluteUri);
        _initialFocus = open;
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
        if (variant.ExpectedSize is long size) parts.Add(SizeText.Format(size));
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

    private Control SourceTags(BooruMediaDetails details)
    {
        StackPanel panel = new() { Name = "BooruSourceTags", Spacing = 4 };
        TextBlock heading = new() { Text = Texts.T("booru.tags.sourceLabel"), TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add("section-title");
        panel.Children.Add(heading);
        if (details.Tags.Count == 0)
        {
            panel.Children.Add(Note("BooruSourceTagsNone", Texts.T("booru.media.tags.none")));
            panel.Children.Add(Note("BooruSourceTagsNote", Texts.T("booru.media.sourceTags.note")));
            return panel;
        }

        // Actions first, so they are reachable without scrolling past a long tag list. Copying never needs the provider.
        Button copyAll = new() { Name = "BooruCopyAllTags", Content = Texts.T("booru.media.tags.copyAll") };
        copyAll.Click += async (_, _) => await CopyTagsAsync(details.Tags.Select(tag => tag.Name).ToArray());
        _copySelectedTags.Content = Texts.T("booru.media.tags.copySelected");
        _copySelectedTags.Click += async (_, _) => await CopyTagsAsync(SelectedTagNames());
        _useAsSearchButton.Content = Texts.T("booru.media.tags.useAsSearch");
        ToolTip.SetTip(_useAsSearchButton, Texts.T("booru.media.tags.useAsSearch.tip"));
        AutomationProperties.SetHelpText(_useAsSearchButton, Texts.T("booru.media.tags.useAsSearch.tip"));
        _useAsSearchButton.Click += (_, _) => UseSelectionAsSearch();
        panel.Children.Add(new WrapPanel { Name = "BooruSourceTagActions", ItemSpacing = 8, LineSpacing = 6,
            Children = { copyAll, _copySelectedTags, _useAsSearchButton } });
        _tagSelection.Classes.Add("workspace-meta");
        panel.Children.Add(_tagSelection);
        panel.Children.Add(_tagStatus);

        foreach (IGrouping<BooruPostClaimTagCategory, BooruPostClaimTag> group in details.Tags.GroupBy(tag => tag.Category))
        {
            // The category names the group; each tag keeps the provider's exact spelling. Tags are selectable list items
            // (one tab stop per category, arrow keys inside), not tag chips of the user's own: C1 source-tag chips (dashed, category
            // mark), and a marked tag turns solid with a 2-px accent outline. The item keeps the tag's exact name for screen readers.
            string category = BooruMessages.TagCategory(group.Key);
            string[] names = group.Select(tag => tag.Name).ToArray();
            TextBlock title = new() { Name = "BooruSourceTagCategory", Text = category, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Button copyCategory = new() { Name = "BooruCopyCategory:" + group.Key, Content = Texts.T("booru.media.tags.copyCategory") };
            AutomationProperties.SetName(copyCategory, Texts.T("booru.media.tags.copyCategoryFor", category));
            ToolTip.SetTip(copyCategory, Texts.T("booru.media.tags.copyCategoryFor", category));
            copyCategory.Click += async (_, _) => await CopyTagsAsync(names);
            Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            header.Children.Add(title);
            Grid.SetColumn(copyCategory, 1); header.Children.Add(copyCategory);
            panel.Children.Add(header);

            ListBox list = new()
            {
                Name = "BooruSourceTagList:" + group.Key, SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle,
                ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { ItemSpacing = 4, LineSpacing = 4 }),
                ItemsSource = names.Select(name => SourceTagItem(name, group.Key)).ToArray()
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(list, Texts.T("booru.media.tags.listFor", category));
            list.SelectionChanged += (_, _) => UpdateTagSelection();
            list.KeyDown += async (_, e) =>
            {
                if (e.Handled || !IsCopyGesture(e)) return;
                e.Handled = true;
                await CopyTagsAsync(SelectedTagNames());
            };
            _tagLists.Add(list);
            panel.Children.Add(list);
        }
        panel.Children.Add(Note("BooruSourceTagsNote", Texts.T("booru.media.sourceTags.note")));
        UpdateTagSelection();
        return panel;
    }

    private static ListBoxItem SourceTagItem(string name, BooruPostClaimTagCategory category)
    {
        ListBoxItem item = new() { Content = WorkspaceChips.CreateSourceTag(name, category, compact: false), Tag = name, Classes = { WorkspaceChips.Item } };
        AutomationProperties.SetName(item, name);
        return item;
    }

    private bool IsCopyGesture(KeyEventArgs e) =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy.Any(gesture => gesture.Matches(e))
        ?? (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control);

    /// <summary>The marked source tags in the order the dialog lists them.</summary>
    internal IReadOnlyList<string> SelectedTagNames() => _tagLists
        .SelectMany(list => list.Items.OfType<ListBoxItem>().Where(item => item.IsSelected).Select(item => (string)item.Tag!)).ToArray();

    private void UpdateTagSelection()
    {
        int count = SelectedTagNames().Count;
        _copySelectedTags.IsEnabled = count > 0;
        _useAsSearchButton.IsEnabled = count > 0;
        _tagSelection.Text = count == 0 ? Texts.T("booru.media.tags.selection.none")
            : count > BooruSourceTagSearch.MaximumTags ? Texts.Count("booru.media.tags.selection.tooMany", count, count, BooruSourceTagSearch.MaximumTags)
            : Texts.Count("booru.media.tags.selection.count", count);
    }

    private async Task CopyTagsAsync(IReadOnlyList<string> names)
    {
        if (names.Count == 0) { _tagStatus.Text = Texts.T("booru.media.tags.search.none"); return; }
        try
        {
            await _copyText(BooruSourceTagSearch.Written(names));
            _tagStatus.Text = Texts.Count("booru.media.tags.copied", names.Count);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _tagStatus.Text = Texts.T("booru.media.tags.copyFailed");
        }
    }

    // The marked tags become the Danbooru window's search field. Nothing is requested: the user still presses Search there.
    private async void UseSelectionAsSearch()
    {
        BooruTagSearchPlan plan = BooruSourceTagSearch.Plan(SelectedTagNames());
        switch (plan.Problem)
        {
            case BooruTagSearchProblem.NothingSelected: _tagStatus.Text = Texts.T("booru.media.tags.search.none"); return;
            case BooruTagSearchProblem.TooMany:
                _tagStatus.Text = Texts.T("booru.media.tags.search.tooMany", BooruSourceTagSearch.MaximumTags, plan.Count); return;
            case BooruTagSearchProblem.Unusable:
                _tagStatus.Text = Texts.T("booru.media.tags.search.unusable", plan.Tag!); return;
        }
        string query = plan.Query!;
        if (_useAsSearch?.Invoke(query) == true) { Close(); return; }
        try
        {
            await _copyText(query);
            _tagStatus.Text = Texts.T("booru.media.tags.search.copied", query);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _tagStatus.Text = Texts.T("booru.media.tags.copyFailed");
        }
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
