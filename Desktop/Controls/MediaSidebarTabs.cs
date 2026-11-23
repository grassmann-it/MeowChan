using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

internal sealed class MediaSidebarTabs
{
    internal MediaSidebarTabs(string prefix)
    {
        Post = Tab(prefix + "Post");
        Tags = Tab(prefix + "Tags");
        Row = new ShrinkRowPanel
        {
            Name = prefix + "Segment", Spacing = 4, ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Children = { Post, Tags }
        };
    }

    internal ShrinkRowPanel Row { get; }
    internal ToggleButton Post { get; }
    internal ToggleButton Tags { get; }

    internal void SelectTags(bool selected)
    {
        Post.IsChecked = !selected;
        Tags.IsChecked = selected;
    }

    internal void Localize(string? tagsUnavailableReason = null, string? postCommandId = "gallery.context-toggle")
    {
        Present(Post, Texts.T("gallery.panel.post"), postCommandId, null);
        Present(Tags, Texts.T("gallery.panel.tags"), "media.tags.edit", tagsUnavailableReason);
        AutomationProperties.SetName(Row, Texts.T("gallery.panel.segments"));
        if (postCommandId != null) AutomationProperties.SetHelpText(Row, Texts.T("gallery.panel.segmentHelp"));
    }

    private static ToggleButton Tab(string name) => new()
    {
        Name = name, Classes = { "media-control" }, Height = WorkspaceMetrics.InputHeight,
        Content = new TextBlock { Classes = { "panel-action-label" }, TextTrimming = TextTrimming.CharacterEllipsis }
    };

    private static void Present(ToggleButton button, string label, string? commandId, string? reason)
    {
        ((TextBlock)button.Content!).Text = label;
        AutomationProperties.SetName(button, label);
        string tip = reason ?? (commandId != null && CommandCatalog.ShortcutDisplay(commandId) is string gesture ? label + " · " + gesture : label);
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetHelpText(button, tip);
    }
}
