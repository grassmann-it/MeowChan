using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

// Actual focus + synthetic Avalonia routed input; not native OS keyboard evidence.
// Independent scenes always run, with failures reported together at the end.
internal static class ReaderGalleryShortcutParityChecks
{
    private static readonly ResourceId Fixture = new("4chan", "g", "9989");
    internal static async Task RunAsync(MainWindow window)
    {
        ArchivePage page = window.ArchivePage;
        var mediaBefore = page.ReaderMedia;
        var galleryBefore = page.OpenGallery;
        var queueBefore = page.AddToTagQueue;
        ContentControl host = window.FindControl<ContentControl>("ReaderHost")!;
        List<string> failures = new();
        async Task Scene(string name, Func<Task> run)
        {
            try { await run(); Console.WriteLine("SMOKE shortcut-parity PASS " + name); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); Console.WriteLine("SMOKE shortcut-parity FAIL " + name + ": " + error); }
            finally
            {
                QuickTagPopover.Current?.Close();
                page.OpenGallery = galleryBefore;
                page.AddToTagQueue = queueBefore;
                if (page.PostFilters?.IsHidden(Fixture, "7000") == true) await page.UnhidePostAsync(Fixture, "7000");
                if (host.IsVisible) SmokeActionClick.Execute(page.FindControl<Button>("ArchiveCloseReader")!);
            }
        }
        async Task Setup(bool large)
        {
            window.Width = 1920; window.Height = 1080;
            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Archive);
            await Program.WaitUntilAsync(() => !page.IsBusy);
            page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Shortcut routing fixture";
            await page.ApplySearchAsync();
            page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == Fixture);
            await page.MediaLoadTask;
            page.ApplyReaderMediaPreferences(mediaBefore with { Sidebar = true, SidebarAlways = true,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMinimum });
            if (large) SmokeActionClick.Execute(page.FindControl<Button>("ArchiveReadPosts")!);
            await Program.WaitUntilAsync(() => host.IsVisible == large && page.IsMediaSidebarShown && !page.IsBusy);
            await page.FocusPostAsync("7000");
            await page.MediaLoadTask;
            await page.MediaSidebarTask;
            await page.MediaSidebarStripTask;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
        Control Post() => (Control)page.FindControl<ListBox>("ArchivePosts")!.ContainerFromItem(
            page.FindControl<ListBox>("ArchivePosts")!.SelectedItem!)!;
        Control Thumbnail(int index) => page.MediaSidebar!.Strip.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, index));
        try
        {
            foreach (bool large in new[] { true, false })
            {
                string mode = large ? "large" : "embedded";
                await Scene(mode + "-reader-post-focus-Q", async () =>
                {
                    await Setup(large);
                    int calls = 0;
                    page.AddToTagQueue = entries =>
                    {
                        calls++;
                        Require(entries.Count == 1 && entries[0].EntryKey.PostId == "7000" && entries[0].EntryKey.AttachmentPosition == 0, "Q targeted wrong association");
                        return Task.FromResult("queued");
                    };
                    Control source = Post(); Focus(window, source);
                    var key = Press(source, Key.Q);
                    await Program.WaitUntilAsync(() => calls > 0);
                    Require(key.Handled && calls == 1 && source.IsFocused, "Q must route exactly once without moving focus");
                });
                await Scene(mode + "-sidebar-medium-focus-H", async () =>
                {
                    await Setup(large);
                    Control source = Thumbnail(1); Focus(window, source);
                    var key = Press(source, Key.H);
                    await page.PostFilterTask;
                    Require(key.Handled && page.PostFilters!.IsHidden(Fixture, "7000"), "H must hide the selected post per the catalog");
                });
                await Scene(mode + "-sidebar-medium-ShiftEnter", async () =>
                {
                    await Setup(large);
                    int opens = 0;
                    page.OpenGallery = (_, key, _) =>
                    {
                        opens++;
                        Require(key?.PostId == "7000" && key?.AttachmentPosition == 2, "gallery must open exact focused attachment");
                        return Task.CompletedTask;
                    };
                    Control source = Thumbnail(2); Focus(window, source);
                    var key = Press(source, Key.Enter, KeyModifiers.Shift);
                    await Program.WaitUntilAsync(() => opens > 0);
                    Require(key.Handled && opens == 1, "Shift+Enter must open once");
                });
                await Scene(mode + "-inline-medium-ShiftEnter", async () =>
                {
                    await Setup(large);
                    int opens = 0;
                    page.OpenGallery = (_, key, _) =>
                    {
                        opens++;
                        Require(key?.PostId == "7000" && key?.AttachmentPosition == 2, "inline focus drifted to thread's first medium");
                        return Task.CompletedTask;
                    };
                    var post = (ArchivePostRow)page.FindControl<ListBox>("ArchivePosts")!.SelectedItem!;
                    var view = Post().GetVisualDescendants().OfType<ArchiveAttachmentView>().Single(v => ReferenceEquals(v.DataContext, post.Attachments[2]));
                    Focus(window, view.PreviewButton);
                    Press(view.PreviewButton, Key.Enter, KeyModifiers.Shift);
                    await Program.WaitUntilAsync(() => opens > 0);
                    Require(opens == 1, "inline Shift+Enter must open once");
                });
                await Scene(mode + "-button-native-Enter", async () =>
                {
                    await Setup(large);
                    int opens = 0;
                    page.OpenGallery = (_, _, _) => { opens++; return Task.CompletedTask; };
                    Button source = page.FindControl<Button>("ReaderFindOpen")!; Focus(window, source);
                    Press(source, Key.Enter);
                    Require(opens == 0 && source.IsFocused, "Enter was stolen by a reader command");
                });
                await Scene(mode + "-tag-input-focus", async () =>
                {
                    await Setup(large);
                    Control source = Post(); Focus(window, source); Press(source, Key.R);
                    await Program.WaitUntilAsync(() => QuickTagPopover.Current?.IsOpen == true);
                    QuickTagPopover popover = QuickTagPopover.Current!;
                    TextBox editor = popover.Frame.GetVisualDescendants().OfType<TextBox>().First(box => box.IsEffectivelyVisible && !box.IsReadOnly);
                    Focus(window, editor); editor.Text = "";
                    foreach (var (key, text) in new[] { (Key.Q, "q"), (Key.H, "h"), (Key.L, "l") })
                    {
                        Press(editor, key);
                        editor.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
                    }
                    Press(editor, Key.Enter, KeyModifiers.Shift);
                    Require(editor.Text == "qhl" && editor.IsFocused && popover.IsOpen && !page.PostFilters!.IsHidden(Fixture, "7000"), "text focus/letters were stolen");
                });
            }
            await Scene("gallery-focus", async () =>
            {
                await Setup(true);
                page.SelectMediaSidebarIndex(2); await page.MediaSidebarTask;
                Focus(window, Thumbnail(2)); Press(Thumbnail(2), Key.Enter, KeyModifiers.Shift);
                ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
                await Program.WaitUntilAsync(() => galleryHost.IsEffectivelyVisible && galleryHost.Content is GalleryViewer);
                GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
                try
                {
                    await Program.WaitUntilAsync(() => viewer.CurrentEntry != null);
                    Require(viewer.CurrentEntry!.EntryKey.PostId == "7000" && viewer.CurrentEntry.EntryKey.AttachmentPosition == 2, "production gallery lost association");
                    Focus(window, viewer);
                    Press(viewer, Key.Escape);
                    await Program.WaitUntilAsync(() => !galleryHost.IsVisible);
                }
                finally { if (galleryHost.IsVisible) Press(viewer, Key.Escape); }
            });
        }
        finally { page.ApplyReaderMediaPreferences(mediaBefore); }
        if (failures.Count > 0) throw new InvalidOperationException("Shortcut parity failures (all scenes ran): " + string.Join("; ", failures));
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Focus(MainWindow window, Control source) => Require(source.Focus() && ReferenceEquals(window.FocusManager?.GetFocusedElement(), source), "actual focus unavailable: " + source.GetType().Name);
    private static KeyEventArgs Press(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        source.RaiseEvent(e);
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        return e;
    }
}
