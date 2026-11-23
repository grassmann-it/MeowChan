using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private int ListDecodeWidth => (int)Math.Ceiling(Math.Max(ListLayout.At(double.PositiveInfinity).Width, ListLayout.WidePreviewHeight)
        * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));

    private bool UsedDecodeWidth(int width) => _resultView switch
    {
        BooruResultView.List => width == ListDecodeWidth,
        BooruResultView.Grid => width == 0,
        _ => false
    };

    private void OnListScalingChanged(object? sender, EventArgs e)
    {
        if (_resultView == BooruResultView.List && !_closed) Render(force: true);
    }

    private Control CreateResultCard(BooruResultRowView row)
    {
        BooruPostView post = row.Post;
        Border card = new() { Name = "BooruResult:" + post.PostId, Classes = { "workspace-card" } };
        // The wide band leaves 24 DIP for the command strip and card edge.
        card.Padding = new Thickness(12, 0);
        Border frame = CreatePreview(row, 0, ListDecodeWidth);
        if (frame.Child is Image image)
        {
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
        }
        Control preview = WithPreviewCorners(frame, post, 0);

        WrapPanel commands = new() { Name = "BooruResultActions:" + post.PostId, ItemSpacing = 4, LineSpacing = 4 };
        Button? showInMedia = AddResultCommands(row, commands, compact: true);
        commands.Children.Add(CreateResultViewerActions(post.PostId, compact: true));
        StackPanel picture = new() { Name = "BooruResultPicture:" + post.PostId, Children = { preview, commands } };

        StackPanel facts = new()
        {
            Name = "BooruResultFacts:" + post.PostId,
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = Texts.T("booru.ui.post.title", post.PostId), Classes = { "card-title" }, TextWrapping = TextWrapping.Wrap },
                CreateDetails(post)
            }
        };

        StackPanel notices = new() { Spacing = 4 };
        bool withdrawn = post.IsDeleted || post.IsBanned;
        if (withdrawn || post.OriginalAccess != BooruOriginalAccess.Available)
            notices.Children.Add(new TextBlock
            {
                Text = BooruText.Original(post.OriginalAccess, withdrawn), TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeight.Medium, Classes = { "workspace-warning" }
            });
        if (BooruText.Load(row) is { } loadText)
        {
            TextBlock state = new() { Text = loadText, TextWrapping = TextWrapping.Wrap };
            state.Classes.Add(row.Load switch
            {
                BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded => "workspace-status-success",
                BooruRowLoad.Handing => "workspace-meta",
                BooruRowLoad.Failed or BooruRowLoad.DownloadFailed or BooruRowLoad.LinkFailed or BooruRowLoad.RefusalPending => "workspace-error",
                _ => "workspace-warning"
            });
            notices.Children.Add(state);
        }

        card.Child = new BooruListCardLayout(card, ListLayout, picture, preview, frame, facts, CreateTags(post), notices);
        AttachCardMenu(card, () => ComposeResultMenu(card, post.PostId), () => FocusHit(post.PostId));
        ConfigureHitCard(card, row, showInMedia);
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

    private static Grid CreateDetails(BooruPostView post)
    {
        ColumnDefinition labelColumn = new(GridLength.Auto) { SharedSizeGroup = "BooruFactLabel" };
        Grid list = new() { Name = DetailsPrefix + post.PostId, ColumnSpacing = 8, RowSpacing = 2 };
        list.ColumnDefinitions.Add(labelColumn);
        list.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        WorkViewElement.SetId(list, "booru.result.details");

        void Add(string termKey, string value)
        {
            int row = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string termText = Texts.T(termKey);
            TextBlock term = new() { Text = termText, Classes = { "workspace-meta" }, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAccessibilityView(term, AccessibilityView.Raw);
            TextBlock detail = new() { Text = value, TextWrapping = TextWrapping.Wrap, Classes = { "tnum" } };
            detail[!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Text.Meta");
            AutomationProperties.SetName(detail, termText + ": " + value);
            Grid.SetRow(term, row);
            Grid.SetRow(detail, row);
            Grid.SetColumn(detail, 1);
            list.Children.Add(term);
            list.Children.Add(detail);
        }

        string rating = BooruText.Rating(post.Rating);
        if (post.Rating != BooruRating.Unknown && !string.IsNullOrEmpty(post.RawRating)
            && !string.Equals(post.RawRating, post.Rating.ToString(), StringComparison.OrdinalIgnoreCase))
            rating += " (" + post.RawRating + ")";
        Add("booru.ui.post.field.rating", rating);
        if (post.FavoriteCount is { } favorites) Add("booru.ui.post.field.favorites", favorites.ToString(Texts.Culture));
        if (post.Original is { } original)
        {
            StringBuilder media = new();
            if (original.Width is { } width && original.Height is { } height)
                media.Append(width.ToString(CultureInfo.InvariantCulture)).Append('×').Append(height.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(original.Extension))
                media.Append(media.Length > 0 ? " " : string.Empty).Append(original.Extension.TrimStart('.'));
            if (original.ExpectedSize is { } size)
                media.Append(media.Length > 0 ? " " : string.Empty).Append(Size(size));
            if (media.Length > 0) Add("booru.ui.post.field.file", media.ToString());
        }
        if (post.Score is { } score) Add("booru.ui.post.field.score", score.ToString(Texts.Culture));
        if (post.CreatedAt is { } created) Add("booru.ui.post.field.created", created.ToLocalTime().ToString("d", Texts.Culture));
        return list;
    }
}

/// <summary>Uses the width offered by the scroll presenter, including on a resize of either host.</summary>
internal sealed class BooruListCardLayout : Panel
{
    private const double Gap = 12;
    private const double TextInset = 8;
    private const double StackedFactsWidth = 224;
    private const double PreferredFactsWidth = 320;

    private readonly Border _card;
    private readonly BooruListLayoutSettings _settings;
    private readonly Control _picture;
    private readonly Control _preview;
    private readonly Control _frame;
    private readonly Control _facts;
    private readonly BooruListTagFlow _tags;
    private readonly Panel _notices;
    private Rect _pictureSlot, _factsSlot, _tagsSlot, _noticesSlot;

    internal BooruListCardLayout(Border card, BooruListLayoutSettings settings, Control picture, Control preview,
        Control frame, Control facts, BooruListTagFlow tags, Panel notices)
    {
        _card = card;
        _settings = settings;
        _picture = picture;
        _preview = preview;
        _frame = frame;
        _facts = facts;
        _tags = tags;
        _notices = notices;
        Children.AddRange([picture, facts, tags, notices]);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double horizontalEdges = _card.Padding.Left + _card.Padding.Right + _card.BorderThickness.Left + _card.BorderThickness.Right;
        double verticalEdges = _card.Padding.Top + _card.Padding.Bottom + _card.BorderThickness.Top + _card.BorderThickness.Bottom;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : _settings.MediumFrom;
        var band = _settings.At(width + horizontalEdges);
        double previewWidth = Math.Min(width, band.Width);
        _preview.Width = _frame.Width = previewWidth;
        _preview.Height = _frame.Height = band.Height;
        _picture.Measure(new Size(previewWidth, double.PositiveInfinity));
        _pictureSlot = new Rect(0, 0, previewWidth, _picture.DesiredSize.Height);

        double remaining = width - previewWidth - Gap;
        bool stacked = remaining < StackedFactsWidth;
        bool tagsAlongside = band.Tier > 0 && !stacked;
        double factsWidth = stacked ? width : tagsAlongside ? Math.Min(PreferredFactsWidth, remaining / 2) : remaining;
        _facts.Measure(new Size(factsWidth, double.PositiveInfinity));
        _factsSlot = new Rect(stacked ? 0 : previewWidth + Gap,
            stacked ? _pictureSlot.Bottom + TextInset : TextInset, factsWidth, _facts.DesiredSize.Height);

        double tagX = tagsAlongside ? _factsSlot.Right + Gap : 0;
        double tagTop = tagsAlongside ? TextInset : Math.Max(_factsSlot.Bottom, _pictureSlot.Bottom) + TextInset;
        double tagWidth = Math.Max(0, width - tagX);
        _tags.SetLayout(band.Tier == 2 && tagsAlongside ? 2 : 1,
            Math.Max(0, band.RowHeight - verticalEdges - tagTop - TextInset));
        _tags.Measure(new Size(tagWidth, double.PositiveInfinity));
        _tagsSlot = new Rect(tagX, tagTop, tagWidth, _tags.DesiredSize.Height);

        double height = Math.Max(_pictureSlot.Bottom, Math.Max(_factsSlot.Bottom, _tagsSlot.Bottom) + TextInset);
        _notices.Measure(new Size(width, double.PositiveInfinity));
        _noticesSlot = new Rect(0, height, width, _notices.DesiredSize.Height);
        return new Size(width, _noticesSlot.Bottom + (_notices.Children.Count > 0 ? TextInset : 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _picture.Arrange(_pictureSlot);
        _facts.Arrange(_factsSlot);
        _tags.Arrange(_tagsSlot);
        _notices.Arrange(_noticesSlot);
        return finalSize;
    }
}
