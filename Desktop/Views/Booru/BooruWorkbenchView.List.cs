using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private int _renderedListDecodeEdge;

    private int ListDecodeEdge
    {
        get
        {
            BooruListLayoutSettings layout = ListLayout;
            int edge = Math.Max(layout.At(double.PositiveInfinity).Width, Math.Max(layout.MediumRowHeight, layout.WideRowHeight));
            edge = Math.Max(edge, layout.NarrowPreviewHeight + BooruListLayoutSettings.ActionHeight);
            return (int)Math.Ceiling(edge * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
        }
    }

    private bool UsedDecodeWidth(int width) => _resultView switch
    {
        BooruResultView.List => width == _renderedListDecodeEdge,
        BooruResultView.Grid => width == 0,
        _ => false
    };

    private void OnListLayoutUpdated(object? sender, EventArgs e)
    {
        // The host has committed its render scaling by the time the new layout is published.
        if (_resultView == BooruResultView.List && _renderedListDecodeEdge != ListDecodeEdge) OnModelChanged();
    }

    private Control CreateResultCard(BooruResultRowView row, ResultActions result)
    {
        BooruPostView post = row.Post;
        string postId = post.PostId;
        // The card supplies the shared side inset. Its panel sets the vertical inset for each width band.
        Thickness listInset = this.TryFindResource("Workspace.Inset.ListCard", out object? value) && value is Thickness inset
            ? inset
            : new Thickness(WorkspaceTokens.Space(12));
        Border card = new()
        {
            Name = "BooruResult:" + postId, Classes = { "workspace-card" },
            Padding = new Thickness(listInset.Left, 0, listInset.Right, 0)
        };
        Border frame = CreatePreview(row, 0, _renderedListDecodeEdge);
        if (frame.Child is Image image)
        {
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
            frame[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        }
        Control preview = WithPreviewCorners(frame, post, 0);
        if (preview is Panel corners && corners.Children.Count > 1 && corners.Children[1] is Border kind)
        {
            kind.HorizontalAlignment = HorizontalAlignment.Left;
            kind.Margin = new Thickness(4, 4, 0, 0);
        }

        WrapPanel viewerActions = CreateResultViewerActions(result, compact: true);
        Button gallery = (Button)viewerActions.Children[0];
        CheckBox select = (CheckBox)viewerActions.Children[1];
        viewerActions.Children.Clear();
        AutomationProperties.SetAutomationId(gallery, gallery.Name);
        AutomationProperties.SetAutomationId(select, select.Name);
        AutomationProperties.SetAutomationId(frame, frame.Name);
        select.Classes.Add("booru-tile-select");
        select.HorizontalAlignment = HorizontalAlignment.Right;
        select.VerticalAlignment = VerticalAlignment.Top;
        select.Margin = new Thickness(0, 4, 4, 0);

        Panel picture = new() { Name = "BooruResultPicture:" + postId, Children = { preview, select } };
        Border local = WorkspaceChips.CreateCornerMark("BooruResultLocal:" + postId, HorizontalAlignment.Right, VerticalAlignment.Bottom);
        local.Margin = new Thickness(0, 0, 4, 4);
        WorkspaceChips.SetCornerMark(local, row.Load == BooruRowLoad.AlreadyLoaded ? BooruTileMetrics.LocalMark : null, Texts.T("booru.ui.tile.local"));
        picture.Children.Add(local);

        Border selectionBar = new()
        {
            Name = "BooruListSelectionBar:" + postId, Classes = { "booru-tile-bar" },
            IsVisible = _selectedResults.Contains(postId), IsHitTestVisible = false
        };
        AutomationProperties.SetAccessibilityView(selectionBar, AccessibilityView.Raw);

        TextBlock facts = CreateListFacts(post);
        TextBlock title = new()
        {
            Text = Texts.T("booru.ui.post.title", postId), Classes = { "card-title" }, TextWrapping = TextWrapping.Wrap
        };

        StackPanel notices = new() { Spacing = 4 };
        AutomationProperties.SetAutomationId(notices, "BooruResultNotices:" + postId);
        bool withdrawn = post.IsDeleted || post.IsBanned;
        if (withdrawn || post.OriginalAccess != BooruOriginalAccess.Available)
            notices.Children.Add(new TextBlock
            {
                Text = BooruText.Original(post.OriginalAccess, withdrawn), TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.Medium, Classes = { "workspace-warning" }
            });

        BooruListTagFlow tags = CreateTags(post, result, card);
        card.Child = new BooruListCardLayout(card, ListLayout, picture, preview, frame, new BooruListFacts(postId, title, facts),
            CreateListActionRow(row, result, gallery, card), tags, notices, selectionBar);
        ConfigureHitCard(card, row, result, menu =>
        {
            menu.Section().Command("gallery.open", () => { if (result.IsCurrent()) OpenGallery(postId); }, showShortcut: false);
            tags.AddFoldAction(menu);
        });
        return card;
    }

    private static void CompactAction(Button button, string icon)
    {
        IconButtons.Record(button, icon);
        button.ContentTemplate = new FuncDataTemplate<object>((_, _) => CommandBindings.NamedIcon(icon, 12));
        SizeCompactAction(button);
    }

    private static void SizeCompactAction(TemplatedControl control)
    {
        control.Width = BooruListLayoutSettings.ActionHeight;
        control.Height = BooruListLayoutSettings.ActionHeight;
        control.MinWidth = 0;
        control.MinHeight = 0;
        control.Padding = new Thickness(0);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is ContentControl content)
        {
            content.HorizontalContentAlignment = HorizontalAlignment.Center;
            content.VerticalContentAlignment = VerticalAlignment.Center;
        }
    }

    private static TextBlock CreateListFacts(BooruPostView post)
    {
        List<string> values = [];
        List<string> descriptions = [];

        void Add(string term, string value, string? compact = null)
        {
            values.Add(compact ?? value);
            descriptions.Add(Texts.T("booru.ui.post.field." + term) + ": " + value);
        }

        if (post.Rating != BooruRating.Unknown || !string.IsNullOrEmpty(post.RawRating))
        {
            string rating = BooruText.Rating(post.Rating);
            if (!string.IsNullOrEmpty(post.RawRating)
                && !string.Equals(post.RawRating, post.Rating.ToString(), StringComparison.OrdinalIgnoreCase))
                rating += " (" + post.RawRating + ")";
            Add("rating", rating);
        }
        if (post.Original is { } original)
        {
            StringBuilder media = new();
            if (original.Width is { } width && original.Height is { } height)
                media.Append(width.ToString(CultureInfo.InvariantCulture)).Append('×').Append(height.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(original.Extension))
                media.Append(media.Length > 0 ? " " : string.Empty).Append(original.Extension.TrimStart('.'));
            string? size = original.ExpectedSize is { } bytes ? Size(bytes) : null;
            string file = media.ToString();
            if (file.Length > 0) values.Add(file);
            if (size != null) values.Add(size);
            if (file.Length > 0 || size != null)
                descriptions.Add(Texts.T("booru.ui.post.field.file") + ": " + string.Join(' ', new[] { file, size }).Trim());
        }
        if (post.Score is { } score) Add("score", score.ToString(Texts.Culture), Texts.T("booru.ui.result.short.score", score));
        if (post.FavoriteCount is { } favorites)
            Add("favorites", favorites.ToString(Texts.Culture), Texts.T("booru.ui.result.short.favorites", favorites));
        if (post.CreatedAt is { } created) Add("created", created.ToLocalTime().ToString("d", Texts.Culture));

        TextBlock details = new()
        {
            Name = DetailsPrefix + post.PostId, Classes = { "workspace-meta", "tnum" },
            Text = string.Join(" · ", values), TextWrapping = TextWrapping.Wrap, IsVisible = values.Count > 0
        };
        string accessible = string.Join(Environment.NewLine, descriptions);
        AutomationProperties.SetAutomationId(details, details.Name);
        AutomationProperties.SetName(details, accessible);
        ToolTip.SetTip(details, accessible);
        WorkViewElement.SetId(details, "booru.result.details");
        return details;
    }
}

internal sealed class BooruListFacts : Panel
{
    private const double InlineGap = 20;
    private const double InlineMinimumWidth = 560;
    private const double StackedGap = 4;

    private readonly TextBlock _title;
    private readonly TextBlock _details;
    private Rect _detailsSlot;

    internal BooruListFacts(string postId, TextBlock title, TextBlock details)
    {
        Name = "BooruResultFacts:" + postId;
        AutomationProperties.SetAutomationId(this, Name);
        _title = title;
        _details = details;
        Children.AddRange([title, details]);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        _title.Measure(availableSize);
        _details.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        _detailsSlot = default;
        double titleHeight = _title.DesiredSize.Height;
        if (!_details.IsVisible) return new Size(width, titleHeight);

        double detailsX = _title.DesiredSize.Width + InlineGap;
        if (width >= InlineMinimumWidth && detailsX + _details.DesiredSize.Width <= width)
        {
            double offset = Math.Max(0, (titleHeight - _details.DesiredSize.Height) / 2);
            _detailsSlot = new Rect(detailsX, offset, _details.DesiredSize.Width, _details.DesiredSize.Height);
        }
        else
        {
            _details.Measure(availableSize);
            _detailsSlot = new Rect(0, titleHeight + StackedGap, width, _details.DesiredSize.Height);
        }
        return new Size(width, Math.Max(titleHeight, _detailsSlot.Bottom));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _title.Arrange(new Rect(_title.DesiredSize));
        _details.Arrange(_detailsSlot);
        return finalSize;
    }
}

/// <summary>Uses the width offered by the scroll presenter, including on a resize of either host.</summary>
internal sealed class BooruListCardLayout : Panel
{
    private const double Gap = 12;
    private const double StackedTextWidth = 224;

    private readonly Border _card;
    private readonly BooruListLayoutSettings _settings;
    private readonly double _textInset = WorkspaceTokens.Space(8);
    private readonly double _factsTagGap = WorkspaceTokens.Space(4);
    private readonly Control _picture;
    private readonly Control _preview;
    private readonly Control _frame;
    private readonly Control _facts;
    private readonly BooruResultActionRow _actions;
    private readonly BooruListTagFlow _tags;
    private readonly Panel _notices;
    private readonly Border _selectionBar;
    private Rect _pictureSlot, _factsSlot, _actionsSlot, _tagsSlot, _noticesSlot;

    internal BooruListCardLayout(Border card, BooruListLayoutSettings settings, Control picture, Control preview,
        Control frame, Control facts, BooruResultActionRow actions, BooruListTagFlow tags, Panel notices, Border selectionBar)
    {
        _card = card;
        _settings = settings;
        _picture = picture;
        _preview = preview;
        _frame = frame;
        _facts = facts;
        _actions = actions;
        _tags = tags;
        _notices = notices;
        _selectionBar = selectionBar;
        Children.AddRange([picture, facts, actions, tags, notices, selectionBar]);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double textInset = _textInset;
        double horizontalEdges = _card.Padding.Left + _card.Padding.Right + _card.BorderThickness.Left + _card.BorderThickness.Right;
        double verticalEdges = _card.Padding.Top + _card.Padding.Bottom + _card.BorderThickness.Top + _card.BorderThickness.Bottom;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : _settings.MediumFrom;
        var band = _settings.At(width + horizontalEdges);
        double remaining = width - band.Width - Gap;
        bool stacked = remaining < StackedTextWidth;
        bool tagsAlongside = band.Tier > 0 && !stacked;
        double pictureWidth = stacked ? width : band.Width;
        double textX = stacked ? 0 : pictureWidth + Gap;
        double textWidth = stacked ? width : remaining;
        double rowHeight = band.RowHeight - verticalEdges;
        double factsY = stacked ? band.Height + textInset : textInset;
        _facts.Measure(new Size(textWidth, double.PositiveInfinity));
        _factsSlot = new Rect(textX, factsY, textWidth, _facts.DesiredSize.Height);
        _actions.Measure(new Size(textWidth, BooruListLayoutSettings.ActionHeight));

        // Side-by-side results share one bottom edge for the preview and actions, above the card inset.
        double pictureInset = tagsAlongside ? textInset : 0;
        double minimumActionsBottom = tagsAlongside ? Math.Max(pictureInset + band.Height, rowHeight - pictureInset)
            : band.Height + BooruListLayoutSettings.ActionHeight;
        double actionsBottom = Math.Max(minimumActionsBottom, _factsSlot.Bottom + textInset + _actions.DesiredSize.Height);
        double tagsX = tagsAlongside ? textX : 0;
        double tagsY = tagsAlongside ? _factsSlot.Bottom + _factsTagGap : actionsBottom + textInset;
        double tagsWidth = tagsAlongside ? textWidth : width;
        double reserveBelowTags = tagsAlongside ? textInset + _actions.DesiredSize.Height : textInset;
        _tags.SetHeightBudget(rowHeight - tagsY - reserveBelowTags - pictureInset);
        _tags.Measure(new Size(tagsWidth, double.PositiveInfinity));
        _tagsSlot = new Rect(tagsX, tagsY, tagsWidth, _tags.DesiredSize.Height);
        if (tagsAlongside) actionsBottom = Math.Max(actionsBottom, _tagsSlot.Bottom + reserveBelowTags);
        _actionsSlot = new Rect(textX, actionsBottom - _actions.DesiredSize.Height, textWidth, _actions.DesiredSize.Height);

        double pictureHeight = stacked ? band.Height : actionsBottom - pictureInset;
        _preview.Width = _frame.Width = pictureWidth;
        _preview.Height = _frame.Height = pictureHeight;
        _pictureSlot = new Rect(0, pictureInset, pictureWidth, pictureHeight);
        _picture.Measure(_pictureSlot.Size);

        double height = tagsAlongside ? actionsBottom + pictureInset : Math.Max(rowHeight, _tagsSlot.Bottom + textInset);
        _notices.Measure(new Size(width, double.PositiveInfinity));
        _noticesSlot = new Rect(0, height, width, _notices.DesiredSize.Height);
        _selectionBar.Measure(new Size(BooruTileMetrics.SelectionBar, height));
        return new Size(width, _noticesSlot.Bottom + (_notices.Children.Count > 0 ? textInset : 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _picture.Arrange(_pictureSlot);
        _facts.Arrange(_factsSlot);
        _actions.Arrange(_actionsSlot);
        _tags.Arrange(_tagsSlot);
        _notices.Arrange(_noticesSlot);
        _selectionBar.Arrange(new Rect(-_card.Padding.Left - _card.BorderThickness.Left, -_card.BorderThickness.Top,
            BooruTileMetrics.SelectionBar, finalSize.Height + _card.BorderThickness.Top + _card.BorderThickness.Bottom));
        return finalSize;
    }
}
