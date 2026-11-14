using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Modals plan MD1: the dialog factory's size steps, caption variant, Escape order, footer order and the optional frame
// header. A Window needs a platform backend, so the shown windows are DesktopSmoke's dialog-frame and window-chrome runs.
public sealed class DialogWindowTests
{
    [Theory]
    [InlineData(DialogSize.S, 480, 360, 360, 300)]
    [InlineData(DialogSize.M, 640, 520, 420, 360)]
    [InlineData(DialogSize.L, 760, 640, 480, 400)]
    [InlineData(DialogSize.XL, 980, 720, 640, 560)]
    public void EachStepHasItsPreferredAndMinimumSize(DialogSize size, double width, double height, double minWidth, double minHeight)
    {
        Assert.Equal(new Size(width, height), WorkspaceMetrics.DialogPreferredSize(size));
        Assert.Equal(new Size(minWidth, minHeight), WorkspaceMetrics.DialogMinimumSize(size));
    }

    [Fact]
    public void StepsGrowAndTheirMinimumsKeepRoomForTitleBarBodyAndFooter()
    {
        DialogSize[] steps = Enum.GetValues<DialogSize>();
        Assert.Equal(new[] { DialogSize.S, DialogSize.M, DialogSize.L, DialogSize.XL }, steps);
        for (int index = 0; index < steps.Length; index++)
        {
            Size preferred = WorkspaceMetrics.DialogPreferredSize(steps[index]), minimum = WorkspaceMetrics.DialogMinimumSize(steps[index]);
            Assert.True(minimum.Width <= preferred.Width && minimum.Height <= preferred.Height, steps[index] + " minimum exceeds its size");
            // DialogMinimumHeightTests: title bar 48 + heading + footer + a usable body need 260 dip.
            Assert.True(minimum.Height >= 260, steps[index] + " minimum is too low for a framed window");
            if (index == 0) continue;
            Size smaller = WorkspaceMetrics.DialogPreferredSize(steps[index - 1]), smallerMinimum = WorkspaceMetrics.DialogMinimumSize(steps[index - 1]);
            Assert.True(preferred.Width > smaller.Width && preferred.Height > smaller.Height, steps[index] + " is not larger than " + steps[index - 1]);
            Assert.True(minimum.Width > smallerMinimum.Width && minimum.Height > smallerMinimum.Height, steps[index] + " minimum does not grow");
        }
        Assert.True(WorkspaceMetrics.DialogContentMinimumHeight > WorkspaceMetrics.TitleBarHeight
            && WorkspaceMetrics.DialogContentMinimumHeight <= WorkspaceMetrics.DialogMinimumSize(DialogSize.S).Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceMetrics.DialogPreferredSize((DialogSize)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceMetrics.DialogMinimumSize((DialogSize)99));
    }

    [Fact]
    public void AModalDialogHasNoMinimizeAndAModelessWindowKeepsIt()
    {
        Assert.Equal(WindowChromeKind.Dialog, DialogWindow.KindFor(modal: true));
        Assert.Equal(WindowChromeKind.Window, DialogWindow.KindFor(modal: false));
        Assert.False(WindowChromePolicy.ShowsMinimize(DialogWindow.KindFor(modal: true)));
        Assert.True(WindowChromePolicy.ShowsMinimize(DialogWindow.KindFor(modal: false)));
    }

    [Fact]
    public void TheFactoryCentresFitsAndKeepsModalsOutOfTheTaskbar()
    {
        string source = File.ReadAllText(Source("Desktop/Controls/DialogWindow.cs"));
        Assert.Contains("window.WindowStartupLocation = WindowStartupLocation.CenterOwner;", source);
        Assert.Contains("if (modal) window.ShowInTaskbar = false;", source);
        Assert.Contains("DialogFit.OnOpen(window);", source);
        Assert.Contains("WindowChrome.Frame(window, content, KindFor(modal), captionName)", source);
    }

    [Fact]
    public void EscapeCancelsLocalWorkFirstThenClosesButNeverWhileBusy()
    {
        int local = 0;
        Func<bool> cancels = () => { local++; return true; };
        Func<bool> nothingToCancel = () => { local++; return false; };
        Assert.Equal(DialogEscapeOutcome.CancelledLocal, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, cancels, () => true));
        Assert.Equal(DialogEscapeOutcome.CancelledLocal, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, cancels, () => false));
        Assert.Equal(DialogEscapeOutcome.Busy, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, nothingToCancel, () => true));
        Assert.Equal(DialogEscapeOutcome.Close, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, nothingToCancel, () => false));
        Assert.Equal(4, local);
        Assert.Equal(DialogEscapeOutcome.Close, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, null, null));
        Assert.Equal(DialogEscapeOutcome.Busy, DialogEscape.Decide(Key.Escape, KeyModifiers.None, false, null, () => true));
    }

    [Theory]
    [InlineData(Key.Escape, KeyModifiers.None, true)]   // a drop-down, flyout or key recorder used it
    [InlineData(Key.Escape, KeyModifiers.Shift, false)]
    [InlineData(Key.Escape, KeyModifiers.Control, false)]
    [InlineData(Key.Enter, KeyModifiers.None, false)]
    [InlineData(Key.Back, KeyModifiers.None, false)]
    public void OnlyAPlainUnhandledEscapeCounts(Key key, KeyModifiers modifiers, bool handled)
    {
        int local = 0;
        Assert.Equal(DialogEscapeOutcome.Ignored, DialogEscape.Decide(key, modifiers, handled, () => { local++; return true; }, () => false));
        Assert.Equal(0, local);
    }

    [Fact]
    public void FooterPutsThePrimaryActionRightMostAndKeepsTheOtherOrder()
    {
        Button primary = new() { Name = "Primary" }, first = new() { Name = "First" }, second = new() { Name = "Second" };
        primary.Classes.Add("danger");
        Assert.Equal(new Control[] { first, second, primary }, DialogFooter.Order(primary, new Control[] { first, primary, second }));
        Assert.Equal(new Control[] { first }, DialogFooter.Order(null, new Control[] { first }));
        Assert.Equal(new Control[] { primary }, DialogFooter.Order(primary, null));

        Border footer = DialogFooter.Build(primary, new Control[] { first, second });
        Assert.Contains("workspace-dialog-footer", footer.Classes);
        WrapPanel actions = Assert.IsType<WrapPanel>(footer.Child);
        Assert.Equal(DialogFooter.ActionsName, actions.Name);
        Assert.Equal(new Control[] { first, second, primary }, actions.Children.ToArray());
        Assert.Equal(8d, actions.ItemSpacing);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, actions.HorizontalAlignment);
        Assert.Contains("danger", primary.Classes); // the footer never changes a button's own classes
        Assert.DoesNotContain("primary", primary.Classes);
    }

    [Fact]
    public void FooterStatusStandsLeftOfTheActions()
    {
        TextBlock status = new() { Text = "3 of 5" };
        Button close = new();
        Border footer = DialogFooter.Build(close, status: status);
        DockPanel row = Assert.IsType<DockPanel>(footer.Child);
        WrapPanel actions = Assert.IsType<WrapPanel>(row.Children[0]);
        Assert.Equal(Dock.Right, DockPanel.GetDock(actions));
        Assert.Same(status, row.Children[1]);
        Assert.Equal(DialogFooter.StatusName, status.Name);
        Assert.Equal(new Control[] { close }, actions.Children.ToArray());
    }

    [Fact]
    public void FrameLeavesOutAnAbsentHeaderOrFooter()
    {
        Grid withAll = DialogFrame.Build(new TextBlock(), new TextBlock(), new Button());
        Assert.Contains(withAll.Children, child => child.Name == DialogFrame.HeaderName);
        Assert.Contains(withAll.Children, child => child.Name == DialogFrame.FooterName);
        Assert.Equal(default, withAll.Margin);

        Grid headerless = DialogFrame.Build(header: null, new TextBlock(), DialogFooter.Build(new Button()));
        Assert.DoesNotContain(headerless.Children, child => child.Name == DialogFrame.HeaderName);
        Assert.Contains(headerless.Children, child => child.Name == DialogFrame.FooterName);
        Assert.Equal(16d, headerless.Margin.Top); // the body starts one header inset below the title bar

        Grid bare = DialogFrame.Build(header: null, new TextBlock(), footer: null);
        Assert.DoesNotContain(bare.Children, child => child.Name == DialogFrame.FooterName);
        Assert.Single(bare.Children, child => child.Name == DialogFrame.BodyName);
    }

    // The four reference dialogs of MD1: no title repeated in the body, Escape through the policy, the footer through the factory.
    [Theory]
    [InlineData("Desktop/MediaRatingDialog.cs", null, true)]
    [InlineData("Desktop/Views/Media/MediaOccurrencesWindow.cs", null, false)]
    [InlineData("Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs", "internal static class ReaderGroupsPrompt", true)]
    [InlineData("Desktop/Views/Collections/PostBookmarksDialog.axaml.cs", "private async Task<bool> ConfirmAsync(", true)]
    public void ReferenceDialogsUseTheFactoryAndShowTheTitleOnlyInTheTitleBar(string path, string? anchor, bool footer)
    {
        string source = File.ReadAllText(Source(path));
        string part = anchor == null ? source : source[source.IndexOf(anchor, StringComparison.Ordinal)..];
        Assert.Contains("DialogEscape.Attach(", part);
        Assert.DoesNotContain("Key.Escape", part);
        Assert.DoesNotContain("_heading", part);
        Assert.DoesNotContain("Text = Title", part);
        Assert.Equal(footer, part.Contains("DialogFooter.Build(", StringComparison.Ordinal));
        Assert.Matches(@"DialogWindow\.(Configure|Create)\(", part);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
