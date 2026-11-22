using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class TaggingResponsiveSelectionBarTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void CountAndTwoDirectActionsKeepTheirOrderInEveryWorkView(string language) => TaggingLayoutRhythmTests.WithPage(page =>
    {
        Texts.SetLanguage(language);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        TagQueueItem first = TaggingLayoutRhythmTests.Item();
        TagQueueItem second = first with { AssetId = "asset-2", Name = "second.png" };
        TaggingLayoutRhythmTests.RenderSnapshot(page, new TagQueueSnapshot(1, 0, [], [first, second], TagQueueLoadState.Ready));
        TaggingControlRow row = Assert.IsType<TaggingControlRow>(page.SelectionBar.Child);
        Assert.Same(page.SelectionText, row.Children[0]);
        Assert.Same(page.SelectionClearButton, row.TrailingChild);
        Assert.Equal(new[] { page.SelectionTagButton, page.SelectionDoneButton, page.SelectionMoreButton },
            Assert.IsType<StackPanel>(row.Children[1]).Children);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(page.SelectionText));
        Assert.False(page.SelectionBar.IsVisible);
        Assert.Null(page.SelectionMenuModel());
        page.List.SelectedIndex = 0;
        Assert.Equal(CommandLabel("media.tags.edit"), page.SelectionTagButton.Content);
        page.List.SelectedItems!.Add(page.Rows[1]);
        foreach (WorkView view in WorkViewSetting.All)
        {
            page.ApplyWorkView(view);
            Assert.Equal(Texts.T("media.selection.count", 2), page.SelectionText.Text);
            Assert.Equal(CommandLabel("tagging.tag-together"), page.SelectionTagButton.Content);
            Assert.Equal(CommandLabel("tagging.mark-done"), page.SelectionDoneButton.Content);
            Assert.True(page.SelectionTagButton.IsEffectivelyVisible);
            Assert.True(page.SelectionDoneButton.IsEffectivelyVisible);
            Assert.True(page.SelectionMoreButton.IsEffectivelyVisible);
        }
        page.ClearSelection();
        Assert.False(page.SelectionBar.IsVisible);
        Assert.Equal(0, page.SelectedCount);
    });

    private static string CommandLabel(string id) => MeowChan.Desktop.Commands.CommandCatalog.Label(id);
}
