using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_TAGGING_PAGE_ONLY. PT2: the tagging page in the real main window over the real host and tag-queue.json
// (three synthetic queue entries without files): the footer button opens it, progress and start label, Delete and Alt+Down from
// the list, the context menu, the stamp editor, the state filter, the clear question, the three work views and Light/Dark/English/
// German captures. The explicit gallery command uses a separate pair of archived images.
internal static class TaggingPageChecks
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");

    private static TagQueueItem Item(int n) => new(Thread, "/media/123", "asset-" + n, n.ToString("x64"), 100 + n, "media-" + n,
        "original", "p" + n, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open,
        new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero).AddSeconds(n));

    internal static async Task RunAsync(MainWindow window)
    {
        string language = Texts.Language;
        string mode = window.SessionHost.Config.WorkspaceMode;
        try
        {
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
            await window.SessionHost.SaveViewPreferencesAsync(config => config.TaggingStampBlock = "Open");
            await window.SessionHost.EnqueueTagQueueAsync(new[] { Item(1), Item(2), Item(3) });

            // The page has a footer button behind Media (its digit follows the visible footer buttons: Ctrl+4 in the default footer) and an entry
            // in the footer's More menu. Since F1 the ⋯ menu lists no pages.
            Button footerButton = window.FindControl<Button>("TaggingNavigationButton") ?? throw new InvalidOperationException("The tagging page has no footer button.");
            Require(Avalonia.Automation.AutomationProperties.GetName(footerButton) == "Tagging" && ToolTip.GetTip(footerButton) is string footerTip
                && footerTip.StartsWith("Tagging", StringComparison.Ordinal), "The tagging footer button lost its accessible name or tooltip.");
            MenuItem? moreEntry = SmokeNavigation.MoreEntry(window, MainWindow.WorkspacePage.Tagging);
            Require(moreEntry != null && moreEntry.Tag as string == "nav.tagging" && Equals(moreEntry.Header, "Tagging"),
                "The More entry of the tagging page is missing.");
            Require(window.FindControl<MenuItem>("TaggingMenuItem") == null
                && window.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.OfType<MenuItem>().All(item => item.Tag as string != "nav.tagging"),
                "The ⋯ menu still lists the tagging page.");
            Require(MeowChan.Desktop.Commands.MainPageOrder.Digit("navigation.tagging") == 4,
                "The tagging page is not the fourth footer button of the default footer.");
            Require(footerButton.IsEnabled, "The footer button of the tagging page is disabled.");
            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Tagging);
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Tagging && window.TaggingPage is { IsBusy: false } page
                && page.Rows.Count == 3, () => "page " + window.SelectedPage + ", rows " + window.TaggingPage?.Rows.Count);
            TaggingPage tagging = window.TaggingPage!;
            await SettleAsync(window);
            CheckProgress(tagging, open: 3, finished: 0);
            await Program.WaitUntilAsync(() => tagging.StartButton.IsEffectivelyEnabled
                && Avalonia.Automation.AutomationProperties.GetName(tagging.StartButton) == Texts.T("command.tagging.start"),
                () => "start enabled=" + tagging.StartButton.IsEffectivelyEnabled + ", name='"
                    + Avalonia.Automation.AutomationProperties.GetName(tagging.StartButton) + "', host=" + window.SessionHost.State);
            Require(tagging.StartButton.IsFocused && tagging.SelectedCount == 0, "First opening did not focus Start with an empty selection.");
            Require(AutomationProperties.GetHelpText(tagging.StartButton)?.Contains(Texts.T("tagging.page.start.space"), StringComparison.Ordinal) == true,
                "The focused Start button lost its Space hint.");
            var originalStart = tagging.StartTagging;
            TagQueueKey? started = null;
            tagging.StartTagging = scope => { started = scope.Start; return Task.CompletedTask; };
            try
            {
                PressSpace(tagging.StartButton, KeyModifiers.Meta);
                Require(started == null, "A system Space chord started tagging.");
                PressSpace(tagging.StartButton);
                await Program.WaitUntilAsync(() => started == tagging.Rows[0].Key && tagging.SelectedCount == 0);
            }
            finally { tagging.StartTagging = originalStart; }
            Program.CaptureWindow(window, "smoke-tagging-page-light-en.png");

            await CheckSelectionLifecycleAsync(window, tagging);

            // Delete from the list removes the selected entry; Alt+Down moves the lead entry one place down.
            SelectOnly(tagging, tagging.Rows[1]);
            await SettleAsync(window);
            Control row = (Control)tagging.List.ContainerFromIndex(1)!;
            row.Focus();
            await ReviewArtifacts.GestureAsync(window, "tagging-delete-row", () => Press(row, Key.Delete),
                () => tagging.StatusText.Text == "Removed from the queue: 1.",
                () => Program.WaitUntilAsync(() => window.SessionHost.GetTagQueue()!.Count == 2 && tagging.Rows.Count == 2 && !tagging.IsBusy));
            await SettleAsync(window);
            Require(tagging.StatusText.Text == "Removed from the queue: 1.", "Status after Delete: " + tagging.StatusText.Text);
            SelectOnly(tagging, tagging.Rows[0]);
            await SettleAsync(window);
            Control first = (Control)tagging.List.ContainerFromIndex(0)!;
            first.Focus();
            Press(first, Key.Down, KeyModifiers.Alt);
            await Program.WaitUntilAsync(() => window.SessionHost.GetTagQueue()!.Items[1].AssetId == "asset-1" && !tagging.IsBusy);
            // The move re-selects the moved entry once its command has finished (SelectOnly); let that land before the next selection.
            await SettleAsync(window);

            // The context menu (TG1, ContextMenuBuilder): continue/tag, state, look, order and remove, every entry a catalog command that
            // says why it is off; for one selected entry "Tag together…" and the order of "Tag…" are as the spec lists them.
            MeowChan.Desktop.Controls.ContextMenuModel model = tagging.ListMenuModel() ?? throw new InvalidOperationException("No menu model for a selected entry.");
            string[] labels = model.Entries.Select(item => item.Label).ToArray();
            string[] expectedLabels =
            {
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.continue-here"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("media.tags.edit"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.tag-together"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tags.quick.stamp-apply"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.mark-done"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.mark-open"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.mark-skipped"),
                Texts.T("tagging.page.group.assign"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("gallery.open"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("gallery.show-in-thread"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.show-in-media"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("media.show-in-folder"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("media.copy-path"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.move-up"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.move-down"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.move-first"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.move-last"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.remove")
            };
            Require(labels.SequenceEqual(expectedLabels), "Context menu: " + string.Join(" | ", labels));
            Require(model.OversizedSections.Count == 0 && model.Entries.Where(item => !item.Available).All(item => !string.IsNullOrEmpty(item.Reason)),
                "A disabled entry of the tagging menu has no reason, or a section is too long.");
            Require(!model.Entries.Single(item => item.Id == "tagging.tag-together").Available, "Tag together is on for a single entry.");
            await ReviewArtifacts.GestureAsync(window, "tagging-open-list-menu", () => tagging.ShowListMenu(),
                () => tagging.OpenListMenu is { IsOpen: true }, () => SettleAsync(window));
            Require(tagging.OpenListMenu is { IsOpen: true }, "The context menu did not open on the list.");
            await ReviewArtifacts.GestureAsync(window, "tagging-close-list-menu", () => tagging.OpenListMenu!.Close(),
                () => tagging.OpenListMenu is not { IsOpen: true }, () => SettleAsync(window));

            // TG1: the selection bar above the list. Hidden without a selection; one entry offers "Tag…", two "Tag together…"; the state menu
            // lists the three marks; Esc clears. The grid then shows the same entries and keeps the selection (and the saved view follows).
            tagging.List.SelectedItems!.Clear();
            await SettleAsync(window);
            Require(!tagging.SelectionBar.IsVisible, "The selection bar shows without a selection.");
            tagging.List.SelectedItems.Add(tagging.Rows[0]);
            await SettleAsync(window);
            Require(tagging.SelectionBar.IsVisible && tagging.SelectionText.Text == "1 selected" && (string?)tagging.SelectionTagButton.Content == "Edit tags…"
                && tagging.SelectionTagButton.IsEnabled, "Selection bar for one entry: '" + tagging.SelectionText.Text + "' / " + tagging.SelectionTagButton.Content);
            tagging.List.SelectedItems.Add(tagging.Rows[1]);
            await SettleAsync(window);
            Require(tagging.SelectionText.Text == "2 selected" && (string?)tagging.SelectionTagButton.Content == "Tag together…",
                "Selection bar for two entries: '" + tagging.SelectionText.Text + "' / " + tagging.SelectionTagButton.Content);
            var selectionMenu = tagging.SelectionMenuModel() ?? throw new InvalidOperationException("No actions menu for a selection.");
            Require(selectionMenu.Entries.Select(entry => entry.Id).SequenceEqual(new[]
            {
                TaggingCommands.ApplyStamp, TaggingCommands.MarkSkipped, TaggingCommands.MarkOpen,
                TaggingCommands.ContinueHere, TaggingCommands.GroupMenu, TaggingCommands.Remove
            }), "The selection menu has unexpected entries.");
            tagging.ShowSelectionMenu();
            await SettleAsync(window);
            Require(tagging.OpenSelectionMenu is { IsOpen: true }, "The selection menu did not open under More.");
            tagging.OpenSelectionMenu!.Close();
            await SettleAsync(window);
            if (mode != "Advanced")
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                await SettleAsync(window);
            }
            tagging.SetViewMode(TaggingViewMode.Grid, save: true);
            await SettleAsync(window);
            Require(tagging.ViewMode == TaggingViewMode.Grid && tagging.TileGrid.IsVisible && !tagging.List.IsVisible
                && !tagging.ColumnsButton.IsEffectivelyVisible && tagging.ViewToggle.SelectedIndex == 1,
                "The grid view is not shown after the switch.");
            Require(tagging.TileGrid.SelectedItems!.Count == 2 && tagging.SelectionText.Text == "2 selected", "The grid lost the selection: " + tagging.TileGrid.SelectedItems.Count);
            Require(tagging.TileGrid.ContainerFromIndex(0) is ListBoxItem, "The grid built no tile.");
            // MB1b: the badges are the shared view over the picture: not focusable, not hit-testable, and their sentence is in the tile's name.
            TaggingQueueTileView? firstTile = ((Control)tagging.TileGrid.ContainerFromIndex(0)!).GetVisualDescendants().OfType<TaggingQueueTileView>().FirstOrDefault();
            Require(firstTile?.Row != null && !firstTile.Badges.View.Focusable && !firstTile.Badges.View.IsHitTestVisible
                && AutomationProperties.GetName(firstTile).StartsWith(firstTile.Row.AccessibleName, StringComparison.Ordinal)
                && (firstTile.Badges.Description.Length == 0 || AutomationProperties.GetName(firstTile).EndsWith(firstTile.Badges.Description, StringComparison.Ordinal)),
                "The tile's badges or its name: " + (firstTile == null ? "no tile" : AutomationProperties.GetName(firstTile)));
            // MB1b: the badges sit on the corners of the tile's slot (the preview's bounds), the same place on every tile of the grid; the selection check
            // shares the top right corner with them and never covers a badge.
            Rect SlotRect(Control control, Control root) => new(control.TranslatePoint(default, root) ?? default, control.Bounds.Size);
            foreach (TaggingQueueTileView gridTile in tagging.TileGrid.GetVisualDescendants().OfType<TaggingQueueTileView>().Where(tile => tile.Row != null && tile.Badges.View.IsVisible))
            {
                Rect slot = SlotRect(gridTile.Preview, gridTile), view = SlotRect(gridTile.Badges.View, gridTile);
                Require(gridTile.Badges.View.Options.Anchor == MeowChan.Desktop.Controls.MediaBadgeAnchor.Slot
                    && Math.Abs(view.X - slot.X) < 1.5 && Math.Abs(view.Y - slot.Y) < 1.5 && Math.Abs(view.Width - slot.Width) < 1.5 && Math.Abs(view.Height - slot.Height) < 1.5,
                    "The tile's badge view " + view + " is not on the tile's slot " + slot + ".");
                Rect check = SlotRect(gridTile.SelectedMark, gridTile);
                foreach (MeowChan.Desktop.Controls.PlacedBadge placed in gridTile.Badges.View.Plan.Badges)
                {
                    Require(new Rect(default, slot.Size).Inflate(0.5).Contains(placed.Bounds), "Badge " + placed.Badge.Id + " " + placed.Bounds + " is outside the slot " + slot + ".");
                    Require(!gridTile.SelectedMark.IsVisible || placed.Badge.Id == MeowChan.Desktop.Controls.MediaBadgeId.PlayMark
                        || !check.Intersects(placed.Bounds.Translate(new Vector(view.X, view.Y))), "The selection check " + check + " covers badge " + placed.Badge.Id + " " + placed.Bounds + ".");
                }
                if (gridTile.SelectedMark.IsVisible) Require(slot.Inflate(0.5).Contains(check), "The selection check " + check + " is outside the slot " + slot + ".");
            }
            Require(tagging.ListMenuModel() is { } gridMenu && gridMenu.Entries.Count() == 18, "The grid has no context menu for its selection.");
            await Program.WaitUntilAsync(() => Config.LoadConfig().TaggingPageView == "Grid", () => "saved view " + Config.LoadConfig().TaggingPageView);
            Program.CaptureWindow(window, "smoke-tagging-page-grid.png");
            Press((Control)tagging.TileGrid.ContainerFromIndex(0)!, Key.Escape);
            await SettleAsync(window);
            Require(tagging.TileGrid.SelectedItems!.Count == 0 && !tagging.SelectionBar.IsVisible, "Esc did not clear the grid selection.");
            tagging.SetViewMode(TaggingViewMode.List, save: true);
            await SettleAsync(window);
            Require(tagging.List.IsVisible && !tagging.TileGrid.IsVisible && tagging.ColumnsButton.IsVisible, "Back to the list failed.");
            await Program.WaitUntilAsync(() => Config.LoadConfig().TaggingPageView == "List", () => "saved view " + Config.LoadConfig().TaggingPageView);

            await CheckResultKeysAsync(window, tagging);
            await CheckStampAsync(window, tagging);

            // Mark one finished, filter by state, then the work views.
            SelectOnly(tagging, tagging.Rows[0]);
            await tagging.ExecuteRowCommandAsync("tagging.mark-done");
            CheckProgress(tagging, open: 1, finished: 1);
            tagging.Filter = TaggingQueueFilter.Skipped;
            await SettleAsync(window);
            Require(tagging.Rows.Count == 0, "The Skipped filter still lists entries.");
            tagging.Filter = TaggingQueueFilter.All;
            foreach (string view in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, view);
                await SettleAsync(window);
                Require(tagging.CurrentWorkView.ToString() == view, "The page did not follow the work view " + view + ".");
                Program.CaptureWindow(window, "smoke-tagging-page-" + view.ToLowerInvariant() + ".png");
            }
            await SetThemeAsync(window, "Dark");
            await FooterNavigationChecks.PersistLanguageAsync(window, "de");
            await ReopenAsync(window, tagging);
            CheckProgress(tagging, open: 1, finished: 1);
            Program.CaptureWindow(window, "smoke-tagging-page-dark-de.png");
            await CheckHeaderWidthAsync(window, tagging);
            await CheckFiltersAsync(window, tagging);
            await CheckResponsiveMatrixAsync(window, tagging);
            await SetThemeAsync(window, "Light");
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
            await ReopenAsync(window, tagging);
            CheckSegments(tagging, labelsVisible: true);
            Program.CaptureWindow(window, "smoke-tagging-page-wide-en.png");

            // Start: PT1's walk; the synthetic entries have no saved files, so the gallery does not open.
            await tagging.StartAsync(null);
            await SettleAsync(window);
            Require(window.SelectedPage == MainWindow.WorkspacePage.Tagging, "Starting without saved files left the page: " + window.SelectedPage + ", gallery " + (window.GalleryTagging != null));

            // Clear asks first, then empties the queue.
            tagging.ClearButton.Command!.Execute(null);
            await SettleAsync(window);
            Require(tagging.ClearQuestionOpen, "Clear did not ask first.");
            tagging.ClearYesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => window.SessionHost.GetTagQueue()!.Count == 0 && tagging.Rows.Count == 0);
            Require(!tagging.StartButton.IsEnabled, "Start stays enabled on an empty queue.");
            CheckProgress(tagging, open: 0, finished: 0);
            Program.CaptureWindow(window, "smoke-tagging-page-empty.png");
            await CheckQueueWalkProgressAsync(window, tagging);
            await CheckGalleryCommandAsync(window, tagging);
            Console.WriteLine("SMOKE tagging-page: footer button and More entry, progress, Delete/Alt+Down, context menu, selection bar, grid view and switch, stamp, filter, work views, clear; Light/Dark EN/DE/RU/JA captures.");
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            await FooterNavigationChecks.PersistLanguageAsync(window, language);
        }
    }

    private static async Task CheckResultKeysAsync(MainWindow window, TaggingPage tagging)
    {
        var original = ShortcutService.CurrentSection;
        TagQueueKey[] keys = tagging.Rows.Select(row => row.Key).ToArray();
        try
        {
            foreach (TaggingViewMode view in Enum.GetValues<TaggingViewMode>())
            {
                tagging.SetViewMode(view, save: false);
                tagging.ClearSelection();
                await SettleAsync(window);
                Control first = tagging.ShownList.ContainerFromIndex(0)!;
                first.Focus();
                Press(first, Key.Space);
                PressSpace(first);
                Require(tagging.SelectedCount == 1 && first.IsFocused, "Repeating Space changed the selection more than once.");
                await SettleAsync(window);
                Control second = tagging.ShownList.ContainerFromIndex(1)!;
                Require(second.Focus() && second.IsFocused, "The second result did not take focus in " + view);
                PressSpace(second, KeyModifiers.Meta);
                Require(tagging.SelectedCount == 1 && tagging.ShownList.Selection.IsSelected(0) && second.IsFocused,
                    "A system Space chord changed which row was selected or moved its focus.");
                PressSpace(second);
                Require(tagging.SelectedCount == 2 && second.IsFocused,
                    $"Space did not extend the selection in {view}: count {tagging.SelectedCount}, focused {second.IsFocused}.");
                PressSpace(second);
                Require(tagging.SelectedCount == 1 && second.IsFocused, "Space did not preserve the other row and focus.");
                Press(second, Key.D);
                await Program.WaitUntilAsync(() => !tagging.IsBusy && tagging.Queue.Items[0].State == TagQueueState.Done);
                await SettleAsync(window);
                Control active = tagging.ShownList.ContainerFromIndex(0)!;
                active.Focus();
                Press(active, Key.S);
                await Program.WaitUntilAsync(() => !tagging.IsBusy && tagging.Queue.Items[0].State == TagQueueState.Skipped);
                tagging.SelectionMoreButton.Focus();
                Press(tagging.SelectionMoreButton, Key.Escape);
                Require(tagging.SelectedCount == 0 && tagging.ShownList.IsKeyboardFocusWithin, "Esc in the selection bar did not return to results.");
                await window.SessionHost.SetTagQueueStatesAsync(keys.Select(key => KeyValuePair.Create(key, TagQueueState.Open)).ToArray());
                tagging.Refresh();
            }

            tagging.SetViewMode(TaggingViewMode.List, save: false);
            tagging.ShownList.SelectedIndex = 0;
            await SettleAsync(window);
            Control row = tagging.ShownList.ContainerFromIndex(0)!;
            row.Focus();
            ShortcutService.Publish(original.Set(TaggingCommands.MarkDone, [ShortcutGesture.Parse("Space")]));
            Require(ToolTip.GetTip(tagging.SelectionDoneButton) is string tip && tip.Contains("Space", StringComparison.Ordinal),
                "A live rebinding did not refresh the decision hint.");
            PressSpace(row);
            await Program.WaitUntilAsync(() => !tagging.IsBusy && tagging.Queue.Items[0].State == TagQueueState.Done);
            Require(tagging.SelectedCount == 1, "Row Space overrode a rebound selection command.");
            tagging.Filter = TaggingQueueFilter.Open;
            await SettleAsync(window);
            Require(tagging.SelectedCount == 0 && tagging.ShownList.IsKeyboardFocusWithin, "Filtering away the focused row lost result focus.");
            tagging.Filter = TaggingQueueFilter.All;
            Require(tagging.SelectedCount == 0, "Resetting the filter restored a discarded selection.");
        }
        finally
        {
            ShortcutService.Publish(original);
            await window.SessionHost.SetTagQueueStatesAsync(keys.Select(key => KeyValuePair.Create(key, TagQueueState.Open)).ToArray());
            tagging.Refresh();
        }
    }

    private static async Task CheckStampAsync(MainWindow window, TaggingPage tagging)
    {
        Button trigger = tagging.StampButton;
        TextBox input = tagging.StampInput;
        Require(!tagging.StampOpen, "An old Open profile value opened the editor.");
        trigger.Focus();
        foreach (KeyModifiers modifiers in new[] { KeyModifiers.Meta, KeyModifiers.Control, KeyModifiers.Control | KeyModifiers.Alt })
            PressSpace(trigger, modifiers);
        Require(!tagging.StampOpen, "A system Space chord opened the stamp.");
        PressSpace(trigger);
        await Program.WaitUntilAsync(() => tagging.StampOpen && input.IsFocused);

        if (!await ClipboardChecks.PasteFixtureTextAsync(input, "surface\nreview\nkeep"))
            input.Text = "surface, review, keep";
        await Program.WaitUntilAsync(() => input.Text == "surface, review, keep", () => "paste: '" + input.Text + "'");
        Press(input, Key.Enter);
        string[] expected = ["surface", "review", "keep"];
        await Program.WaitUntilAsync(() => window.QuickTagStampSet.ToNames().SequenceEqual(expected)
            && window.SessionHost.Config.TagStamp.SequenceEqual(expected) && tagging.StampChips.Children.Count == 3);
        Require(tagging.StampOpen && input.IsFocused && string.IsNullOrEmpty(input.Text), "The stamp did not stay ready for another input.");
        input.Text = "surface";
        Press(input, Key.Enter);
        TextBlock feedback = ((StackPanel)((Flyout)trigger.Flyout!).Content!).Children.OfType<TextBlock>()
            .Single(text => text.Name == "TaggingStampFeedback");
        await Program.WaitUntilAsync(() => feedback.Text == Texts.T("tagging.page.stamp.already", "surface"));
        Require(window.SessionHost.GetTagQueue()!.Stamp.Count == 0, "The shared stamp went into tag-queue.json.");
        Press(input, Key.Escape);
        await Program.WaitUntilAsync(() => !tagging.StampOpen && trigger.IsFocused);
        Require(Config.LoadConfig().TaggingStampBlock == "Open", "Toggling the flyout wrote a legacy view preference.");
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1")
        {
            using var native = new MediaNativeInput(window);
            Press(trigger, Key.Enter);
            await Program.WaitUntilAsync(() => tagging.StampOpen && input.IsFocused);
            await native.KeyAsync(MediaNativeInput.Space);
            await Program.WaitUntilAsync(() => input.Text == " ", () => "Stamp input after Space: '" + input.Text + "'");
            await native.ClickAsync(tagging.FilterBox, requireHitTest: false);
            await Program.WaitUntilAsync(() => !tagging.StampOpen && string.IsNullOrEmpty(input.Text)
                && (tagging.FilterBox.IsDropDownOpen || tagging.FilterBox.IsKeyboardFocusWithin));
            tagging.FilterBox.IsDropDownOpen = false;
        }

        string first = (await window.SessionHost.CreateTagQueueGroupAsync("Stamp A")).Groups[^1].Id;
        string second = (await window.SessionHost.CreateTagQueueGroupAsync("Stamp B")).Groups[^1].Id;
        await window.SessionHost.SetTagQueueGroupStampAsync(first, ["aaa"]);
        await window.SessionHost.SetTagQueueGroupStampAsync(second, ["bbb", "ccc"]);
        tagging.Refresh();
        tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Id == first);
        trigger.Focus();
        Press(trigger, Key.Enter);
        await Program.WaitUntilAsync(() => tagging.StampOpen && input.IsFocused && tagging.StampChips.Children.Count == 1);
        input.Text = "uncommitted";
        tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Id == second);
        Require(!tagging.StampOpen && string.IsNullOrEmpty(input.Text) && tagging.StampChips.Children.Count == 2
            && AutomationProperties.GetName(trigger)?.Contains("Stamp B", StringComparison.Ordinal) == true,
            "Group A → B retained the old editor, draft or scope.");
        trigger.Focus();
        Press(trigger, Key.Enter);
        await Program.WaitUntilAsync(() => tagging.StampOpen && input.IsFocused);
        input.Text = "BBB";
        Press(input, Key.Enter);
        await Program.WaitUntilAsync(() => feedback.Text == Texts.T("tagging.page.stamp.already", "BBB"), () => $"stamp feedback = {feedback.Text}, input = {input.Text}");
        Button remove = ((StackPanel)tagging.StampChips.Children[0]).Children.OfType<Button>().Single();
        remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => tagging.StampOpen && input.IsFocused && tagging.StampChips.Children.Count == 1);
        Require(tagging.Queue.Group(second)!.Stamp.SequenceEqual(new[] { "ccc" }), "Removing a group chip changed the wrong stamp tags.");
        tagging.GroupBox.SelectedIndex = 0;
        Require(tagging.StampChips.Children.Count == 3, "All groups did not restore the window stamp.");
        await window.SessionHost.DeleteTagQueueGroupAsync(first);
        await window.SessionHost.DeleteTagQueueGroupAsync(second);
        tagging.Refresh();
    }

    private static void CheckProgress(TaggingPage tagging, int open, int finished, int skipped = 0)
    {
        int total = open + finished + skipped;
        int decided = finished + skipped;
        Require(tagging.ProgressText.IsEffectivelyVisible
            && tagging.ProgressText.Text == Texts.Count("tagging.page.progress.results", total, decided, total),
            "Progress: " + tagging.ProgressText.Text);
        Require(Equals(ToolTip.GetTip(tagging.ProgressText), Texts.T("tagging.page.progress", open, finished, skipped)), "Missing progress breakdown.");
        double expected = total == 0 ? 0 : (double)decided / total * 100;
        Require(Math.Abs(ProgressTrack(tagging).Value - expected) < 0.001, "The track did not match the decisions.");
    }

    private static async Task CheckHeaderWidthAsync(MainWindow window, TaggingPage tagging)
    {
        double originalWidth = window.Width;
        double originalMaxWidth = tagging.MaxWidth;
        TaggingViewMode mode = tagging.ViewMode;
        QuickTagStamp? originalStamp = tagging.Stamp;
        ProgressBar bar = ProgressTrack(tagging);
        Exception? checkFailure = null;
        try
        {
            // Match the empty stamp in the width-growth reference, so both widths can use the same detail.
            tagging.Stamp = null;
            await ResizeAsync(window, 1440);
            Program.CaptureWindow(window, "smoke-tagging-page-1440-de.png");
            CheckHeaderGeometry(tagging, 1, "wide growth start");
            double trackWidth = bar.Bounds.Width;
            bool labelled = SegmentLabelsVisible(tagging);
            await ResizeAsync(window, 1600);
            CheckHeaderGeometry(tagging, 1, "wide growth end");
            Require(SegmentLabelsVisible(tagging) == labelled && Math.Abs(bar.Bounds.Width - trackWidth - 160) <= 1,
                $"The progress track did not absorb the added 160 DIP ({trackWidth} → {bar.Bounds.Width}).");
            Console.WriteLine($"SMOKE tagging progress track: {trackWidth} → {bar.Bounds.Width} DIP at 1440 → 1600 DIP.");

            tagging.Stamp = originalStamp;
            await ResizeAsync(window, 1400);
            CheckHeaderGeometry(tagging, 1, "at breakpoint");
            double tableTop = tagging.ColumnHeader.TranslatePoint(default, tagging)!.Value.Y;
            tagging.StampButton.Focus(NavigationMethod.Tab);
            Press(tagging.StampButton, Key.Enter);
            await Program.WaitUntilAsync(() => tagging.StampOpen && tagging.StampInput.IsFocused);
            tagging.StampInput.Text = "resize draft";
            await ResizeAsync(window, 1399);
            CheckHeaderGeometry(tagging, 2, "below breakpoint");
            double tableShift = tagging.ColumnHeader.TranslatePoint(default, tagging)!.Value.Y - tableTop;
            Require(Math.Abs(tableShift - 40) <= 1, "The table did not move by one 32-DIP control row plus its 8-DIP gap.");
            Console.WriteLine($"SMOKE tagging breakpoint: the table starts {tableShift} DIP higher at 1400 than at 1399 DIP.");
            await ResizeAsync(window, 760);
            CheckHeaderGeometry(tagging, 2, "narrow, stamp open");
            await ResizeAsync(window, 1400);
            CheckHeaderGeometry(tagging, 1, "back at breakpoint");
            Require(tagging.StampOpen && tagging.StampInput.IsFocused && tagging.StampInput.Text == "resize draft",
                "Resizing closed the stamp or lost its input focus or draft.");
            Press(tagging.StampInput, Key.Escape);
            await Program.WaitUntilAsync(() => !tagging.StampOpen && tagging.StampButton.IsFocused);

            await ResizeAsync(window, 760);
            Program.CaptureWindow(window, "smoke-tagging-page-narrow-de.png");
            tagging.MaxWidth = 440;
            await SettleAsync(window);
            CheckSegments(tagging, labelsVisible: false);
            CheckHeaderGeometry(tagging, null, "440 DIP page");
            Require(tagging.ViewMode == mode && Config.LoadConfig().TaggingPageView == mode.ToString(), "Resizing changed the saved view.");
            Require(tagging.StampButton.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "TaggingStampSummary").Text
                == Texts.T("tagging.page.stamp.summary", tagging.StampChips.Children.Count), "The narrow stamp did not switch to a count.");
            tagging.Filter = TaggingQueueFilter.Skipped;
            await SettleAsync(window);
            Require(tagging.Rows.Count == 0 && Equals(ToolTip.GetTip(tagging.FilterBox), TaggingQueueView.FilterText(tagging.Filter)),
                "Compact Show lost its full filter value.");
            CheckProgress(tagging, open: 1, finished: 1);
        }
        catch (Exception error)
        {
            checkFailure = error;
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(checkFailure, async () =>
            {
                tagging.Stamp = originalStamp;
                tagging.Filter = TaggingQueueFilter.All;
                tagging.MaxWidth = originalMaxWidth;
                await ResizeAsync(window, originalWidth);
            }, error => Console.Error.WriteLine("SMOKE tagging header cleanup failed: " + error));
        }
        CheckSegments(tagging, labelsVisible: true);
    }

    private static async Task ResizeAsync(MainWindow window, double width)
    {
        window.Width = width;
        await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - width) < 0.5);
        await SettleAsync(window);
    }

    private static ProgressBar ProgressTrack(TaggingPage tagging) =>
        tagging.Header.GetVisualDescendants().OfType<ProgressBar>().Single(control => control.Name == "TaggingProgressBar");

    private static void CheckHeaderGeometry(TaggingPage tagging, int? expectedLines, string stage)
    {
        TaggingHeader header = tagging.Header;
        Control[] groups = header.Children.Where(child => child.IsEffectivelyVisible).ToArray();
        double lineCenter = double.NegativeInfinity;
        int lines = 0;
        foreach (Control group in groups)
        {
            if (Math.Abs(group.Bounds.Center.Y - lineCenter) <= 1) continue;
            lineCenter = group.Bounds.Center.Y;
            lines++;
        }
        Require(expectedLines == null || lines == expectedLines,
            $"{stage}: expected {expectedLines} lines, got {lines} at {header.Bounds.Width} DIP. "
            + string.Join(", ", groups.Select(group => $"{group.Name}={group.Bounds}")));
        Rect area = new Rect(header.Bounds.Size).Inflate(1);
        for (int i = 0; i < groups.Length; i++)
        {
            Require(area.Contains(groups[i].Bounds), $"{stage}: {groups[i].Name} {groups[i].Bounds} falls outside {header.Bounds}.");
            for (int j = i + 1; j < groups.Length; j++)
                Require(!groups[i].Bounds.Intersects(groups[j].Bounds), $"{stage}: {groups[i].Name} overlaps {groups[j].Name}.");
        }

        ProgressBar track = ProgressTrack(tagging);
        Point count = tagging.ProgressText.TranslatePoint(default, track) ?? throw new InvalidOperationException("Progress number and track are detached.");
        Require(track.IsEffectivelyVisible && track.Bounds.Width >= 63 && Math.Abs(count.X - track.Bounds.Width - 4) <= 1,
            $"{stage}: the number does not follow a visible and flexible progress track.");
        Point countOrigin = tagging.ProgressText.TranslatePoint(default, header)!.Value;
        Require(area.Contains(new Rect(countOrigin, tagging.ProgressText.Bounds.Size)), $"{stage}: the progress count is outside the header.");
        Require(Math.Abs(groups[^1].Bounds.Right - header.Bounds.Width) <= 1, $"{stage}: view controls are not docked at the right edge.");
        ToggleButton more = tagging.Actions.More ?? throw new InvalidOperationException("The page has no More button.");
        Control[] stops = [tagging.StartButton, tagging.AddButton, tagging.HelpButton, tagging.StampButton,
            tagging.GroupBox, tagging.FilterBox, more, tagging.ColumnsButton, tagging.ViewToggle.First, tagging.ViewToggle.Second];
        for (int i = 0; i < stops.Length; i++)
        {
            Control stop = stops[i];
            Point origin = stop.TranslatePoint(default, header) ?? throw new InvalidOperationException(stop.Name + " is detached.");
            Rect bounds = new(origin, stop.Bounds.Size);
            Require(stop.IsEffectivelyVisible && area.Contains(bounds), $"{stage}: {stop.Name} is hidden or outside the header at {bounds}.");
            if (i == 0) continue;
            Control previous = stops[i - 1];
            Point before = previous.TranslatePoint(default, header)!.Value;
            bool nextLine = origin.Y >= before.Y + previous.Bounds.Height - 1;
            bool nextOnLine = bounds.Bottom > before.Y && origin.X >= before.X + previous.Bounds.Width - 1;
            Require(nextLine || nextOnLine,
                $"{stage}: {stop.Name} at {bounds} is not after {previous.Name} at {before}.");
        }
        Require(!string.IsNullOrEmpty(AutomationProperties.GetName(more))
            && ToolTip.GetTip(more) is string { Length: > 0 }, $"{stage}: More lost its accessible name or tooltip.");
        Point secondSegment = tagging.ViewToggle.Second.TranslatePoint(default, header)!.Value;
        Require(Math.Abs(secondSegment.X + tagging.ViewToggle.Second.Bounds.Width - header.Bounds.Width) <= 1,
            $"{stage}: List/Grid leaves a gap at the right edge.");
        foreach (ComboBox box in new[] { tagging.GroupBox, tagging.FilterBox })
        {
            PathIcon chevron = box.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "DropDownGlyph");
            Point origin = chevron.TranslatePoint(default, box) ?? throw new InvalidOperationException("Dropdown chevron is detached.");
            Require(chevron.IsEffectivelyVisible && new Rect(box.Bounds.Size).Inflate(1).Contains(new Rect(origin, chevron.Bounds.Size)),
                $"{stage}: {box.Name} clipped its dropdown chevron.");
        }
    }

    private static bool SegmentLabelsVisible(TaggingPage tagging) =>
        ((StackPanel)tagging.ViewToggle.First.Content!).Children.OfType<TextBlock>().Single().IsEffectivelyVisible;

    private static async Task CheckFiltersAsync(MainWindow window, TaggingPage tagging)
    {
        Require(AutomationProperties.GetName(tagging.GroupBox) == Texts.T("tagging.page.group")
            && AutomationProperties.GetName(tagging.FilterBox) == Texts.T("tagging.page.filter"), "Missing accessible filter names.");
        Require(((TaggingGroupChoice)tagging.GroupBox.SelectedItem!).Label == Texts.T("tagging.page.group.all"), "Missing All groups default.");
        Require(tagging.Actions.Items.Contains(tagging.GroupManageButton)
            && Ursa.Controls.ToolBar.GetOverflowMode(tagging.GroupManageButton) == Ursa.Controls.OverflowMode.Always,
            "Group management did not move under the page's More button.");
        ToggleButton pageMore = tagging.Actions.More ?? throw new InvalidOperationException("The page has no More button.");
        pageMore.IsChecked = true;
        await SettleAsync(window);
        Require(tagging.GroupManageButton.IsAttachedToVisualTree() && tagging.GroupManageButton.IsEffectivelyVisible,
            "Group management is missing from page More.");
        Press(tagging.GroupManageButton, Key.Enter);
        await Program.WaitUntilAsync(() => tagging.GroupManageButton.ContextMenu is { IsOpen: true });
        tagging.GroupManageButton.ContextMenu!.Close();
        pageMore.IsChecked = false;
        Require(tagging.GroupManageMenuModel()?.Entries.Select(entry => entry.Available).SequenceEqual(new[] { true, false, false }) == true,
            "Manage groups has wrong availability without a named group.");

        string longName = new('W', 48);
        TagQueueKey first = tagging.Rows[0].Key;
        string group = (await window.SessionHost.CreateTagQueueGroupAsync(longName, [first])).Groups[^1].Id;
        try
        {
            tagging.Refresh();
            tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Id == group);
            await SettleAsync(window);
            Require(tagging.GroupBox.Bounds.Width <= 160 && Equals(ToolTip.GetTip(tagging.GroupBox), longName), "A long group name took over the filter line.");
            Require(tagging.Rows.Count == 1, "The named group did not narrow the results.");
            CheckProgress(tagging, open: 0, finished: 1);
            Require(tagging.GroupManageMenuModel()?.Entries.All(entry => entry.Available) == true, "Named group operations were unavailable.");
            tagging.Filter = TaggingQueueFilter.Untagged;
            await SettleAsync(window);
            Require(tagging.Rows.Count == 0 && tagging.FilterBox.IsEffectivelyVisible && tagging.EmptyShowAllButton.IsEffectivelyVisible,
                "Without tags lost the zero-result controls.");
            CheckProgress(tagging, open: 0, finished: 1);
        }
        finally
        {
            tagging.Filter = TaggingQueueFilter.All;
            tagging.GroupBox.SelectedIndex = 0;
            await window.SessionHost.DeleteTagQueueGroupAsync(group);
            tagging.Refresh();
        }
    }

    private static async Task CheckResponsiveMatrixAsync(MainWindow window, TaggingPage tagging)
    {
        double originalWidth = window.Width;
        string originalView = tagging.CurrentWorkView.ToString();
        string originalTheme = Config.LoadConfig().Theme;
        string originalLanguage = Texts.Language;
        await window.SessionHost.EnqueueTagQueueAsync(Enumerable.Range(4, 201 - tagging.Queue.Count).Select(Item).ToArray());
        await window.SessionHost.SetTagQueueStatesAsync(tagging.Queue.Items.Select(item => KeyValuePair.Create(item.Key, TagQueueState.Open)).ToArray());
        TagQueueSnapshot queue = await window.SessionHost.SetTagQueueCursorAsync(37);
        string groupName = "Reise nach Kyoto · Путешествие в Киото · 京都への長い旅の記録";
        string group = (await window.SessionHost.CreateTagQueueGroupAsync(groupName, queue.Items.Select(item => item.Key).ToArray())).Groups[^1].Id;
        await window.SessionHost.SetTagQueueGroupStampAsync(group, ["Waldlichtung", "Morgennebel", "Wanderweg"]);
        tagging.Refresh();
        tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Id == group);
        tagging.List.SelectedItems!.Clear();
        foreach (TaggingQueueRow row in tagging.Rows.Take(2)) tagging.List.SelectedItems.Add(row);
        TagQueueKey[] selected = tagging.Rows.Take(2).Select(row => row.Key).ToArray();
        try
        {
            foreach (string language in new[] { "en", "de", "ru", "ja" })
            {
                await FooterNavigationChecks.PersistLanguageAsync(window, language);
                await ReopenAsync(window, tagging);
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    await SetThemeAsync(window, theme);
                    foreach (WorkView view in WorkViewSetting.All)
                    {
                        await PresentationChecks.SwitchModeAsync(window, view.ToString());
                        foreach (int width in new[] { 1440, 1100, 760 })
                        {
                            tagging.SelectionMoreButton.Focus(NavigationMethod.Tab);
                            await ResizeAsync(window, width);
                            string stage = $"{view}/{language}/{theme}/{width}";
                            CheckHeaderGeometry(tagging, width == 1440 ? 1 : 2, stage);
                            CheckProgress(tagging, open: 201, finished: 0);
                            Require(AutomationProperties.GetName(tagging.StartButton) == Texts.T("tagging.page.continue", 38),
                                stage + ": Start did not preserve queue position 38.");
                            Require(tagging.GroupBox.Bounds.Width is >= 119 and <= 161
                                && tagging.FilterBox.Bounds.Width is >= 111 and <= 145
                                && Equals(ToolTip.GetTip(tagging.GroupBox), groupName)
                                && AutomationProperties.GetHelpText(tagging.GroupBox) == groupName,
                                stage + ": filters lost their bounded geometry or full value.");
                            Require(tagging.GroupBox.GetVisualDescendants().OfType<TrimTipTextBlock>()
                                .Any(label => label.IsEffectivelyVisible && label.Text == groupName && label.IsTrimmed),
                                stage + ": the selected group label did not show an ellipsis.");
                            CheckSelection(tagging, selected, stage);
                            CheckSelectionAction(tagging.SelectionTagButton, tagging.SelectionDoneButton);
                            CheckSelectionAction(tagging.SelectionDoneButton, tagging.SelectionMoreButton);
                            Require(tagging.SelectionMoreButton.IsFocused, "Resizing lost focus on More.");
                            CheckSelectionAction(tagging.SelectionMoreButton, tagging.SelectionClearButton);
                            CheckSegments(tagging, SegmentLabelsVisible(tagging));
                            Program.CaptureWindow(window, $"smoke-tagging-{view}-{language}-{theme}-{width}.png");
                            Console.WriteLine($"SMOKE tagging geometry: {stage}, track {ProgressTrack(tagging).Bounds.Width} DIP.");
                        }
                    }
                }
                double pageMaxWidth = tagging.MaxWidth;
                try
                {
                    tagging.MaxWidth = 440;
                    await SettleAsync(window);
                    CheckHeaderGeometry(tagging, null, language + "/440");
                    CheckProgress(tagging, open: 201, finished: 0);
                }
                finally { tagging.MaxWidth = pageMaxWidth; }
            }
            await FooterNavigationChecks.PersistLanguageAsync(window, "de");
            await ReopenAsync(window, tagging);
            await PresentationChecks.SwitchModeAsync(window, WorkView.Advanced.ToString());
            await ResizeAsync(window, 1100);
            double originalMaxWidth = tagging.MaxWidth;
            try
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    await SetThemeAsync(window, theme);
                    tagging.MaxWidth = originalMaxWidth;
                    await SettleAsync(window);
                    await CheckHeaderTabOrderAsync(window, tagging, theme, 1100);

                    tagging.MaxWidth = 440;
                    await SettleAsync(window);
                    CheckHeaderGeometry(tagging, null, theme + "/440");
                    await CheckHeaderTabOrderAsync(window, tagging, theme, 440);
                }
            }
            finally { tagging.MaxWidth = originalMaxWidth; }
        }
        finally
        {
            tagging.GroupBox.SelectedIndex = 0;
            await window.SessionHost.DeleteTagQueueGroupAsync(group);
            tagging.Refresh();
            await PresentationChecks.SwitchModeAsync(window, originalView);
            window.Width = originalWidth;
            await SetThemeAsync(window, originalTheme);
            await FooterNavigationChecks.PersistLanguageAsync(window, originalLanguage);
            await ReopenAsync(window, tagging);
        }

        void CheckSelectionAction(Control first, Control next)
        {
            Point a = first.TranslatePoint(default, tagging.SelectionBar) ?? new Point(-1, -1);
            Point b = next.TranslatePoint(default, tagging.SelectionBar) ?? new Point(-1, -1);
            Require(first.IsEffectivelyVisible && next.IsEffectivelyVisible && a.X >= 0 && a.X + first.Bounds.Width <= b.X
                && b.X + next.Bounds.Width <= tagging.SelectionBar.Bounds.Width + 1 && Math.Abs(a.Y - b.Y) <= 1,
                "The selection actions overlapped, wrapped, or ran outside the available width.");
        }
    }

    private static async Task CheckHeaderTabOrderAsync(MainWindow window, TaggingPage tagging, string theme, int width)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1") return;

        using MediaNativeInput native = new(window);
        tagging.StartButton.Focus(NavigationMethod.Tab);
        Require(tagging.StartButton.Classes.Contains(":focus-visible") && tagging.StartButton.FocusAdorner != null,
            theme + ": the Start button lost its keyboard focus frame.");
        ProgressBar track = ProgressTrack(tagging);
        Require(track.Background is ISolidColorBrush background && background.Color.A > 0
            && window.Background is ISolidColorBrush surface && background.Color != surface.Color,
            theme + ": the empty progress track does not stand out from the page.");
        ToggleButton more = tagging.Actions.More ?? throw new InvalidOperationException("The page has no More button.");
        Control[] stops = [tagging.AddButton, tagging.HelpButton, tagging.StampButton, tagging.GroupBox, tagging.FilterBox,
            more, tagging.ColumnsButton, tagging.ViewToggle.First, tagging.SelectionTagButton];
        foreach (Control stop in stops)
        {
            await native.KeyAsync(MediaNativeInput.Tab);
            await SettleAsync(window);
            Control? actual = window.FocusManager?.GetFocusedElement() as Control;
            string path = actual == null ? "<none>" : string.Join(" > ", actual.GetSelfAndVisualAncestors().OfType<Control>()
                .Select(control => $"{control.GetType().Name}#{control.Name}[{control.TabIndex}/{KeyboardNavigation.GetTabNavigation(control)}]"));
            Require(stop.IsFocused || stop.IsKeyboardFocusWithin,
                $"{theme}/{width}: Tab expected {stop.Name} (enabled={stop.IsEffectivelyEnabled}, visible={stop.IsEffectivelyVisible}), found {path}.");
            if (ReferenceEquals(stop, more))
            {
                await native.KeyAsync(MediaNativeInput.Space);
                await Program.WaitUntilAsync(() => more.IsChecked == true && tagging.GroupManageButton.IsFocused);
                await native.KeyAsync(MediaNativeInput.Escape);
                await Program.WaitUntilAsync(() => more.IsChecked == false && more.IsFocused);
            }
            if (ReferenceEquals(stop, tagging.ViewToggle.First))
            {
                ToggleButton active = tagging.ViewToggle.First;
                ContentPresenter face = active.GetVisualDescendants().OfType<ContentPresenter>()
                    .Single(presenter => presenter.Name == "PART_ContentPresenter" && ReferenceEquals(presenter.TemplatedParent, active));
                Require(active.IsChecked == true && active.Classes.Contains(":focus-visible")
                    && face.BorderBrush is ISolidColorBrush accent && accent.Color.A > 0,
                    theme + ": the active view lost its accent or keyboard focus.");
                string suffix = width == 1100 ? "" : $"-{width}";
                Program.CaptureWindow(window, $"smoke-tagging-view-focus-{theme.ToLowerInvariant()}-de{suffix}.png");
            }
        }
    }

    private static void CheckSegments(TaggingPage tagging, bool labelsVisible)
    {
        CheckSegment(tagging.ViewToggle.First, "tagging.page.view.list");
        CheckSegment(tagging.ViewToggle.Second, "tagging.page.view.grid");

        void CheckSegment(ToggleButton segment, string key)
        {
            string name = Texts.T(key);
            Require(segment.IsEffectivelyVisible && AutomationProperties.GetName(segment) == name
                && Equals(ToolTip.GetTip(segment), Texts.T(key + ".hint")), "Missing view segment semantics for " + name);
            Require(segment.Content is StackPanel visual && visual.Children.OfType<TextBlock>().Single() is { } label
                && label.Text == name && label.IsEffectivelyVisible == labelsVisible, "Wrong label presentation for " + name);
        }
    }

    private static async Task CheckQueueWalkProgressAsync(MainWindow window, TaggingPage tagging)
    {
        DesktopSessionHost session = window.SessionHost;
        MediaLibrarySnapshot library = await MediaLibraryIndexBuilder.BuildAsync(session.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync);
        TagQueueItem a = TagQueueItem.From(library.Entries.Single(entry => entry.PostId == "100"), DateTimeOffset.UtcNow)!;
        TagQueueItem b = TagQueueItem.From(library.Entries.Single(entry => entry.PostId == "200"), DateTimeOffset.UtcNow)!;
        await session.EnqueueTagQueueAsync([Item(1), a, b, .. Enumerable.Range(4, 198).Select(Item)]);
        await session.SetTagQueueCursorAsync(1);
        await Program.WaitUntilAsync(() => tagging.Queue.Count == 201 && tagging.Queue.Cursor == 1 && tagging.StartButton.IsEffectivelyEnabled);
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Exception? failureBeforeCleanup = null;
        try
        {
            CheckProgress(tagging, open: 201, finished: 0);
            GalleryViewer viewer = await StartWalkAsync();
            CheckPosition(viewer, a, 2, 201);
            Program.CaptureWindow(window, "smoke-tagging-queue-position.png");
            GalleryViewerChecksAccess.Click(viewer, "GalleryNext");
            await Program.WaitUntilAsync(() => viewer.TagPanelController?.Entry.Post?.PostId == b.PostId);
            CheckPosition(viewer, b, 3, 201);
            await ReturnAsync(viewer, b);
            await Program.WaitUntilAsync(() => tagging.Queue.Cursor == 2);
            CheckProgress(tagging, open: 201, finished: 0);
            Require(AutomationProperties.GetName(tagging.StartButton) == "Continue tagging (no. 3)", "Resume did not follow the browsed medium.");

            string groupId = (await session.CreateTagQueueGroupAsync("Progress regression", [a.Key, b.Key])).Groups.Single().Id;
            await Program.WaitUntilAsync(() => tagging.GroupChoices.Any(choice => choice.Id == groupId));
            tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Id == groupId);
            tagging.Filter = TaggingQueueFilter.Open;
            Require(AutomationProperties.GetHelpText(tagging.StartButton)?.Contains(Texts.T("tagging.page.lastShown", b.Name, 2, 2), StringComparison.Ordinal) == true,
                "The resume tooltip did not use the group ordinal.");
            CheckProgress(tagging, open: 2, finished: 0);

            viewer = await StartWalkAsync();
            CheckPosition(viewer, b, 2, 2);
            // Resume rotates the walk: finishing its first medium advances to the earlier open group entry.
            viewer.Focus();
            Press(viewer, Key.Enter, primary);
            await Program.WaitUntilAsync(() => State(b) == TagQueueState.Done && viewer.TagPanelController?.Entry.Post?.PostId == a.PostId);
            CheckPosition(viewer, a, 1, 2);
            viewer.Focus();
            Press(viewer, Key.S);
            await Program.WaitUntilAsync(() => State(a) == TagQueueState.Skipped && window.GalleryTagging is { Busy: false, Steps: 2 });
            Require(window.GalleryTagging!.Progress() == (1, 1, 0), "The gallery did not report one finished and one skipped.");
            viewer.Focus();
            Press(viewer, Key.Back);
            await Program.WaitUntilAsync(() => State(a) == TagQueueState.Open && window.GalleryTagging is { Busy: false, Steps: 1 });
            await ReturnAsync(viewer, a);
            Require(tagging.SelectedGroupId == groupId && tagging.Filter == TaggingQueueFilter.Open && tagging.Rows.Count == 1,
                "The group or status filter was lost on return.");
            CheckProgress(tagging, open: 1, finished: 1);

            viewer = await StartWalkAsync();
            CheckPosition(viewer, a, 1, 2);
            viewer.Focus();
            Press(viewer, Key.S);
            await Program.WaitUntilAsync(() => State(a) == TagQueueState.Skipped && window.GalleryTagging is { Busy: false, Steps: 1 });
            await ReturnAsync(viewer, a);
            tagging.Filter = TaggingQueueFilter.Open;
            Require(tagging.Rows.Count == 0 && !tagging.StartButton.IsEffectivelyEnabled, "No open entries should remain in the group.");
            CheckProgress(tagging, open: 0, finished: 1, skipped: 1);
            Texts.SetLanguage("de");
            await SettleAsync(window);
            CheckProgress(tagging, open: 0, finished: 1, skipped: 1);
            Program.CaptureWindow(window, "smoke-tagging-group-progress.png");
            Texts.SetLanguage("en");
            await SettleAsync(window);
            tagging.GroupBox.SelectedIndex = 0;
            CheckProgress(tagging, open: 199, finished: 1, skipped: 1);
            await session.DeleteTagQueueGroupAsync(groupId);
        }
        catch (Exception failure)
        {
            failureBeforeCleanup = failure;
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                Texts.SetLanguage("en");
                tagging.Filter = TaggingQueueFilter.All;
                await session.ClearTagQueueAsync();
                await Program.WaitUntilAsync(() => tagging.Queue.Count == 0);
            }, failure => Console.Error.WriteLine("SMOKE tagging queue walk cleanup: " + failure));
        }

        TagQueueState? State(TagQueueItem item) =>
            session.GetTagQueue()?.Items.FirstOrDefault(entry => entry.Key == item.Key)?.State;

        async Task<GalleryViewer> StartWalkAsync()
        {
            Require(tagging.StartButton.Focus(NavigationMethod.Tab), "The Start button could not take focus.");
            PressSpace(tagging.StartButton);
            await Program.WaitUntilAsync(() => host.IsVisible && window.GalleryTagging != null);
            await GalleryViewerChecksAccess.WaitForLayoutAsync(window);
            return (GalleryViewer)host.Content!;
        }

        async Task ReturnAsync(GalleryViewer viewer, TagQueueItem item)
        {
            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await Program.WaitUntilAsync(() => !host.IsVisible && window.SelectedPage == MainWindow.WorkspacePage.Tagging
                && tagging.SelectedCount == 1 && tagging.ShownList.SelectedItem is TaggingQueueRow row && row.Key == item.Key);
            await window.GalleryCleanupTask;
        }

        static void CheckPosition(GalleryViewer viewer, TagQueueItem item, int number, int total)
        {
            Require(viewer.TagPanelController?.Entry.Post?.PostId == item.PostId
                && viewer.SidePanel.Position.IsEffectivelyVisible
                && AutomationProperties.GetName(viewer.SidePanel.Position) == Texts.T("tagging.position", number, total),
                $"The gallery did not show {item.Name} at queue position {number} of {total}.");
        }
    }

    private static async Task CheckGalleryCommandAsync(MainWindow window, TaggingPage tagging)
    {
        TimeSpan deadline = TimeSpan.FromSeconds(15);
        DesktopSessionHost session = window.SessionHost;
        MediaLibrarySnapshot library = await MediaLibraryIndexBuilder.BuildAsync(session.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync);
        MediaLibraryEntry a = library.Entries.Single(entry => entry.PostId == "100");
        MediaLibraryEntry b = library.Entries.Single(entry => entry.PostId == "200");
        TagQueueItem chosen = TagQueueItem.From(a, DateTimeOffset.UtcNow)!;
        // An older queue may omit the attachment revision while retaining the exact asset and post.
        await session.EnqueueTagQueueAsync([chosen with { AttachmentRevisionKey = null }, TagQueueItem.From(b, DateTimeOffset.UtcNow)!]);
        await ReopenAsync(window, tagging);
        await Program.WaitUntilAsync(() => tagging.Rows.Count == 2 && !tagging.IsBusy);
        SelectOnly(tagging, tagging.Rows.Single(row => row.Key == chosen.Key));

        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        SessionFeedbackStore feedback = (SessionFeedbackStore)typeof(MainWindow).GetField("_feedback", PrivateInstance)!.GetValue(window)!;
        int Warnings() => feedback.Entries.Where(entry => entry.Operation == "tagging.menu" && entry.Category == FeedbackCategory.Warning
            && entry.Summary == Texts.T("tagging.page.openGallery.missing")).Sum(entry => entry.Count);
        Task OpenAsync() => tagging.ExecuteRowCommandAsync(TaggingCommands.OpenGallery).WaitAsync(deadline);

        string file = Path.Combine(a.Directory!, "one.png");
        string manifest = Path.Combine(a.Directory!, MediaStore.ManifestFileName);
        foreach (string path in new[] { file, manifest })
        {
            int warnings = Warnings();
            File.Move(path, path + ".held");
            try
            {
                await OpenAsync();
                Require(!host.IsVisible && Warnings() == warnings + 1, "A missing start entry must warn without opening another medium: " + path);
            }
            finally { File.Move(path + ".held", path); }
        }

        int before = Warnings();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var openManifest = window.TaggingGalleryOpenManifest;
        window.TaggingGalleryOpenManifest = async (directory, token) =>
        {
            if (directory == a.Directory)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return await openManifest(directory, token);
        };
        Task opening = OpenAsync();
        try
        {
            await entered.Task.WaitAsync(deadline);
            Require(!opening.IsCompleted && !host.IsVisible, "The gallery command did not wait at resolution.");
            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Downloads);
            Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads, "Could not leave the original Tagging visit.");
            await ReopenAsync(window, tagging);
        }
        finally
        {
            window.TaggingGalleryOpenManifest = openManifest;
            release.TrySetResult();
            await opening;
        }
        Require(!host.IsVisible && Warnings() == before, "A resolved request from a previous Tagging visit affected the current one.");

        SelectOnly(tagging, tagging.Rows.Single(row => row.Key == chosen.Key));
        await OpenAsync();
        Require(host.IsVisible && host.Content is GalleryViewer viewer && viewer.CaptureState().PostId == a.PostId,
            "A current gallery request must open the selected medium.");
        await GalleryViewerChecksAccess.WaitForLayoutAsync(window);
        GalleryViewer opened = (GalleryViewer)host.Content!;
        WindowState beforeFullscreen = window.WindowState;
        GalleryViewerChecksAccess.Click(opened, "GalleryFullscreen");
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.FullScreen);
        ShellPresentationChecks.RequireFooter(window, visible: false);
        opened.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Program.WaitUntilAsync(() => window.WindowState == beforeFullscreen);
        ShellPresentationChecks.RequireFooter(window, visible: true);
        Require(host.IsVisible && ReferenceEquals(host.Content, opened) && opened.CaptureState().PostId == a.PostId,
            "Leaving fullscreen changed the gallery opened from Tagging.");
        GalleryViewerChecksAccess.Click(opened, "GalleryClose");
        await window.GalleryCleanupTask.WaitAsync(deadline);
        Console.WriteLine("SMOKE tagging-page gallery: missing bytes and missing manifest warn, an old visit cannot open, and a current visit opens the chosen medium.");
    }

    // UX-LEDGER-08: exercise the real selection model and rendered containers. Source-text checks alone
    // cannot catch an ItemsSource reset retaining a row and then appending its replacement to SelectedItems.
    private static async Task CheckSelectionLifecycleAsync(MainWindow window, TaggingPage tagging)
    {
        foreach (TaggingViewMode view in Enum.GetValues<TaggingViewMode>())
        {
            tagging.SetViewMode(view, save: false);
            foreach (int count in new[] { 1, 2 })
            {
                tagging.ClearSelection();
                TagQueueKey[] expected = tagging.Rows.Skip(1).Take(count).Select(row => row.Key).ToArray();
                foreach (TaggingQueueRow row in tagging.Rows.Skip(1).Take(count)) tagging.ShownList.SelectedItems!.Add(row);
                for (int visit = 0; visit < 5; visit++)
                {
                    await ReviewArtifacts.GestureAsync(window, $"tagging-{view}-{count}-visit-{visit}-away",
                        () => SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Downloads),
                        () => window.SelectedPage == MainWindow.WorkspacePage.Downloads, () => SettleAsync(window));
                    await ReviewArtifacts.GestureAsync(window, $"tagging-{view}-{count}-visit-{visit}-back",
                        () => ReopenAsync(window, tagging),
                        () => tagging.IsEffectivelyVisible && tagging.SelectedCount == count, () => SettleAsync(window));
                    CheckSelection(tagging, expected, $"Revisit {visit} in {view}");
                }
                if (count == 2) Program.CaptureWindow(window, $"smoke-tagging-revisit-{view.ToString().ToLowerInvariant()}.png");
                TaggingViewMode other = view == TaggingViewMode.List ? TaggingViewMode.Grid : TaggingViewMode.List;
                tagging.SetViewMode(other, save: false);
                await SettleAsync(window);
                CheckSelection(tagging, expected, "Switch to " + other);
                ListBox hidden = view == TaggingViewMode.List ? tagging.List : tagging.TileGrid;
                Require(hidden.ItemsSource == null && hidden.SelectedItems!.Count == 0, "The hidden view retained rows or selection.");
                tagging.SetViewMode(view, save: false);
                foreach (WorkView workView in Enum.GetValues<WorkView>())
                {
                    tagging.ApplyWorkView(workView);
                    await SettleAsync(window);
                    CheckSelection(tagging, expected, "Work view " + workView);
                }
                tagging.ApplyWorkView(Enum.Parse<WorkView>(window.SessionHost.Config.WorkspaceMode));
            }

            TagQueueKey kept = tagging.Rows[1].Key;
            tagging.Scope = item => item.Key == kept;
            await tagging.ActivateAsync();
            await SettleAsync(window);
            CheckSelection(tagging, [kept], "Narrow scope");
            tagging.Scope = null;
            await tagging.ActivateAsync();
            await SettleAsync(window);
            CheckSelection(tagging, [kept], "Widen scope must not resurrect hidden selections");

            TagQueueSnapshot grouped = await window.SessionHost.CreateTagQueueGroupAsync("Selection regression", [kept]);
            string groupId = grouped.Groups.Single().Id;
            tagging.Refresh();
            tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Id == groupId);
            await SettleAsync(window);
            CheckSelection(tagging, [kept], "Named group");
            tagging.GroupBox.SelectedItem = tagging.GroupChoices.Single(choice => choice.Ungrouped);
            await SettleAsync(window);
            CheckSelection(tagging, [], "Ungrouped drops the hidden selected row");
            tagging.GroupBox.SelectedItem = tagging.GroupChoices[0];
            await SettleAsync(window);
            CheckSelection(tagging, [], "All groups must not resurrect selection");
            await window.SessionHost.DeleteTagQueueGroupAsync(groupId);
            tagging.Refresh();

            tagging.ShownList.SelectedItem = tagging.Rows[1];
            tagging.Filter = TaggingQueueFilter.Skipped;
            await SettleAsync(window);
            CheckSelection(tagging, [], "Empty state filter");
            tagging.Filter = TaggingQueueFilter.All;
            await SettleAsync(window);
            CheckSelection(tagging, [], "Reset filter must not resurrect selection");
            tagging.ShownList.SelectedItem = tagging.Rows[1];
            tagging.ClearSelection();
            await SettleAsync(window);
            CheckSelection(tagging, [], "Explicit clear");
        }
        tagging.SetViewMode(TaggingViewMode.List, save: false);
        Console.WriteLine("SMOKE tagging-selection-lifecycle: 20 revisit cycles; single/multi selection, lead, native model, current row identity, containers, list/grid, work views, scope, groups, filter and clear passed.");
    }

    private static void CheckSelection(TaggingPage tagging, TagQueueKey[] expected, string stage)
    {
        ListBox list = tagging.ShownList;
        TaggingQueueRow[] selected = list.SelectedItems!.Cast<TaggingQueueRow>().ToArray();
        string label = expected.Length == 0 ? string.Empty : Texts.T("media.selection.count", expected.Length.ToString("N0", Texts.Culture));
        Require(selected.Length == expected.Length && selected.Select(row => row.Key).ToHashSet().SetEquals(expected)
            && selected.All(row => tagging.Rows.Any(current => ReferenceEquals(current, row)))
            && list.Selection.SelectedIndexes.Count == expected.Length && tagging.SelectedCount == expected.Length
            && tagging.SelectionText.Text == label && tagging.SelectionBar.IsVisible == (expected.Length > 0)
            && (expected.Length == 0 ? list.SelectedItem == null : (list.SelectedItem as TaggingQueueRow)?.Key == expected[0]),
            $"{stage}: native={selected.Length}, model={list.Selection.SelectedIndexes.Count}, count={tagging.SelectedCount}, label='{tagging.SelectionText.Text}', expected={expected.Length}.");
        for (int index = 0; index < tagging.Rows.Count; index++)
            if (list.ContainerFromIndex(index) is ListBoxItem container)
                Require(container.IsSelected == expected.Contains(tagging.Rows[index].Key), stage + ": container selection disagrees at " + index);
    }

    // Saving the language goes through Settings and returns to Downloads (FooterNavigationChecks.PersistLanguageAsync); the page is
    // a footer destination, so the smoke opens it again the way the user does and expects the same page object with its queue.
    private static async Task ReopenAsync(MainWindow window, TaggingPage tagging)
    {
        SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Tagging);
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Tagging && ReferenceEquals(window.TaggingPage, tagging) && !tagging.IsBusy,
            () => "page " + window.SelectedPage);
        await SettleAsync(window);
    }

    // The list is multi-select and a finished command re-selects its entry (SelectOnly of the page), so a smoke selection replaces it.
    private static void SelectOnly(TaggingPage tagging, TaggingQueueRow row)
    {
        tagging.List.SelectedItems!.Clear();
        tagging.List.SelectedItem = row;
        Require(tagging.List.SelectedItems!.Count == 1, "Selecting one row left " + tagging.List.SelectedItems.Count + " selected.");
    }

    private static void Press(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static void PressSpace(Control control, KeyModifiers modifiers = KeyModifiers.None)
    {
        Press(control, Key.Space, modifiers);
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, KeyModifiers = modifiers });
    }

    private static async Task SetThemeAsync(MainWindow window, string theme)
    {
        if (Config.LoadConfig().Theme != theme)
            window.FindControl<Button>("ThemeToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Styling.ThemeVariant variant = theme == "Light" ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == theme && Application.Current?.RequestedThemeVariant == variant);
    }

    private static async Task SettleAsync(MainWindow window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
