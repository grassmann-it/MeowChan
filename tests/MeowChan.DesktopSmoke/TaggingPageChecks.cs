using Avalonia;
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
// (three synthetic queue entries, no media files): the app-menu entry opens it, progress and start label, Delete and Alt+Down from
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

            // The page has a footer button behind Media (Ctrl+1…7 stay as shipped, so it owns no digit) and keeps its ⋯ entry.
            MenuItem menu = window.FindControl<MenuItem>("WorkspaceMenuItem")!;
            MenuItem entry = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Tagging"));
            Button footerButton = window.FindControl<Button>("TaggingNavigationButton") ?? throw new InvalidOperationException("The tagging page has no footer button.");
            Require(Avalonia.Automation.AutomationProperties.GetName(footerButton) == "Tagging" && ToolTip.GetTip(footerButton) is string footerTip
                && footerTip.StartsWith("Tagging", StringComparison.Ordinal), "The tagging footer button lost its accessible name or tooltip.");
            Require(entry.Name == "TaggingMenuItem" && entry.IsEnabled, "The ⋯ entry of the tagging page is missing or disabled.");
            entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Tagging && window.TaggingPage is { IsBusy: false } page
                && page.Rows.Count == 3, () => "page " + window.SelectedPage + ", rows " + window.TaggingPage?.Rows.Count);
            TaggingPage tagging = window.TaggingPage!;
            await SettleAsync(window);
            Require(tagging.ProgressText.Text == "Open: 3 · Finished: 0 · Skipped: 0", "Progress: " + tagging.ProgressText.Text);
            Require(tagging.StartButton.IsEffectivelyEnabled && Avalonia.Automation.AutomationProperties.GetName(tagging.StartButton) == "Start tagging",
                "The start button is not offered for a fresh queue.");
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

            // The context menu lists the row commands with their keys.
            string[] labels = tagging.ListMenu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? "").ToArray();
            Require(labels.SequenceEqual(new[] { "Continue here", "Mark as finished", "Mark as open", "Mark as skipped", "Move up", "Move down", "Remove from queue" }),
                "Context menu: " + string.Join(" | ", labels));

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
            Require(tagging.ProgressText.Text == "Open: 1 · Finished: 1 · Skipped: 0", "Progress after a decision: " + tagging.ProgressText.Text);
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
            await ReopenAsync(window, entry, tagging);
            Require(tagging.ProgressText.Text == "Offen: 1 · Erledigt: 1 · Übersprungen: 0", "German progress: " + tagging.ProgressText.Text);
            Program.CaptureWindow(window, "smoke-tagging-page-dark-de.png");
            await SetThemeAsync(window, "Light");
            await FooterNavigationChecks.PersistLanguageAsync(window, "en");
            await ReopenAsync(window, entry, tagging);

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
            Console.WriteLine("SMOKE tagging-page: app-menu entry, progress, Delete/Alt+Down, context menu, stamp, filter, work views, clear; Light/Dark EN/DE captures.");
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            await FooterNavigationChecks.PersistLanguageAsync(window, language);
        }
    }

    // Saving the language goes through Settings and returns to Downloads (FooterNavigationChecks.PersistLanguageAsync); the page is
    // an app-menu destination, so the smoke opens it again the way the user does and expects the same page object with its queue.
    private static async Task ReopenAsync(MainWindow window, MenuItem entry, TaggingPage tagging)
    {
        entry.Command!.Execute(null);
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
