using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// GL3 (PLAN-GALERIE-LAYOUT-BLOECKE §3, Tom's E2): the layout menu. The rows are pure (GalleryLayoutMenu.Rows / Apply) and both the flyout
/// and the right-click menu are made from them, so the rows × plan × choice × view table, the disabled reasons, the reset with its undo and
/// the per-mode editing are tested here without a window. The visual proof (the button, the flyout, the right click, Light/Dark) is the
/// gallery-layout smoke.
/// </summary>
[Collection("Localization")]
public sealed class GalleryLayoutMenuTests : IDisposable
{
    public GalleryLayoutMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static GalleryChromeInput Input(GalleryChromeMode mode = GalleryChromeMode.View, WorkView view = WorkView.Simple,
        GalleryLayoutChoice? choice = null, bool mediaFocus = false, bool panelCoupled = false, double height = 1030) =>
        new(mode, view, choice ?? GalleryLayoutChoice.Default, mediaFocus, false, false, false, false, panelCoupled, 1920, height) { Walk = mode == GalleryChromeMode.Tag };

    private static LayoutMenuModel Menu(GalleryChromeInput input) =>
        GalleryLayoutMenu.Rows(GalleryChromePlan.Resolve(input), input.Choice, GalleryChromePlan.ViewDefault(input.Mode, input.View), input);

    private static LayoutMenuRow Row(LayoutMenuModel menu, string id) => menu.Find(id) ?? throw new InvalidOperationException(id);

    private static GalleryLayoutChoice? Apply(string id, GalleryChromeInput input) =>
        GalleryLayoutMenu.Apply(id, GalleryChromePlan.Resolve(input), input.Choice, GalleryChromePlan.ViewDefault(input.Mode, input.View), input);

    // ---- the rows ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRowsAreTheFourBlocksThePositionAndTheResetInOneOrder()
    {
        LayoutMenuModel menu = Menu(Input());
        Assert.Equal(
        [
            "gallery.layout.filmstrip", "gallery.layout.filmstrip-top", "gallery.layout.filmstrip-bottom", "gallery.layout.tools",
            "gallery.layout.info", "gallery.layout.hints", "gallery.layout.reset"
        ], menu.Interactive.Select(row => row.Id));
        Assert.Equal(GalleryLayoutMenu.RowIds, menu.Interactive.Select(row => row.Id));
        Assert.Equal([LayoutMenuRowKind.Toggle, LayoutMenuRowKind.Choice, LayoutMenuRowKind.Choice, LayoutMenuRowKind.Toggle,
            LayoutMenuRowKind.Toggle, LayoutMenuRowKind.Toggle, LayoutMenuRowKind.Action], menu.Interactive.Select(row => row.Kind));
        // The two positions are one exclusive group, indented under the filmstrip; the ids are unique.
        Assert.Equal(GalleryLayoutMenu.DockGroup, Row(menu, "gallery.layout.filmstrip-top").Group);
        Assert.Equal(GalleryLayoutMenu.DockGroup, Row(menu, "gallery.layout.filmstrip-bottom").Group);
        Assert.All(menu.Interactive.Where(row => row.Kind == LayoutMenuRowKind.Choice), row => Assert.Equal(1, row.Indent));
        Assert.Equal(menu.Rows.Count, menu.Rows.Select(row => row.Id).Distinct().Count());
        // Every row but the last is a block or its position; the reset is last, after a separator.
        Assert.Equal(LayoutMenuRowKind.Separator, menu.Rows[^2].Kind);
        Assert.Equal("gallery.layout.reset", menu.Rows[^1].Id);
    }

    [Theory]
    [InlineData(WorkView.Barebones, false)]
    [InlineData(WorkView.Simple, true)]
    [InlineData(WorkView.Advanced, true)]
    public void TheRowsShowTheEffectiveStateOfThePlanForEveryWorkView(WorkView view, bool filmstripByView)
    {
        LayoutMenuModel menu = Menu(Input(view: view));
        Assert.Equal(filmstripByView, Row(menu, "gallery.layout.filmstrip").Checked);
        Assert.Equal(filmstripByView, Row(menu, "gallery.layout.filmstrip-bottom").Checked); // the default position is the bottom
        Assert.False(Row(menu, "gallery.layout.filmstrip-top").Checked);
        Assert.True(Row(menu, "gallery.layout.tools").Checked);
        Assert.True(Row(menu, "gallery.layout.info").Checked);
        Assert.True(Row(menu, "gallery.layout.hints").Checked);
        // Nothing chosen: the reset has nothing to do and says so; every block is usable.
        Assert.False(Row(menu, "gallery.layout.reset").Enabled);
        Assert.Equal("Already the default of the work view.", Row(menu, "gallery.layout.reset").DisabledReason);
        Assert.All(new[] { "tools", "info", "hints" }, id => Assert.True(Row(menu, "gallery.layout." + id).Enabled));
    }

    [Fact]
    public void TheChoiceBeatsTheViewAndTheRowsShowIt()
    {
        // Barebones leaves the filmstrip away; the person chose it on, at the top, and the info line off.
        LayoutMenuModel menu = Menu(Input(view: WorkView.Barebones, choice: new(Filmstrip: true, FilmstripDock: GalleryFilmstripDock.Top, Info: false)));
        Assert.True(Row(menu, "gallery.layout.filmstrip").Checked);
        Assert.True(Row(menu, "gallery.layout.filmstrip-top").Checked);
        Assert.False(Row(menu, "gallery.layout.filmstrip-bottom").Checked);
        Assert.False(Row(menu, "gallery.layout.info").Checked);
        Assert.True(Row(menu, "gallery.layout.reset").Enabled);
        // The hint says whether a row is the person's own choice or the view's default.
        Assert.Equal("Your choice", Row(menu, "gallery.layout.filmstrip").Hint);
        Assert.Equal("Your choice", Row(menu, "gallery.layout.info").Hint);
        Assert.Equal("Default of the work view", Row(menu, "gallery.layout.tools").Hint);
        // A choice equal to the view's default is no choice for the hint.
        Assert.Equal("Default of the work view", Row(Menu(Input(choice: new(Filmstrip: true))), "gallery.layout.filmstrip").Hint);
    }

    [Fact]
    public void ThePositionRowsWaitForAShownFilmstripAndSayWhy()
    {
        LayoutMenuModel menu = Menu(Input(view: WorkView.Barebones));
        foreach (string id in new[] { "gallery.layout.filmstrip-top", "gallery.layout.filmstrip-bottom" })
        {
            Assert.False(Row(menu, id).Enabled);
            Assert.Equal("Turn the filmstrip on first.", Row(menu, id).DisabledReason);
            Assert.False(Row(menu, id).Checked); // no filmstrip, no position
        }
        Assert.True(Row(menu, "gallery.layout.filmstrip").Enabled); // the switch itself is always usable
    }

    // ---- what the state makes unavailable: disabled, with the reason ------------------------------------------------------------------

    [Fact]
    public void HiddenToolsMakeEveryBlockUnavailableButNotTheReset()
    {
        LayoutMenuModel menu = Menu(Input(choice: new(Info: false), mediaFocus: true));
        foreach (string id in new[] { "filmstrip", "tools", "info", "hints" })
        {
            LayoutMenuRow row = Row(menu, "gallery.layout." + id);
            Assert.False(row.Enabled, id);
            Assert.False(row.Checked, id); // the effective state: nothing shows while the tools are hidden
            Assert.Equal("The tools are hidden. Show them again to change the layout.", row.DisabledReason);
        }
        Assert.True(Row(menu, "gallery.layout.reset").Enabled);
        // A disabled row does nothing, whatever calls it.
        Assert.Null(Apply("gallery.layout.tools", Input(mediaFocus: true)));
        Assert.Null(Apply("gallery.layout.filmstrip-top", Input(mediaFocus: true)));
    }

    [Fact]
    public void ALowViewerHasNoRoomForTheFilmstrip()
    {
        LayoutMenuModel menu = Menu(Input(height: GalleryChromePlan.FilmstripMinimumHeight - 1));
        Assert.False(Row(menu, "gallery.layout.filmstrip").Enabled);
        Assert.Equal("The window is too low for the filmstrip.", Row(menu, "gallery.layout.filmstrip").DisabledReason);
        Assert.Equal("The window is too low for the filmstrip.", Row(menu, "gallery.layout.filmstrip-top").DisabledReason);
        Assert.True(Row(menu, "gallery.layout.info").Enabled);
        Assert.True(Row(Menu(Input(height: GalleryChromePlan.FilmstripMinimumHeight)), "gallery.layout.filmstrip").Enabled);
    }

    [Fact]
    public void TheTagModeReplacesTheToolsAndAPanelSpeaksTheHints()
    {
        LayoutMenuModel menu = Menu(Input(GalleryChromeMode.Tag, panelCoupled: true));
        Assert.False(Row(menu, "gallery.layout.tools").Enabled);
        Assert.False(Row(menu, "gallery.layout.tools").Checked);
        Assert.Equal("The Tag mode has its own controls instead of the tools.", Row(menu, "gallery.layout.tools").DisabledReason);
        Assert.False(Row(menu, "gallery.layout.hints").Enabled);
        Assert.Equal("The tag panel says these sentences itself.", Row(menu, "gallery.layout.hints").DisabledReason);
        // The filmstrip and the info line stay the person's to choose.
        Assert.True(Row(menu, "gallery.layout.filmstrip").Enabled);
        Assert.True(Row(menu, "gallery.layout.info").Enabled);
        // Tag without a coupled panel: the hints are the person's again (off by the Tag default).
        LayoutMenuRow hints = Row(Menu(Input(GalleryChromeMode.Tag)), "gallery.layout.hints");
        Assert.True(hints.Enabled);
        Assert.False(hints.Checked);
    }

    // ---- what a row does ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EachSwitchChangesExactlyOneBlock()
    {
        GalleryChromeInput input = Input();
        GalleryLayoutChoice off = Apply("gallery.layout.info", input)!;
        Assert.Equal(new GalleryLayoutChoice(Info: false), off);
        Assert.Equal(new GalleryLayoutChoice(Tools: false), Apply("gallery.layout.tools", input));
        Assert.Equal(new GalleryLayoutChoice(Hints: false), Apply("gallery.layout.hints", input));
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false), Apply("gallery.layout.filmstrip", input));
        // The other members of the choice are untouched.
        GalleryChromeInput mixed = Input(choice: new(Tools: false, FilmstripDock: GalleryFilmstripDock.Top));
        Assert.Equal(new GalleryLayoutChoice(Tools: false, FilmstripDock: GalleryFilmstripDock.Top, Info: false), Apply("gallery.layout.info", mixed));
    }

    [Fact]
    public void AValueEqualToTheViewsDefaultIsStoredAsTheViewDecides()
    {
        // Simple: the filmstrip is on by the view; off is a choice, on again is "the view decides" (null), not an explicit true.
        GalleryChromeInput off = Input(choice: new(Filmstrip: false));
        Assert.Equal(GalleryLayoutChoice.Default, Apply("gallery.layout.filmstrip", off));
        // Barebones: the filmstrip is off by the view; on is a choice, off again is the view again.
        GalleryChromeInput on = Input(view: WorkView.Barebones, choice: new(Filmstrip: true));
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: true), Apply("gallery.layout.filmstrip", Input(view: WorkView.Barebones)));
        Assert.Equal(GalleryLayoutChoice.Default, Apply("gallery.layout.filmstrip", on));
        // The Tag mode's hints are off by its default: on is the choice, off again is the default.
        GalleryChromeInput tagOn = Input(GalleryChromeMode.Tag, choice: new(Hints: true));
        Assert.Equal(new GalleryLayoutChoice(Hints: true), Apply("gallery.layout.hints", Input(GalleryChromeMode.Tag)));
        Assert.Equal(GalleryLayoutChoice.Default, Apply("gallery.layout.hints", tagOn));
    }

    [Fact]
    public void ThePositionRowsChooseTheDockAndTheDefaultPositionIsNoChoice()
    {
        GalleryChromeInput input = Input();
        Assert.Equal(new GalleryLayoutChoice(FilmstripDock: GalleryFilmstripDock.Top), Apply("gallery.layout.filmstrip-top", input));
        // Bottom is the default position: choosing it takes the choice away; choosing the shown one again changes nothing.
        GalleryChromeInput top = Input(choice: new(FilmstripDock: GalleryFilmstripDock.Top));
        Assert.Equal(GalleryLayoutChoice.Default, Apply("gallery.layout.filmstrip-bottom", top));
        Assert.Equal(top.Choice, Apply("gallery.layout.filmstrip-top", top));
        // The filmstrip's own choice is kept when only the position changes.
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: true, FilmstripDock: GalleryFilmstripDock.Top),
            Apply("gallery.layout.filmstrip-top", Input(choice: new(Filmstrip: true))));
        // An unknown row is nothing.
        Assert.Null(Apply("gallery.layout.nonsense", input));
    }

    [Fact]
    public void ResetGivesTheViewsDefaultAndTheUndoRestoresThePreviousChoice()
    {
        GalleryLayoutChoice chosen = new(Filmstrip: false, Info: false, FilmstripDock: GalleryFilmstripDock.Top);
        GalleryLayoutPreferences saved = GalleryLayoutPreferences.Default.With(GalleryChromeMode.View, chosen)
            .With(GalleryChromeMode.Tag, new GalleryLayoutChoice(Hints: true));
        GalleryChromeInput input = Input(choice: saved.For(GalleryChromeMode.View));
        GalleryLayoutChoice reset = Apply("gallery.layout.reset", input)!;
        Assert.Equal(GalleryLayoutChoice.Default, reset);
        // The window saves the reset for this mode only; the other mode keeps its choice.
        GalleryLayoutPreferences after = saved.With(GalleryChromeMode.View, reset);
        Assert.Equal(GalleryLayoutChoice.Default, after.View);
        Assert.Equal(saved.Tag, after.Tag);
        // Undo (ChangeLayoutChoice(previous)) puts the choice back exactly.
        Assert.Equal(saved, after.With(GalleryChromeMode.View, saved.For(GalleryChromeMode.View)));
        // Resetting an already default mode is offered nothing to undo: the row is disabled.
        Assert.False(Row(Menu(Input()), "gallery.layout.reset").Enabled);
    }

    [Fact]
    public void EachModeIsEditedOnItsOwnAndTheMenuNamesTheMode()
    {
        Assert.Equal("Layout for the mode: View", Menu(Input()).Title);
        Assert.Equal("Layout for the mode: Tag", Menu(Input(GalleryChromeMode.Tag)).Title);
        // The same row gives a choice of its own mode; the two sets of the preferences stay independent.
        GalleryLayoutChoice view = Apply("gallery.layout.info", Input())!;
        GalleryLayoutChoice tag = Apply("gallery.layout.info", Input(GalleryChromeMode.Tag))!;
        GalleryLayoutPreferences prefs = GalleryLayoutPreferences.Default.With(GalleryChromeMode.View, view);
        Assert.Equal(GalleryLayoutChoice.Default, prefs.For(GalleryChromeMode.Tag));
        prefs = prefs.With(GalleryChromeMode.Tag, tag);
        Assert.Equal(new GalleryLayoutChoice(Info: false), prefs.For(GalleryChromeMode.View));
        Assert.Equal(new GalleryLayoutChoice(Info: false), prefs.For(GalleryChromeMode.Tag));
        // Tag's tools are the mode's own; the View choice for the tools does not leak into the Tag menu.
        Assert.False(Row(Menu(Input(GalleryChromeMode.Tag, choice: new(Tools: true))), "gallery.layout.tools").Checked);
    }

    [Fact]
    public void EveryRowExistsForEveryViewAndModeWithAReasonOrNone()
    {
        foreach (WorkView view in new[] { WorkView.Barebones, WorkView.Simple, WorkView.Advanced })
            foreach (GalleryChromeMode mode in new[] { GalleryChromeMode.View, GalleryChromeMode.Tag })
            {
                LayoutMenuModel menu = Menu(Input(mode, view));
                Assert.Equal(GalleryLayoutMenu.RowIds, menu.Interactive.Select(row => row.Id)); // Barebones shows the same rows
                Assert.All(menu.Interactive, row =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(row.Label));
                    Assert.Equal(row.Enabled, row.DisabledReason == null);
                    Assert.True(row.DisabledReason == null || row.DisabledReason.Length > 8); // a reason is a sentence
                });
            }
    }

    // ---- the two presentations from one source ---------------------------------------------------------------------------------------

    [Fact]
    public void TheContextMenuIsBuiltFromTheSameRows()
    {
        GalleryChromeInput input = Input(view: WorkView.Barebones, choice: new(Info: false));
        LayoutMenuModel menu = Menu(input);
        List<string> chosen = [];
        ContextMenuModel model = LayoutMenuBuilder.ToContextMenu(menu, row => chosen.Add(row.Id), "Layout").ToModel();
        Assert.Empty(model.OversizedSections);
        ContextMenuEntry[] entries = model.Entries.ToArray();
        Assert.Equal(ContextMenuEntryKind.Note, entries[0].Kind); // the title names the mode
        Assert.Equal(menu.Title, entries[0].Label);
        ContextMenuEntry[] rows = entries.Where(entry => entry.Kind != ContextMenuEntryKind.Note).ToArray();
        Assert.Equal(menu.Interactive.Select(row => row.Id), rows.Select(entry => entry.Id));
        foreach (LayoutMenuRow row in menu.Interactive)
        {
            ContextMenuEntry entry = rows.Single(item => item.Id == row.Id);
            Assert.Equal(row.Label, entry.Label);
            Assert.Equal(row.Enabled, entry.Available);
            Assert.Equal(row.DisabledReason, entry.Reason);
            Assert.Equal(row.Kind switch
            {
                LayoutMenuRowKind.Toggle => ContextMenuEntryKind.Check,
                LayoutMenuRowKind.Choice => ContextMenuEntryKind.Radio,
                _ => ContextMenuEntryKind.Command
            }, entry.Kind);
            if (row.Kind != LayoutMenuRowKind.Action) Assert.Equal(row.Checked, entry.Checked);
        }
        Assert.Equal(GalleryLayoutMenu.DockGroup, rows.Single(entry => entry.Id == "gallery.layout.filmstrip-top").Group);
        // The reset sits in a section of its own, after the blocks.
        Assert.Equal(["gallery.layout.reset"], model.Sections[^1].Select(entry => entry.Id));
        // Choosing an entry runs the row's action (here recording its id).
        rows.Single(entry => entry.Id == "gallery.layout.tools").Invoke!();
        rows.Single(entry => entry.Id == "gallery.layout.reset").Invoke!();
        Assert.Equal(["gallery.layout.tools", "gallery.layout.reset"], chosen);
        Assert.NotNull(LayoutMenuBuilder.ToContextMenu(menu, _ => { }, "Layout").Build());
    }

    [Fact]
    public void ADisabledSwitchStaysVisibleWithItsStateInTheAvaloniaMenu()
    {
        ContextMenu menu = new ContextMenuBuilder()
            .Check("a", "A", true, () => { }, available: false, reason: "Because.")
            .Radio("g", "g.1", "One", true, () => { }, available: false, reason: "Not now.")
            .Check("b", "B", false, () => { })
            .Build()!;
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.False(items[0].IsEnabled);
        Assert.True(items[0].IsChecked);
        Assert.Equal(MenuItemToggleType.CheckBox, items[0].ToggleType);
        Assert.False(items[1].IsEnabled);
        Assert.True(items[1].IsChecked);
        Assert.Equal(MenuItemToggleType.Radio, items[1].ToggleType);
        Assert.True(items[2].IsEnabled);
    }

    // ---- wiring, commands, policy, texts -------------------------------------------------------------------------------------------

    [Fact]
    public void TheLayoutCommandsExistInTheGalleryScopeAndAreUnbound()
    {
        foreach (string id in new[] { "gallery.layout-menu", "gallery.layout.tools", "gallery.layout.info", "gallery.layout.hints",
                     "gallery.layout.filmstrip-top", "gallery.layout.filmstrip-bottom", "gallery.layout.reset" })
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.Equal(CommandScope.Gallery, command.Scope);
            Assert.Null(command.Shortcut); // plan §2.4: no catalog key; the gallery key scheme (revision 13) gives info and hints a yielding default
            Assert.Contains("gallery-keyboard", command.Contexts);
            if (id is "gallery.layout.info" or "gallery.layout.hints")
                Assert.Equal(GalleryShortcutScheme.LayoutDefaults[id], Assert.Single(command.DefaultGestures).Display(ShortcutPlatform.Linux));
            else
                Assert.Empty(command.DefaultGestures);
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)));
        }
        // The rows' ids are the command ids; the filmstrip's switch shows the filmstrip command and the tools row the hide-tools switch (Shift+H).
        foreach (string id in GalleryLayoutMenu.RowIds.Where(id => id != GalleryLayoutMenu.FilmstripId && id != GalleryLayoutMenu.ToolsId))
            Assert.Equal(id, GalleryLayoutMenu.CommandOf(id));
        Assert.Equal("gallery.filmstrip-toggle", GalleryLayoutMenu.CommandOf(GalleryLayoutMenu.FilmstripId));
        Assert.Equal("gallery.tools-toggle", GalleryLayoutMenu.CommandOf(GalleryLayoutMenu.ToolsId));
        // Only the two scheme defaults (revision 13) yield; the other layout commands stay unbound.
        Assert.Equal(new[] { "gallery.layout.hints", "gallery.layout.info" },
            EffectiveShortcutBindings.YieldingDefaultCommandIds.Where(id => id.StartsWith("gallery.layout", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLayoutButtonIsOnTheBarInAdvancedAndASectionOfMoreInTheOtherViews()
    {
        WorkViewEntry row = WorkViewPolicy.Get("gallery.toolbar.layout");
        Assert.Equal(WorkViewPage.Gallery, row.Page);
        Assert.Equal("gallery.layout-menu", row.CommandId);
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("gallery.toolbar.layout", WorkView.Advanced));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("gallery.toolbar.layout", WorkView.Simple));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("gallery.toolbar.layout", WorkView.Barebones));
        Assert.Contains("gallery.toolbar.layout", GalleryChromePlan.Priority);
        Assert.True(Array.IndexOf(GalleryChromePlan.Priority.ToArray(), "gallery.toolbar.layout") > Array.IndexOf(GalleryChromePlan.Priority.ToArray(), "gallery.toolbar.tools-toggle"));
    }

    [Theory]
    [InlineData(WorkView.Advanced, 1920, true)]
    [InlineData(WorkView.Advanced, 1100, true)]
    [InlineData(WorkView.Advanced, 1099, false)]   // below CompactWidth every view has the section
    [InlineData(WorkView.Advanced, 760, false)]
    [InlineData(WorkView.Simple, 1920, false)]
    [InlineData(WorkView.Barebones, 1920, false)]
    public void ThePlanPlacesTheLayoutButtonOnTheBarOnlyInAdvancedFromCompactWidthUp(WorkView view, double width, bool onBar)
    {
        GalleryChromePlan plan = GalleryChromePlan.Resolve(new GalleryChromeInput(GalleryChromeMode.View, view, GalleryLayoutChoice.Default,
            false, false, false, false, false, false, width, 900));
        Assert.Equal(onBar, plan.LayoutOnBar);
        Assert.True(plan.More); // the section lives under "More", which never goes
    }

    [Fact]
    public void TheHiddenBarTakesTheLayoutButtonAwayLikeEveryOtherTool()
    {
        GalleryChromePlan plan = GalleryChromePlan.Resolve(new GalleryChromeInput(GalleryChromeMode.View, WorkView.Advanced, GalleryLayoutChoice.Default,
            false, true, true, false, false, false, 1920, 900));
        Assert.False(plan.LayoutOnBar);
    }

    [Fact]
    public void TheViewerWiresTheButtonBesideMoreTheContextRequestsAndTheCommands()
    {
        string viewer = Source("Desktop/GalleryViewer.cs"), menu = Source("Desktop/GalleryViewer.LayoutMenu.cs"), ids = Source("Desktop/GalleryViewer.WorkView.cs");
        Assert.Contains("view.Children.Add(_overflowButton);\n        view.Children.Add(_layoutButton);", viewer.Replace("\r\n", "\n"));
        Assert.Contains("InitializeLayoutMenu();", viewer);
        Assert.Contains("HandleLayoutKey(e)", viewer);
        Assert.Contains("WorkViewElement.SetId(_layoutButton, \"gallery.toolbar.layout\"); Mark(_layoutButton);", ids);
        // A right click on free area of the dock, the footer and the filmstrip band; the picture keeps the media menu.
        Assert.Contains("new Control[] { _dockStack, _overlayFooter, _filmstrip }", menu);
        Assert.Contains("ContextRequestedEvent, OnDockContextRequested", menu);
        Assert.DoesNotContain("_viewport.ContextRequested", menu);
        // The flyout and the context menu come from the same model; ApplyChrome stays the one place that applies visibility.
        Assert.Contains("LayoutMenuBuilder.ToContextMenu(LayoutMenuNow()", menu);
        Assert.Contains("new LayoutMenuPanel(\"GalleryLayoutMenu\", LayoutMenuNow, ApplyLayoutRow)", menu);
        Assert.Contains("ChangeLayoutChoice(mode, next)", menu);
        Assert.Contains("ResetLayout(mode)", menu);
        foreach (string block in new[] { "_chrome.IsVisible", "_infoOverlay.IsVisible", "_filmstrip.IsVisible", "_dockTransportHost.IsVisible", "_quality.IsVisible" })
            Assert.DoesNotContain(block, menu);
        // The window offers the undo of a reset.
        Assert.Contains("LayoutResetPerformed += (mode, previous) => OnGalleryLayoutReset(preferenceOwner, mode, previous)", Source("Desktop/Shell/MainWindow.Gallery.cs"));
        Assert.Contains("owner.ChangeLayoutChoice(mode, previous)", Source("Desktop/Shell/MainWindow.GalleryLayoutUndo.cs"));
    }

    [Fact]
    public void TheRowsAndTheirReasonsAreTranslatedInEveryLanguage()
    {
        string[] keys = ["gallery.layout.button", "gallery.layout.help", "gallery.layout.mode", "gallery.layout.filmstrip", "gallery.layout.filmstripTop",
            "gallery.layout.filmstripBottom", "gallery.layout.tools", "gallery.layout.info", "gallery.layout.hints", "gallery.layout.reset",
            "gallery.layout.resetHelp", "gallery.layout.resetDone", "gallery.layout.whyFocus", "gallery.layout.whyLow", "gallery.layout.whyTag",
            "gallery.layout.whyPanel", "gallery.layout.whyFilmstripOff", "gallery.layout.whyDefault", "gallery.layout.hintOwn", "gallery.layout.hintView"];
        string english = Texts.T("gallery.layout.mode", "X");
        Assert.Equal("Layout for the mode: X", english);
        foreach (string code in Texts.BundledPackCodes().Append("de"))
        {
            Texts.SetLanguage(code);
            foreach (string key in keys) Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)), code + " " + key);
            // The modes' names are substituted, never left as a placeholder.
            Assert.DoesNotContain("{0}", Texts.T("gallery.layout.mode", "X"));
            Assert.Contains("X", Texts.T("gallery.layout.mode", "X"));
            Assert.Contains("X", Texts.T("gallery.layout.resetDone", "X"));
        }
        Texts.SetLanguage("en");
    }

    [Fact]
    public void TheSlideshowCommandHasPlayAndPauseMasks()
    {
        Assert.Equal("play", MeowChan.Desktop.Commands.CommandCatalog.IconForState("gallery.slideshow-toggle", "stopped"));
        Assert.Equal("pause", MeowChan.Desktop.Commands.CommandCatalog.IconForState("gallery.slideshow-toggle", "running"));
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));
}
