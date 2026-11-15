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
    public void SuggestionBoxLeavesEscapeToTheOwnerOnlyWhenAskedAndOnlyWithoutAnOpenList()
    {
        string source = File.ReadAllText(Source("Desktop/IdentitySuggestionBox.cs"));
        Assert.Contains("internal bool ReleaseEscape { get; set; }", source);
        Assert.Contains("bool listWasOpen = IsDropDownOpen;", source);
        Assert.Contains("if (listWasOpen || !ReleaseEscape) e.Handled = true;", source);
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

    // The remaining reference dialogs of MD1: no title repeated in the body, Escape through the policy, the footer through the factory.
    [Theory]
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

    // MD2: the class-based dialogs of the archive, transfer, search, tag and media groups. Each takes its size step and its
    // modal or modeless kind from the factory, frames through it, answers Escape through the policy, shows its title only in
    // the title bar (no duplicated _heading) and spells Close as action.close. A lone footer Close is left out where × suffices.
    [Theory]
    [InlineData("Desktop/ArchiveCleanupDialog.cs", true, true)]
    [InlineData("Desktop/MediaRatingsManagerDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/FilenameSearchDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/InterruptedTransfersDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/MediaConflictsDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/MediaHashSearchDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/NativeTransferImportDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/NativeTransferPreviewDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/PostFilterDialog.cs", true, true)]
    [InlineData("Desktop/Views/Archive/UrlTextExportDialog.cs", true, true)]
    [InlineData("Desktop/Views/Collections/MediaTagDialog.cs", true, true)]
    [InlineData("Desktop/Views/Collections/TagManagerDialog.cs", true, true)]
    [InlineData("Desktop/Views/Media/MediaSelectionTagDialog.cs", true, true)]
    [InlineData("Desktop/Views/Media/ManualImportDialog.cs", false, true)]
    // Read-only details dialogs: no footer at all.
    [InlineData("Desktop/Views/Media/BooruMediaDetailsDialog.cs", true, false)]
    public void ClassDialogsOfMd2UseTheFactoryTheFooterAndTheEscapePolicy(string path, bool modal, bool footer)
    {
        string source = File.ReadAllText(Source(path));
        string kind = modal ? "true" : "false";
        Assert.Matches(@"DialogWindow\.Configure\(this, DialogSize\.[SMLX], modal: " + kind + @"\)", source);
        Assert.Matches(@"DialogWindow\.Frame\(this, [^;]*modal: " + kind + @"[,)]", source);
        Assert.Contains("DialogEscape.Attach(this", source);
        // The tag dialogs and the import dialog keep one Escape rule of their own: the first press closes an open suggestion list.
        bool listEscape = path.Contains("MediaTagDialog", StringComparison.Ordinal) || path.Contains("MediaSelectionTagDialog", StringComparison.Ordinal)
            || path.Contains("ManualImportDialog", StringComparison.Ordinal);
        if (!listEscape) Assert.DoesNotContain("Key.Escape", source);
        // The suggestion box consumes every Escape unless its owner opts out; without ReleaseEscape the dialog's Escape policy
        // (DialogEscape, a bubbling handler that ignores handled keys) would never see an Escape typed in the tag field.
        if (listEscape) Assert.Contains("ReleaseEscape = true", source);
        Assert.DoesNotContain("_heading", source);
        Assert.DoesNotContain("WindowChrome.Apply", source);
        Assert.DoesNotContain("WindowChromeBar _chrome", source);
        Assert.DoesNotContain("WindowStartupLocation", source);
        Assert.Equal(footer, source.Contains("DialogFooter.Build(", StringComparison.Ordinal));
        foreach (string obsolete in new[] { "archive.cleanup.close", "transfer.import.close", "mediahash.close", "postFilter.close", "tags.media.close",
            "tags.manage.close", "import.close", "urltext.export.close", "filename.search.close", "media.rating.close" })
            Assert.DoesNotContain("\"" + obsolete + "\"", source);
    }

    [Theory]
    [InlineData("Desktop/Shell/MainWindow.GalleryActions.cs", "Name = \"GalleryDetailsDialog\"")]
    [InlineData("Desktop/Views/Archive/ArchivePage.Commands.cs", "private async Task ShowArchiveMetadataAsync(")]
    public void ReadOnlyDetailsDialogsHaveNeitherHeadingNorLoneCloseButton(string path, string anchor)
    {
        string source = File.ReadAllText(Source(path));
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, anchor);
        string part = source[start..Math.Min(source.Length, start + 1800)];
        Assert.Contains("DialogWindow.Create(", source[(path.EndsWith("GalleryActions.cs", StringComparison.Ordinal) ? start - 400 : start)..Math.Min(source.Length, start + 1800)]);
        Assert.Contains("DialogEscape.Attach(dialog)", part);
        Assert.Contains("footer: null", part);
        Assert.DoesNotContain("heading", part, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Button close", part);
    }

    [Fact]
    public void TheAboutWindowHasNoLoneCloseButtonAndClosesOnEscapeThroughThePolicy()
    {
        string xaml = File.ReadAllText(Source("Desktop/Views/About/AboutWindow.axaml"));
        string code = File.ReadAllText(Source("Desktop/Views/About/AboutWindow.axaml.cs"));
        Assert.DoesNotContain("CloseButton", xaml);
        Assert.DoesNotContain("DialogFrameFooter", xaml);
        Assert.DoesNotContain("CloseButton", code);
        Assert.Contains("DialogEscape.Attach(this);", code);
    }

    [Fact]
    public void AStatusAboveTheActionsKeepsTheFullWidthAndTheActionOrder()
    {
        TextBlock status = new() { Name = "Outcome" };
        Button first = new(), primary = new();
        Border footer = DialogFooter.Build(primary, new Control[] { first }, status, statusAbove: true);
        Assert.Contains("workspace-dialog-footer", footer.Classes);
        StackPanel stack = Assert.IsType<StackPanel>(footer.Child);
        Assert.Same(status, stack.Children[0]);
        Assert.Equal("Outcome", status.Name); // an existing name is kept
        WrapPanel actions = Assert.IsType<WrapPanel>(stack.Children[1]);
        Assert.Equal(new Control[] { first, primary }, actions.Children.ToArray());
        Assert.Equal(default, status.Margin);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
