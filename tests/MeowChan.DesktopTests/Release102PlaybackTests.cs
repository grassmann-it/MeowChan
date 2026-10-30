using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class Release102PlaybackTests
{
    [Theory]
    [InlineData("gallery.slideshow-toggle")]
    [InlineData("gallery.overlay-toggle")]
    [InlineData("gallery.overlay-autohide-toggle")]
    [InlineData("gallery.post-overlay-toggle")]
    public void NewActionsHaveNoDefaultsAndCanBeBoundWithoutInventingAnotherRouter(string id)
    {
        Assert.Empty(CommandCatalog.Get(id).DefaultGestures);
        var section = new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("Primary+Shift+F9")]);
        foreach (var platform in Enum.GetValues<ShortcutPlatform>())
        {
            var effective = EffectiveShortcutBindings.Build(section, platform);
            Assert.DoesNotContain(effective.Issues, issue => issue.BlocksSave);
            Assert.Single(effective.For(id));
            Assert.Equal(CommandScope.Gallery, CommandCatalog.Get(id).Scope);
        }
    }

    private static SlideshowStatusInput Ready => new(true, false, false, false, true, false, false, false, false, false, true, false);
    [Fact]
    public void StoredModeIsReadyAndCurrentBlockingFactsHavePriority()
    {
        Assert.Equal(SlideshowStatus.Ready, Ready.Resolve());
        Assert.Equal(SlideshowStatus.Running, (Ready with { Running = true }).Resolve());
        Assert.Equal(SlideshowStatus.Failed, (Ready with { Failed = true, Loading = true, Context = true }).Resolve());
        Assert.Equal(SlideshowStatus.NoMedia, (Ready with { HasMedia = false }).Resolve());
        Assert.Equal(SlideshowStatus.Loading, (Ready with { Loading = true, Running = true }).Resolve());
        Assert.Equal(SlideshowStatus.Context, (Ready with { Context = true }).Resolve());
        Assert.Equal(SlideshowStatus.Help, (Ready with { Help = true }).Resolve());
        Assert.Equal(SlideshowStatus.Menu, (Ready with { Menu = true }).Resolve());
        Assert.Equal(SlideshowStatus.Inactive, (Ready with { Active = false }).Resolve());
    }
    [Fact]
    public void ClosingContextDoesNotRetainAFalseBlockOrInventRunning()
    {
        var state = Ready with { Started = true, Context = true };
        Assert.Equal(SlideshowStatus.Context, state.Resolve());
        Assert.Equal(SlideshowStatus.Paused, (state with { Context = false }).Resolve());
        Assert.Equal(SlideshowStatus.SeekPaused, (Ready with { Started = true, SeekPaused = true }).Resolve());
        Assert.Equal(SlideshowStatus.Ended, (Ready with { Started = true, Terminal = true }).Resolve());
        Assert.Equal(SlideshowStatus.None, (Ready with { Selected = false, Started = true }).Resolve());
    }
}
