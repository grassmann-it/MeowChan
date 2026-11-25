using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// R2 (14.11.2026): the reader's "Thread" menu in both hosts in a private profile. The toolbar has the menu button before the settings gear (RL1)
// and no capture button; the menu lists the thread actions in their order; an unwatched thread keeps the entries that need a watch
// disabled with the reason in the tip. Available entries run through the shell callbacks with the open thread as target.
// The "Show new posts automatically" entry and its status-line text are covered by the reader live smoke.
internal static class ReaderThreadMenuChecks
{
    private const string BookmarkManager = "post-bookmarks.open-manager";

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
            const string title = "Thread menu fixture";
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, url, title, ThreadSourceState.Active, DateTimeOffset.UtcNow, profile,
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
                SelectionDetailsRequested = target => calls.Add("details:" + target.Thread),
                OpenPostBookmarks = () => calls.Add("bookmarks"),
                ShowThreadInMediaRequested = (target, threadTitle) => calls.Add("media:" + target.Thread + ":" + threadTitle)
            };
            window = new Window { Width = 1440, Height = 820, Content = page, Title = "Private reader thread menu" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread != null);

            Button open = page.FindControl<Button>("ReaderThreadOpen")!;
            Button gear = page.FindControl<Button>("ReaderSettingsOpen")!;
            MenuItem[] Items() => page.BuildReaderThreadMenu().Items.OfType<MenuItem>().ToArray();
            MenuItem Item(string command) => Items().Single(item => AutomationProperties.GetAutomationId(item) == command);
            foreach (bool detached in new[] { false, true })
            {
                if (detached) window.Content = page.DetachReader();
                await SmokeDispatcher.SettleAsync();

                Require(page.FindControl<Button>("CapturePostsButton") == null, "The capture button is still on the reader bar.");
                Require(ReferenceEquals(TopLevel.GetTopLevel(open), window)
                    && open.Content as string == Texts.T("reader.thread.menu") && open.IsEffectivelyVisible,
                    "The hosted Thread button is missing or unlabeled.");
                Require(gear.IsEffectivelyVisible && gear.Bounds.Left > open.Bounds.Left && gear.Bounds.Top == open.Bounds.Top,
                    "The settings gear does not stand right of the Thread button.");
                if (detached) Program.CaptureWindow(window, "smoke-reader-thread-menu-bar.png");

                watched = false;
                Require(Items().Select(AutomationProperties.GetAutomationId).SequenceEqual(ArchivePage.ReaderThreadMenuIds),
                    "The Thread menu lists other entries.");
                Require(page.BuildReaderThreadMenu().Items.OfType<Separator>().Count() == 2, "The Thread menu must have two separators.");
                MenuItem capture = Item("archive.capture-toggle");
                Require(capture.ToggleType == MenuItemToggleType.CheckBox
                    && capture.IsChecked == page.CurrentThread!.Summary.CaptureEnabled, "The capture entry's check mark is not the capture state.");
                foreach (string command in new[] { "watch.check", "watch.retry-media", "rules.edit-scope", "watch.remove" })
                {
                    MenuItem item = Item(command);
                    Require(!item.IsEnabled && ToolTip.GetTip(item) is string tip
                        && tip.Contains(Texts.T("archive.command.noWatch"), StringComparison.Ordinal),
                        command + " is enabled without a watch or does not name the reason.");
                }
                foreach (string command in new[] { "archive.capture-toggle", "thread.details", "thread.show-in-media", "location.open-folder", "location.open-source", "selection.copy-links" })
                    Require(Item(command).IsEnabled, command + " is disabled although it needs no watch.");

                if (detached) RequireBookmarkManagerUnavailable(Item(BookmarkManager));
                else
                {
                    MenuItem manager = Item(BookmarkManager);
                    Require(manager.IsEnabled, "The embedded bookmark manager is disabled without a watch.");
                    calls.Clear();
                    SmokeActionClick.Execute(manager);
                    Require(calls.SequenceEqual(new[] { "bookmarks" }),
                        "The embedded bookmark manager did not activate exactly once without a watch.");
                }

                watched = true;
                Require(Items().All(item => item.IsEnabled == (!detached || AutomationProperties.GetAutomationId(item) != BookmarkManager)),
                    "The Thread menu has unexpected availability for a watched, idle thread.");
                if (detached) RequireBookmarkManagerUnavailable(Item(BookmarkManager));
                bool captureBefore = page.CurrentThread!.Summary.CaptureEnabled;
                Dictionary<string, string> expected = new()
                {
                    ["archive.capture-toggle"] = "capture:7300:" + !captureBefore, ["watch.check"] = "check:7300", ["watch.retry-media"] = "retry:7300",
                    ["rules.edit-scope"] = "rules:7300", [BookmarkManager] = "bookmarks", ["thread.details"] = "details:7300",
                    ["thread.show-in-media"] = "media:7300:" + title, ["location.open-folder"] = "folder:7300",
                    ["location.open-source"] = "browser:7300", ["selection.copy-links"] = "copy:" + url, ["watch.remove"] = "remove:7300"
                };
                foreach (string command in ArchivePage.ReaderThreadMenuIds)
                {
                    if (detached && command == BookmarkManager) continue;
                    calls.Clear();
                    SmokeActionClick.Execute(Item(command));
                    await Program.WaitUntilAsync(() => calls.Count > 0, () => command + " did nothing.");
                    Require(calls.Contains(expected[command]), command + " reached the shell as " + string.Join("|", calls) + ", expected " + expected[command] + ".");
                    await Program.WaitUntilAsync(() => !page.IsBusy, () => "busy after " + command);
                }
                Console.WriteLine("SMOKE reader thread menu " + (detached ? "detached" : "embedded") + ": watch gates, bookmark manager and callbacks passed.");
            }

            Texts.SetLanguage("de");
            try
            {
                await Program.WaitUntilAsync(() => open.Content as string == Texts.T("reader.thread.menu"));
                Require(Item("watch.check").Header as string == Texts.T("command.watch.check"), "The Thread menu was not localized.");
                RequireBookmarkManagerUnavailable(Item(BookmarkManager));
            }
            finally { Texts.SetLanguage("en"); }
            Console.WriteLine("SMOKE reader thread menu: toolbar, entry order and German labels passed.");
        }
        finally
        {
            page?.EndLifetime(); window?.Close();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static void RequireBookmarkManagerUnavailable(MenuItem manager)
    {
        string reason = Texts.T("reader.thread.bookmarksUnavailable");
        Require(!manager.IsEnabled && manager.Command?.CanExecute(manager.CommandParameter) == false
            && ToolTip.GetShowOnDisabled(manager) && ToolTip.GetTip(manager) is string tip
            && tip.Contains(reason, StringComparison.Ordinal)
            && AutomationProperties.GetHelpText(manager)?.Contains(reason, StringComparison.Ordinal) == true,
            "The detached bookmark manager must be unavailable with a readable localized reason.");
    }
}
