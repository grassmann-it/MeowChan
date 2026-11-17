using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.DesktopTests;

// Interaction audit 1.1.0 (.claude/coordination/INTERAKTIONS-AUDIT-1.1.0-2026-11-15.md): one rule for list, grid and card surfaces.
// A double click is the same primary action as Enter, a right-click opens the same menu as the Menu key and Shift+F10, a double click
// on an interactive part of an item keeps that part's meaning. The guards are pure and tested on real controls; the wiring of each
// page is a GUI control, so its source is the contract here, as in the other page tests.
[Collection("Localization")]
public sealed class InteractionAuditTests
{
    private static string Read(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // ---- the shared guard ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ADoubleClickOnAnInteractivePartOfAnItemIsNotTheItemsPrimaryAction()
    {
        // Panels and decorators give their children a visual parent without a window (templates are not applied here).
        TextBlock label = new();
        Button star = new();
        ToggleSwitch toggle = new();
        SelectableTextBlock words = new();
        TextBox field = new();
        TextBlock inField = new();
        Border fieldFrame = new() { Child = inField };
        StackPanel content = new() { Children = { label, star, toggle, words, field } };
        StackPanel surface = new() { Children = { content } };
        Assert.False(ItemActivation.IsOwnedByInteractivePart(label, surface));
        Assert.True(ItemActivation.IsOwnedByInteractivePart(star, surface));
        Assert.True(ItemActivation.IsOwnedByInteractivePart(toggle, surface));
        Assert.True(ItemActivation.IsOwnedByInteractivePart(words, surface), "a double click in selectable text selects a word");
        Assert.True(ItemActivation.IsOwnedByInteractivePart(field, surface));
        Assert.False(ItemActivation.IsOwnedByInteractivePart(inField, fieldFrame));
        Assert.False(ItemActivation.IsOwnedByInteractivePart(null, surface));
        Assert.False(ItemActivation.IsOwnedByInteractivePart(surface, surface));
    }

    [Fact]
    public void TheGuardStopsAtTheSurfaceAndCountsAThumbOrSplitterAsInteractive()
    {
        // Above the surface nothing counts: the walk stops at the card the primary action belongs to.
        TextBlock text = new();
        Border card = new() { Child = text };
        Assert.False(ItemActivation.IsOwnedByInteractivePart(text, card));
        Assert.False(ItemActivation.IsOwnedByInteractivePart(text, null));
        // A drag handle is never a double click on the item.
        Avalonia.Controls.Primitives.Thumb thumb = new();
        GridSplitter splitter = new();
        StackPanel host = new() { Children = { thumb, splitter } };
        Assert.True(ItemActivation.IsOwnedByInteractivePart(thumb, host));
        Assert.True(ItemActivation.IsOwnedByInteractivePart(splitter, host));
    }

    [Fact]
    public void TheItemUnderTheSourceIsTheDataOfTheNearestRowAndEmptySpaceHasNone()
    {
        // Without a window the item's template (and the visual path from its content to it) does not exist; the item itself is the source.
        ListBoxItem item = new() { DataContext = "row-1" };
        StackPanel host = new() { Children = { item } };
        Border outside = new() { DataContext = "not-a-row" };
        host.Children.Add(outside);
        Assert.Equal("row-1", ItemActivation.ItemAt<string>(item, host));
        Assert.Same(item, ItemActivation.ContainerAt(item, host));
        // Empty space and a control that carries data but is no row have no item.
        Assert.Null(ItemActivation.ItemAt<string>(host, host));
        Assert.Null(ItemActivation.ItemAt<string>(outside, host));
        Assert.Null(ItemActivation.ContainerAt(host, host));
        // A row of another data type is not this surface's item.
        Assert.Null(ItemActivation.ItemAt<Uri>(item, host));
    }

    [Fact]
    public void EnterWithoutModifiersIsThePrimaryKeyAndNothingElse()
    {
        Assert.True(ItemActivation.IsPrimaryKey(new KeyEventArgs { Key = Key.Enter }));
        Assert.True(ItemActivation.IsPrimaryKey(new KeyEventArgs { Key = Key.Return }));
        Assert.False(ItemActivation.IsPrimaryKey(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Shift }));
        Assert.False(ItemActivation.IsPrimaryKey(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Alt }));
        Assert.False(ItemActivation.IsPrimaryKey(new KeyEventArgs { Key = Key.Space }));
        Assert.False(ItemActivation.IsPrimaryKey(new KeyEventArgs { Key = Key.Enter, Handled = true }));
    }

    [Fact]
    public void OnlyCtrlTurnsTheWheelIntoZoom()
    {
        Assert.True(ItemActivation.IsZoomWheel(KeyModifiers.Control));
        Assert.False(ItemActivation.IsZoomWheel(KeyModifiers.None));
        Assert.False(ItemActivation.IsZoomWheel(KeyModifiers.Shift));
        Assert.False(ItemActivation.IsZoomWheel(KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(OperatingSystem.IsMacOS(), ItemActivation.IsZoomWheel(KeyModifiers.Meta));
    }

    [Fact]
    public void WheelStepsCountNotchesAndAddUpTouchpadDeltas()
    {
        WheelSteps steps = new();
        DateTime now = new(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, steps.Add(1, now));
        Assert.Equal(-1, steps.Add(-1, now.AddMilliseconds(10)));
        // A touchpad: small deltas add up to one step.
        Assert.Equal(0, steps.Add(0.4, now.AddMilliseconds(20)));
        Assert.Equal(0, steps.Add(0.4, now.AddMilliseconds(30)));
        Assert.Equal(1, steps.Add(0.4, now.AddMilliseconds(40)));
        // A change of direction drops the rest; a pause starts a new gesture.
        Assert.Equal(0, steps.Add(-0.5, now.AddMilliseconds(50)));
        Assert.Equal(0, steps.Add(0.6, now.AddMilliseconds(50) + WheelSteps.Pause + TimeSpan.FromMilliseconds(1)));
        Assert.Equal(0, steps.Add(0, now));
        Assert.Equal(0, steps.Add(double.NaN, now));
    }

    [Fact]
    public void TheTileSizeStepsWithinSmallMediumAndLargeAndStaysAtTheEnds()
    {
        Assert.Equal(MediaTileSize.Large, MediaPage.StepTileSize(MediaTileSize.Medium, 1));
        Assert.Equal(MediaTileSize.Small, MediaPage.StepTileSize(MediaTileSize.Medium, -1));
        Assert.Equal(MediaTileSize.Large, MediaPage.StepTileSize(MediaTileSize.Large, 1));
        Assert.Equal(MediaTileSize.Small, MediaPage.StepTileSize(MediaTileSize.Small, -3));
        // Several notches at once still move one size: no setting beyond the three of the Layout menu.
        Assert.Equal(MediaTileSize.Large, MediaPage.StepTileSize(MediaTileSize.Medium, 5));
        Assert.Equal(MediaTileSize.Medium, MediaPage.StepTileSize(MediaTileSize.Medium, 0));
    }

    // ---- the new defaults (revision 16) --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TheAuditsDefaultsUsePrimaryAndYieldToSavedBindings(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 16);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        string Keys(string id) => string.Join(" ", bindings.For(id).Select(g => string.Join("+", g.Modifiers.Append(g.Key))));
        Assert.Equal("Primary+OemPlus Primary+Add", Keys("media.tile-size.larger"));
        Assert.Equal("Primary+OemMinus Primary+Subtract", Keys("media.tile-size.smaller"));
        Assert.Equal("Primary+C", Keys("diagnostics.copy"));
        Assert.Equal("Primary+C", Keys("statistics.copy-row"));
        foreach (string id in new[] { "media.tile-size.larger", "media.tile-size.smaller", "diagnostics.copy", "statistics.copy-row" })
        {
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            Assert.True(EffectiveShortcutBindings.YieldsOn(id, platform), id);
        }
        // Each runs only where its list is: the media page, the Diagnostics table, the Statistics table (never next to Copy link(s)).
        // (The tagging grid shares the media page's tile size, so the commands also run there: the "tagging" context.)
        Assert.Equal(new[] { "media", "tagging" }, EffectiveShortcutBindings.ExecutionContexts("media.tile-size.larger"));
        Assert.Equal(new[] { "media", "tagging" }, EffectiveShortcutBindings.ExecutionContexts("media.tile-size.smaller"));
        Assert.Equal(new[] { "diagnostics" }, EffectiveShortcutBindings.ExecutionContexts("diagnostics.copy"));
        Assert.Equal(new[] { "statistics" }, EffectiveShortcutBindings.ExecutionContexts("statistics.copy-row"));
    }

    [Fact]
    public void ANewDefaultGivesWayToAnOwnBindingOnTheSameKeys()
    {
        // A saved Primary+Plus of another media command keeps working; the new default yields instead of silencing both.
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("media.select-all",
            new[] { ShortcutGesture.Parse("Primary+Plus") });
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "media.tile-size.larger" && issue.OtherCommandId == "media.select-all");
        Assert.Single(bindings.For("media.select-all"));
        // The tile size also runs in the tagging grid, where nothing has that key: the default gives way on the media page only (Opus review 15.11.).
        Assert.Contains(bindings.For("media.tile-size.larger"), gesture => gesture.Key == "Add");
        ShortcutGesture plus = Assert.Single(bindings.For("media.tile-size.larger"), gesture => gesture.Key != "Add");
        Assert.Equal(new[] { "media" }, bindings.YieldedContexts("media.tile-size.larger", plus));
        Assert.False(bindings.Matches(new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.OemPlus, KeyModifiers = Avalonia.Input.KeyModifiers.Control }, "media.tile-size.larger", "media"));
        Assert.True(bindings.Matches(new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.OemPlus, KeyModifiers = Avalonia.Input.KeyModifiers.Control }, "media.tile-size.larger", "tagging"));
    }

    // ---- routing per surface: double click and Enter reach the same primary action -------------------------------------------

    [Fact]
    public void MediaTilesOpenTheGalleryAtTheDoubleClickedTileLikeEnter()
    {
        string interaction = Read("Desktop/Views/Media/MediaPage.Interaction.cs");
        string page = Read("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("_grid.DoubleTapped += OnTileDoubleTapped;", interaction);
        Assert.Contains("ItemActivation.IsItemDoubleClick(e, _grid) || RowAt(e.Source) is not { } row", interaction);
        Assert.Contains("OpenActions.Start(() => OpenTileAsync(row), \"media.grid.doubleClick\");", interaction);
        // The window around the tile is the one Enter on one selected tile opens.
        Assert.Contains("MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index)", interaction);
        Assert.Contains("MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index)", page);
        Assert.Contains("if (_grid.Selection.Count > 1) await OpenSelectionGalleryAsync(); else await OpenSelectedAsync();", page);
        // The old handler opened whatever was selected, also from the star or from empty space.
        Assert.DoesNotContain("_grid.DoubleTapped += (_, _) => _ = OpenSelectedAsync();", page);
        Assert.Contains("InstallTileInteraction();", page);
    }

    [Fact]
    public void CtrlWheelAndCtrlPlusMinusStepTheSavedTileSizeOfTheLayoutMenu()
    {
        string interaction = Read("Desktop/Views/Media/MediaPage.Interaction.cs");
        string input = Read("Desktop/Controls/TileSizeInput.cs");
        // The media page uses the shared tile size input (also used by the tagging grid and the archive's media tiles).
        Assert.Contains("TileSizeInput.Install(_grid, TileSizeCommands.Media, () => _layout.TileSize, ApplyTileSize);", interaction);
        Assert.Contains("surface.AddHandler(InputElement.PointerWheelChangedEvent, input.OnWheel, RoutingStrategies.Tunnel);", input);
        Assert.Contains("ItemActivation.IsZoomWheel(e.KeyModifiers)", input);
        Assert.Contains("ApplyLayout(_layout with { TileSize = size });", interaction);
        Assert.Contains("CommandRouter.IsNativeInput(e.Source, e.Key)", input);
        Assert.Equal("media.tile-size.larger", MediaPage.TileSizeLargerCommandId);
        Assert.Equal("media.tile-size.smaller", MediaPage.TileSizeSmallerCommandId);
        Assert.Equal("media.tile-size.reset", MediaPage.TileSizeResetCommandId);
        Assert.Equal(MediaPage.TileSizeLargerCommandId, TileSizeCommands.Media.Larger);
    }

    [Fact]
    public void TablesAndListsRouteTheirDoubleClickAndEnterToOneAction()
    {
        // Diagnostics: Show in Downloads for a row with a target, from both gestures; Ctrl+C is "Copy details".
        string diagnostics = Read("Desktop/Views/Diagnostics/DiagnosticsPage.Interaction.cs");
        Assert.Contains("grid.DoubleTapped += OnDiagnosticRowDoubleTapped;", diagnostics);
        Assert.Equal(2, Count(diagnostics, "OpenFrozenDiagnostic(entry);"));
        Assert.Contains("ItemActivation.IsPrimaryKey(e)", diagnostics);
        Assert.Contains("CommandBindings.Matches(e, \"diagnostics.copy\")", diagnostics);
        Assert.Contains("InitializeRowInteraction();", Read("Desktop/Views/Diagnostics/DiagnosticsPage.axaml.cs"));
        // Shortcuts window: a double click on a command row records like Enter. The Enter half is a behaviour test now
        // (ListItemEnterParityTests: Enter raised on the row's own container starts the recording); here only the double click wiring.
        Assert.Contains("_actions.DoubleTapped += (_, e) =>", Read("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs"));
        // Tagging list and grid: continue here, the Enter command of the list.
        Assert.Equal("Enter", CommandCatalog.Get("tagging.continue-here").Shortcut);
        Assert.Contains("ExecuteRowCommandAsync(TaggingCommands.ContinueHere, explain: true)", Read("Desktop/Views/Tagging/TaggingPage.Grid.cs"));
        Assert.Contains("ExecuteRowCommandAsync(\"tagging.continue-here\", explain: true)", Read("Desktop/Views/Tagging/TaggingPage.Commands.cs"));
        // Archive media list and tiles: the gallery at the asset, from both gestures.
        Assert.Equal(2, Count(Read("Desktop/Views/Archive/ArchivePage.Gallery.cs"), "OpenAssetGalleryAsync(row, e.Source as Control), \"archive.media.open\""));
        // Sources watch list and Downloads boards: only on a row, never on empty space.
        Assert.Contains("ItemActivation.IsItemDoubleClick(e, list) && SelectedWatch != null", Read("Desktop/Views/Sources/SourcesPage.axaml.cs"));
        Assert.Contains("ItemActivation.IsItemDoubleClick(e, grid)) return;", Read("Desktop/Shell/MainWindow.axaml.cs"));
    }

    [Fact]
    public void SidebarSingleClickDoesNotOpenGalleryUnlessConfiguredAndDoubleClickStillDoes()
    {
        string sidebar = Read("Desktop/Views/Archive/ReaderMediaSidebar.cs");
        Assert.Contains("_frame.PointerReleased", sidebar);
        Assert.Contains("_frame.DoubleTapped", sidebar);
        Assert.Contains("GalleryRequested?.Invoke(this, EventArgs.Empty);", sidebar);

        string archive = Read("Desktop/Views/Archive/ArchivePage.InlineVideo.cs");
        Assert.Contains("if (ReaderMediaSidebarRules.OpensGalleryOnPictureClick(_inlineVideo.Options)) OpenMediaSidebarMedia();", archive);

        string media = Read("Desktop/Views/Media/MediaPage.Panel.cs");
        Assert.Contains("if (ReaderMediaSidebarRules.OpensGalleryOnPictureClick(_panelVideoController.Options)) StartOpenPanelMedia();", media);
    }

    [Fact]
    public void GalleryDoubleClickEntersWindowFullscreen()
    {
        string transform = Read("Desktop/GalleryViewer.ViewTransform.cs");
        string viewer = Read("Desktop/GalleryViewer.cs");
        // Double-click enters window fullscreen; Fit/1:1 remain explicit toolbar commands.
        Assert.Contains("FullscreenRequested?.Invoke();", transform);
        Assert.Contains("_viewport.DoubleTapped += OnViewDoubleTapped;", viewer);
        Assert.Contains("GalleryFullscreen", viewer);
        Assert.Contains("FullscreenRequested", viewer);
    }

    [Fact]
    public void CardsRunTheirFirstButtonOnADoubleClick()
    {
        string rules = Read("Desktop/Views/Rules/RulesPage.Overview.cs");
        Assert.Contains("card.DoubleTapped += (_, e) =>", rules);
        Assert.Contains("ItemActivation.IsCardDoubleClick(e, card) || !IsEditable || _wizard.IsOpen", rules);
        // The card's Edit/Continue button and its double click open the same scope.
        Assert.True(Count(rules, "OpenRuleFromOverview(scope)") >= 3);
        // Booru hits (decision 95): the double click and Enter run "Show in Media" for a saved hit and nothing else (no download).
        string booru = Read("Desktop/Views/Booru/BooruWorkbenchView.Hits.cs");
        Assert.Contains("ItemActivation.IsCardDoubleClick(e, card)", booru);
        Assert.Contains("showInMedia is { IsEffectivelyEnabled: true } ? showInMedia : null", booru);
        Assert.Contains("ItemActivation.IsCardDoubleClick(e, card)", booru.Substring(booru.IndexOf("ConfigureWatcherCard", StringComparison.Ordinal)));
        string sidebar = Read("Desktop/Views/Archive/ReaderMediaSidebar.cs");
        Assert.Contains("StripActivated?.Invoke(this, index);", sidebar);
        Assert.Contains("sidebar.StripActivated += (_, index) => { SelectMediaSidebarIndex(index); OpenMediaSidebarMedia(); };",
            Read("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs"));
    }

    // ---- context menu parity: right-click, Menu key, Shift+F10 and the rebindable command ----------------------------------

    [Fact]
    public void TheMenuKeyShiftF10AndTheContextCommandAreOneGesture()
    {
        Assert.True(MediaMenuPresenter.IsContextKey(new KeyEventArgs { Key = Key.Apps }));
        Assert.True(MediaMenuPresenter.IsContextKey(new KeyEventArgs { Key = Key.F10, KeyModifiers = KeyModifiers.Shift }));
        Assert.False(MediaMenuPresenter.IsContextKey(new KeyEventArgs { Key = Key.F10 }));
        Assert.False(MediaMenuPresenter.IsContextKey(new KeyEventArgs { Key = Key.Apps, Handled = true }));
        // In a text field the keys stay the field's own.
        Assert.False(MediaMenuPresenter.IsContextKey(new KeyEventArgs { Key = Key.Apps, Source = new TextBox() }));
        Assert.Contains("Apps", EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux).For("menu.context-open").Select(g => g.Key));
    }

    [Fact]
    public void AContextMenuHostClosesWhenItsTargetIsRebuilt()
    {
        string host = Read("Desktop/Controls/ContextMenuHost.cs");
        // Result cards and virtualized rows may be detached while a popup is open. The popup must not retain
        // button handlers for the old card and must unregister the lifecycle hook when disposed.
        Assert.Contains("origin.DetachedFromVisualTree += OnOriginDetached;", host, StringComparison.Ordinal);
        Assert.Contains("private void OnOriginDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close();", host, StringComparison.Ordinal);
        Assert.Contains("_origin.DetachedFromVisualTree -= OnOriginDetached;", host, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryContextMenuHostOpensFromThePointerAndFromTheKeyboard()
    {
        string host = Read("Desktop/Controls/ContextMenuHost.cs");
        // Lists and tables take the key in the tunnel phase (a focused item consumes keys before a bubble handler); other origins keep the bubble phase.
        Assert.Contains("origin.AddHandler(InputElement.KeyDownEvent, OnKeyDown, (origin is ListBox or DataGrid) ? RoutingStrategies.Tunnel : RoutingStrategies.Bubble);", host);
        Assert.Contains("origin.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);", host);
        Assert.Contains("MediaMenuPresenter.IsContextKey(e)", host);
        // The surfaces of the audit that use the host: tagging list and grid, rules cards, statistics table and compact list.
        Assert.Contains("new ContextMenuHost(_list, ComposeRowMenu", Read("Desktop/Views/Tagging/TaggingPage.Commands.cs"));
        Assert.Contains("new ContextMenuHost(_grid, ComposeRowMenu", Read("Desktop/Views/Tagging/TaggingPage.Grid.cs"));
        Assert.Contains("new ContextMenuHost(card,", Read("Desktop/Views/Rules/RulesPage.RowMenu.cs"));
        string statistics = Read("Desktop/Views/Statistics/StatisticsPage.RowMenu.cs");
        Assert.Contains("new ContextMenuHost(StatisticsGrid,", statistics);
        Assert.Contains("CommandBindings.Matches(e, StatisticsRowMenu.CopyRow)", statistics);
        // The media grid has its own presenter with the same three ways in.
        string media = Read("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("_grid.AddHandler(ContextRequestedEvent, OnSharedContextRequested, RoutingStrategies.Bubble);", media);
        Assert.Contains("_grid.AddHandler(KeyDownEvent, OnSharedContextKeyDown, RoutingStrategies.Tunnel);", media);
    }

    // ---- open actions nobody awaits: one safe helper (Tom's crash log, reader attachment click) -----------------------------

    [Fact]
    public async Task AFailedOpenIsRecoveredOnceAndACancellationIsNoFailure()
    {
        List<(Exception Error, string Operation)> recovered = new();
        bool Recover(Exception error, string operation) { recovered.Add((error, operation)); return true; }
        await OpenActions.RunAsync(() => throw new InvalidOperationException("sync"), "test.sync", Recover);
        await OpenActions.RunAsync(async () => { await Task.Yield(); throw new IOException("async"); }, "test.async", Recover);
        await OpenActions.RunAsync(() => Task.FromCanceled(new CancellationToken(true)), "test.cancel", Recover);
        await OpenActions.RunAsync(() => Task.CompletedTask, "test.ok", Recover);
        Assert.Equal(new[] { "test.sync", "test.async" }, recovered.Select(item => item.Operation));
        Assert.IsType<InvalidOperationException>(recovered[0].Error);
        Assert.IsType<IOException>(recovered[1].Error);
    }

    [Fact]
    public async Task AFailureThatIsNotRecoveredKeepsPropagatingSoSmokesFail()
    {
        // Without an installed handler (tests, smokes) TryRecover refuses: the error is not swallowed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenActions.RunAsync(() => throw new InvalidOperationException(), "test.refused",
            (_, _) => false));
    }

    [Fact]
    public void OpenActionsOfClicksDoubleClicksKeysAndMenusNeverFireAndForgetAnUnobservedTask()
    {
        string archive = Read("Desktop/Views/Archive/ArchivePage.axaml.cs");
        // The crash: OpenPreview is an Action, an async lambda there was an async void without a catch.
        Assert.DoesNotContain("OpenPreview = async", archive);
        Assert.Equal(2, Count(archive, "OpenPreview = () => OpenActions.Start("));
        string gallery = Read("Desktop/Views/Archive/ArchivePage.Gallery.cs");
        Assert.DoesNotContain("        await LoadThreadAsync(row.Summary.Id);", gallery);
        Assert.Equal(2, Count(gallery, "await OpenActions.RunAsync(() => OpenAssetGalleryAsync(row, e.Source as Control), \"archive.media.open\");"));
        Assert.Contains("OpenActions.RunAsync(() => OpenReaderOnThreadAsync(id, () => OpenReaderRequested?.Invoke(list)), \"archive.list.read\")", Read("Desktop/Views/Archive/ArchivePage.Selection.cs"));
        string commands = Read("Desktop/Views/Archive/ArchivePage.Commands.cs");
        Assert.DoesNotContain("Add(\"thread.read\", async", commands);
        Assert.DoesNotContain("Add(\"gallery.open\", async", commands);
        string menus = Read("Desktop/Shell/MainWindow.Menus.cs");
        Assert.DoesNotContain("Add(\"thread.read\", async", menus);
        Assert.DoesNotContain("Add(\"gallery.open\", async", menus);
        Assert.Contains("OpenActions.RunAsync(() => OpenDownloadReaderAsync(row.Snapshot.Id, target)", Read("Desktop/Shell/MainWindow.axaml.cs"));
        string selection = Read("Desktop/Shell/MainWindow.Selection.cs");
        Assert.Contains("OpenActions.RunAsync(() => OpenGalleryAsync(row.Snapshot.Id, origin: grid)", selection);
        Assert.Contains("OpenActions.RunAsync(() => OpenDownloadReaderAsync(row.Snapshot.Id, grid)", selection);
        string shellGallery = Read("Desktop/Shell/MainWindow.Gallery.cs");
        Assert.DoesNotContain("_ = ReadGalleryPostAsync(entry)", shellGallery);
        Assert.DoesNotContain("_ = ReadEmptyGalleryPostsAsync()", shellGallery);
        // Media page and tagging page: no "_ = Open…"/"_ = ExecuteRowCommandAsync" left for the open paths.
        Assert.Contains("OpenActions.Start(() => OpenTileAsync(row)", Read("Desktop/Views/Media/MediaPage.Interaction.cs"));
        Assert.Contains("OpenActions.Start(OpenSelectionGalleryAsync", Read("Desktop/Views/Media/MediaPage.Selection.cs"));
        foreach (string file in new[] { "TaggingPage.Commands.cs", "TaggingPage.Grid.cs", "TaggingPage.Selection.cs", "TaggingPage.cs" })
        {
            string text = Read("Desktop/Views/Tagging/" + file);
            Assert.DoesNotContain("_ = ExecuteRowCommandAsync(", text);
            Assert.DoesNotContain("_ = StartAsync(", text);
        }
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
