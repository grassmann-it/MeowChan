using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveCommandChecks
{
    internal static async Task RunAsync(ArchivePage page, DataGrid table)
    {
        ArchiveThreadRow[] rows = table.ItemsSource.Cast<ArchiveThreadRow>().Take(3).ToArray();
        Require(rows.Length == 3, "Archive command fixture needs three rows.");
        var oldCheck = page.CanCheckWatch;
        var oldRetry = page.CanRetryWatch;
        var oldRemove = page.CanRemoveWatch;
        var oldChecks = page.CheckSelectedWatches;
        var oldCopy = page.CopySelectedUrls;
        try
        {
            ResourceId[] checkedIds = Array.Empty<ResourceId>();
            string[] copied = Array.Empty<string>();
            page.CanCheckWatch = id => id == rows[0].Summary.Id;
            page.CanRetryWatch = id => id == rows[1].Summary.Id;
            page.CanRemoveWatch = _ => false;
            page.CheckSelectedWatches = (ids, retry) => { checkedIds = ids.ToArray(); return Task.CompletedTask; };
            page.CopySelectedUrls = urls => { copied = urls.ToArray(); return Task.CompletedTask; };
            table.SelectedItems.Clear();
            table.SelectedItems.Add(rows[0]);
            table.SelectedItems.Add(rows[1]);
            await Program.WaitUntilAsync(() => !page.IsBusy);
            Require(table.SelectedItems.Count == 2, "Loading the archive lead collapsed a batch selection.");
            ContextMenu menu = table.ContextMenu!;
            menu.Open(table);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            MenuItem[] items = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
            Require(items.Select(item => (string)item.Tag!).SequenceEqual(new[]
            {
                "thread.read", "gallery.open", "thread.details", "export.open-html-dialog", "selection.copy-links",
                "location.open-folder", "location.open-source", "watch.check", "watch.retry-media", "rules.edit-scope", "watch.remove"
            }), "Archive context menu action order differs from the common thread menu: " + string.Join(", ", items.Select(item => item.Tag)));
            MenuItem Find(string id) => items.Single(item => (string)item.Tag! == id);
            Require(Find("thread.read").Command?.CanExecute(null) == false, "Archive batch selection offered the single-thread reader action.");
            Require(Find("thread.details").Command?.CanExecute(null) == true, "Archive without watch has no local metadata details.");
            Require(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, Texts.T("archive.command.unavailable")) && item.IsEnabled),
                "Unavailable archive commands have no keyboard-accessible explanation.");
            Find("watch.check").Command!.Execute(null);
            Require(checkedIds.SequenceEqual(new[] { rows[0].Summary.Id }), "Archive check did not filter mixed watch eligibility.");
            Find("watch.retry-media").Command!.Execute(null);
            Require(checkedIds.SequenceEqual(new[] { rows[1].Summary.Id }), "Archive retry did not filter mixed media eligibility.");
            // A language refresh replaces row instances. The open menu must keep
            // using its captured selection after that refresh and a new selection.
            Texts.SetLanguage("de");
            await Program.WaitUntilAsync(() => !page.IsBusy);
            ArchiveThreadRow replacement = table.ItemsSource.Cast<ArchiveThreadRow>().First(row => row.Summary.Id == rows[2].Summary.Id);
            table.SelectedItems.Clear(); table.SelectedItems.Add(replacement);
            await Program.WaitUntilAsync(() => !page.IsBusy);
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
            table.ContextMenu?.Close();
        }
        Console.WriteLine("SMOKE archive commands: shared order, selection loading, mixed eligibility, watchless reasons and frozen targets after refresh passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
