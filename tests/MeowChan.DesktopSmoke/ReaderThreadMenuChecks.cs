using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// R2 (14.11.2026): the reader's "Thread" menu in a private profile with a detached reader. The toolbar has the menu button after "View"
// and no capture button; the menu lists the thread actions in their order; an unwatched thread keeps the entries that need a watch
// disabled with the reason in the tip, a watched one runs every entry through the shell callbacks with the open thread as target.
// The "Show new posts automatically" entry and its status-line text are covered by the reader live smoke.
internal static class ReaderThreadMenuChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-reader-thread-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ArchivePage? page = null;
        Window? window = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            ResourceId id = new("4chan", "g", "7300");
            string url = "https://boards.4chan.org/g/thread/7300";
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, url, "Thread menu fixture", ThreadSourceState.Active, DateTimeOffset.UtcNow, profile,
                Enumerable.Range(7300, 3).Select((post, index) => new CapturedPost(post.ToString(), index, PostFieldPresence.All,
                    Html: "<p>Saved post " + post + "</p>")).ToArray()));
            bool watched = false;
            List<string> calls = [];
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (target, enabled) => { calls.Add("capture:" + target.Thread + ":" + enabled); return Task.CompletedTask; }, _ => { })
            {
                CanRemoveWatch = _ => watched,
                CheckSelectedWatches = (ids, retry) => { calls.Add((retry ? "retry:" : "check:") + string.Join(",", ids.Select(item => item.Thread))); return Task.CompletedTask; },
                CopySelectedUrls = urls => { calls.Add("copy:" + string.Join(",", urls)); return Task.CompletedTask; },
                RemoveSelectedWatches = ids => { calls.Add("remove:" + string.Join(",", ids.Select(item => item.Thread))); return Task.CompletedTask; },
                SelectionLeadAction = (target, action) => calls.Add(action + ":" + target.Thread),
                SelectionDetailsRequested = target => calls.Add("details:" + target.Thread)
            };
            Control pane = page.DetachReader();
            window = new Window { Width = 1440, Height = 820, Content = pane, Title = "Private reader thread menu" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread != null);

            Button open = page.FindControl<Button>("ReaderThreadOpen")!;
            Require(page.FindControl<Button>("CapturePostsButton") == null, "The capture button is still on the reader bar.");
            Require(open.Content as string == Texts.T("reader.thread.menu") && open.IsEffectivelyVisible, "The Thread button is missing or unlabeled.");
            Button view = page.FindControl<Button>("ReaderViewOpen")!;
            Require(open.Bounds.Left > view.Bounds.Left && open.Bounds.Top == view.Bounds.Top, "The Thread button does not stand next to View.");
            Program.CaptureWindow(window, "smoke-reader-thread-menu-bar.png");

            MenuItem[] Items() => page!.BuildReaderThreadMenu().Items.OfType<MenuItem>().ToArray();
            MenuItem Item(string command) => Items().Single(item => Equals(item.Tag, command));
            Require(Items().Select(item => (string?)item.Tag).SequenceEqual(ArchivePage.ReaderThreadMenuIds), "The Thread menu lists other entries.");
            Require(page.BuildReaderThreadMenu().Items.OfType<Separator>().Count() == 2, "The Thread menu has not two separators.");
            Require(Item("archive.capture-toggle").ToggleType == MenuItemToggleType.CheckBox
                && Item("archive.capture-toggle").IsChecked == page.CurrentThread!.Summary.CaptureEnabled, "The capture entry's check mark is not the capture state.");

            // Unwatched: what needs a watch is disabled and says why, the rest works.
            foreach (string command in new[] { "watch.check", "watch.retry-media", "rules.edit-scope", "watch.remove" })
                Require(!Item(command).IsEnabled && ToolTip.GetTip(Item(command)) is string tip && tip.Contains(Texts.T("archive.command.noWatch"), StringComparison.Ordinal),
                    command + " is enabled without a watch or does not name the reason.");
            foreach (string command in new[] { "archive.capture-toggle", "thread.details", "location.open-source", "selection.copy-links" })
                Require(Item(command).IsEnabled, command + " is disabled although it needs no watch.");

            // Watched: every entry runs with the open thread as its target.
            watched = true;
            Require(Items().All(item => item.IsEnabled), "A Thread menu entry is disabled for a watched, idle thread.");
            bool captureBefore = page.CurrentThread!.Summary.CaptureEnabled;
            Dictionary<string, string> expected = new()
            {
                ["archive.capture-toggle"] = "capture:7300:" + !captureBefore, ["watch.check"] = "check:7300", ["watch.retry-media"] = "retry:7300",
                ["rules.edit-scope"] = "rules:7300", ["thread.details"] = "details:7300", ["location.open-folder"] = "folder:7300",
                ["location.open-source"] = "browser:7300", ["selection.copy-links"] = "copy:" + url, ["watch.remove"] = "remove:7300"
            };
            foreach (string command in ArchivePage.ReaderThreadMenuIds)
            {
                calls.Clear();
                Item(command).Command!.Execute(null);
                await Program.WaitUntilAsync(() => calls.Count > 0, () => command + " did nothing.");
                Require(calls.Contains(expected[command]), command + " reached the shell as " + string.Join("|", calls) + ", expected " + expected[command] + ".");
                await Program.WaitUntilAsync(() => !page.IsBusy, () => "busy after " + command);
            }

            Texts.SetLanguage("de");
            try
            {
                await Program.WaitUntilAsync(() => open.Content as string == Texts.T("reader.thread.menu"));
                Require(Item("watch.check").Header as string == Texts.T("command.watch.check"), "The Thread menu was not localized.");
            }
            finally { Texts.SetLanguage("en"); }
            Console.WriteLine("SMOKE reader thread menu: button after View, no capture button, entry order, unwatched reasons, watched callbacks and German labels passed.");
        }
        finally
        {
            page?.EndLifetime(); window?.Close();
            Directory.Delete(profile, recursive: true);
        }
    }
}
