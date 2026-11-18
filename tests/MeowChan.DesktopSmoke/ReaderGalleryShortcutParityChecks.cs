using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

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
                page.BeforeReaderMediaActivationAsync = null;
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
            // The same thread id can survive a list refresh with its old, empty
            // media snapshot. Load explicitly before capturing a reader origin.
            await page.LoadThreadAsync(Fixture);
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
            LogRoutingState(window, page, page.FindControl<ListBox>("ArchivePosts")!, large ? "large" : "embedded");
        }
        Control Post() => (Control)page.FindControl<ListBox>("ArchivePosts")!.ContainerFromItem(
            page.FindControl<ListBox>("ArchivePosts")!.SelectedItem!)!;
        Control Thumbnail(int index) => page.MediaSidebar!.Strip.Items.OfType<ListBoxItem>().Single(item => Equals(item.Tag, index));
        try
        {
            foreach (bool large in new[] { true, false })
            {
                string mode = large ? "large" : "embedded";
                foreach (Key gesture in new[] { Key.Space, Key.Enter })
                {
                    await Scene(mode + "-medium-activation-" + gesture, async () =>
                    {
                        await Setup(large);
                        page.SelectMediaSidebarIndex(2); await page.MediaSidebarTask;
                        int activations = 0, opens = 0;
                        page.BeforeReaderMediaActivationAsync = () => { activations++; return Task.CompletedTask; };
                        page.OpenGallery = (resource, key, origin) =>
                        {
                            opens++;
                            Require(resource == Fixture && key?.PostId == "7000" && key?.AttachmentPosition == 2,
                                "medium activation lost the chosen association");
                            Require(ReferenceEquals(origin, Post()) && page.ReaderMediaActivationGuard?.Invoke() == true,
                                "gallery handoff must retain a current post origin");
                            return Task.CompletedTask;
                        };
                        Control source = Post(); Focus(window, source);
                        var key = Press(source, gesture);
                        await page.ReaderMediaActivationTask;
                        Require(key.Handled && activations == 1 && opens == 1,
                            $"{gesture} must reach activation and open once (handled={key.Handled}, activations={activations}, opens={opens})");
                    });
                    await Scene(mode + "-medium-refusal-" + gesture, async () =>
                    {
                        await Setup(large);
                        page.SelectMediaSidebarIndex(2); await page.MediaSidebarTask;
                        int activations = 0, opens = 0;
                        page.OpenGallery = (_, _, _) => { opens++; return Task.CompletedTask; };
                        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        page.BeforeReaderMediaActivationAsync = () => { activations++; entered.TrySetResult(); return release.Task; };
                        Control source = Post(); Focus(window, source);
                        var key = Press(source, gesture);
                        try
                        {
                            Require(entered.Task.IsCompleted && activations == 1, "key never reached the guarded activation");
                            // Revoke the handoff after admission, deterministically across an await.
                            page.OpenGallery = null;
                        }
                        finally { release.TrySetResult(); }
                        await page.ReaderMediaActivationTask;
                        Require(opens == 0, "a revoked activation opened the gallery");
                        Require(!key.Handled || page.FindControl<Control>("ArchiveReaderPane")!
                            .GetVisualDescendants().OfType<TextBlock>().Any(message => message.IsEffectivelyVisible
                                && message.Text == Texts.T("reader.media.activation.contextChanged")),
                            "a refused key was consumed without a visible explanation in the reader");
                    });
                }
                await Scene(mode + "-medium-handoff-refusal", async () =>
                {
                    await Setup(large);
                    page.SelectMediaSidebarIndex(2); await page.MediaSidebarTask;
                    int handoffs = 0;
                    page.OpenGallery = async (_, _, _) =>
                    {
                        handoffs++;
                        await Task.Yield();
                        page.OpenGallery = null;
                        Require(page.ReaderMediaActivationGuard?.Invoke() == false, "shell accepted a revoked guard");
                    };
                    Control source = Post(); Focus(window, source);
                    var key = Press(source, Key.Space);
                    await page.ReaderMediaActivationTask;
                    Require(handoffs == 1 && (!key.Handled || page.FindControl<Control>("ArchiveReaderPane")!
                        .GetVisualDescendants().OfType<TextBlock>().Any(message => message.IsEffectivelyVisible
                            && message.Text == Texts.T("reader.media.activation.contextChanged"))),
                        "a shell-side refusal consumed the key without reader-visible feedback");
                });
                await Scene(mode + "-medium-production-gallery", async () =>
                {
                    await Setup(large);
                    page.SelectMediaSidebarIndex(2); await page.MediaSidebarTask;
                    Control source = Post(); Focus(window, source);
                    var key = Press(source, Key.Space);
                    await page.ReaderMediaActivationTask;
                    ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
                    Require(key.Handled && galleryHost.IsEffectivelyVisible && galleryHost.Content is GalleryViewer,
                        "medium key did not present the production gallery");
                    GalleryViewer viewer = (GalleryViewer)galleryHost.Content!;
                    Exception? failureBeforeCleanup = null;
                    try
                    {
                        await Program.WaitUntilAsync(() => viewer.CurrentEntry != null);
                        Require(viewer.CurrentEntry!.EntryKey.PostId == "7000" && viewer.CurrentEntry.EntryKey.AttachmentPosition == 2,
                            "production medium activation lost the chosen association");
                    }
                    catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
                    finally
                    {
                        await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
                        {
                            Focus(window, viewer); Press(viewer, Key.Escape);
                            await Program.WaitUntilAsync(() => !galleryHost.IsVisible);
                        }, exception => Console.Error.WriteLine("SMOKE cleanup failed in ReaderGalleryShortcutParityChecks: " + exception));
                    }
                });
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
                    // ReaderFindOpen is intentionally not keyboard-focusable. Use
                    // the actual medium button: native Enter must click it, not
                    // run the explicit gallery command (the sidebar is shown).
                    var post = (ArchivePostRow)page.FindControl<ListBox>("ArchivePosts")!.SelectedItem!;
                    var view = Post().GetVisualDescendants().OfType<ArchiveAttachmentView>().Single(v => ReferenceEquals(v.DataContext, post.Attachments[0]));
                    Button source = view.PreviewButton; Focus(window, source);
                    int clicks = 0;
                    void Clicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => clicks++;
                    source.Click += Clicked;
                    try
                    {
                        Press(source, Key.Enter);
                        Require(clicks == 1, "Enter did not perform exactly one native medium-button click");
                        Require(opens == 0 && source.IsFocused, "Enter was stolen by a reader command");
                    }
                    finally { source.Click -= Clicked; }
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
    internal static void LogRoutingState(Window window, ArchivePage page, Control source, string scene)
    {
        object? Field(string name) => typeof(ArchivePage).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(page);
        Control pane = page.FindControl<Control>("ArchiveReaderPane")!;
        Console.WriteLine("SMOKE reader-key-state " + System.Text.Json.JsonSerializer.Serialize(new {
            scene, window.IsActive, source.IsEffectivelyVisible, PaneVisible = pane.IsEffectivelyVisible,
            RoutingActive = MeowChan.Desktop.Commands.CommandRouter.IsRoutingContextActive(pane, source),
            NativeMedium = ArchivePage.ReaderMediaBelongsToNativeControl(source, page.FindControl<ListBox>("ArchivePosts")!),
            Large = Field("_largeReader"), Available = Field("_available"), Busy = Field("_busy"),
            Obscured = Field("_obscuredByGallery"), Thread = Field("_selectedThread")?.ToString(),
            Assets = (Field("_assets") as System.Collections.ICollection)?.Count,
            LoadingSuspended = ArchiveAttachmentView.GetLoadingSuspended(page) }));
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
