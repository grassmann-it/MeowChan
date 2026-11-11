using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_READER_FIND_ONLY. Requires initialized application
// resources and the native UI thread. Routed managed input is NOT OS keyboard input.
internal static class ReaderThreadFindChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Needle = "w17needle";
    private const string Body = "before w17needle between w17needle after";
    private const string Emoji = "\U0001F63A";
    private static readonly string MixedBoundary = new string('a', 510) + Emoji;
    private static readonly string EmojiBoundary = string.Concat(Enumerable.Repeat(Emoji, 256));

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint h && h != 0,
            "Reader find requires a visible native application owner and real resources.");
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("reader-find-native-");
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            ResourceId first = new("4chan", "g", "1000");
            ResourceId second = new("4chan", "g", "2000");
            foreach (ResourceId id in new[] { first, second })
            {
                CapturedPost[] posts = Enumerable.Range(0, 651).Select(i => new CapturedPost(
                    (int.Parse(id.Thread!) + i).ToString(), i, PostFieldPresence.All,
                    Html: i switch { 648 => MixedBoundary, 649 => EmojiBoundary,
                        650 => Body, _ => "offline plain post " + i })).ToArray();
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                    "https://boards.4chan.org/g/thread/" + id.Thread, "Offline find fixture",
                    ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile, posts));
            }
            foreach (string code in new[] { "en", "de", "es" })
            {
                Texts.SetLanguage(code); // ordinary catalogs/fallback, never fake packs
                await JourneyAsync(owner, profile, first, second, code);
                foreach (bool fullscreen in new[] { false, true })
                foreach (string source in new[] { "ReaderFindQuery", "ReaderFindClose" })
                    await ShellRoutingAsync(owner, code, fullscreen, source);
            }
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            Texts.SetLanguage(language);
            // Synthetic evidence retained, no user profile or global app setup touched.
        }
    }

    private static async Task JourneyAsync(MainWindow owner, string profile,
        ResourceId first, ResourceId second, string language)
    {
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token),
            (_, _) => throw new InvalidOperationException("No capture mutation permitted."),
            _ => throw new InvalidOperationException("No external launch permitted."));
        Window window = new() { Width = 1120, Height = 820, Content = page,
            Title = "Synthetic offline reader find", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            Require(window.TryGetPlatformHandle()?.Handle is nint h && h != 0, "Missing native fixture owner.");
            page.IsAvailable = true;
            await page.LoadThreadAsync(first).WaitAsync(Deadline);
            await page.MediaLoadTask.WaitAsync(Deadline);
            Require(page.CurrentThread?.PostCount == 651
                && page.CurrentThread.Summary.SourceState == ThreadSourceState.NotFound,
                "Offline 404 snapshot was not loaded through the catalog.");
            ListBox posts = C<ListBox>("ArchivePosts");
            string[] initial = Rows();
            Require(initial.Length == 50 && !initial.Contains("1650"), "Offscreen fixture is vacuous.");
            Click("ReaderFindOpen");
            await page.ThreadFindTask.WaitAsync(Deadline);
            TextBox query = C<TextBox>("ReaderFindQuery");
            Require(query.IsFocused && C<StackPanel>("ReaderFindBar").IsVisible, "Open did not focus own bar.");
            Task previous = page.ThreadFindTask;
            query.Text = Needle; // actual TextChanged subscription, not private method invocation
            await UntilAsync(() => !ReferenceEquals(previous, page.ThreadFindTask));
            await page.ThreadFindTask.WaitAsync(Deadline);
            Require(query.IsFocused && Rows().SequenceEqual(initial), "Search finish stole focus or navigated.");
            Status(0, 2);
            Require(C<TextBlock>("ReaderFindExcerpt").Inlines!.Count == 0, "Search auto-selected a hit.");
            Click("ReaderFindNext");
            CheckHit(1, 8);
            Require(Rows().SequenceEqual(new[] { "1650" }) && posts.SelectedItem is ArchivePostRow { Id: "1650" },
                "Explicit hit did not ShowPost on the final (651st-post) page.");
            Dispatch(Key.Enter);
            CheckHit(2, 26);
            Dispatch(Key.Enter, KeyModifiers.Shift);
            CheckHit(1, 8);
            Click("ReaderFindPrevious");
            CheckHit(2, 26); // wrap, two occurrences in one post remain distinct
            Dispatch(Key.Escape);
            Require(!C<StackPanel>("ReaderFindBar").IsVisible, "Own-bar Escape did not close.");
            Console.WriteLine($"SMOKE reader-find journey [{language}]: offline404/651, count/exact excerpt, offscreen ShowPost, own-bar managed Enter/ShiftEnter/Escape checked.");

            await InputBoundariesAsync();

            // Open starts the existing query synchronously. End before yielding the UI
            // thread, so even a completed worker cannot publish its continuation first.
            Click("ReaderFindOpen");
            await page.ThreadFindTask.WaitAsync(Deadline);
            previous = page.ThreadFindTask;
            query.Text = "absent-w17-token";
            await UntilAsync(() => !ReferenceEquals(previous, page.ThreadFindTask));
            await page.ThreadFindTask.WaitAsync(Deadline);
            Status(0, 0);
            Require(!C<Button>("ReaderFindNext").IsEnabled
                && C<TextBlock>("ReaderFindExcerpt").Inlines!.Count == 0, "No-hit query retained hits.");
            previous = page.ThreadFindTask;
            query.Text = Needle;
            await UntilAsync(() => !ReferenceEquals(previous, page.ThreadFindTask));
            await page.ThreadFindTask.WaitAsync(Deadline);
            Click("ReaderFindClose");
            Click("ReaderFindOpen");
            Task closing = page.ThreadFindTask;
            Click("ReaderFindClose");
            await closing.WaitAsync(Deadline);
            Closed();
            Click("ReaderFindOpen");
            Task switching = page.ThreadFindTask;
            await page.LoadThreadAsync(second).WaitAsync(Deadline);
            await switching.WaitAsync(Deadline);
            await page.MediaLoadTask.WaitAsync(Deadline);
            Closed();
            Require(page.CurrentThread?.Summary.Id == second && !Rows().Contains("1650"), "Switch retained old posts.");
            Click("ReaderFindOpen");
            Task ending = page.ThreadFindTask;
            page.EndLifetime();
            page.EndLifetime();
            await ending.WaitAsync(Deadline);
            Closed();
            Console.WriteLine($"SMOKE reader-find lifetime [{language}]: close/switch/EndLifetime before UI continuation; no stale result publication checked. Managed dispatch only.");

            async Task InputBoundariesAsync()
            {
                Click("ReaderFindOpen");
                await page.ThreadFindTask.WaitAsync(Deadline);
                // Actual TextBox class TextInput path, including selection replacement.
                // No Text setter, clipboard access or direct product-handler call.
                query.SelectAll();
                await InputAsync(new string('a', 511) + Emoji);
                Invalid(new string('a', 511) + Emoji);

                // Replace two ASCII units with one: 513 -> 512, retaining the
                // complete supplementary scalar at the end. Query must match exactly.
                query.SelectionStart = 0; query.SelectionEnd = 2;
                await InputAsync("a");
                Valid(MixedBoundary);
                Click("ReaderFindNext");
                ExactBoundaryHit("1648", MixedBoundary);

                // Replacing one selected ASCII with a complete emoji increases the
                // length to 513; neither inserted emoji may be split or trimmed.
                query.SelectionStart = 0; query.SelectionEnd = 1;
                string replacementOverflow = Emoji + new string('a', 509) + Emoji;
                await InputAsync(Emoji);
                Invalid(replacementOverflow);
                query.SelectionStart = 0; query.SelectionEnd = Emoji.Length;
                await InputAsync("a");
                Valid(MixedBoundary);

                query.SelectAll();
                await InputAsync(EmojiBoundary);
                Valid(EmojiBoundary);
                Require(query.Text!.Length == 512 && query.Text.EnumerateRunes().Count() == 256,
                    "256 supplementary emoji were not retained as 512 UTF-16 units.");
                Click("ReaderFindNext");
                ExactBoundaryHit("1649", EmojiBoundary);
                query.SelectAll();
                await InputAsync(Needle);
                Status(0, 2);
                Click("ReaderFindClose");
                // SF-06: after a failed search one explicit retry runs with the unchanged query; no automatic retry.
                int attempts = 0;
                page.ThreadFindIndexFactory = (_, _, _) => { attempts++; throw new IOException("Synthetic find failure"); };
                Click("ReaderFindOpen");
                query.Text = Needle;
                await UntilAsync(() => C<TextBlock>("ReaderFindStatus").Text == Texts.T("readerFind.failed"));
                await page.ThreadFindTask.WaitAsync(Deadline);
                int failedAttempts = attempts;
                Require(C<Button>("ReaderFindRetry").IsEffectivelyVisible && query.Text == Needle,
                    "A failed find did not offer an explicit retry with the unchanged query.");
                await Task.Delay(300);
                Require(attempts == failedAttempts, "A failed find retried automatically.");
                page.ThreadFindIndexFactory = null;
                Task failedTask = page.ThreadFindTask;
                Click("ReaderFindRetry");
                await UntilAsync(() => !ReferenceEquals(failedTask, page.ThreadFindTask));
                await page.ThreadFindTask.WaitAsync(Deadline);
                Status(0, 2);
                Require(!C<Button>("ReaderFindRetry").IsVisible && query.Text == Needle, "Retry kept its button or changed the query.");
                Click("ReaderFindClose");
                Console.WriteLine($"SMOKE reader-find input-boundary [{language}]: TextInput preserves 511ASCII+emoji513 invalid/no-navigation; selection replacement 513/512; 256 complete emoji512 exact hit. Managed TextInput only, no OS keyboard/clipboard claim.");

                async Task InputAsync(string text)
                {
                    Require(ReferenceEquals(TopLevel.GetTopLevel(query), window)
                        && query.IsEffectivelyVisible && query.Focus() && query.IsFocused,
                        "Boundary TextInput lacks the actual focused query/owner.");
                    string[] rows = Rows();
                    object? selected = posts.SelectedItem;
                    Task before = page.ThreadFindTask;
                    query.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
                    await UntilAsync(() => !ReferenceEquals(before, page.ThreadFindTask));
                    await page.ThreadFindTask.WaitAsync(Deadline);
                    Require(query.IsFocused && Rows().SequenceEqual(rows) && ReferenceEquals(posts.SelectedItem, selected),
                        "Boundary input/search finish stole focus, selected or navigated a post.");
                }
                void Invalid(string expected)
                {
                    Require(expected.Length == 513 && query.Text == expected
                        && C<TextBlock>("ReaderFindStatus").Text == Texts.T("readerFind.invalid")
                        && C<TextBlock>("ReaderFindExcerpt").Inlines!.Count == 0
                        && !C<Button>("ReaderFindNext").IsEnabled && !C<Button>("ReaderFindPrevious").IsEnabled,
                        "Over-boundary TextInput was truncated/split, searched, or not marked invalid.");
                    string[] rows = Rows();
                    object? selected = posts.SelectedItem;
                    Dispatch(Key.Enter);
                    Dispatch(Key.Enter, KeyModifiers.Shift);
                    Require(Rows().SequenceEqual(rows) && ReferenceEquals(posts.SelectedItem, selected)
                        && query.Text == expected && C<TextBlock>("ReaderFindStatus").Text == Texts.T("readerFind.invalid"),
                        "Invalid query Enter/ShiftEnter navigated or changed original input/status.");
                }
                void Valid(string expected)
                {
                    Require(expected.Length == 512 && query.Text == expected,
                        "Boundary selection replacement changed the exact 512-unit query.");
                    Status(0, 1);
                    Require(C<TextBlock>("ReaderFindExcerpt").Inlines!.Count == 0
                        && C<Button>("ReaderFindNext").IsEnabled && C<Button>("ReaderFindPrevious").IsEnabled,
                        "Valid 512-unit query was rejected or auto-selected a hit.");
                }
                void ExactBoundaryHit(string postId, string expected)
                {
                    Status(1, 1);
                    Run[] runs = C<TextBlock>("ReaderFindExcerpt").Inlines!.OfType<Run>().ToArray();
                    Require(posts.SelectedItem is ArchivePostRow row && row.Id == postId
                        && runs.Length == 4 && runs[0].Text == Texts.T("readerFind.location", postId,
                            Texts.T("readerFind.body"), 1, 512) + "\n"
                        && runs[1].Text == "" && runs[2].Text == expected
                        && runs[2].FontWeight == FontWeight.Bold && runs[3].Text == "",
                        "Boundary query was not searched/displayed as the exact complete saved-text hit.");
                }
            }

            T C<T>(string name) where T : Control => page.FindControl<T>(name)
                ?? throw new InvalidOperationException("Missing actual control " + name);
            string[] Rows() => posts.ItemsSource!.Cast<ArchivePostRow>().Select(row => row.Id).ToArray();
            void Click(string name)
            {
                Button button = C<Button>(name);
                Require(button.IsEffectivelyEnabled, "Disabled fixture action " + name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            void Dispatch(Key key, KeyModifiers modifiers = KeyModifiers.None)
            {
                var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
                query.RaiseEvent(e); // source belongs to this page's own bar only
                Require(e.Handled, "Own-bar routed key was not handled: " + key);
            }
            void Status(int selected, int total) => Require(C<TextBlock>("ReaderFindStatus").Text ==
                Texts.T("readerFind.count", selected, total), "Actual localized count differs.");
            void CheckHit(int selected, int position)
            {
                Status(selected, 2);
                Run[] runs = C<TextBlock>("ReaderFindExcerpt").Inlines!.OfType<Run>().ToArray();
                Require(runs.Length == 4 && runs[0].Text == Texts.T("readerFind.location", "1650",
                    Texts.T("readerFind.body"), position, Needle.Length) + "\n"
                    && runs[2].Text == Needle && runs[2].FontWeight == FontWeight.Bold
                    && string.Concat(runs.Skip(1).Select(run => run.Text)) == Body,
                    "Exact inert excerpt/location does not identify the selected occurrence.");
            }
            void Closed() => Require(!C<StackPanel>("ReaderFindBar").IsVisible
                && C<TextBlock>("ReaderFindStatus").Text == Texts.T("readerFind.ready")
                && C<TextBlock>("ReaderFindExcerpt").Inlines!.Count == 0
                && !C<Button>("ReaderFindNext").IsEnabled && !C<Button>("ReaderFindPrevious").IsEnabled,
                "Ended find published stale count/excerpt/actions.");
        }
        finally
        {
            page.EndLifetime();
            try { await page.ThreadFindTask.WaitAsync(Deadline); }
            finally { window.Close(); }
        }
    }

    // A separate real shell is essential: a plain Window hosting ArchivePage does
    // not contain MainWindow's early tunnel/dismissal chain (RFUI-P2-01).
    private static async Task ShellRoutingAsync(MainWindow owner, string language, bool fullscreen, string sourceName)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("reader-find-shell-");
        ResourceId id = new("4chan", "g", "3000");
        const string triple = "w17needle / w17needle / w17needle";
        new Config { DownloadsPaused = true, AutomaticUpdateChecks = false,
            SaveUrlsOnExit = false, FirstRunSetupPending = false, FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0, CloseToTray = false, MinimizeToTray = false,
            WorkspaceMode = "Advanced", Language = language }.SaveConfig(profile);
        // Release the seed lease before the real host acquires this profile.
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            CapturedPost[] data = Enumerable.Range(0, 651).Select(i => new CapturedPost(
                (3000 + i).ToString(), i, PostFieldPresence.All,
                Html: i == 650 ? triple : "offline shell post " + i)).ToArray();
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                "https://boards.4chan.org/g/thread/3000", "Offline shell routing",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile, data));
        }
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        MainWindow? shell = null;
        try
        {
            shell = new MainWindow(host);
            shell.Show(owner);
            shell.Activate();
            await host.StartAsync().WaitAsync(Deadline);
            await UntilAsync(() => host.State == DesktopSessionState.Ready
                && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            Require(shell.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                "Shell routing fixture has no native handle.");
            shell.FindControl<MenuItem>("ArchiveMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ArchivePage page = shell.ArchivePage;
            await UntilAsync(() => !page.IsBusy);
            ListBox threads = C<ListBox>("ArchiveThreads");
            // Normal list presentation and selection establish the single selected
            // archive identity required by the actual Read posts entry button.
            SmokeActionClick.Execute(C<Button>("ArchiveLayoutList"));
            await UntilAsync(() => threads.ItemsSource is IEnumerable<ArchiveThreadRow> rows
                && rows.Any(row => row.Summary.Id == id));
            threads.SelectedItem = threads.ItemsSource!.Cast<ArchiveThreadRow>().Single(row => row.Summary.Id == id);
            await UntilAsync(() => !page.IsBusy && C<Button>("ArchiveReadPosts").IsEnabled);
            ContentControl reader = shell.FindControl<ContentControl>("ReaderHost")!;
            Click("ArchiveReadPosts");
            await UntilAsync(() => reader.IsVisible && !page.IsBusy && page.CurrentThread?.Summary.Id == id);
            await page.MediaLoadTask.WaitAsync(Deadline);
            await OpenFindAsync();
            int unexpectedClicks = 0;
            Button[] buttons = [C<Button>("ReaderFindPrevious"), C<Button>("ReaderFindNext"), C<Button>("ReaderFindClose")];
            foreach (Button button in buttons) button.Click += CountClick;
            int routedAtShell = 0;
            shell.AddHandler(InputElement.KeyDownEvent, ObserveShell, RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                // Establish hit1 solely through routed Enter. Three hits distinguish
                // reverse movement from accidental forward clicks even at wrap.
                await SendAsync(C<TextBox>("ReaderFindQuery"), Key.Enter);
                CheckHit(1);
                Control[] sources = [C<TextBox>("ReaderFindQuery"), buttons[1], buttons[0]];
                foreach (Control source in sources)
                {
                    // SF-03: Enter on the focused Previous button runs "previous", not "next".
                    foreach (int selected in ReferenceEquals(source, buttons[0]) ? new[] { 3, 2, 1 } : new[] { 2, 3, 1 })
                    {
                        await SendAsync(source, Key.Enter);
                        CheckHit(selected);
                    }
                    foreach (int selected in new[] { 3, 2, 1 })
                    {
                        await SendAsync(source, Key.Enter, KeyModifiers.Shift);
                        CheckHit(selected);
                    }
                }
                Require(unexpectedClicks == 0 && routedAtShell == 19,
                    "Find Enter gestures clicked a button or did not traverse the real Window tunnel.");
                // SF-03: Enter on the focused Close button closes the bar.
                await SendAsync(buttons[2], Key.Enter);
                Require(!C<StackPanel>("ReaderFindBar").IsVisible, "Enter on the focused Close button did not close the find bar.");
                // SF-05: Primary+F opens thread find from a focused reader control.
                await SendAsync(C<Button>("ReaderFindOpen"), Key.F,
                    OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
                Require(C<StackPanel>("ReaderFindBar").IsVisible && C<TextBox>("ReaderFindQuery").IsFocused,
                    "Primary+F did not open thread find in the reader.");
                await page.ThreadFindTask.WaitAsync(Deadline);
                await OpenFindAsync();
                Console.WriteLine($"SMOKE reader-find shell-enter [{language}]: Query/Previous/Next/Close; 3-hit forward/reverse wrap; zero additional Click/Close; real Window tunnel checked. Managed events only.");

                // Each Escape variant gets a fresh shell. No private fullscreen-
                // restore flag, polling toggle/retry or assumed WM timing is needed.
                {
                    Require(reader.IsVisible, "Escape baseline lost the large reader.");
                    if (fullscreen)
                    {
                        Click("ArchiveReaderFullscreen");
                        await UntilAsync(() => shell.WindowState == WindowState.FullScreen);
                    }
                    else Require(shell.WindowState != WindowState.FullScreen, "Normal Escape case began fullscreen.");
                    await OpenFindAsync();
                    var snapshot = page.CurrentThread;
                    object? pane = reader.Content;
                    WindowState state = shell.WindowState;
                    int beforeRoute = routedAtShell;
                    await SendAsync(C<Control>(sourceName), Key.Escape);
                    Require(routedAtShell == beforeRoute + 1
                        && !C<StackPanel>("ReaderFindBar").IsVisible && reader.IsVisible
                        && ReferenceEquals(reader.Content, pane) && ReferenceEquals(page.CurrentThread, snapshot)
                        && page.CurrentThread?.Summary.Id == id && shell.WindowState == state
                        && unexpectedClicks == 0,
                        "First own-bar Escape changed reader/fullscreen/thread or clicked Close.");
                    // Source is now explicitly outside the closed bar. The next
                    // Escape must retain the shell's pre-existing dismissal action.
                    await SendAsync(C<Button>("ReaderFindOpen"), Key.Escape);
                    if (fullscreen)
                    {
                        await UntilAsync(() => shell.WindowState != WindowState.FullScreen);
                        Require(reader.IsVisible && ReferenceEquals(page.CurrentThread, snapshot),
                            "Second Escape should exit fullscreen, not also close the reader.");
                        Click("ArchiveCloseReader");
                    }
                    else Require(!reader.IsVisible, "Second Escape did not close the normal large reader.");
                    await UntilAsync(() => !reader.IsVisible && !page.IsBusy);
                    Console.WriteLine($"SMOKE reader-find shell-escape [{language}]: source={sourceName}; fullscreen={fullscreen}; first closes only bar, second preserves shell dismissal. Managed events only.");
                }
            }
            finally
            {
                shell.RemoveHandler(InputElement.KeyDownEvent, ObserveShell);
                foreach (Button button in buttons) button.Click -= CountClick;
            }

            T C<T>(string name) where T : Control => page.FindControl<T>(name)
                ?? throw new InvalidOperationException("Missing shell reader control " + name);
            void Click(string name)
            {
                Button button = C<Button>(name);
                Require(button.IsEffectivelyEnabled, "Disabled shell fixture action " + name);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            async Task OpenFindAsync()
            {
                Click("ReaderFindOpen");
                await page.ThreadFindTask.WaitAsync(Deadline);
                TextBox query = C<TextBox>("ReaderFindQuery");
                if (query.Text != Needle)
                {
                    Task previous = page.ThreadFindTask;
                    query.Text = Needle;
                    await UntilAsync(() => !ReferenceEquals(previous, page.ThreadFindTask));
                    await page.ThreadFindTask.WaitAsync(Deadline);
                }
                Require(C<StackPanel>("ReaderFindBar").IsVisible
                    && C<TextBlock>("ReaderFindStatus").Text == Texts.T("readerFind.count", 0, 3),
                    "Shell fixture did not open a fresh three-hit search.");
            }
            void CountClick(object? sender, RoutedEventArgs e) => unexpectedClicks++;
            void ObserveShell(object? sender, KeyEventArgs e) => routedAtShell++;
            async Task SendAsync(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
            {
                Require(ReferenceEquals(TopLevel.GetTopLevel(source), shell) && source.IsEffectivelyVisible
                    && source.Focus() && ReferenceEquals(shell.FocusManager?.GetFocusedElement(), source),
                    "Key source is not focused in the real shell tree: " + source.Name);
                var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
                source.RaiseEvent(down); // complete Window tunnel -> target -> bubble, no handler invocation
                Require(down.Handled, "Shell-routed find gesture was not consumed: " + source.Name);
                source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
                await Dispatcher.UIThread.InvokeAsync(shell.UpdateLayout, DispatcherPriority.Background);
            }
            void CheckHit(int selected)
            {
                Run[] runs = C<TextBlock>("ReaderFindExcerpt").Inlines!.OfType<Run>().ToArray();
                Require(reader.IsVisible && C<StackPanel>("ReaderFindBar").IsVisible
                    && C<TextBlock>("ReaderFindStatus").Text == Texts.T("readerFind.count", selected, 3)
                    && runs.Length == 4 && runs[0].Text == Texts.T("readerFind.location", "3650",
                        Texts.T("readerFind.body"), 1 + (selected - 1) * 12, Needle.Length) + "\n"
                    && runs[2].Text == Needle && runs[2].FontWeight == FontWeight.Bold
                    && string.Concat(runs.Skip(1).Select(run => run.Text)) == triple
                    && C<ListBox>("ArchivePosts").SelectedItem is ArchivePostRow { Id: "3650" }
                    && unexpectedClicks == 0,
                    "Routed find key moved the wrong direction/wrapped twice/clicked/closed instead.");
            }
        }
        finally
        {
            if (shell != null)
            {
                shell.Close();
                await UntilAsync(() => !shell.IsVisible);
                await shell.GalleryCleanupTask.WaitAsync(Deadline);
            }
            await host.DisposeAsync();
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Reader find UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        Dispatcher.UIThread.VerifyAccess();
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
