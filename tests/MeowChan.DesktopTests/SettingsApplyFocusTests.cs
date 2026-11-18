using System.Runtime.CompilerServices;

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

    [Fact]
    public void TheSettingsApplyHoldsTheFocusBeforeTheGateAndRestoresItAfterTheControlsAreEnabled()
    {
        string source = File.ReadAllText(Path.Combine(Root(), "Desktop/Shell/MainWindow.axaml.cs"));
        int start = source.IndexOf("private async Task ApplySettingsAsync(Config candidate)", StringComparison.Ordinal);
        string method = source[start..source.IndexOf("TryCompleteBooruUnavailableFallback();", start, StringComparison.Ordinal)];
        int hold = method.IndexOf("HoldFocusForSettingsApply()", StringComparison.Ordinal);
        int gate = method.IndexOf("_applyingPreferences = true;", StringComparison.Ordinal);
        int reopen = method.LastIndexOf("UpdateInteractionState();", StringComparison.Ordinal);
        int restore = method.IndexOf("RestoreFocusAfterSettingsApply(heldFocus);", StringComparison.Ordinal);
        Assert.True(hold >= 0 && hold < gate, "The focus is held before the workspace is gated.");
        Assert.True(restore > reopen, "The focus returns after the controls are enabled again.");
    }

    private static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
