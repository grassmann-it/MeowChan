namespace MeowChan.DesktopTests;

// A settings save gates the workspace while it applies; the focus each window had comes back afterwards (settings-reader-display:
// "Date-only apply stole focus").
public sealed class SettingsApplyFocusTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)] // the control left its window
    [InlineData(true, false, true, false, false)] // still disabled
    [InlineData(true, true, false, false, false)] // hidden meanwhile
    [InlineData(true, true, true, true, false)]   // the user (or the apply) focused something else
    public void TheHeldFocusReturnsOnlyToAnEnabledShownControlOfAWindowWithoutFocus(bool inWindow, bool enabled, bool visible, bool taken, bool restore) =>
        Assert.Equal(restore, MainWindow.ShouldRestoreFocus(inWindow, enabled, visible, taken));
}
