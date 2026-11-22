using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>Copyable source tags from the displayed post, in the provider's order within each category.</summary>
internal sealed class BooruGalleryTags : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    private readonly StackPanel _groups = new() { Spacing = 8 };
    private BooruPostView? _post;
    private string? _language;

    internal BooruGalleryTags()
    {
        Name = "BooruGalleryTags";
        Width = 224;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _groups;
        IsVisible = false;
    }

    internal void Present(BooruPostView? post)
    {
        if (ReferenceEquals(_post, post) && _language == Texts.Language) return;
        bool changedPost = !ReferenceEquals(_post, post);
        _post = post;
        _language = Texts.Language;
        _groups.Children.Clear();
        IsVisible = post is { Tags.IsEmpty: false };
        if (!IsVisible || post is null) return;
        string title = Texts.T("booru.ui.tags.label");
        AutomationProperties.SetName(this, title);
        _groups.Children.Add(new TextBlock { Text = title, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap });
        foreach (var group in post.Tags.GroupBy(tag => tag.Category).OrderBy(group => group.Key))
        {
            string category = BooruText.TagCategory(group.Key);
            SelectableTextBlock tags = new()
            {
                Text = string.Join(' ', group.Select(tag => tag.Name)),
                TextWrapping = TextWrapping.Wrap
            };
            AutomationProperties.SetName(tags, category + ": " + tags.Text);
            _groups.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children = { new TextBlock { Text = category, Classes = { "workspace-meta" } }, tags }
            });
        }
        if (changedPost) ScrollToHome();
    }
}
