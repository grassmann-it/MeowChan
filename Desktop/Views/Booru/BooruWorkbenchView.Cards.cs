using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

/// <summary>The result card of one post: preview, facts, original line, tags, links and the one primary action.</summary>
internal sealed partial class BooruWorkbenchView
{
    /// <summary>
    /// One built result card with what it was built from. The panel keeps the tile of a post while nothing the card shows changed
    /// (<see cref="CanReuse"/>), so a state or preview change of one hit replaces only that hit's card.
    /// </summary>
    private sealed record ResultTile(Border Card, BooruResultRowView Row, bool ShowInMedia, bool Expanded, string Language, bool Selected, bool BatchRunning, Button[] WorkViewActions);

    private readonly Dictionary<string, ResultTile> _tiles = new(StringComparer.Ordinal);

    /// <summary>Whether the card of this hit offers "Show in Media": saved locally (as far as the host can tell) and the host can navigate.</summary>
    private bool ShowsInMedia(BooruResultRowView row) =>
        row.Load == BooruRowLoad.AlreadyLoaded && _showInMedia != null
        && (_isPostSavedInMedia == null || _isPostSavedInMedia(row.Post.PostId));

    private bool CanReuse(ResultTile tile, BooruResultRowView row, bool showInMedia) =>
        tile.Selected == _model.Search.Selected.Contains(row.Post.PostId) && tile.BatchRunning == _model.Search.Batch.Running
        && tile.ShowInMedia == showInMedia && tile.Language == Texts.Language
        && tile.Expanded == _expandedTagPosts.Contains(row.Post.PostId)
        && tile.Row.Load == row.Load && string.Equals(tile.Row.LoadCode, row.LoadCode, StringComparison.Ordinal)
        && tile.Row.Preview == row.Preview && ReferenceEquals(tile.Row.PreviewBytes, row.PreviewBytes)
        && (ReferenceEquals(tile.Row.Post, row.Post) || tile.Row.Post.Equals(row.Post));

    /// <summary>
    /// The cards of the page in row order, numbered for assistive technology. With <paramref name="force"/> (language, host callbacks, attach)
    /// every card is built again; otherwise a card whose row did not change is the same control as before, so its focus, hover and
    /// pending clicks are untouched. Also refills the work-view action list of the result cards.
    /// </summary>
    private List<Control> BuildResultCards(IReadOnlyList<BooruResultRowView> rows, bool force)
    {
        if (force) _tiles.Clear();
        Dictionary<string, ResultTile> next = new(StringComparer.Ordinal);
        List<Control> cards = new(rows.Count);
        _resultWorkViewActions.Clear();
        foreach (BooruResultRowView row in rows)
        {
            string id = row.Post.PostId;
            bool showInMedia = ShowsInMedia(row);
            ResultTile? tile = null;
            bool repeated = next.ContainsKey(id); // a repeated id never shares a control
            if (repeated || !_tiles.TryGetValue(id, out tile) || !CanReuse(tile, row, showInMedia))
                tile = CreateResultTile(row, showInMedia);
            if (!repeated) next[id] = tile;
            cards.Add(tile.Card);
            _resultWorkViewActions.AddRange(tile.WorkViewActions);
        }
        _tiles.Clear();
        foreach (KeyValuePair<string, ResultTile> pair in next) _tiles[pair.Key] = pair.Value;
        return NumberHits(cards);
    }

    private ResultTile CreateResultTile(BooruResultRowView row, bool showInMedia)
    {
        List<Button> actions = new();
        Border card = CreateResultCard(row, actions);
        return new ResultTile(card, row, showInMedia, _expandedTagPosts.Contains(row.Post.PostId), Texts.Language, _model.Search.Selected.Contains(row.Post.PostId), _model.Search.Batch.Running, actions.ToArray());
    }

    private Border CreateResultCard(BooruResultRowView row, List<Button> workViewActions)
    {
        BooruPostView post = row.Post;
        // The card is a keyboard stop with its own accessible name (ConfigureHitCard); its title line is the visible form of the same post.
        Border card = new() { Name = "BooruResult:" + post.PostId };
        card.Classes.Add("workspace-card");

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(WithPreviewCorners(CreatePreview(row), post));

        StackPanel info = new() { Spacing = 4 };
        info.Children.Add(new TextBlock { Text = Texts.T("booru.ui.post.title", post.PostId), Classes = { "card-title" }, TextWrapping = TextWrapping.Wrap });
        // I8: the post facts as a definition list; the address is no longer a text line, it is the tooltip of the link buttons.
        CheckBox select = new() { Name = "BooruSelect:" + post.PostId,
            Content = Texts.T("booru.ui.selection.select", post.PostId),
            IsChecked = _model.Search.Selected.Contains(post.PostId), IsEnabled = !_model.Search.Batch.Running };
        select.Click += (_, _) =>
        {
            _model.Search.SetSelected(post.PostId, select.IsChecked == true);
            select.IsChecked = _model.Search.Selected.Contains(post.PostId); // rejected overflow stays visibly unchecked
        };
        info.Children.Add(select);
        info.Children.Add(CreateDetails(post));

        bool withdrawn = post.IsDeleted || post.IsBanned;
        TextBlock original = new()
        {
            Text = BooruText.Original(post.OriginalAccess, withdrawn), TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.Medium
        };
        original.Classes.Add(post.OriginalAccess == BooruOriginalAccess.Available ? "workspace-status-success" : "workspace-warning");
        info.Children.Add(original);
        info.Children.Add(CreateTags(post));
        if (ShowsInMedia(row))
            info.Children.Add(CreateShowInMediaAction(ShowInMediaPrefix + post.PostId,
                Texts.T("booru.ui.result.showInMediaFor", post.PostId), post.PostId));
        // Secondary links after the content; the primary action (load original) keeps its place on the right.
        if (BooruPostLinks.TryCanonical(post, out string canonical))
            info.Children.Add(CreateLinkActions(OpenPostPrefix + post.PostId, CopyLinkPrefix + post.PostId, canonical,
                Texts.T("booru.ui.post.openFor", post.PostId), Texts.T("booru.ui.post.copyFor", post.PostId), workViewActions));

        if (BooruText.Load(row) is { } loadText)
        {
            TextBlock state = new() { Text = loadText, TextWrapping = TextWrapping.Wrap };
            state.Classes.Add(row.Load switch
            {
                BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded => "workspace-status-success",
                BooruRowLoad.Handing => "workspace-meta",
                BooruRowLoad.Failed => "workspace-error",
                _ => "workspace-warning"
            });
            // Announced through the persistent notice line (_noticeText); this rebuilt line would stay silent.
            info.Children.Add(state);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        bool cancel = row.CanCancel;
        bool retry = !cancel && row.CanRetry;
        string actionText = cancel ? Texts.T("booru.ui.cancel")
            : retry ? Texts.T("booru.ui.status.retry") : Texts.T("booru.ui.load");
        string actionName = cancel
            ? $"{Texts.T("booru.ui.cancel")}: {Texts.T("booru.ui.post.title", post.PostId)}"
            : retry ? $"{Texts.T("booru.ui.status.retry")}: {Texts.T("booru.ui.post.title", post.PostId)}"
            : Texts.T("booru.ui.loadFor", post.PostId);
        string actionPrefix = cancel ? CancelLoadPrefix : retry ? RetryLoadPrefix : LoadPrefix;
        Button load = new() { Name = actionPrefix + post.PostId, Content = actionText, VerticalAlignment = VerticalAlignment.Top };
        load.Classes.Add("primary");
        CommandBindings.ShowIconBeforeText(load, "download-cloud");
        RegisterWorkViewAction(load, cancel ? "booru.result.cancel" : retry ? "booru.result.retry" : "booru.result.load",
            workViewActions);
        load.IsEnabled = !_model.Search.Batch.Running && (cancel || retry || row.CanLoad);
        AutomationProperties.SetName(load, actionName);
        string? why = post.OriginalAccess != BooruOriginalAccess.Available ? BooruText.Original(post.OriginalAccess, withdrawn)
            : BooruText.Load(row);
        ToolTip.SetTip(load, why ?? actionName);
        ToolTip.SetShowOnDisabled(load, true);
        if (why is not null) AutomationProperties.SetHelpText(load, why);
        if (cancel) load.Click += async (_, _) => await _model.Search.CancelOriginalAsync(post.PostId);
        else if (retry) load.Click += async (_, _) => await _model.Search.RetryOriginalAsync(post.PostId);
        else load.Click += async (_, _) => await _model.Search.LoadOriginalAsync(post.PostId);
        Grid.SetColumn(load, 2);
        grid.Children.Add(load);
        card.Child = grid;
        // The hit's one context menu (BooruWorkbenchView.CardMenus.cs) opens on a right click, the Menu key, Shift+F10 and menu.context-open; after an
        // entry rebuilt the cards the focus returns to the same hit. Keyboard and double click of the hit (interaction audit V11): BooruWorkbenchView.Hits.cs.
        AttachCardMenu(card, () => ComposeResultMenu(card, post.PostId), () => FocusHit(post.PostId));
        Button? showInMedia = info.Children.OfType<Button>().FirstOrDefault(button => button.Name == ShowInMediaPrefix + post.PostId);
        ConfigureHitCard(card, row, showInMedia);
        return card;
    }
}
