using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// Shown-window regression for ListBox typeahead vs reader-owned shortcuts. Input below is
// explicitly synthetic Avalonia routed input, NOT XTest/native keyboard/IME evidence.
// Run via MEOWCHAN_READER_MEDIA_SIDEBAR_ONLY=1 and --reader-ux (private Xvfb runner).
internal static class ReaderSidebarShortcutChecks
{
    private static readonly ResourceId Fixture = new("4chan", "g", "9989");

    internal static async Task SeedAsync(ArchiveCatalog catalog, string media)
    {
        // Three attachments make a duplicate media-step observable (0 -> 2 instead of 1).
        // Shared verified bytes are intentional; distinct positions are real capture bindings.
        CapturedPost[] posts = await StrictMediaFixture.BindAsync(media, Fixture,
            Enumerable.Range(0, 2).Select(i => new CapturedPost((7000 + i).ToString(), i,
                PostFieldPresence.All, Html: "<p>Shortcut routing post</p>", Attachments:
                Enumerable.Range(0, 3).Select(n => new CapturedAttachment(n, "shared", OriginalFileName: "reader.png")).ToArray())));
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Fixture, "https://offline.invalid/g/thread/9989",
            "Shortcut routing fixture", ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, posts));
    }

    internal static async Task RunAsync(MainWindow window)
    {
        Config original = window.SessionHost.Config.Clone();
        ArchivePage page = window.ArchivePage;
        var mediaBefore = page.ReaderMedia;
        var originalPopover = page.TagPopoverAsync
            ?? throw new InvalidOperationException("Production Rate and tag delegate is not connected.");
        int opens = 0;
        string? targetPost = null;
        page.TagPopoverAsync = async (entry, origin, context, current, request) =>
        {
            opens++;
            targetPost = entry.EntryKey.PostId;
            Require(entry.Key.Thread == Fixture && !request.FocusRating, "Wrong rating target/request.");
            await originalPopover(entry, origin, context, current, request);
        };
        ContentControl host = window.FindControl<ContentControl>("ReaderHost")!;
        async Task Commit(Config config)
        {
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(config);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
        }
        try
        {
            Config configured = original.Clone();
            configured.ShortcutBindings = configured.ShortcutBindings
                .Set("media.rating.edit", [ShortcutGesture.Parse("R")])
                .Set("reader.view.hover-enlarge", [ShortcutGesture.Parse("J")]);
            await Commit(configured);
            Require(!ShortcutService.Current.Issues.Any(), "Configured shortcut fixture has conflicts.");
            window.Width = 1920; window.Height = 1080;
            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Archive);
            await Program.WaitUntilAsync(() => !page.IsBusy);
            page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Shortcut routing fixture";
            await page.ApplySearchAsync();
            page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == Fixture);
            // Opening the large reader snapshots the embedded origin, including its assets.
            // Finish that origin's scan before detaching, so close restores usable media.
            await page.MediaLoadTask;
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Sidebar = true, SidebarAlways = true,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMinimum });

            // The detached sidebar is a sibling of the reader pane, unlike the embedded host.
            foreach (bool large in new[] { true, false })
            {
                if (large) SmokeActionClick.Execute(page.FindControl<Button>("ArchiveReadPosts")!);
                else SmokeActionClick.Execute(page.FindControl<Button>("ArchiveCloseReader")!);
                await Program.WaitUntilAsync(() => host.IsVisible == large && page.IsMediaSidebarShown && !page.IsBusy);
                await page.FocusPostAsync("7000");
                await page.MediaLoadTask;
                await Program.WaitUntilAsync(() => page.MediaSidebar!.StripVisible && page.MediaSidebar.StripIndexes.Count == 3);
                await page.MediaSidebarStripTask;
                await Flush(window);
                ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
                ReaderMediaSidebar sidebar = page.MediaSidebar!;
                Require(large ? !sidebar.GetVisualAncestors().Contains(page.FindControl<Grid>("ArchiveReaderPane")!)
                    : sidebar.GetVisualAncestors().Contains(page.FindControl<Grid>("ArchiveReaderPane")!), "Wrong sidebar host ancestry.");
                string mode = large ? "large" : "embedded";
                Control Post() => posts.ContainerFromItem(posts.SelectedItem!) as ListBoxItem
                    ?? throw new InvalidOperationException("Selected post has no materialized ListBoxItem.");
                Control Thumbnail() => sidebar.Strip.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, 0));

                foreach (bool thumbnail in new[] { false, true })
                {
                    page.SelectMediaSidebarIndex(0);
                    await page.MediaSidebarTask;
                    await Flush(window);
                    Control source = thumbnail ? Thumbnail() : Post();
                    string name = mode + (thumbnail ? "-thumbnail" : "-post");
                    Focus(window, source);
                    Require(source is ListBoxItem && source.GetVisualAncestors().Contains(thumbnail ? sidebar.Strip : posts), "Not a real owned list item.");
                    int before = opens;
                    KeyEventArgs rating = Press(source, Key.R);
                    await Program.WaitUntilAsync(() => QuickTagPopover.Current is { IsOpen: true, DecisionRow: not null },
                        () => $"{name}: R popover missing; callback delta={opens - before}, handled={rating.Handled}");
                    QuickTagPopover popover = QuickTagPopover.Current!;
                    await Flush(window);
                    Require(rating.Handled && opens == before + 1 && targetPost == "7000"
                        && popover.DecisionRow!.StarButtons.Count == 5, name + ": R must open Rate and tag exactly once.");
                    Evidence(name + "-rating", new { rating.Handled, opens = opens - before, targetPost });
                    TextBox tagEditor = popover.Frame.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.IsEffectivelyVisible && !box.IsReadOnly)
                        ?? throw new InvalidOperationException("Rate and tag has no editable text field.");
                    await EditorLetters(window, page, tagEditor, () => opens, name + "-tag-editor");
                    Require(ReferenceEquals(popover, QuickTagPopover.Current) && popover.IsOpen && opens == before + 1,
                        name + ": editor reopened/replaced the popover.");
                    popover.Close();
                    await popover.Result;
                    await Flush(window);

                    source = thumbnail ? Thumbnail() : Post();
                    Focus(window, source);
                    bool hover = page.ReaderMedia.HoverEnlarge;
                    KeyEventArgs view = Press(source, Key.J);
                    await Flush(window);
                    Require(view.Handled && page.ReaderMedia.HoverEnlarge != hover, name + ": configured view letter lost to typeahead or toggled twice.");
                    Press(source, Key.J);
                    await Flush(window);
                    Require(page.ReaderMedia.HoverEnlarge == hover, name + ": view toggle failed to restore.");
                    KeyEventArgs step = Press(source, Key.Right, KeyModifiers.Alt | KeyModifiers.Shift);
                    await page.MediaSidebarTask;
                    await Flush(window);
                    Require(step.Handled && page.MediaSidebarIndex == 1 && sidebar.StripCurrent == 1
                        && (posts.SelectedItem as ArchivePostRow)?.Id == "7000", name + ": step must advance once, not move post or skip to 2.");
                    Evidence(name + "-view-and-step", new { view.Handled, index = page.MediaSidebarIndex, sidebar.StripCurrent });
                }

                // A real editor inside the pane also traverses its tunnel (not only popup ownership).
                SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindOpen")!);
                TextBox find = page.FindControl<TextBox>("ReaderFindQuery")!;
                await Program.WaitUntilAsync(() => find.IsEffectivelyVisible);
                await EditorLetters(window, page, find, () => opens, mode + "-find-editor");
                SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindClose")!);
            }

            // Effective-binding check: old media command yields to the list; saved K preempts typeahead.
            configured.ShortcutBindings = configured.ShortcutBindings.Set("reader.sidebar.next-media", [ShortcutGesture.Parse("K")]);
            await Commit(configured);
            Require(!ShortcutService.Current.Issues.Any(), "Remapped step fixture has conflicts.");
            ReaderMediaSidebar panel = page.MediaSidebar!;
            foreach (bool thumbnail in new[] { false, true })
            {
                page.SelectMediaSidebarIndex(0);
                await page.MediaSidebarTask;
                await Flush(window);
                ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
                Control source = thumbnail ? panel.Strip.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, 0))
                    : (Control)posts.ContainerFromItem(posts.SelectedItem!)!;
                Focus(window, source);
                bool oldChordReachedItem = false;
                void AtItem(object? sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Right && !e.Handled && page.MediaSidebarIndex == 0) oldChordReachedItem = true;
                }
                source.AddHandler(InputElement.KeyDownEvent, AtItem, RoutingStrategies.Tunnel);
                try { Press(source, Key.Right, KeyModifiers.Alt | KeyModifiers.Shift); }
                finally { source.RemoveHandler(InputElement.KeyDownEvent, AtItem); }
                await Flush(window);
                Require(oldChordReachedItem, "Old media-step command still intercepted the chord before the list item.");
                // The strip's built-in ListBox bubble handler may still move selection with this
                // modified arrow. That is local selection, NOT evidence of a stale command.
                Evidence("embedded-old-chord-yields-" + (thumbnail ? "thumbnail" : "post"),
                    new { oldChordReachedItem, finalLocalIndex = page.MediaSidebarIndex });
                page.SelectMediaSidebarIndex(0);
                await page.MediaSidebarTask;
                await Flush(window);
                Focus(window, source);
                KeyEventArgs remapped = Press(source, Key.K);
                await page.MediaSidebarTask;
                await Flush(window);
                Require(remapped.Handled && page.MediaSidebarIndex == 1 && panel.StripCurrent == 1, "Saved K must step exactly once.");
                Evidence("embedded-remapped-" + (thumbnail ? "thumbnail" : "post"), new { remapped.Handled, index = page.MediaSidebarIndex });
            }
            SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindOpen")!);
            TextBox remappedEditor = page.FindControl<TextBox>("ReaderFindQuery")!;
            await Program.WaitUntilAsync(() => remappedEditor.IsEffectivelyVisible);
            await EditorLetters(window, page, remappedEditor, () => opens, "embedded-remapped-find-editor");
            SmokeActionClick.Execute(page.FindControl<Button>("ReaderFindClose")!);
            Program.CaptureWindow(window, "smoke-reader-sidebar-shortcuts.png");
            Console.WriteLine("SMOKE reader sidebar shortcuts PASS: large/embedded materialized post and thumbnail R -> one production popover callback/open; J view toggle; media step once; popup/pane editors retain rjk; saved K preempts typeahead and old command yields to local list routing. Synthetic Avalonia routed key/text events, not native keyboard input.");
        }
        finally
        {
            QuickTagPopover.Current?.Close();
            if (host.IsVisible) SmokeActionClick.Execute(page.FindControl<Button>("ArchiveCloseReader")!);
            page.TagPopoverAsync = originalPopover;
            await Commit(original);
            page.ApplyReaderMediaPreferences(mediaBefore);
        }
    }

    private static async Task EditorLetters(MainWindow window, ArchivePage page, TextBox editor, Func<int> opens, string name)
    {
        editor.Text = "";
        Focus(window, editor);
        int before = opens(), index = page.MediaSidebarIndex;
        bool hover = page.ReaderMedia.HoverEnlarge;
        foreach ((Key key, string text) in new[] { (Key.R, "r"), (Key.J, "j"), (Key.K, "k") })
        {
            bool reachedEditor = false;
            void AtEditor(object? sender, KeyEventArgs e) { if (e.Key == key && !e.Handled) reachedEditor = true; }
            editor.AddHandler(InputElement.KeyDownEvent, AtEditor, RoutingStrategies.Tunnel);
            try { Press(editor, key); }
            finally { editor.RemoveHandler(InputElement.KeyDownEvent, AtEditor); }
            // The popover deliberately consumes otherwise-unhandled letters at its frame on
            // bubble. Assert delivery to the editor BEFORE that guard, not final Handled=false.
            Require(reachedEditor, name + ": key never reached editor unhandled: " + key);
            // A routed KeyDown does not generate text. Supply TextInput explicitly; never assign
            // the expected result to Text or describe this as physical typing/native delivery.
            editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        }
        await Flush(window);
        Require(editor.Text == "rjk" && opens() == before && page.MediaSidebarIndex == index
            && page.ReaderMedia.HoverEnlarge == hover, name + ": letters lost or command fired in editor.");
        Evidence(name, new { editor.Text, opens = opens() - before, index });
    }

    private static void Focus(MainWindow window, Control source) => Require(source.IsEffectivelyVisible && source.Focus()
        && source.IsFocused && ReferenceEquals(window.FocusManager?.GetFocusedElement(), source), "Actual focus not acquired: " + source.GetType().Name);
    private static KeyEventArgs Press(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyEventArgs down = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        source.RaiseEvent(down);
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        return down;
    }
    private static async Task Flush(MainWindow window) => await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    private static void Evidence(string check, object result) => Console.WriteLine("SMOKE reader-sidebar-routed " + JsonSerializer.Serialize(new { check, input = "synthetic Avalonia routed events", result }));
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
