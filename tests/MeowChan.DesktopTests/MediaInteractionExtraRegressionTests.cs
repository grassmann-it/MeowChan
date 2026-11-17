using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class MediaInteractionExtraRegressionTests
{
    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void VolumeArrowDefaultsRequireExactShiftedPresses(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        var actions = new[]
        {
            (Key.Up, "video.volume-up"),
            (Key.Down, "video.volume-down")
        };
        KeyModifiers[] modifiedPresses =
        [
            KeyModifiers.Alt,
            KeyModifiers.Control,
            KeyModifiers.None,
            KeyModifiers.Meta,
            KeyModifiers.Control | KeyModifiers.Alt,
            KeyModifiers.Control | KeyModifiers.Shift,
            KeyModifiers.Meta | KeyModifiers.Shift
        ];

        foreach (var (key, command) in actions)
        {
            Assert.True(bindings.Matches(new KeyEventArgs { Key = key, KeyModifiers = KeyModifiers.Shift }, command));
            foreach (KeyModifiers modifiers in modifiedPresses)
                Assert.False(bindings.Matches(new KeyEventArgs
                {
                    Key = key,
                    KeyModifiers = modifiers
                }, command));
        }

        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }
}
