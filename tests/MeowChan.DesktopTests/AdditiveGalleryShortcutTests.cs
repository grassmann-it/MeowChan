using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class AdditiveGalleryShortcutTests
{
    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void NewVolumeDefaultsNeverDisableExistingExplicitGalleryNavigation(ShortcutPlatform platform)
    {
        var section = new ShortcutBindingsSection()
            .Set("gallery.previous", [ShortcutGesture.Parse("Up")])
            .Set("gallery.next", [ShortcutGesture.Parse("Down")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, platform);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "gallery.previous"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Down }, "gallery.next"));
        Assert.Empty(bindings.For("video.volume-up"));
        Assert.Empty(bindings.For("video.volume-down"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "video.volume-up" && !issue.BlocksSave);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void ExplicitVolumeNavigationConflictStillRejectsBothRatherThanChoosingAWinner(ShortcutPlatform platform)
    {
        var section = new ShortcutBindingsSection()
            .Set("gallery.next", [ShortcutGesture.Parse("Up")])
            .Set("video.volume-up", [ShortcutGesture.Parse("Up")]);
        var bindings = EffectiveShortcutBindings.Build(section, platform);
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "gallery.next"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "video.volume-up"));
        Assert.Contains(bindings.Issues, issue => issue.BlocksSave && issue.OtherCommandId is not null);
    }

    [Fact]
    public void ReaderBindingsAndExplicitClearedVolumeKeepTheirOwnContexts()
    {
        var bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("reader.previous-post", [ShortcutGesture.Parse("Up")])
            .Set("video.volume-down", []));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "reader.previous-post"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "video.volume-up"));
        Assert.Empty(bindings.For("video.volume-down"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }
}
