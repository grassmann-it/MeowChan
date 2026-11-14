using System.Runtime.CompilerServices;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Modals plan MD3: the ad-hoc windows, the XAML dialogs and the tool windows on the dialog factory, the footer and the
// Escape policy. A Window needs a platform backend, so these are footer-model and source-seam tests; the shown windows are
// DesktopSmoke's dialog-frame, dialog-gallery and window-chrome runs.
public sealed class DialogMd3Tests
{
    [Fact]
    public void FillTakesTheXamlButtonsOutOfTheirPanelsAndOrdersThemPrimaryLast()
    {
        Button reload = new() { Name = "Reload" }, close = new() { Name = "Close" }, save = new() { Name = "Save" };
        TextBlock status = new() { Name = "Status" };
        ScrollViewer statusHost = new() { Name = "StatusHost", Content = status };
        WrapPanel declared = new() { Children = { save, reload, close } };
        StackPanel oldFooter = new() { Children = { statusHost, declared } };
        Border host = new() { Name = "DialogFrameFooter", Child = oldFooter };
        host.Classes.Add("workspace-dialog-footer");

        DialogFooter.Fill(host, save, new Control[] { reload, close }, statusHost, statusAbove: true);

        Assert.Contains("workspace-dialog-footer", host.Classes);
        StackPanel stack = Assert.IsType<StackPanel>(host.Child);
        Assert.Same(statusHost, stack.Children[0]);
        WrapPanel actions = Assert.IsType<WrapPanel>(stack.Children[1]);
        Assert.NotSame(declared, actions);
        Assert.Equal(new Control[] { reload, close, save }, actions.Children.ToArray());
        Assert.Empty(declared.Children);
        Assert.Equal("Reload", reload.Name); // names stay, so the smokes and the code still find the buttons
        Assert.Equal("StatusHost", statusHost.Name);
        Assert.Equal(DialogFooter.ActionsName, actions.Name);
    }

    [Fact]
    public void FillWithoutAStatusLeavesOnlyTheActions()
    {
        Button close = new(), save = new();
        Border host = new() { Child = new WrapPanel { Children = { close, save } } };
        DialogFooter.Fill(host, save, new Control[] { close });
        WrapPanel actions = Assert.IsType<WrapPanel>(host.Child);
        Assert.Equal(new Control[] { close, save }, actions.Children.ToArray());
    }

    [Fact]
    public void ThePromptAsksWithTheKeepActionFirstAndTheAcceptActionRightMost()
    {
        string source = File.ReadAllText(Source("Desktop/Controls/DialogPrompt.cs"));
        Assert.Contains("DialogWindow.Create(title, DialogSize.S, modal: true, heightToContent: true)", source);
        Assert.Contains("DialogFooter.Build(accept, new Control[] { cancel })", source);
        Assert.Contains("DialogEscape.Attach(window, close: () => window.Close(false));", source);
        Assert.Contains("cancel.Focus()", source);
        Assert.DoesNotContain("Key.Escape", source);
    }

    // The ad-hoc windows: factory window, no repeated title heading in the body, footer through the factory.
    [Theory]
    [InlineData("Desktop/Shell/MainWindow.ArchiveDeletion.cs", "DialogSize.M")]
    [InlineData("Desktop/Views/Archive/ArchivePage.Favorites.cs", "DialogSize.L")]
    [InlineData("Desktop/Views/Archive/ArchivePage.Favorites.cs", "DialogSize.M")]
    [InlineData("Desktop/Views/Plugins/PluginsPage.Migration.cs", "DialogSize.L")]
    public void AdHocWindowsTakeTheirSizeStepFromTheFactory(string path, string step)
    {
        string source = File.ReadAllText(Source(path));
        Assert.Contains("DialogWindow.Create(", source);
        Assert.Contains(step + ", modal: true", source);
        Assert.Contains("DialogFooter.Build(", source);
        Assert.Contains("DialogEscape.Attach(", source);
        Assert.DoesNotContain("Key.Escape", source);
        Assert.DoesNotContain("new Window", source);
        Assert.DoesNotContain("WindowChromeKind.Dialog", source);
        Assert.DoesNotContain("TextBlock heading", source);
        Assert.DoesNotContain("TextBlock title = new() { Text = dialog.Title", source);
    }

    [Fact]
    public void ArchiveDeletionKeepsCancelBeforeTheDestructiveActionAndNeverClosesWhileDeleting()
    {
        string source = File.ReadAllText(Source("Desktop/Shell/MainWindow.ArchiveDeletion.cs"));
        Assert.Contains("DialogFooter.Build(delete, new Control[] { cancel, ratingRetry, tagRetry }, status, statusAbove: true)", source);
        Assert.Contains("DialogEscape.Attach(dialog, isBusy: () => executing);", source);
        Assert.Contains("dialog.Closing += (_, e) => { if (executing) e.Cancel = true; };", source);
    }

    [Fact]
    public void OriginalExportHasItsPrimaryActionRightMostAndEscapeStopsARunningCopyFirst()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Archive/ArchivePage.Favorites.cs"));
        Assert.Contains("DialogFooter.Build(start, new Control[] { cancel, openFolder },", source);
        Assert.Contains("DialogEscape.Attach(dialog, tryCancelLocal: () =>", source);
        Assert.Contains("run.Cancel();", source);
    }

    // The XAML dialogs keep their named frame parts, but sizes, startup location and taskbar entry come from the factory.
    [Theory]
    [InlineData("Desktop/Views/Collections/PostBookmarksDialog.axaml", "DialogSize.L", false)]
    [InlineData("Desktop/Views/Collections/ReaderGroupsDialog.axaml", "DialogSize.L", true)]
    [InlineData("Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml", "DialogSize.L", true)]
    [InlineData("Desktop/Views/Settings/FirstRunSetupWindow.axaml", "DialogSize.L", true)]
    [InlineData("Desktop/Views/About/AboutWindow.axaml", "DialogSize.L", true)]
    [InlineData("Desktop/Views/Settings/SettingsWindow.axaml", "DialogSize.XL", false)]
    [InlineData("Desktop/Views/Admission/ExperimentalActivationDialog.axaml", "DialogSize.M", true)]
    public void XamlDialogsHaveNoLiteralSizesAndTakeTheStepFromTheFactory(string path, string step, bool modal)
    {
        string markup = File.ReadAllText(Source(path));
        foreach (string literal in new[] { " Width=\"", " Height=\"", "MinWidth=\"", "MinHeight=\"", "WindowStartupLocation=", "ShowInTaskbar=" })
            Assert.DoesNotContain(literal, markup.Split("<Window.Styles>")[0].Split("<!--")[0]);
        string code = File.ReadAllText(Source(path + ".cs"));
        Assert.Contains("DialogWindow.Configure(this, " + step + ", modal: " + (modal ? "true" : "false") + ");", code);
        Assert.Contains("DialogEscape.Attach(this", code);
    }

    [Theory]
    [InlineData("Desktop/Views/Collections/PostBookmarksDialog.axaml.cs")]
    [InlineData("Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs")]
    [InlineData("Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs")]
    public void ManagerDialogsFillTheirFooterThroughTheFactoryWithThePrimaryActionRightMost(string path)
    {
        string source = File.ReadAllText(Source(path));
        Assert.Contains("DialogFooter.Fill(", source);
        Assert.DoesNotContain("Key.Escape", source);
    }

    [Fact]
    public void SavedSearchesKeepTheirOwnMinimumHeightAboveTheStepsMinimum()
    {
        double step = WorkspaceMetrics.DialogMinimumSize(DialogSize.L).Height;
        Assert.True(SavedArchiveSearchesDialog.MinimumHeight >= step, "the footer's eight actions never need less than the step");
        string source = File.ReadAllText(Source("Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs"));
        Assert.Contains("MinHeight = Math.Max(MinHeight, MinimumHeight);", source);
        // Apply (primary) right-most, Close left of it.
        int close = source.IndexOf("Button(\"SavedSearchClose\")", StringComparison.Ordinal);
        Assert.True(close > source.IndexOf("DialogFooter.Fill(", StringComparison.Ordinal));
        Assert.Contains("DialogFooter.Fill(this.FindControl<Border>(\"DialogFrameFooter\")!, Button(\"SavedSearchApply\"),", source);
    }

    [Fact]
    public void FirstRunHasNoRepeatedHeadingAndIsAModalDialog()
    {
        string markup = File.ReadAllText(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml"));
        string code = File.ReadAllText(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs"));
        Assert.DoesNotContain("SetupHeading", markup);
        Assert.DoesNotContain("SetupHeading", code);
        Assert.Contains("AppCaption.Kind = MeowChan.Desktop.Controls.DialogWindow.KindFor(modal: true);", code);
        Assert.Contains("isBusy: () => _committing", code);
    }

    [Fact]
    public void SettingsEscapeAsksBeforeDiscardingUnsavedChangesAndIsSwallowedWhileSaving()
    {
        string code = File.ReadAllText(Source("Desktop/Views/Settings/SettingsWindow.axaml.cs"));
        Assert.Contains("DialogEscape.Attach(this, tryCancelLocal: AskBeforeDiscardingOnEscape, isBusy: () => Editor.IsBusy);", code);
        Assert.Contains("if (_asking || !Editor.HasUnsavedChanges) return false;", code);
        Assert.Contains("DialogPrompt.ConfirmAsync(", code);
        Assert.Contains("\"settings.discardQuestion\"", code);
        // Without a draft Escape closes, with one it is a local cancel (the question) and the window stays open.
        bool asked = false;
        Assert.Equal(DialogEscapeOutcome.Close, DialogEscape.Decide(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None, false, () => false, () => false));
        Assert.Equal(DialogEscapeOutcome.CancelledLocal, DialogEscape.Decide(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None, false, () => asked = true, () => false));
        Assert.True(asked);
        Assert.Equal(DialogEscapeOutcome.Busy, DialogEscape.Decide(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None, false, () => false, () => true));
        Assert.Contains("internal bool HasUnsavedChanges", File.ReadAllText(Source("Desktop/Views/Settings/SettingsEditor.WorkViews.cs")));
    }

    [Fact]
    public void ToolWindowsStayModelessWithoutARepeatedTitle()
    {
        string workbench = File.ReadAllText(Source("Desktop/Views/Booru/BooruWorkbenchWindow.cs"));
        Assert.Contains("DialogWindow.Configure(this, DialogSize.XL, modal: false);", workbench);
        Assert.Contains("modal: false);", workbench);
        Assert.DoesNotContain("_title", workbench);
        Assert.DoesNotContain("Key.Escape", workbench);
        Assert.Contains("DialogEscape.Attach(this, tryCancelLocal: CancelEscapeLocally);", workbench);

        string startup = File.ReadAllText(Source("Desktop/StartupErrorWindow.cs"));
        Assert.Contains("DialogWindow.Configure(this, DialogSize.S, modal: false);", startup);
        Assert.DoesNotContain("_heading", startup);
        Assert.DoesNotContain("IsCancel", startup);

        string admission = File.ReadAllText(Source("Desktop/Views/Admission/BoardAdmissionWindow.cs"));
        Assert.Contains("DialogWindow.Configure(this, DialogSize.XL, modal: true);", admission);
        Assert.Contains("DialogFooter.Build(_apply, new Control[] { cancel }, footer, statusAbove: true)", admission);
        // Escape is no longer a close shortcut of its own: the one remaining mention only keeps it away from the command router.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(admission, "Key\\.Escape"));
        Assert.Contains("e.Key != Avalonia.Input.Key.Escape", admission);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
