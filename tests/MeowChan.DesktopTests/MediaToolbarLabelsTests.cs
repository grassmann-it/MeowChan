using MeowChan.Desktop.Localization;
using Xunit;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaToolbarLabelsTests
{
    [Fact]
    public void LabelsDependOnTheRowWidthOnlyAndTheFilterTextLeavesBeforeTheSidebarWordsAppear()
    {
        Assert.True(MediaToolbarLabels.FilterLabelWidth < MediaToolbarLabels.SidebarLabelsWidth);
        Assert.True(MediaToolbarLabels.FilterLabelWidth > MediaToolbarLayout.ReducedWidth);
        Assert.True(MediaToolbarLabels.ShowsFilterLabel(0));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(0));
        Assert.False(MediaToolbarLabels.ShowsFilterLabel(MediaToolbarLabels.FilterLabelWidth - 1));
        Assert.True(MediaToolbarLabels.ShowsFilterLabel(MediaToolbarLabels.FilterLabelWidth));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(MediaToolbarLabels.SidebarLabelsWidth - 1));
        Assert.True(MediaToolbarLabels.ShowsSidebarLabels(MediaToolbarLabels.SidebarLabelsWidth));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(double.NaN));
    }

    [Fact]
    public void TheSidebarWordsAreShortAndLocalizedInEnglishAndGerman()
    {
        Texts.Initialize();
        try
        {
            Texts.SetLanguage("en");
            Assert.Equal("Tags", Texts.T("media.toolbar.tagsLabel"));
            Assert.Equal("Media", Texts.T("media.toolbar.mediaLabel"));
            Texts.SetLanguage("de");
            Assert.Equal("Tags", Texts.T("media.toolbar.tagsLabel"));
            Assert.Equal("Medien", Texts.T("media.toolbar.mediaLabel"));
        }
        finally { Texts.SetLanguage("en"); }
    }
}
