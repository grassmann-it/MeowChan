using Avalonia.Input;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// K3: a held Enter in the settings search picks one hit; its key repeats never reach the control that took the focus.
public sealed class SettingsSearchEnterGuardTests
{
    [Fact]
    public void RepeatsOfThePickingPressAreSwallowedUntilEnterIsReleased()
    {
        SettingsSearchEnterGuard guard = new();
        Assert.False(guard.SwallowKeyDown(Key.Enter)); // the first key-down acts
        guard.Arm();
        Assert.True(guard.SwallowKeyDown(Key.Enter)); // key repeat
        Assert.True(guard.SwallowKeyDown(Key.Enter));
        guard.KeyUp(Key.Enter);
        Assert.False(guard.IsArmed);
        Assert.False(guard.SwallowKeyDown(Key.Enter)); // a new press acts again
        Assert.False(guard.SwallowKeyDown(Key.Enter)); // ...and is only swallowed after a pick
    }

    [Fact]
    public void AFreshPressOrAnotherKeyEndsTheGuardEvenWhenTheKeyUpWasMissed()
    {
        SettingsSearchEnterGuard guard = new();
        Assert.False(guard.SwallowKeyDown(Key.Enter));
        guard.Arm();
        Assert.False(guard.SwallowKeyDown(Key.Down));
        Assert.False(guard.IsArmed);
        Assert.False(guard.SwallowKeyDown(Key.Enter)); // fresh press after another key: not a repeat

        guard.Arm();
        guard.Reset(); // window deactivated, key-up lost
        Assert.False(guard.SwallowKeyDown(Key.Enter));
        guard.KeyUp(Key.A);
        guard.Arm();
        Assert.True(guard.IsArmed); // other key-ups do not end an Enter press
    }
}
