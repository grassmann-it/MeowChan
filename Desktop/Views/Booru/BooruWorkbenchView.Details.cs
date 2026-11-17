using System.Globalization;
using System.Text;
using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>The provider facts of a post as a definition list.</summary>
internal sealed partial class BooruWorkbenchView
{
    /// <summary>
    /// I8: the provider's facts of a post as a definition list (term | value) instead of a "·" chain. Each value is named
    /// "term: value" for screen readers; the term itself is not read twice.
    /// </summary>
    private static Control CreateDetails(BooruPostView post)
    {
        Grid list = new() { Name = DetailsPrefix + post.PostId, ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, RowSpacing = 2 };
        WorkViewElement.SetId(list, "booru.result.details");
        void Add(string termKey, string value)
        {
            int row = list.RowDefinitions.Count;
            list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            string termText = Texts.T(termKey);
            TextBlock term = new() { Text = termText, Classes = { "workspace-meta" }, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAccessibilityView(term, AccessibilityView.Raw);
            TextBlock detail = new() { Text = value, TextWrapping = TextWrapping.Wrap };
            detail[!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Text.Meta"); // D0 token, value in the text colour
            AutomationProperties.SetName(detail, termText + ": " + value);
            Grid.SetRow(term, row); Grid.SetRow(detail, row); Grid.SetColumn(detail, 1);
            list.Children.Add(term); list.Children.Add(detail);
        }
        string rating = BooruText.Rating(post.Rating);
        if (post.Rating != BooruRating.Unknown && !string.IsNullOrEmpty(post.RawRating)
            && !string.Equals(post.RawRating, post.Rating.ToString(), StringComparison.OrdinalIgnoreCase))
            rating += " (" + post.RawRating + ")";
        Add("booru.ui.post.field.rating", rating);
        if (post.Score is { } score) Add("booru.ui.post.field.score", score.ToString(Texts.Culture));
        if (post.FavoriteCount is { } favorites) Add("booru.ui.post.field.favorites", favorites.ToString(Texts.Culture));
        if (post.CreatedAt is { } created) Add("booru.ui.post.field.created", created.ToLocalTime().ToString("d", Texts.Culture));
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
        return list;
    }

    private static string Size(long bytes) => SizeText.Format(bytes);
}
