using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

[Collection("Media shortcut routing")]
public sealed class GallerySeekNavigationPolicyTests
{
    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.Home)]
    [InlineData(Key.End)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    public void TimelineNeverPerformsAnUnmatchedKeyboardSeek(Key key)
    {
        var slider = new TimelineHost { Value = 50, Minimum = 0, Maximum = 100 };
        var e = new KeyEventArgs { Source = slider, Key = key };
        slider.ApplyKey(e);
        Assert.True(e.Handled);
        Assert.Equal(50, slider.Value);
        Assert.True(slider.Focusable); // accessible/tab focus does not imply keyboard seeking
        Assert.True(GalleryTransportSlider.IsValueKey(key));
    }

    [Fact]
    public void DisablingKeyboardSeekingDoesNotDisablePointerOrProgrammaticValues()
    {
        var slider = new TimelineHost { Minimum = 0, Maximum = 1000 };
        slider.Value = 275;
        Assert.Equal(275, slider.Value);
        Assert.True(slider.IsHitTestVisible);
        var e = new KeyEventArgs { Source = slider, Key = Key.F9 };
        slider.ApplyKey(e);
        Assert.False(e.Handled);
        Assert.Equal(275, slider.Value);
    }

    [Theory]
    [InlineData(Key.Home, 0)]
    [InlineData(Key.End, 100)]
    public void VolumeRetainsUnchangedNativeHomeEndOutsideEffectiveMediaBindings(Key key, double expected)
    {
        var volume = new VolumeHost { Value = 50, Minimum = 0, Maximum = 100 };
        var e = new KeyEventArgs { Source = volume, Key = key };
        volume.ApplyKey(e);
        Assert.True(e.Handled);
        Assert.Equal(expected, volume.Value);
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    public void UnmatchedVolumeArrowsDoNotRestoreClearedOrRemappedActionsAsNativeEditing(Key key)
    {
        var slider = new VolumeHost { Minimum = 0, Maximum = 100, Value = 50 };
        var e = new KeyEventArgs { Source = slider, Key = key };
        slider.ApplyKey(e);
        Assert.True(e.Handled);
        Assert.Equal(50, slider.Value);
    }

    [Fact]
    public void OnlyActualTransportInstancesReceiveTheScopedCommandException()
    {
        var seek = new TimelineHost();
        var volume = new Slider();
        var unrelated = new Slider();
        var e = new KeyEventArgs { Source = seek, Key = Key.Right };
        Assert.False(CommandBindings.Matches(e, "gallery.next")); // generic guard remains
        Assert.True(CommandBindings.MatchesGalleryTransport(e, "gallery.next", seek, volume));
        e.Source = volume;
        Assert.True(CommandBindings.MatchesGalleryTransport(e, "gallery.next", seek, volume));
        e.Source = unrelated;
        Assert.False(CommandBindings.MatchesGalleryTransport(e, "gallery.next", seek, volume));
        e.Source = new TextBox();
        Assert.False(CommandBindings.MatchesGalleryTransport(e, "gallery.next", seek, volume));
        e.Source = seek; e.Handled = true;
        Assert.False(CommandBindings.MatchesGalleryTransport(e, "gallery.next", seek, volume));
        e.Handled = false;
        using (ShortcutService.BeginCapture())
            Assert.False(CommandBindings.MatchesGalleryTransport(e, "gallery.next", seek, volume));
    }

    [Fact]
    public void VolumeDefaultsAreGalleryOnlyAndClearingOrReplacingDoesNotRestoreArrows()
    {
        foreach (string id in new[] { "video.volume-up", "video.volume-down" })
        {
            Assert.Contains("gallery", CommandCatalog.Get(id).ExecutionContexts);
            Assert.DoesNotContain("reader", CommandCatalog.Get(id).ExecutionContexts);
        }
        var section = new ShortcutBindingsSection()
            .Set("video.volume-up", [ShortcutGesture.Parse("Control+Shift+F9")])
            .Set("video.volume-down", []);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "video.volume-up"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.Down }, "video.volume-down"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.F9, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift }, "video.volume-up"));
    }

    private sealed class TimelineHost : GalleryTransportSlider
    {
        internal void ApplyKey(KeyEventArgs e) => base.OnKeyDown(e);
    }
    private sealed class VolumeHost : GalleryVolumeSlider
    {
        internal void ApplyKey(KeyEventArgs e) => base.OnKeyDown(e);
    }
}
