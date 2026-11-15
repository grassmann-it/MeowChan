using Avalonia;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_TAGGING_PAGE_ONLY. PT2: the tagging page in the real main window over the real host and tag-queue.json
// (three synthetic queue entries, no media files): the footer button opens it, progress and start label, Delete and Alt+Down from
// the list, the context menu, the stamp editor, the state filter, the clear question, the three work views and Light/Dark/English/
// German captures. "Start tagging" uses PT1's walk; with no saved files it only says that nothing is available. Managed input only.
internal static class TaggingPageChecks
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");

    private static TagQueueItem Item(int n) => new(Thread, "/media/123", "asset-" + n, n.ToString("x") + new string('b', 63), 100 + n, "media-" + n,
        "original", "p" + n, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open,
        new DateTimeOffset(2026, 11, 14, 8, 0, n, TimeSpan.Zero));

    internal static async Task RunAsync(MainWindow window)
    {
        string language = Texts.Language;
        string mode = window.SessionHost.Config.WorkspaceMode;
        try
        {
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
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
            Require(tagging.ProgressText.Text == Texts.T("tagging.page.progress", 3, 0, 0), "Progress: " + tagging.ProgressText.Text);
            await Program.WaitUntilAsync(() => tagging.StartButton.IsEffectivelyEnabled
                && Avalonia.Automation.AutomationProperties.GetName(tagging.StartButton) == Texts.T("command.tagging.start"),
                () => "start enabled=" + tagging.StartButton.IsEffectivelyEnabled + ", name='"
                    + Avalonia.Automation.AutomationProperties.GetName(tagging.StartButton) + "', host=" + window.SessionHost.State);
            Program.CaptureWindow(window, "smoke-tagging-page-light-en.png");

            // Delete from the list removes the selected entry; Alt+Down moves the lead entry one place down.
            SelectOnly(tagging, tagging.Rows[1]);
            await SettleAsync(window);
            Control row = (Control)tagging.List.ContainerFromIndex(1)!;
            row.Focus();
            Press(row, Key.Delete);
            await Program.WaitUntilAsync(() => window.SessionHost.GetTagQueue()!.Count == 2 && tagging.Rows.Count == 2 && !tagging.IsBusy);
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
            tagging.ShowListMenu();
            await SettleAsync(window);
            Require(tagging.OpenListMenu is { IsOpen: true }, "The context menu did not open on the list.");
            tagging.OpenListMenu!.Close();
            await SettleAsync(window);

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
            MeowChan.Desktop.Controls.ContextMenuModel stateMenu = tagging.StateMenuModel() ?? throw new InvalidOperationException("No state menu for a selection.");
            Require(stateMenu.Entries.Select(item => item.Label).SequenceEqual(new[]
            {
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.mark-done"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.mark-open"),
                MeowChan.Desktop.Commands.CommandCatalog.Label("tagging.mark-skipped")
            }),
                "State menu: " + string.Join(" | ", stateMenu.Entries.Select(item => item.Label)));
            // "Set state" (tagging.selection.state) sits on the bar in Advanced only; Barebones and Simple keep it under More, so the menu has no
            // button to open under there. Open it in Advanced and return to the work view the run started in.
            if (mode != "Advanced")
            {
                await PresentationChecks.SwitchModeAsync(window, "Advanced");
                await SettleAsync(window);
            }
            tagging.ShowStateMenu();
            await SettleAsync(window);
            Require(tagging.OpenStateMenu is { IsOpen: true }, "The state menu did not open under its button.");
            tagging.OpenStateMenu!.Close();
            await SettleAsync(window);
            if (mode != "Advanced")
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                await SettleAsync(window);
            }
            tagging.SetViewMode(TaggingViewMode.Grid, save: true);
            await SettleAsync(window);
            Require(tagging.ViewMode == TaggingViewMode.Grid && tagging.TileGrid.IsVisible && !tagging.List.IsVisible && tagging.ViewToggle.SelectedIndex == 1,
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
            Require(tagging.List.IsVisible && !tagging.TileGrid.IsVisible, "Back to the list failed.");
            await Program.WaitUntilAsync(() => Config.LoadConfig().TaggingPageView == "List", () => "saved view " + Config.LoadConfig().TaggingPageView);

            // Stamp (wave3 glue e224cd02): Enter in the field adds the name to the window's shared stamp, which is saved in the settings
            // (Config.TagStamp) and shown by the page; the stamp field of tag-queue.json stays unused.
            tagging.StampInput.Text = "surface";
            tagging.StampInput.Focus();
            Press(tagging.StampInput, Key.Enter);
            await Program.WaitUntilAsync(() => window.QuickTagStampSet.ToNames().SequenceEqual(new[] { "surface" })
                && window.SessionHost.Config.TagStamp.SequenceEqual(new[] { "surface" }) && tagging.StampChips.Children.Count == 1,
                () => "stamp " + string.Join(",", window.QuickTagStampSet.ToNames()) + ", saved " + string.Join(",", window.SessionHost.Config.TagStamp)
                    + ", chips " + tagging.StampChips.Children.Count + ", input '" + tagging.StampInput.Text + "' enabled " + tagging.StampInput.IsEnabled + ", status " + tagging.StatusText.Text);
            Require(window.SessionHost.GetTagQueue()!.Stamp.Count == 0, "The stamp must not be written into tag-queue.json any more.");

            // Mark one finished, filter by state, then the work views.
            SelectOnly(tagging, tagging.Rows[0]);
            await tagging.ExecuteRowCommandAsync("tagging.mark-done");
            Require(tagging.ProgressText.Text == Texts.T("tagging.page.progress", 1, 1, 0), "Progress after a decision: " + tagging.ProgressText.Text);
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
            Require(tagging.ProgressText.Text == Texts.T("tagging.page.progress", 1, 1, 0), "German progress: " + tagging.ProgressText.Text);
            Program.CaptureWindow(window, "smoke-tagging-page-dark-de.png");
            await SetThemeAsync(window, "Light");
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
            await ReopenAsync(window, tagging);

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
            Program.CaptureWindow(window, "smoke-tagging-page-empty.png");
            Console.WriteLine("SMOKE tagging-page: footer button and More entry, progress, Delete/Alt+Down, context menu, selection bar, grid view and switch, stamp, filter, work views, clear; Light/Dark EN/DE captures.");
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            await FooterNavigationChecks.PersistLanguageAsync(window, language);
        }
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
