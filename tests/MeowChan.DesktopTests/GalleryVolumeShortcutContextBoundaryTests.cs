using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

[Collection("Media shortcut routing")]
public sealed class GalleryVolumeShortcutContextBoundaryTests
{
    // A template can contain a native editor below an owned slider. Existing
    // descendant tests use only Border; existing editor tests use sibling editors.
    // Exercise the real transport matcher, not a copy of its ancestry policy.
    [Theory]
    [InlineData(false, false, Key.Up, "video.volume-up")]
    [InlineData(false, true, Key.Down, "video.volume-down")]
    [InlineData(true, false, Key.Down, "video.volume-down")]
    [InlineData(true, true, Key.Up, "video.volume-up")]
    public void OwnedTransportMustNotOverrideANestedEditor(
        bool editorInVolume, bool sourceIsEditorChild, Key key, string command)
    {
        var leaf = new Border();
        var editor = new EditorHost(leaf) { Text = "synthetic multiline note", AcceptsReturn = true };
        var seek = new SeekHost(editorInVolume ? new Border() : editor);
        var volume = new VolumeHost(editorInVolume ? editor : new Border());
        Control source = sourceIsEditorChild ? leaf : editor;
        var press = new KeyEventArgs { Source = source, Key = key };

        Assert.True(EffectiveShortcutBindings.Build().Matches(press, command));
        Assert.True(CommandRouter.IsNativeInput(source, key));
        Assert.False(CommandBindings.Matches(press, command));
        Assert.False(GalleryViewer.IsGalleryShortcutInput(press));
        // Regression expectation: slider ownership must not erase the closer
        // editor boundary, even though an owned slider is an ancestor.
        Assert.False(CommandBindings.MatchesGalleryTransport(press, command, seek, volume));
        Assert.False(press.Handled);
        Assert.Equal("synthetic multiline note", editor.Text);
    }

    [Theory]
    [InlineData(Key.Up, "video.volume-up")]
    [InlineData(Key.Down, "video.volume-down")]
    public void CaptureAndHandledGuardsStillPrecedeTheNestedTransportException(Key key, string command)
    {
        var editor = new EditorHost(new Border());
        var seek = new SeekHost(editor);
        var volume = new VolumeHost(new Border());
        var press = new KeyEventArgs { Source = editor, Key = key, Handled = true };
        Assert.False(CommandBindings.MatchesGalleryTransport(press, command, seek, volume));
        press.Handled = false;
        using (ShortcutService.BeginCapture())
            Assert.False(CommandBindings.MatchesGalleryTransport(press, command, seek, volume));
        Assert.False(press.Handled);
    }

    [Theory]
    [InlineData(Key.Up, "video.volume-up")]
    [InlineData(Key.Down, "video.volume-down")]
    public void DecorativeDescendantsStillUseOwnedTransportButGenericSlidersStayNative(Key key, string command)
    {
        var seekLeaf = new Border();
        var volumeLeaf = new Border();
        var seek = new SeekHost(seekLeaf);
        var volume = new VolumeHost(volumeLeaf);
        foreach (Control source in new Control[] { seek, seekLeaf, volume, volumeLeaf })
        {
            var press = new KeyEventArgs { Source = source, Key = key };
            Assert.False(CommandBindings.Matches(press, command));
            Assert.True(CommandBindings.MatchesGalleryTransport(press, command, seek, volume));
            Assert.False(press.Handled);
        }

        var nestedLeaf = new Border();
        var generic = new GenericSliderHost(nestedLeaf);
        var outerSeek = new SeekHost(generic);
        foreach (Control source in new Control[] { generic, nestedLeaf, new Slider() })
        {
            var press = new KeyEventArgs { Source = source, Key = key };
            Assert.True(CommandRouter.IsNativeInput(source, key));
            Assert.False(CommandBindings.MatchesGalleryTransport(press, command, outerSeek, volume));
            Assert.False(press.Handled);
        }
    }

    private sealed class GenericSliderHost : Slider
    {
        internal GenericSliderHost(Control child) => VisualChildren.Add(child);
    }

    private sealed class EditorHost : TextBox
    {
        internal EditorHost(Control child) => VisualChildren.Add(child);
    }

    private sealed class SeekHost : GalleryTransportSlider
    {
        internal SeekHost(Control child) => VisualChildren.Add(child);
    }

    private sealed class VolumeHost : GalleryVolumeSlider
    {
        internal VolumeHost(Control child) => VisualChildren.Add(child);
    }
}
