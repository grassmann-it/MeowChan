using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>What a source-tag chip row calls back into: the view that owns the search field and the link note line.</summary>
/// <param name="Add">A click on a chip: add the tag to the search field (nothing is requested).</param>
/// <param name="Menu">The chip's own menu (add, exclude, copy the name), composed when it opens.</param>
/// <param name="ShowAll">The "+N more" button: list every tag of this post from now on.</param>
internal sealed record BooruTagChipActions(Action<string> Add, Func<string, ContextMenuBuilder?> Menu, Action<string> ShowAll);

/// <summary>
/// The source tags of one provider post as chip lines by category, shared by every place that shows them (the result card today).
/// Names, accessible names and tooltips are the ones the result card always had; the helper owns no state, so each caller keeps its
/// own "expanded" flag and rebuilds the control when it changes.
/// </summary>
internal static class BooruTagChips
{
    internal const int DefaultLimit = 24;
    internal const int MaximumHiddenNamed = 60;

    // Danbooru's category order (artist, copyright, character, general, meta); anything else last.
    private static readonly BooruTagCategory[] Order =
    {
        BooruTagCategory.Artist, BooruTagCategory.Copyright, BooruTagCategory.Character,
        BooruTagCategory.General, BooruTagCategory.Meta, BooruTagCategory.Unknown
    };

    /// <summary>"Tags:" with its chip lines, then "+N more" while <paramref name="expanded"/> is false and tags did not fit.</summary>
    internal static Control Create(BooruPostView post, bool expanded, BooruTagChipActions actions)
    {
        StackPanel panel = new() { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.label"), Classes = { "workspace-meta" }, FontWeight = FontWeight.Medium });
        if (post.Tags.IsEmpty)
        {
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.tags.none"), Classes = { "workspace-meta" } });
            return panel;
        }
        int shown = 0;
        // "+N more" lifts the limit for this card only (a click on it; the cards are rebuilt with the flag in the results key).
        int limit = expanded ? int.MaxValue : DefaultLimit;
        HashSet<string> shownNames = new(StringComparer.Ordinal);
        foreach (BooruTagCategory category in Order)
        {
            string[] names = post.Tags.Where(tag => tag.Category == category).Select(tag => tag.Name).ToArray();
            if (names.Length == 0 || shown >= limit) continue;
            string[] take = names.Take(limit - shown).ToArray();
            shown += take.Length;
            shownNames.UnionWith(take);
            // C1: source tags as compact dashed chips with their category mark; the category is named in front, so the mark is
            // only an addition. I8: a chip adds its tag to the search field (nothing is requested until Search).
            string label = Texts.T("booru.ui.tagCategory." + category);
            WrapPanel line = new() { Name = "BooruResultTags:" + category, ItemSpacing = 4, LineSpacing = 4 };
            line.Children.Add(new TextBlock { Text = label + ":", VerticalAlignment = VerticalAlignment.Center, Classes = { "workspace-meta" } });
            foreach (string name in take)
            {
                Button chip = new()
                {
                    Name = BooruWorkbenchView.ResultTagPrefix + post.PostId + ":" + name, Content = WorkspaceChips.CreateSourceTag(name, category, compact: true),
                    Classes = { WorkspaceChips.SourceButton }, MaxWidth = 240
                };
                string accessible = Texts.T("booru.ui.tags.addFor", name, label);
                AutomationProperties.SetName(chip, accessible);
                ToolTip.SetTip(chip, accessible);
                chip.Click += (_, _) => actions.Add(name);
                // A right click, the Menu key and Shift+F10 on the chip: add, exclude, copy the name (the chip's own menu, not the card's).
                _ = new ContextMenuHost(chip, () => actions.Menu(name), () => chip.Focus(NavigationMethod.Tab));
                line.Children.Add(chip);
            }
            AutomationProperties.SetName(line, label + ": " + string.Join(", ", take));
            panel.Children.Add(line);
        }
        if (post.Tags.Length > shown)
        {
            // The tags that do not fit are named for the mouse and for assistive technology; the details dialog of the saved medium lists all.
            string[] hidden = post.Tags.Select(tag => tag.Name).Where(name => !shownNames.Contains(name)).ToArray();
            string rest = string.Join(", ", hidden.Take(MaximumHiddenNamed)) + (hidden.Length > MaximumHiddenNamed ? "…" : string.Empty);
            string postId = post.PostId;
            string moreText = Texts.T("booru.ui.tags.more", post.Tags.Length - shown);
            Button more = new() { Name = BooruWorkbenchView.MoreTagsPrefix + postId, Content = moreText, HorizontalAlignment = HorizontalAlignment.Left };
            more.Classes.Add("link");
            string showAll = Texts.T("booru.ui.tags.showAll", rest);
            ToolTip.SetTip(more, showAll);
            AutomationProperties.SetName(more, moreText);
            AutomationProperties.SetHelpText(more, showAll);
            more.Click += (_, _) => actions.ShowAll(postId);
            panel.Children.Add(more);
        }
        return panel;
    }
}
