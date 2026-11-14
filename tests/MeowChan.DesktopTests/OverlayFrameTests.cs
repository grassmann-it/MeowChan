using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// Modals plan MD4: the in-window scrim overlays (the reader's link confirmation, the HTML export, the archive filter surface and
/// the statistics reset question) follow the dialog language: a header bar with the title on the left and the cross alone at the
/// top right, one Escape policy, a Tab cycle and focus return. Layout and key handling run without an Avalonia application (plain
/// controls); the shown overlays are DesktopSmoke's dialog-gallery, html-export, reader-ux and settings-statistics runs.
/// </summary>
public sealed class OverlayFrameTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None, bool handled = false) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Handled = handled };

    [Fact]
    public void TheHeaderHoldsTheCrossAloneAtTheFarLeftFirstInTabOrderThenTheTitle()
    {
        TextBlock title = new() { Text = "Export" };
        Button close = new();
        HeaderBar bar = OverlayFrame.Header(title, close, "Close");
        Assert.Equal(OverlayFrame.HeaderName, bar.Name);
        Assert.Equal(new Control[] { close, title }, bar.Children);
        Assert.Equal(new Control[] { close }, bar.BackChildren);
        Assert.Empty(bar.EndChildren);
        Assert.True(bar.BackSlotIsFirst);
        // Placement at the far left, the 24 dip gap and the separator are HeaderBar's contract (HeaderBarContractTests); text needs a font backend.
        Assert.Contains("section-title", title.Classes);
    }

    [Fact]
    public void AnExistingPageTitleStyleIsKept()
    {
        TextBlock title = new();
        title.Classes.Add("page-title");
        OverlayFrame.Header(title, new Button(), "Close");
        Assert.Contains("page-title", title.Classes);
        Assert.DoesNotContain("section-title", title.Classes);
    }

    [Fact]
    public void TheCrossIsTheDrawnCrossWithItsNameAndTheEscapeHintAndRenamingKeepsTheIcon()
    {
        Button close = new();
        OverlayFrame.MakeClose(close, "Close");
        Assert.Equal(WorkspaceIcons.Close, WorkspaceIcons.KeyOf(close.Content));
        Assert.Contains(WorkspaceIcons.SquareClass, close.Classes);
        Assert.Equal("Close", AutomationProperties.GetName(close));
        Assert.Equal("Close · Esc", ToolTip.GetTip(close));
        object icon = close.Content!;
        OverlayFrame.MakeClose(close, "Cancel");
        Assert.Same(icon, close.Content); // "Cancel" while an export runs: the same cross, another name
        Assert.Equal("Cancel", AutomationProperties.GetName(close));
        Assert.Equal("Cancel · Esc", ToolTip.GetTip(close));
    }

    [Fact]
    public void EscapeFirstCancelsLocalWorkThenClosesAndNeverClosesWhileBusy()
    {
        Assert.Equal(DialogEscapeOutcome.Close, OverlayFrame.DecideEscape(Avalonia.Input.Key.Escape, KeyModifiers.None, false));
        Assert.Equal(DialogEscapeOutcome.CancelledLocal, OverlayFrame.DecideEscape(Avalonia.Input.Key.Escape, KeyModifiers.None, false, () => true));
        Assert.Equal(DialogEscapeOutcome.Close, OverlayFrame.DecideEscape(Avalonia.Input.Key.Escape, KeyModifiers.None, false, () => false, () => false));
        Assert.Equal(DialogEscapeOutcome.Busy, OverlayFrame.DecideEscape(Avalonia.Input.Key.Escape, KeyModifiers.None, false, null, () => true));
        // A child (an open drop-down) that took the key, another key or a modified Escape leave the overlay alone.
        Assert.Equal(DialogEscapeOutcome.Ignored, OverlayFrame.DecideEscape(Avalonia.Input.Key.Escape, KeyModifiers.None, true));
        Assert.Equal(DialogEscapeOutcome.Ignored, OverlayFrame.DecideEscape(Avalonia.Input.Key.Enter, KeyModifiers.None, false));
        Assert.Equal(DialogEscapeOutcome.Ignored, OverlayFrame.DecideEscape(Avalonia.Input.Key.Escape, KeyModifiers.Shift, false));
    }

    [Fact]
    public void AConsumedEscapeIsHandledAndClosesExactlyOnce()
    {
        int closed = 0;
        KeyEventArgs plain = Press(Avalonia.Input.Key.Escape);
        Assert.True(OverlayFrame.TryHandleEscape(plain, () => closed++));
        Assert.True(plain.Handled);
        Assert.Equal(1, closed);
        // The same event arriving again (another handler level) no longer closes anything.
        Assert.False(OverlayFrame.TryHandleEscape(plain, () => closed++));
        Assert.Equal(1, closed);

        KeyEventArgs busy = Press(Avalonia.Input.Key.Escape);
        Assert.True(OverlayFrame.TryHandleEscape(busy, () => closed++, isBusy: () => true));
        Assert.True(busy.Handled);
        Assert.Equal(1, closed);

        KeyEventArgs local = Press(Avalonia.Input.Key.Escape);
        Assert.True(OverlayFrame.TryHandleEscape(local, () => closed++, tryCancelLocal: () => true));
        Assert.Equal(1, closed);

        KeyEventArgs other = Press(Avalonia.Input.Key.Tab);
        Assert.False(OverlayFrame.TryHandleEscape(other, () => closed++));
        Assert.False(other.Handled);
        Assert.Equal(1, closed);
    }

    [Theory]
    [InlineData(true, false, true, true)]    // focus still inside the closed overlay: back to where it came from
    [InlineData(false, true, true, true)]    // focus lost with the hidden overlay: back to where it came from
    [InlineData(false, false, true, false)]  // focus moved elsewhere on its own: never pulled back
    [InlineData(true, false, false, false)]  // the origin is gone or hidden: nothing to return to
    [InlineData(false, true, false, false)]
    public void FocusReturnsOnlyWhenItWouldOtherwiseBeLost(bool inside, bool nowhere, bool originCanTakeFocus, bool expected) =>
        Assert.Equal(expected, OverlayFrame.ShouldReturnFocus(inside, nowhere, originCanTakeFocus));

    [Fact]
    public void AttachMakesTheOverlayAKeyboardModalThatClosesOnEscape()
    {
        Border overlay = new() { IsVisible = false };
        int closed = 0;
        OverlayFrame.Scope scope = OverlayFrame.Attach(overlay, () => closed++);
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(overlay));

        KeyEventArgs escape = Press(Avalonia.Input.Key.Escape);
        overlay.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.Equal(1, closed);

        Button child = new();
        overlay.Child = child;
        KeyEventArgs fromChild = Press(Avalonia.Input.Key.Escape);
        child.RaiseEvent(fromChild); // bubbles from a focused control inside
        Assert.Equal(2, closed);

        KeyEventArgs taken = Press(Avalonia.Input.Key.Escape, handled: true);
        child.RaiseEvent(taken); // an open drop-down inside already used it
        Assert.Equal(2, closed);
        Assert.NotNull(scope);
    }

    [Fact]
    public void TheScopeRemembersTheNamedReturnTargetWhileOpenAndForgetsItWhenClosed()
    {
        Border overlay = new() { IsVisible = false };
        OverlayFrame.Scope scope = OverlayFrame.Attach(overlay, () => { });
        Button link = new();
        scope.ReturnTo = link;
        overlay.IsVisible = true;
        Assert.Same(link, scope.Origin);
        overlay.IsVisible = false;
        Assert.Null(scope.Origin);
        Assert.Null(scope.ReturnTo); // the next opening names its own origin
    }

    // The overlays in markup: a header bar whose back slot holds the cross, before the title, first in their card.
    public static TheoryData<string, string, string, string> Headers => new()
    {
        { "Desktop/Views/Archive/ArchivePage.axaml", "ReaderLinkHeader", "ReaderLinkTitle", "ReaderLinkClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveFilterSurfaceHeader", "ArchiveFilterSurfaceTitle", "ArchiveFilterSurfaceClose" },
        { "Desktop/Views/Statistics/StatisticsPage.axaml", "ResetConfirmHeader", "ResetConfirmTitle", "ResetConfirmClose" },
    };

    [Theory]
    [MemberData(nameof(Headers))]
    public void EveryXamlOverlayHasACrossInAHeaderBarBackSlotBeforeItsTitle(string file, string header, string title, string close)
    {
        XElement bar = Named(file, header);
        Assert.Equal("HeaderBar", bar.Name.LocalName);
        Assert.Equal(new[] { close, title }, bar.Elements().Select(element => (string?)element.Attribute(X + "Name")));
        XElement cross = Named(file, close);
        Assert.Equal("True", cross.Attributes().Single(attribute => attribute.Name.LocalName == "HeaderBar.IsBack").Value);
        Assert.Equal("Button", cross.Name.LocalName);
        // The cross is an icon named in code, so it carries no text of its own.
        Assert.DoesNotContain(cross.Attributes(), attribute => attribute.Name.LocalName is "Content" or "Tag");
        Assert.NotNull(cross.Attribute("Click"));
    }

    [Fact]
    public void TheFilterSurfaceHeaderStandsAboveTheScrollingFieldsAndTheButtons()
    {
        XElement scroll = Named("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveFilterSurfaceScroll");
        XElement grid = scroll.Parent!;
        Assert.Equal("Auto,*,Auto", (string?)grid.Attribute("RowDefinitions"));
        Assert.Equal("ArchiveFilterSurfaceHeader", (string?)grid.Elements().First().Attribute(X + "Name"));
        Assert.Equal("1", (string?)scroll.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)Named("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveFilterApply").Parent!.Attribute("Grid.Row"));
    }

    [Fact]
    public void TheResetQuestionKeepsTheSafeAnswerFirstAndTheDestructiveOneRightMost()
    {
        XElement cancel = Named("Desktop/Views/Statistics/StatisticsPage.axaml", "CancelResetButton");
        XElement[] footer = cancel.Parent!.Elements().ToArray();
        Assert.Equal(new[] { "CancelResetButton", "ConfirmResetButton" }, footer.Select(element => (string?)element.Attribute(X + "Name")));
        Assert.Equal("Right", (string?)cancel.Parent.Attribute("HorizontalAlignment"));
        Assert.Contains("danger", (string?)footer[1].Attribute("Classes"));
    }

    [Fact]
    public void TheExportOverlayBuildsItsHeaderInCodeAndItsCrossCancelsLikeTheFooterButton()
    {
        string dialog = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Export.Dialog.cs"));
        Assert.Contains("OverlayFrame.Header(title, headerClose,", dialog);
        Assert.Contains("\"ArchiveExportHeaderClose\"); headerClose.Click += OnExportClose;", dialog);
        // While an export runs the footer Close is Cancel; the cross takes the same name.
        Assert.Contains("OverlayFrame.MakeClose(Control<Button>(\"ArchiveExportHeaderClose\"), Texts.T(_exportRunning ? \"archive.exportCancel\" : \"action.close\"))", dialog);
        Assert.Contains("Control<Button>(\"ArchiveExportClose\").Content = Texts.T(_exportRunning", dialog);
    }

    [Fact]
    public void EveryOverlayTakesEscapeFromTheCentralPolicy()
    {
        string export = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Export.cs"));
        string exportDialog = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Export.Dialog.cs"));
        string links = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.ExternalLinks.cs"));
        string statistics = File.ReadAllText(Root("Desktop/Views/Statistics/StatisticsPage.axaml.cs"));
        string surface = File.ReadAllText(Root("Desktop/Controls/FilterSurfaceController.cs"));
        Assert.Contains("OverlayFrame.TryHandleEscape(e, () => OnExportClose(sender, e))", export);
        Assert.Contains("OverlayFrame.TryHandleEscape(e, () => OnExportClose(sender, e))", exportDialog);
        Assert.DoesNotContain("e.Key == Key.Escape", export.Substring(export.IndexOf("private void OnExportKeyDown", StringComparison.Ordinal)));
        Assert.DoesNotContain("e.Key == Key.Escape", exportDialog);
        Assert.Contains("OverlayFrame.Attach(ReaderLinkDialog, CloseExternalLinkDialog)", links);
        Assert.Contains("OverlayFrame.TryHandleEscape(e, () => CloseConfirmation(focusReset: true))", statistics);
        Assert.Contains("OverlayFrame.TryHandleEscape(e, () => Close(restoreFocus: true))", surface);
        Assert.DoesNotContain("Key.Escape", surface);
    }

    [Fact]
    public void TheFilterSurfaceFocusesContentFirstNeverItsOwnCross()
    {
        string surface = File.ReadAllText(Root("Desktop/Controls/FilterSurfaceController.cs"));
        Assert.Contains("HeaderBar.GetIsBack(control)", surface);
    }

    private static XElement Named(string file, string name) =>
        XDocument.Load(Root(file)).Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
