using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

// Diagnostic path first runs against the unchanged Full44 product. Assertions
// accumulate so a failed content key does not hide the native ownership probes.
internal static class ReaderShortcutContextChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config original = window.SessionHost.Config.Clone();
        List<string> failures = new();
        void Check(bool ok, string name, object? evidence = null)
        {
            Console.WriteLine("SMOKE shortcut-context " + JsonSerializer.Serialize(new { name, ok, evidence }));
            if (!ok) failures.Add(name);
        }
        async Task Commit(Config value)
        {
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(value);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
        }
        try
        {
            await ArchivePaginationFocusChecks.RunAsync(window, Check);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_PAGINATION_FOCUS_ONLY") == "1")
            {
                if (failures.Count > 0) throw new InvalidOperationException("Pagination focus failures: " + string.Join(", ", failures));
                Console.WriteLine("SMOKE pagination focus: retained origin and deliberate new focus passed.");
                return;
            }
            window.Width = 1440; window.Height = 920;
            Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
            ArchivePage page = window.ArchivePage;
            await Program.WaitUntilAsync(() => !page.IsBusy);
            Click(page.FindControl<Button>("ArchiveLayoutTable")!);
            DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
            await Program.WaitUntilAsync(() => table.IsEffectivelyVisible && table.ItemsSource.Cast<object>().Count() >= 2);
            ArchiveThreadRow[] rows = table.ItemsSource.Cast<ArchiveThreadRow>().Take(2).ToArray();
            await SelectionChecks.WaitRow(table, rows[0]);
            await SelectionChecks.PressAsync(window, SelectionChecks.Row(table, rows[0]));
            table.SelectedItems.Add(rows[1]);
            await Program.WaitUntilAsync(() => !page.IsBusy);
            ContextMenu menu = table.ContextMenu!;
            table.Focus();
            table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Program.WaitUntilAsync(() => menu.IsOpen);
            MenuItem Find(string id) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, id));
            MenuItem copy = Find("selection.copy-links"), read = Find("thread.read"), lead = Find("thread.details");
            string copyLabel = copy.Header!.ToString()!, leadLabel = lead.Header!.ToString()!;
            Check(table.SelectedItems.Count == 2 && !read.IsEnabled && read.Command?.CanExecute(null) == false,
                "menu-initial-disabled-batch", new { copyLabel, leadLabel });
            // Observe at publication, before the archive menu's 250ms timer can
            // mask a transient loss of the captured target label or AT name.
            void Published()
            {
                Check(Equals(copy.Header, copyLabel) && AutomationProperties.GetName(copy) == copyLabel,
                    "menu-publish-count-label", new { header = copy.Header, name = AutomationProperties.GetName(copy) });
                Check(Equals(lead.Header, leadLabel) && AutomationProperties.GetName(lead) == leadLabel,
                    "menu-publish-lead-label", new { header = lead.Header, name = AutomationProperties.GetName(lead) });
                Check(copy.InputGesture?.Key == Key.F8 && AutomationProperties.GetHelpText(copy)?.Contains("F8") == true,
                    "menu-publish-gesture-help", new { gesture = copy.InputGesture?.ToString(), help = AutomationProperties.GetHelpText(copy) });
                Check(!read.IsEnabled && read.Command?.CanExecute(null) == false, "menu-publish-disabled-target");
            }
            Config menus = original.Clone();
            menus.ShortcutBindings = menus.ShortcutBindings.Set("selection.copy-links", new[] { ShortcutGesture.Parse("Primary+Shift+F8") });
            ShortcutService.Changed += Published;
            try { await Commit(menus); }
            finally { ShortcutService.Changed -= Published; menu.Close(); }
            table.Focus();
            table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Program.WaitUntilAsync(() => menu.IsOpen);
            Check(Find("selection.copy-links").InputGesture?.Key == Key.F8
                && Equals(Find("selection.copy-links").Header, copyLabel), "menu-new-materialization");
            menu.Close();

            Click(page.FindControl<Button>("ArchiveLayoutList")!);
            page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Reader mixed navigation";
            await page.ApplySearchAsync();
            page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
            Click(page.FindControl<Button>("ArchiveReadPosts")!);
            await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible
                && !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "9998");
            await page.MediaLoadTask;
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            Button next = page.FindControl<Button>("NextPostsButton")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            string? Selected() => (posts.SelectedItem as ArchivePostRow)?.Id;
            async Task Probe(Control source, Key key, bool expectedNavigation, string name)
            {
                await page.FocusPostAsync("1000");
                Control requestedSource = source;
                // ListBox itself is not a focus target. Exercise the real
                // materialized post container, as native keyboard input does.
                if (ReferenceEquals(source, posts))
                    source = posts.ContainerFromItem(posts.SelectedItem!) as Control
                        ?? throw new InvalidOperationException("Post1000 has no materialized focus container.");
                Task before = page.ReaderNavigationTask;
                bool focusRequested = source.Focus();
                bool focused = source.IsFocused && ReferenceEquals(window.FocusManager?.GetFocusedElement(), source);
                KeyEventArgs e = Press(source, key);
                bool changed = !ReferenceEquals(before, page.ReaderNavigationTask);
                await page.ReaderNavigationTask;
                Check(focused && (expectedNavigation ? changed && Selected() == "1001" : !changed), name,
                    new { key = key.ToString(), requestedSource = requestedSource.Name ?? requestedSource.GetType().Name,
                        source = source.Name ?? source.GetType().Name, focusRequested, focused, e.Handled,
                        navigationTaskChanged = changed, selected = Selected(), tab = tabs.SelectedIndex });
            }
            await Commit(original);
            await page.FocusPostAsync("1000");
            Click(next);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            next.Focus(); Press(next, Key.Down); await page.ReaderNavigationTask;
            Check(Selected() == "1051", "protected-full44-down-1051", new { selected = Selected() });
            foreach (Key key in new[] { Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Enter, Key.Space })
            {
                Config candidate = original.Clone();
                // Enter otherwise conflicts with the independently remappable
                // thread.read command. Remove that gesture explicitly in draft.
                if (key == Key.Enter)
                    candidate.ShortcutBindings = candidate.ShortcutBindings.Set("thread.read", Array.Empty<ShortcutGesture>());
                candidate.ShortcutBindings = candidate.ShortcutBindings.Set("reader.next-post", new[] { ShortcutGesture.Parse(key.ToString()) });
                await Commit(candidate);
                tabs.SelectedIndex = 0;
                await Probe(next, Key.Down, false, "old-down-removed-" + key);
                await Probe(next, key, key is not (Key.Enter or Key.Space), "button-content-" + key);
                // Focusable content label is a fixture-only focus destination in
                // the real tab content tree, without adding a synthetic parent.
                TextBlock content = page.FindControl<TextBlock>("ArchivePostPage")!;
                bool focusable = content.Focusable;
                content.Focusable = true;
                try
                {
                    await Probe(content, key, true, "plain-content-" + key);
                    using (ShortcutService.BeginCapture())
                        await Probe(content, key, false, "capture-content-" + key);
                }
                finally { content.Focusable = focusable; }
                if (key is not (Key.Enter or Key.Space))
                    await Probe(posts, key, false, "native-post-list-" + key);
                TabItem header = tabs.GetVisualDescendants().OfType<TabItem>().First();
                await Probe(header, key, false, "native-tab-header-" + key);
                tabs.SelectedIndex = 0;
                Click(page.FindControl<Button>("ExportArchiveButton")!);
                await Program.WaitUntilAsync(() => page.IsReaderDialogOpen);
                TextBox editor = page.FindControl<TextBox>("ArchiveExportDestination")!;
                Task beforeEditor = page.ReaderNavigationTask;
                editor.Focus(); Press(editor, key);
                Check(ReferenceEquals(beforeEditor, page.ReaderNavigationTask), "native-dialog-editor-" + key);
                Click(page.FindControl<Button>("ArchiveExportClose")!);
                await Program.WaitUntilAsync(() => !page.IsReaderDialogOpen);
            }
            Program.CaptureWindow(window, "smoke-reader-shortcut-contexts.png");
            Click(page.FindControl<Button>("ArchiveCloseReader")!);
        }
        finally { await Commit(original); }
        if (failures.Count > 0) throw new InvalidOperationException("Shortcut context failures: " + string.Join(", ", failures));
        Console.WriteLine("SMOKE shortcut contexts: committed remaps, native ownership, capture, menu labels and disabled targets passed.");
    }
    private static KeyEventArgs Press(Control source, Key key)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        source.RaiseEvent(e); return e;
    }
    private static void Click(Control source) => SmokeActionClick.Execute(source);
}
