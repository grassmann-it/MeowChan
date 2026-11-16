using Avalonia.Input;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class PlaybackMenuEscapeGuardTests
{
    [Fact]
    public void OpenMenuKeepsEscapeOutOfGalleryRoutingButLetsPopupHandleIt()
    {
        long now = 10_000;
        var guard = new PlaybackMenuEscapeGuard(() => now);
        guard.Open();

        var escape = new KeyEventArgs { Key = Key.Escape, KeyModifiers = KeyModifiers.None };
        Assert.True(guard.BypassGalleryRouting(escape));
        Assert.False(escape.Handled);
        Assert.True(guard.Consume(escape));
        Assert.True(escape.Handled);
    }

    [Fact]
    public void DelayedEscapeAfterNativePopupCloseIsConsumedExactlyOnce()
    {
        long now = 20_000;
        var guard = new PlaybackMenuEscapeGuard(() => now);
        guard.Open();
        guard.Close();

        var delayedEscape = new KeyEventArgs { Key = Key.Escape, KeyModifiers = KeyModifiers.None };
        Assert.True(guard.BypassGalleryRouting(delayedEscape));
        Assert.True(delayedEscape.Handled);

        var nextEscape = new KeyEventArgs { Key = Key.Escape, KeyModifiers = KeyModifiers.None };
        Assert.False(guard.Consume(nextEscape));
        Assert.False(nextEscape.Handled);
    }

    [Fact]
    public void LeaseExpiresAndDoesNotSwallowAnUnrelatedEscape()
    {
        long now = 30_000;
        var guard = new PlaybackMenuEscapeGuard(() => now);
        guard.Open();
        guard.Close();
        now += PlaybackMenuEscapeGuard.GraceMilliseconds + 1;

        var escape = new KeyEventArgs { Key = Key.Escape, KeyModifiers = KeyModifiers.None };
        Assert.False(guard.BypassGalleryRouting(escape));
        Assert.False(escape.Handled);
    }

    [Fact]
    public void ModifiedEscapeDoesNotSpendTheLease()
    {
        long now = 40_000;
        var guard = new PlaybackMenuEscapeGuard(() => now);
        guard.Open();
        guard.Close();

        var modifiedEscape = new KeyEventArgs { Key = Key.Escape, KeyModifiers = KeyModifiers.Shift };
        Assert.False(guard.BypassGalleryRouting(modifiedEscape));
        Assert.False(modifiedEscape.Handled);

        var delayedEscape = new KeyEventArgs { Key = Key.Escape, KeyModifiers = KeyModifiers.None };
        Assert.True(guard.Consume(delayedEscape));
        Assert.True(delayedEscape.Handled);
    }
}
