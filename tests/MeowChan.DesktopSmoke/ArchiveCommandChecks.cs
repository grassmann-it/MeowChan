using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using MeowChan.Desktop.Commands;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveCommandChecks
{
    internal static async Task RunAsync(ArchivePage page, DataGrid table)
    {
        await ArchiveMenuLayoutChecks.RunAsync(page, table);
        ArchiveThreadRow[] rows = table.ItemsSource.Cast<ArchiveThreadRow>().Take(3).ToArray();
        Require(rows.Length == 3, "Archive command fixture needs three rows.");
        var oldCheck = page.CanCheckWatch;
        var oldRetry = page.CanRetryWatch;
        var oldRemove = page.CanRemoveWatch;
        var oldChecks = page.CheckSelectedWatches;
        var oldCopy = page.CopySelectedUrls;
        var oldDelete = page.DeletePermanentlyRequested;
        try
        {
            ResourceId[] checkedIds = Array.Empty<ResourceId>();
            string[] copied = Array.Empty<string>();
            ResourceId[] deleted = Array.Empty<ResourceId>();
            page.DeletePermanentlyRequested = (ids, _) => { deleted = ids.ToArray(); return Task.CompletedTask; };
            page.CanCheckWatch = id => id == rows[0].Summary.Id;
            page.CanRetryWatch = id => id == rows[1].Summary.Id;
            page.CanRemoveWatch = _ => false;
            page.CheckSelectedWatches = (ids, retry) => { checkedIds = ids.ToArray(); return Task.CompletedTask; };
            page.CopySelectedUrls = urls => { copied = urls.ToArray(); return Task.CompletedTask; };
            // Establish the lead explicitly after the native menu-layout matrix.
            await SelectionChecks.WaitRow(table, rows[0]);
            await SelectionChecks.PressAsync((MainWindow)TopLevel.GetTopLevel(page)!, SelectionChecks.Row(table, rows[0]));
            table.SelectedItems.Add(rows[1]);
            await Program.WaitUntilAsync(() => !page.IsBusy);
            Require(table.SelectedItems.Count == 2, "Loading the archive lead collapsed a batch selection.");
            ContextMenu menu = table.ContextMenu!;
            table.Focus();
            table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Program.WaitUntilAsync(() => menu.IsOpen);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            MenuItem[] items = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
            Require(items.Select(item => (string)item.Tag!).SequenceEqual(new[]
            {
                "thread.favorite-toggle", "thread.read", "gallery.open", "thread.details", "export.open-html-dialog", "selection.copy-links",
                "location.open-folder", "location.open-source", "watch.check", "watch.retry-media", "rules.edit-scope", "watch.remove", "archive.delete-permanently"
            }), "Archive context menu action order differs from the common thread menu: " + string.Join(", ", items.Select(item => item.Tag)));
            MenuItem Find(string id) => items.Single(item => (string)item.Tag! == id);
            Require(Find("thread.read").Command?.CanExecute(null) == false, "Archive batch selection offered the single-thread reader action.");
            Require(Find("thread.details").Command?.CanExecute(null) == true, "Archive without watch has no local metadata details.");
            Require(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, Texts.T("archive.command.unavailable")) && item.IsEnabled),
                "Unavailable archive commands have no keyboard-accessible explanation.");
            Require(Equals(Find("export.open-html-dialog").Header, Texts.T("archive.command.batchCount", CommandCatalog.Label("export.open-html-dialog"), 2)) &&
                Equals(Find("watch.check").Header, Texts.T("archive.command.batchCount", CommandCatalog.Label("watch.check"), 1)) &&
                Equals(Find("watch.remove").Header, Texts.T("archive.command.batchCount", CommandCatalog.Label("watch.remove"), 0)),
                "Archive actions do not show their distinct eligible target counts.");
            Require(Find("gallery.open").Header!.ToString()!.Contains(rows[0].Summary.Id.Thread!) &&
                AutomationProperties.GetName(Find("gallery.open")) == Find("gallery.open").Header!.ToString(),
                "Lead action does not expose its actual target visibly and through automation.");
            Find("watch.check").Command!.Execute(null);
            Require(checkedIds.SequenceEqual(new[] { rows[0].Summary.Id }), "Archive check did not filter mixed watch eligibility.");
            Find("watch.retry-media").Command!.Execute(null);
            Require(checkedIds.SequenceEqual(new[] { rows[1].Summary.Id }), "Archive retry did not filter mixed media eligibility.");
            page.CanCheckWatch = id => id == rows[2].Summary.Id;
            await Program.WaitUntilAsync(() => !Find("watch.check").IsEnabled);
            checkedIds = Array.Empty<ResourceId>();
            Find("watch.check").Command!.Execute(null);
            Require(checkedIds.Length == 0 && Equals(Find("watch.check").Header,
                Texts.T("archive.command.batchCount", CommandCatalog.Label("watch.check"), 0)),
                "Changing eligibility did not update the count or admitted a target outside the frozen selection.");
            await ArchiveMenuLayoutChecks.CheckBoundsAsync(menu, "en-live-eligibility");
            // A language refresh replaces row instances. The open menu must keep
            // using its captured selection after that refresh and a new selection.
            Find("gallery.open").Focus(NavigationMethod.Tab);
            await Program.WaitUntilAsync(() => Find("gallery.open").IsFocused);
            Texts.SetLanguage("de");
            await Program.WaitUntilAsync(() => !page.IsBusy);
            ArchiveThreadRow replacement = table.ItemsSource.Cast<ArchiveThreadRow>().First(row => row.Summary.Id == rows[2].Summary.Id);
            table.SelectedItems.Clear(); table.SelectedItems.Add(replacement);
            await Program.WaitUntilAsync(() => !page.IsBusy);
            await Program.WaitUntilAsync(() => Find("gallery.open").Header!.ToString()!.StartsWith(CommandCatalog.Label("gallery.open"), StringComparison.Ordinal));
            await ArchiveMenuLayoutChecks.CheckBoundsAsync(menu, "de-live-language-frozen-selection");
            Require(menu.IsOpen && Find("gallery.open").IsFocused && items.All(menu.Items.Contains),
                "Resizing the open archive menu replaced its commands or lost keyboard focus.");
            Find("archive.delete-permanently").Command!.Execute(null);
            Require(deleted.SequenceEqual(rows.Take(2).Select(row => row.Summary.Id)), "Permanent deletion retargeted the open menu selection.");
            Find("selection.copy-links").Command!.Execute(null);
            Require(copied.SequenceEqual(rows.Take(2).Select(row => row.Summary.Url)), "Open archive menu retargeted copied links after refresh.");
            Find("watch.retry-media").Command!.Execute(null);
            Require(checkedIds.SequenceEqual(new[] { rows[1].Summary.Id }), "Open archive menu retargeted retry after refresh.");
            menu.Close();
            Texts.SetLanguage("en");
            await Program.WaitUntilAsync(() => !page.IsBusy);
        }
        finally
        {
            page.CanCheckWatch = oldCheck; page.CanRetryWatch = oldRetry; page.CanRemoveWatch = oldRemove;
            page.CheckSelectedWatches = oldChecks; page.CopySelectedUrls = oldCopy;
            page.DeletePermanentlyRequested = oldDelete;
            table.ContextMenu?.Close();
        }
        await CheckPaginationKeysAsync(page, table);
        Console.WriteLine("SMOKE archive commands: shared order, selection loading, mixed eligibility, watchless reasons and frozen targets after refresh passed.");
    }

    private static async Task CheckPaginationKeysAsync(ArchivePage page, DataGrid table)
    {
        ResourceId[] firstPage = table.ItemsSource.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).ToArray();
        Require(firstPage.Length == 50 && page.FindControl<Button>("NextThreadsButton")!.IsEnabled,
            "Pagination fixture must cross a results page.");
        TextBox search = page.FindControl<TextBox>("ArchiveSearchText")!;
        void Key(Control source, Key key, KeyModifiers modifiers) => source.RaiseEvent(new KeyEventArgs
            { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Key(search, Avalonia.Input.Key.PageDown, KeyModifiers.Alt);
        Require(table.ItemsSource.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).SequenceEqual(firstPage),
            "Alt+PageDown escaped the search input.");
        table.Focus();
        Key(table, Avalonia.Input.Key.PageDown, KeyModifiers.Alt);
        await Program.WaitUntilAsync(() => !page.IsBusy && !page.FindControl<Button>("NextThreadsButton")!.IsEnabled);
        Require(table.SelectedItems.Count == 0, "Changing result pages retained hidden selected targets.");
        Key(table, Avalonia.Input.Key.PageDown, KeyModifiers.Alt);
        Require(!page.IsBusy, "Last-page shortcut started another request.");
        Key(table, Avalonia.Input.Key.PageUp, KeyModifiers.Alt);
        await Program.WaitUntilAsync(() => !page.IsBusy && !page.FindControl<Button>("PreviousThreadsButton")!.IsEnabled);
        Require(table.ItemsSource.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).SequenceEqual(firstPage),
            "Page shortcut did not use the existing ordered navigation.");
        Key(table, Avalonia.Input.Key.A, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        Require(table.SelectedItems.Count == 50, "Select all escaped the current 50-row results page.");
        table.SelectedItems.Clear();
        await Program.WaitUntilAsync(() => !page.IsBusy);
        Console.WriteLine("SMOKE archive pagination: scoped Alt+PageUp/Down, boundaries, input shielding, page selection and Ctrl/Cmd+A passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
