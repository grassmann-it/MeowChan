using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MeowChan.DesktopTests;

// Source regressions only: no native window, process, profile or test-display lease.
// Native activation, popup repair and X11 pixels still require the focused smokes.
public sealed class SmokeDeterminismTests
{
    [Fact]
    public void ToolbarCompletesNativeActivationAndPopupSetupBeforeAssigningFocus()
    {
        string source = Source("tests/MeowChan.DesktopSmoke/ActionToolBarFocusChecks.cs");
        int activation = source.IndexOf("await Program.WaitUntilAsync(() => fixture.IsActive);", StringComparison.Ordinal);
        int open = source.IndexOf("toolbar.More!.IsChecked = true;", StringComparison.Ordinal);
        int popup = source.IndexOf("await Program.WaitUntilAsync(() => PopupReady(gallery));", StringComparison.Ordinal);
        int settle = source.IndexOf("await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);", StringComparison.Ordinal);
        int focus = source.IndexOf("Require(gallery.Focus(NavigationMethod.Tab)", StringComparison.Ordinal);
        Assert.True(activation >= 0 && open > activation && popup > open && settle > popup && focus > settle,
            "A late native activation or queued popup setup must not race the fixture's initial focus.");
        Assert.Contains("TopLevel.GetTopLevel(action) is { IsVisible: true }", source);
        Assert.Contains("action.Bounds.Width > 0 && action.Bounds.Height > 0", source);
    }

    [Fact]
    public void ToolbarReopenStillTestsProductionRepairRatherThanAssigningExpectedFocus()
    {
        string source = Source("tests/MeowChan.DesktopSmoke/ActionToolBarFocusChecks.cs");
        Assert.Equal(1, Regex.Matches(source, @"Require\(gallery\.Focus\(").Count);
        int reopen = source.IndexOf("stage = \"reopen overflow and Escape\";", StringComparison.Ordinal);
        int competing = source.IndexOf("stage = \"explicit competing focus wins\";", StringComparison.Ordinal);
        Assert.True(reopen >= 0 && competing > reopen);
        string check = source[reopen..competing];
        Assert.Contains("PopupReady(gallery)", check);
        Assert.Contains("ReferenceEquals(fixture.FocusManager?.GetFocusedElement(), gallery)", check);
        Assert.Contains("Key = Key.Escape", check);
        Assert.DoesNotContain("gallery.Focus(", check);
        Assert.Contains("Require(executions == 1", check);
    }

    [Theory]
    [InlineData("ActionToolBarFocusChecks.cs", "checkFailure")]
    [InlineData("WindowChromeChecks.cs", "sequenceFailure")]
    public void FixtureDiagnosticAndCleanupCannotReplaceThePrimaryFailure(string file, string failure)
    {
        string source = Source("tests/MeowChan.DesktopSmoke/" + file);
        Assert.Contains(failure + " = exception;", source);
        Assert.Contains("await SmokeCleanup.RunAsync(" + failure + ",", source);
        Assert.Contains("SmokeCleanup.Diagnose(() => Program.CaptureWindow", source);
        Assert.Contains("{stage}: {exception}", source);
    }

    [Fact]
    public void ChromeCleanupUsesTheCaptionSnapshotAndKeepsExactNormalBoundsChecks()
    {
        string source = Source("tests/MeowChan.DesktopSmoke/WindowChromeChecks.cs");
        int cleanup = source.IndexOf("await SmokeCleanup.RunAsync(sequenceFailure,", StringComparison.Ordinal);
        int nextCheck = source.IndexOf("window.Width = entryBounds.Width;", StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && nextCheck > cleanup);
        string restore = source[cleanup..nextCheck];
        Assert.Contains("if (original == WindowState.Normal && window.WindowState == WindowState.Maximized)", restore);
        Assert.Contains("Click(maximize);", restore);
        Assert.Contains("else window.WindowState = original;", restore);
        Assert.Contains("Math.Abs(window.Bounds.Width - originalBounds.Width) <= 1", restore);
        Assert.Contains("Math.Abs(window.Bounds.Height - originalBounds.Height) <= 1", restore);
    }

    [Fact]
    public void ChromeReadsTheExactNativeRightColumnWithoutAnExternalCaptureDeadline()
    {
        string source = Source("tests/MeowChan.DesktopSmoke/WindowChromeChecks.cs");
        Assert.DoesNotContain("Process.Start(", source);
        Assert.DoesNotContain("ProcessStartInfo", source);
        Assert.DoesNotContain("TimeSpan.FromSeconds(5)", source);
        Assert.Contains("XGetImage(display, root, area.Right - 1, area.Y, 1, (uint)area.Height, nuint.MaxValue, 2)", source);
        Assert.Contains("XGetPixel(image, 0, y)", source);
        Assert.Contains("y < area.Height", source);
        Assert.Contains("color.Red == 255 && color.Green == 0 && color.Blue == 255", source);
        Assert.Contains("Require(firstGap == null", source);
        Assert.Contains("if (image != 0) XDestroyImage(image);", source);
        Assert.Contains("XCloseDisplay(display);", source);
        Assert.Contains("await SmokeCleanup.RunAsync(edgeFailure,", source);
        Assert.Contains("edgeFailure = exception;", source);
        Assert.Contains("await SmokeCleanup.RunAsync(pixelFailure,", source);
        Assert.Contains("pixelFailure = exception; throw;", source);
    }

    [Fact]
    public void ArchiveBatchMenuRetainsTheExactSharedOrderWithoutSingleThreadUrlExport()
    {
        var source = CSharpSource.Parse(Source("Desktop/Views/Archive/ArchivePage.Commands.cs"));
        var menu = source.Method("BuildArchiveSelectionMenu");
        var body = Assert.IsType<BlockSyntax>(menu.Body);
        var entries = body.Calls("Add").Where(call => call.Expression is IdentifierNameSyntax).ToArray();
        string[] batch = entries.Where(call => call.Parent is ExpressionStatementSyntax statement && statement.Parent == body)
            .Select(CommandId).ToArray();
        Assert.Equal(new[]
        {
            "thread.favorite-toggle", "thread.read", "gallery.open", "thread.details", "post-bookmarks.open-manager", "export.open-html-dialog",
            "selection.copy-links", "location.open-folder", "location.open-source", "watch.check", "watch.retry-media",
            "rules.edit-scope", "watch.remove", "archive.delete-permanently"
        }, batch);
        AssertSingleThread(Assert.Single(entries, call => CommandId(call) == "export.open-urltext-dialog"));
        AssertSingleThread(Assert.Single(entries, call => CommandId(call) == "thread.tags"));
        AssertSingleThread(Assert.Single(body.Calls("AddThreadMediaCommands")));

        void AssertSingleThread(InvocationExpressionSyntax call)
        {
            var branch = Assert.Single(body.Statements.OfType<IfStatementSyntax>(), statement => statement.Statement.Span.Contains(call.Span));
            Assert.True(branch.Condition.IsEquivalentTo(SyntaxFactory.ParseExpression("ids.Length == 1")), call.ToString());
        }

        static string CommandId(InvocationExpressionSyntax call) =>
            Assert.IsType<LiteralExpressionSyntax>(call.ArgumentList.Arguments[0].Expression).Token.ValueText;
    }

    [Fact]
    public void SelectionChecksBothTableAndCardBatchOrderAndSingleTargetExportSlot()
    {
        string source = Source("tests/MeowChan.DesktopSmoke/SelectionChecks.cs");
        Assert.Contains("RequireArchiveMenuOrder(table.ContextMenu, single: false);", source);
        Assert.Contains("RequireArchiveMenuOrder(cardMenu, single: false);", source);
        Assert.Contains("CheckKeyboardContextAsync(table, table.ContextMenu!, singleArchiveSelection: true)", source);
        Assert.Contains("CheckKeyboardContextAsync(table, table.ContextMenu, singleArchiveSelection: false)", source);
        Assert.Contains("expected.InsertRange(expected.IndexOf(\"thread.show-in-media\") + 1, [\"thread.tags\", \"thread.tag-media\", \"thread.stamp-media\"]);", source);
        Assert.Contains("expected.Insert(expected.IndexOf(\"export.open-html-dialog\") + 1, \"export.open-urltext-dialog\");", source);
        Assert.Contains("actual.SequenceEqual(expected)", source);
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
