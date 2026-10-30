using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Testing;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

internal static class ReaderInteractionChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        await CheckReaderMediaActivationAsync(window, page);
        await CheckPendingReaderMediaLifetimeAsync(window);
        await CheckNativeMediaKeysAsync(page);
        Console.WriteLine("SMOKE reader native controls and media Enter passed.");
        await CheckReaderShortcutsAsync(page);
        Console.WriteLine("SMOKE current-thread reader shortcuts and dialog input protection passed.");
        await CheckFavoriteFailureAsync(page);
        Console.WriteLine("SMOKE persistent keyboard/automation favorite failure passed.");
        await CheckHistoryAsync(page);
        Console.WriteLine("SMOKE exact cross-page quote anchor and newest-32 history passed.");
        await CheckFavoritesOverviewAsync(window, page);
        Console.WriteLine("SMOKE reader interactions: native focus, exact media Enter, persistent favorite failure, exact bounded quote return and favorite removal/view/export passed.");
    }

    private static async Task CheckReaderMediaActivationAsync(MainWindow window, ArchivePage page)
    {
        ResourceId thread = page.CurrentThread!.Summary.Id;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        var open = page.OpenGallery;
        var boundary = page.BeforeReaderMediaActivationAsync;
        int opens = 0;
        GalleryEntryKey? opened = null;
        Control? origin = null;
        ReaderBodyTextBlock Body() => posts.GetVisualDescendants().OfType<ReaderBodyTextBlock>()
            .Single(body => body.GetVisualAncestors().OfType<ListBoxItem>()
                .Any(item => ReferenceEquals(item.DataContext, posts.SelectedItem)));
        async Task SelectSecond()
        {
            await page.FocusPostAsync("1001");
            await page.MediaLoadTask;
            await FlushAsync();
            Body().SelectionStart = Body().SelectionEnd = 0;
            Body().Focus();
        }
        async Task AssertNoOpen(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            int before = opens;
            Press(source, key, modifiers);
            await page.ReaderMediaActivationTask;
            Require(opens == before, "Reader media activation escaped a protected input context.");
        }
        try
        {
            page.OpenGallery = (id, key, from) =>
            {
                Require(id == thread && key != null, "Content activation used a thread-first/empty gallery target.");
                opens++; opened = key; origin = from;
                return Task.CompletedTask;
            };
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive);
            foreach (Key key in new[] { Key.Enter, Key.Space })
            {
                await SelectSecond();
                ReaderBodyTextBlock body = Body();
                int before = opens;
                KeyEventArgs e = Press(body, key);
                await page.ReaderMediaActivationTask;
                Require(e.Handled && opens == before + 1 && opened!.PostId == "1001"
                    && opened.AttachmentPosition == 0 && ReferenceEquals(origin, body),
                    "Ordinary selected ReaderBodyTextBlock did not activate its exact second-post association/origin.");
                body.SelectionStart = 0; body.SelectionEnd = 5;
                await AssertNoOpen(body, key);
                body.SelectionEnd = 0;
                using (ShortcutService.BeginCapture()) await AssertNoOpen(body, key);
                await AssertNoOpen(body, key, KeyModifiers.Control | KeyModifiers.Shift);
                await AssertNoOpen(page.FindControl<Button>("ReaderNextPost")!, key);
            }

            // Two keys while resolution is pending, then held-key repeats after
            // completion, must still produce one callback. No fake gallery state.
            await SelectSecond();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.BeforeReaderMediaActivationAsync = () => gate.Task;
            ReaderBodyTextBlock repeatBody = Body();
            int repeatBefore = opens;
            void Down(Key key) => repeatBody.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            Down(Key.Enter); Down(Key.Enter); Down(Key.Space);
            Task pending = page.ReaderMediaActivationTask;
            Require(!pending.IsCompleted, "Repeated-key fixture never reached the async mapping boundary.");
            gate.SetResult(); await pending;
            Down(Key.Enter); await page.ReaderMediaActivationTask;
            Require(opens == repeatBefore + 1, "Repeated Reader media keys opened duplicate galleries.");
            repeatBody.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
            repeatBody.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });

            foreach (string change in new[] { "selection-away-back", "content-request", "thread", "unavailable", "dialog" })
            {
                page.BeforeReaderMediaActivationAsync = null;
                await page.LoadThreadAsync(thread); await SelectSecond();
                gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                page.BeforeReaderMediaActivationAsync = () => gate.Task;
                int before = opens;
                Press(Body(), Key.Space);
                pending = page.ReaderMediaActivationTask;
                Require(!pending.IsCompleted, "Stale activation fixture missed its await boundary.");
                switch (change)
                {
                    case "selection-away-back": await page.FocusPostAsync("1002"); await page.FocusPostAsync("1001"); break;
                    case "content-request": await page.LoadThreadAsync(thread); break;
                    case "thread": await page.LoadThreadAsync(thread with { Thread = "1001" }); break;
                    case "unavailable": page.IsAvailable = false; page.IsAvailable = true; break;
                    case "dialog":
                        Click(page.FindControl<Button>("ExportArchiveButton")!);
                        await Program.WaitUntilAsync(() => page.IsReaderDialogOpen);
                        Click(page.FindControl<Button>("ArchiveExportClose")!);
                        break;
                }
                gate.SetResult(); await pending;
                Require(opens == before, "Stale Reader media activation survived " + change + ".");
            }
            // Model the existing host's retiring-gallery cleanup await. R0 must
            // snapshot this guard during the callback and recheck before showing UI.
            page.BeforeReaderMediaActivationAsync = null;
            await page.LoadThreadAsync(thread); await SelectSecond();
            var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int callbackBefore = opens;
            page.OpenGallery = async (_, _, _) =>
            {
                Func<bool>? stillCurrent = page.ReaderMediaActivationGuard;
                Require(stillCurrent?.Invoke() == true, "Reader callback did not carry its live request guard.");
                callbackEntered.SetResult();
                await callbackRelease.Task;
                if (stillCurrent!()) opens++;
            };
            try
            {
                Press(Body(), Key.Enter);
                Task handoff = page.ReaderMediaActivationTask;
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await page.FocusPostAsync("1002"); await page.FocusPostAsync("1001");
                callbackRelease.SetResult(); await handoff;
                Require(opens == callbackBefore && page.ReaderMediaActivationGuard == null,
                    "Retired Reader callback guard survived selection churn or leaked beyond handoff.");
            }
            finally { callbackRelease.TrySetResult(); }
            Console.WriteLine("SMOKE reader media: second/shared post Enter+Space, body/selection/native/capture/modifier guards, repeats and stale awaits passed.");
        }
        finally
        {
            page.BeforeReaderMediaActivationAsync = boundary;
            page.OpenGallery = open;
            page.IsAvailable = true;
            await page.LoadThreadAsync(thread); await page.FocusPostAsync("1000"); await page.MediaLoadTask;
        }
    }

    private static async Task CheckPendingReaderMediaLifetimeAsync(MainWindow owner)
    {
        ResourceId thread = new("4chan", "g", "1000");
        foreach (string change in new[] { "ready", "selection", "reader-close", "lifetime" })
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int opens = 0;
            GalleryEntryKey? opened = null;
            var page = new ArchivePage(owner.SessionHost.OpenArchiveReaderAsync, (_, _) => Task.CompletedTask, _ => { },
                async (directory, token) =>
                {
                    // Deliberately model a scan that returns late after cancellation.
                    await release.Task;
                    return await MediaAssetReader.OpenAsync(directory, CancellationToken.None);
                }) { IsAvailable = true, ReaderRefreshAllowed = () => false };
            Control pane = page.DetachReader();
            var window = new Window { Width = 1000, Height = 800, Content = pane, Title = "Private Reader media lifetime " + change };
            page.OpenGallery = (_, key, _) => { opens++; opened = key; return Task.CompletedTask; };
            try
            {
                window.Show(owner); window.Activate();
                await Program.WaitUntilAsync(() => window.IsActive);
                await page.LoadThreadAsync(thread);
                Task scan = page.MediaLoadTask;
                await page.FocusPostAsync("1001"); await FlushAsync();
                ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
                Control source = posts.ContainerFromItem(posts.SelectedItem!)!;
                Press(source, Key.Enter);
                Task activation = page.ReaderMediaActivationTask;
                Require(!activation.IsCompleted && !scan.IsCompleted, "Reader activation did not await the existing pending manifest scan.");
                switch (change)
                {
                    case "selection": await page.FocusPostAsync("1002"); await page.FocusPostAsync("1001"); break;
                    case "reader-close": window.Content = null; page.AttachReader(pane); break;
                    case "lifetime": page.EndLifetime(); break;
                }
                release.SetResult();
                await activation; await scan;
                Require(change == "ready" ? opens == 1 && opened?.PostId == "1001" : opens == 0,
                    "Pending local media scan lost identity/cancellation for " + change + ".");
            }
            finally { release.TrySetResult(); page.EndLifetime(); window.Close(); }
        }
        owner.Activate(); await Program.WaitUntilAsync(() => owner.IsActive);
        Console.WriteLine("SMOKE Reader media pending-scan: exact second-post readiness and away/back, close and lifetime cancellation passed.");
    }

    // Opt-in real-input entry point for R0's serialized private-desktop runner.
    // Seed ReaderUxChecks first. No synthetic-key fallback is used in this path.
    internal static async Task RunMediaActivationInputAsync(MainWindow window)
    {
        const ulong enter = MediaNativeInput.Enter, up = MediaNativeInput.Up, down = MediaNativeInput.Down;
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy);
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Reader UX 1000";
        await page.ApplySearchAsync();
        page.FindControl<ListBox>("ArchiveThreads")!.SelectedIndex = 0;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1000");
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible
            && !page.IsBusy && page.CurrentThread?.Summary.Id.Thread == "1000" && page.ReaderShowsPosts
            && page.FindControl<ListBox>("ArchivePosts")!.ItemsSource is IEnumerable<ArchivePostRow> loadedPosts
            && loadedPosts.Any(post => post.Id == "1001")
            && typeof(ArchivePage).GetField("_contentCancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page) == null);
        await page.MediaLoadTask;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ReaderBodyTextBlock Body() => posts.GetVisualDescendants().OfType<ReaderBodyTextBlock>().Single(body =>
            body.IsEffectivelyVisible && body.GetVisualAncestors().OfType<ListBoxItem>()
                .Any(item => item.DataContext is ArchivePostRow { Id: "1001" }));
        async Task SelectSecond()
        {
            // Arrange the target row, but let the ACTUAL pointer select/focus it below.
            // Initial localization/anchor continuations may still own programmatic focus;
            // they are not the user selection whose Enter/Space behavior is being tested.
            await page.ReaderLocalizationTask;
            await page.ReaderNavigationTask;
            page.ShowPost("1001");
            await FlushAsync();
            await Program.WaitUntilAsync(() => posts.GetVisualDescendants().OfType<ReaderBodyTextBlock>()
                .Any(body => body.IsEffectivelyVisible && body.GetVisualAncestors().OfType<ListBoxItem>()
                    .Any(item => item.DataContext is ArchivePostRow { Id: "1001" })));
            Body().SelectionStart = Body().SelectionEnd = 0;
        }
        var realOpen = page.OpenGallery;
        int opens = 0;
        GalleryEntryKey? opened = null, expectedGalleryKey = null;
        ResourceId? openedThread = null;
        Control? from = null;
        page.OpenGallery = (thread, key, origin) => { opens++; openedThread = thread; opened = key; from = origin; return Task.CompletedTask; };
        using var input = new MediaNativeInput(window);
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        TaskCompletionSource? keyDownSeen = null, keyUpSeen = null, pointerSeen = null;
        Key? expectedNativeKey = null;
        Control? keySource = null, keyFocus = null, keyPostContainer = null, pointerSource = null;
        string? keyPost = null;
        Task? activationAtKey = null;
        bool keyHandled = false;
        bool? nativeOwnedAtKey = null, gestureEligibleAtKey = null;
        KeyModifiers keyModifiers = KeyModifiers.None;
        ReaderBodyTextBlock? requestedBody = null;
        object? Describe(Control? control) => control == null ? null : new
        {
            type = control.GetType().Name, control.Name, identity = RuntimeHelpers.GetHashCode(control),
            control.Focusable, control.IsFocused, control.IsEffectivelyVisible,
            post = control.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .Select(item => (item.DataContext as ArchivePostRow)?.Id).FirstOrDefault(id => id != null),
            ownerType = TopLevel.GetTopLevel(control)?.GetType().Name
        };
        void Evidence(string phase, Key? key = null) => Console.WriteLine("SMOKE reader-native-probe " + JsonSerializer.Serialize(new
        {
            phase, key = key?.ToString(), modifiers = keyModifiers.ToString(), opens, openedThread, opened,
            expectedGalleryKey, origin = Describe(from), requestedBody = Describe(requestedBody),
            selected = (posts.SelectedItem as ArchivePostRow)?.Id, thread = page.CurrentThread?.Summary.Id,
            focus = Describe(window.FocusManager?.GetFocusedElement() as Control),
            eventSource = Describe(keySource), eventFocus = Describe(keyFocus),
            eventPost = keyPost, eventPostContainer = Describe(keyPostContainer), keyHandled,
            nativeOwnedAtKey, gestureEligibleAtKey,
            activeSelections = keyPostContainer?.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(text => text.SelectionStart != text.SelectionEnd)
                .Select(text => new { type = text.GetType().Name, text.SelectionStart, text.SelectionEnd }).ToArray(),
            pointerSource = Describe(pointerSource), window.IsActive, page.IsBusy, page.IsMediaLoading,
            page.IsReaderDialogOpen, page.IsObscuredByGallery, ShortcutService.IsCapturing,
            paneVisible = pane.IsEffectivelyVisible,
            activationTask = RuntimeHelpers.GetHashCode(page.ReaderMediaActivationTask),
            activationStatus = page.ReaderMediaActivationTask.Status.ToString()
        }));
        void ObserveKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != expectedNativeKey) return;
            keySource = e.Source as Control;
            keyFocus = window.FocusManager?.GetFocusedElement() as Control;
            keyPost = (posts.SelectedItem as ArchivePostRow)?.Id;
            keyPostContainer = posts.SelectedItem is { } selected ? posts.ContainerFromItem(selected) : null;
            activationAtKey = page.ReaderMediaActivationTask;
            keyHandled = e.Handled;
            keyModifiers = e.KeyModifiers;
            nativeOwnedAtKey = keySource == null ? null : ArchivePage.ReaderMediaBelongsToNativeControl(keySource, posts);
            gestureEligibleAtKey = ArchivePage.IsReaderMediaGesture(e, ShortcutService.Current);
            Evidence("native-key-down", e.Key);
            keyDownSeen?.TrySetResult();
        }
        void ObserveKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != expectedNativeKey) return;
            Evidence("native-key-up", e.Key);
            keyUpSeen?.TrySetResult();
        }
        void ObservePointer(object? sender, PointerPressedEventArgs e)
        {
            if (pointerSeen == null) return;
            pointerSource = e.Source as Control;
            Evidence("native-pointer-down");
            pointerSeen.TrySetResult();
        }
        async Task<Control> ClickBodyAsync(ReaderBodyTextBlock body)
        {
            requestedBody = body;
            pointerSource = null;
            pointerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await input.ClickAsync(body, new Point(16, 10));
                await pointerSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Evidence("after-native-body-click");
                Control? focus = window.FocusManager?.GetFocusedElement() as Control;
                Control? container = posts.SelectedItem is { } selected ? posts.ContainerFromItem(selected) : null;
                Require(pointerSource?.GetSelfAndVisualAncestors().Contains(body) == true,
                    "Native reader click did not hit the requested body; see reader-native-probe pointer evidence.");
                Require((posts.SelectedItem as ArchivePostRow)?.Id == "1001" && container != null
                    && body.GetVisualAncestors().Contains(container)
                    && (ReferenceEquals(focus, body) || ReferenceEquals(focus, container))
                    && !ArchivePage.ReaderMediaBelongsToNativeControl(focus!, posts),
                    "Native body click did not focus its body/selected-post container; see reader-native-probe focus evidence.");
                return focus!;
            }
            finally { pointerSeen = null; }
        }
        async Task SendObservedKeyAsync(ulong symbol, bool modified = false, bool awaitActivation = true,
            bool transfersToGallery = false)
        {
            expectedNativeKey = symbol switch
            {
                MediaNativeInput.Enter => Key.Enter, MediaNativeInput.Space => Key.Space,
                MediaNativeInput.Up => Key.Up, MediaNativeInput.Down => Key.Down,
                _ => throw new ArgumentOutOfRangeException(nameof(symbol))
            };
            keyDownSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            keyUpSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            keySource = keyFocus = keyPostContainer = null; keyPost = null; activationAtKey = null; keyHandled = false;
            nativeOwnedAtKey = gestureEligibleAtKey = null;
            Evidence("before-native-key", expectedNativeKey);
            try
            {
                await input.KeyAsync(symbol, modified);
                // Observe actual routed delivery before reading the task. A driver
                // delay alone is not proof that this key created/owned that task.
                await keyDownSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // The real gallery callback may legitimately receive KeyUp after
                // taking focus. Its native release still comes from the driver.
                if (!transfersToGallery) await keyUpSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                // A tunnel observer can run before the production handler. Its task is
                // diagnostic pre-routing state, not the operation created by this key.
                // Anchor after actual native delivery/routing, then require that exact
                // operation to remain current through its real completion.
                Task routedActivation = page.ReaderMediaActivationTask;
                if (awaitActivation)
                {
                    await routedActivation;
                    Require(ReferenceEquals(routedActivation, page.ReaderMediaActivationTask),
                        "Reader replaced the activation after the native key finished routing.");
                }
                Evidence("after-native-key", expectedNativeKey);
            }
            finally { expectedNativeKey = null; keyDownSeen = keyUpSeen = null; }
        }
        pane.AddHandler(InputElement.PointerPressedEvent, ObservePointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(InputElement.KeyDownEvent, ObserveKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(InputElement.KeyUpEvent, ObserveKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            await SelectSecond();
            ReaderBodyTextBlock body = Body();
            // Activation deliberately uses all completed manifest assets, independent of
            // the media tab's current/favorites/rating filters. Do not derive an expected
            // post association from that unrelated, potentially hidden tile projection.
            await page.MediaLoadTask;
            var allAssets = (IReadOnlyList<ArchiveAssetRow>)typeof(ArchivePage).GetField("_assets",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;
            ArchivePostRow intendedPost = posts.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == "1001");
            expectedGalleryKey = ArchivePage.FindReaderMediaTarget(page.CurrentThread!.Summary.Id,
                intendedPost, allAssets)?.Key;
            Console.WriteLine("SMOKE reader-native-fixture " + JsonSerializer.Serialize(new {
                selected = (posts.SelectedItem as ArchivePostRow)?.Id, allAssets = allAssets.Count,
                visibleAssets = page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>().Count(),
                intendedPost = intendedPost.Id,
                attachments = intendedPost.Attachments.Select(row => new {
                    row.Attachment.Position, row.Attachment.MediaId, row.Attachment.Variant,
                    row.Attachment.AttachmentRevisionKey, row.Attachment.IsDeleted
                }).ToArray(),
                assetStates = allAssets.Select(row => new { row.Asset.MediaIdentifier, row.Asset.Variant,
                    row.Asset.OriginalFileName, row.Asset.IsCurrent, exists = File.Exists(row.Asset.Path) }).ToArray(),
                expected = expectedGalleryKey?.ToString(), mediaTask = page.MediaLoadTask.Status.ToString()
            }));
            Require(expectedGalleryKey?.PostId == "1001" && expectedGalleryKey.AttachmentPosition == 0,
                "Native fixture has no exact eligible second-post association in completed manifest assets.");
            foreach (ulong key in new[] { enter, MediaNativeInput.Space })
            {
                Control expectedOrigin = await ClickBodyAsync(body);
                Require(body.SelectionStart == body.SelectionEnd, "Native post click unexpectedly selected text.");
                int before = opens;
                await SendObservedKeyAsync(key);
                Require(opens == before + 1 && openedThread == page.CurrentThread!.Summary.Id
                    && opened == expectedGalleryKey
                    && ReferenceEquals(keySource, expectedOrigin) && ReferenceEquals(keyFocus, expectedOrigin)
                    && keyPost == "1001" && ReferenceEquals(keyPostContainer, posts.ContainerFromItem(posts.SelectedItem!))
                    && ReferenceEquals(from, expectedOrigin),
                    "Native Reader content key lost the exact selected-post association/origin; see reader-native-probe evidence.");
                body.SelectionStart = 0; body.SelectionEnd = 6;
                before = opens;
                await SendObservedKeyAsync(key);
                Require(opens == before, "Native text-selection gesture opened a gallery.");
                body.SelectionStart = body.SelectionEnd = 0;
                using (ShortcutService.BeginCapture()) await SendObservedKeyAsync(key);
                Require(opens == before, "Native shortcut capture opened a gallery.");
                await SendObservedKeyAsync(key, modified: true);
                Require(opens == before, "Modified native activation escaped its existing meaning.");
            }
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var previousBoundary = page.BeforeReaderMediaActivationAsync;
            int pendingBefore = opens;
            try
            {
                page.BeforeReaderMediaActivationAsync = () => release.Task;
                await SendObservedKeyAsync(enter, awaitActivation: false); await SendObservedKeyAsync(enter, awaitActivation: false);
                await SendObservedKeyAsync(MediaNativeInput.Space, awaitActivation: false);
                Require(!page.ReaderMediaActivationTask.IsCompleted && opens == pendingBefore,
                    "Native repeated-key fixture did not hold activation pending.");
                release.SetResult(); await page.ReaderMediaActivationTask;
                Require(opens == pendingBefore + 1, "Native repeated media keys opened duplicate galleries.");
            }
            finally { release.TrySetResult(); page.BeforeReaderMediaActivationAsync = previousBoundary; }
            await SendObservedKeyAsync(down); await page.ReaderNavigationTask;
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "1002", "Reader Down stopped navigating posts.");
            await SendObservedKeyAsync(up); await page.ReaderNavigationTask;
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "1001", "Reader Up stopped navigating posts.");

            body = Body();
            var host = (StackPanel)body.Parent!;
            var editor = new TextBox { Name = "ReaderActivationFixtureEditor", AcceptsReturn = true, Height = 70, Text = "text" };
            var button = new Button { Name = "ReaderActivationFixtureButton", Content = "Native button" };
            var link = new HyperlinkButton { Name = "ReaderActivationFixtureLink", Content = "Native link" };
            var slider = new Slider { Name = "ReaderActivationFixtureSlider", Minimum = 0, Maximum = 10, Value = 5 };
            int buttonClicks = 0, linkClicks = 0;
            button.Click += (_, _) => buttonClicks++;
            link.Click += (_, _) => linkClicks++;
            Control[] native = [editor, button, link, slider];
            foreach (Control control in native.Reverse()) host.Children.Insert(0, control);
            try
            {
                await page.FocusPostAsync("1001"); await FlushAsync();
                int before = opens;
                editor.Focus(); editor.CaretIndex = editor.Text!.Length;
                await SendObservedKeyAsync(MediaNativeInput.Space); await SendObservedKeyAsync(enter);
                Require(editor.Text!.Contains(' ') && editor.Text.Contains('\n') && opens == before,
                    "Native editor lost space/newline ownership.");
                foreach (ulong key in new[] { enter, MediaNativeInput.Space })
                {
                    button.Focus(); await SendObservedKeyAsync(key);
                    link.Focus(); await SendObservedKeyAsync(key);
                    slider.Focus(); await SendObservedKeyAsync(key);
                }
                Require(buttonClicks == 2 && linkClicks == 2 && opens == before,
                    "Native button/link activation was stolen or duplicated.");
                body.ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "Fixture menu action" } } };
                body.ContextMenu.Open(body);
                await input.KeyAsync(MediaNativeInput.Space);
                Require(opens == before, "An open native menu leaked Reader activation.");
                body.ContextMenu.Close(); body.ContextMenu = null;
            }
            finally { foreach (Control control in native) host.Children.Remove(control); }

            int protectedBefore = opens;
            await input.ClickAsync(page.FindControl<Button>("ExportArchiveButton")!);
            await Program.WaitUntilAsync(() => page.IsReaderDialogOpen);
            page.FindControl<TextBox>("ArchiveExportDestination")!.Focus();
            await input.KeyAsync(MediaNativeInput.Space);
            Require(opens == protectedBefore && page.IsReaderDialogOpen, "Native export dialog leaked Reader activation.");
            await input.ClickAsync(page.FindControl<Button>("ArchiveExportClose")!);
            var foreignBody = new TextBox { AcceptsReturn = true, Text = "foreign", Width = 180 };
            var foreign = new Window { Width = 240, Height = 150, Content = foreignBody };
            try
            {
                foreign.Show(window); foreignBody.Focus();
                await Program.WaitUntilAsync(() => foreign.IsActive);
                using var foreignInput = new MediaNativeInput(foreign);
                await foreignInput.KeyAsync(MediaNativeInput.Space); await foreignInput.KeyAsync(enter);
                Require(opens == protectedBefore, "A foreign window opened Reader media.");
            }
            finally { foreign.Close(); window.Activate(); }
            await Program.WaitUntilAsync(() => window.IsActive);

            // Exercise the production callback and return path once, not only a spy.
            page.OpenGallery = realOpen;
            await SelectSecond(); body = Body();
            Control productionOrigin = await ClickBodyAsync(body);
            await SendObservedKeyAsync(enter, transfersToGallery: true);
            // The real callback may already have focused its gallery by the
            // observation point; the key must still carry the proven input origin.
            Require(ReferenceEquals(keySource, productionOrigin) && keyPost == "1001",
                "Production reader key lost its proven focused selected-post origin.");
            ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
            await Program.WaitUntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer);
            var viewer = (GalleryViewer)galleryHost.Content!;
            GalleryViewerState productionState = viewer.CaptureState();
            Require(productionState.Key == expectedGalleryKey!.Asset && productionState.PostId == expectedGalleryKey.PostId
                && productionState.AttachmentPosition == expectedGalleryKey.AttachmentPosition,
                "Production callback opened unrelated first-thread media or lost the exact asset association.");
            Button closeGallery = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(b => b.Name == "GalleryClose");
            try
            {
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    if (!closeGallery.IsEffectivelyVisible || closeGallery.Bounds.Width <= 0 || closeGallery.Bounds.Height <= 0
                        || closeGallery.TranslatePoint(new Point(closeGallery.Bounds.Width / 2, closeGallery.Bounds.Height / 2), window) is not Point point)
                        return false;
                    return window.InputHitTest(point) is Visual hit
                        && hit.GetSelfAndVisualAncestors().Contains(closeGallery);
                }); // Do not send a native click into the preceding Reader render scene.
            }
            finally
            {
                Point? center = closeGallery.TranslatePoint(new Point(closeGallery.Bounds.Width / 2, closeGallery.Bounds.Height / 2), window);
                Console.WriteLine("SMOKE reader-native-close-geometry " + JsonSerializer.Serialize(new {
                    center = center?.ToString(),
                    adorners = window.GetVisualDescendants().OfType<AdornerLayer>().SelectMany(layer => layer.Children).Select(adorner => new {
                        type = adorner.GetType().FullName, adorner.Name,
                        adornedType = adorner.GetValue(AdornerLayer.AdornedElementProperty)?.GetType().FullName,
                        adornedName = (adorner.GetValue(AdornerLayer.AdornedElementProperty) as Control)?.Name,
                        adornedParent = (adorner.GetValue(AdornerLayer.AdornedElementProperty) as Control)?.Parent?.GetType().FullName
                    }).ToArray(),
                    hit = center is Point at ? (window.InputHitTest(at) as Control)?.GetType().FullName : null,
                    hitAncestors = center is Point hitPoint && window.InputHitTest(hitPoint) is Visual hitVisual
                        ? hitVisual.GetSelfAndVisualAncestors().OfType<Control>().Select(control => new {
                            type = control.GetType().FullName, control.Name, bounds = control.Bounds.ToString(),
                            control.IsVisible, control.IsHitTestVisible, control.ZIndex
                        }).ToArray() : null,
                    ancestors = closeGallery.GetSelfAndVisualAncestors().OfType<Control>().Select(control => new {
                        type = control.GetType().Name, control.Name, bounds = control.Bounds.ToString(),
                        control.IsVisible, control.IsEffectivelyVisible, control.IsHitTestVisible, control.Opacity
                    }).ToArray(),
                    galleryVisible = galleryHost.IsVisible,
                    readerVisible = window.FindControl<ContentControl>("ReaderHost")!.IsVisible
                }));
                Program.CaptureWindow(window, "smoke-reader-native-close-geometry.png");
            }
            int closePresses = 0, closeReleases = 0, closeClicks = 0, closeRequests = 0;
            double? returnedTop = null;
            bool ReturnedPostHasFocusAndAnchor()
            {
                if (posts.SelectedItem is not ArchivePostRow { Id: "1001" }
                    || posts.ContainerFromItem(posts.SelectedItem!) is not Control returnedPost
                    || window.FocusManager?.GetFocusedElement() is not Control returnedFocus
                    || !returnedFocus.GetSelfAndVisualAncestors().Contains(returnedPost)) return false;
                var viewport = posts.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
                returnedTop = viewport == null ? null : returnedPost.TranslatePoint(default, viewport)?.Y;
                return returnedTop is double top && Math.Abs(top - 8) <= 2;
            }
            string? pressedSource = null, releasedSource = null, pressedPosition = null, releasedPosition = null;
            void WindowPressed(object? sender, PointerPressedEventArgs e)
            {
                pressedSource = (e.Source as Control)?.Name ?? e.Source?.GetType().Name;
                pressedPosition = e.GetPosition(window).ToString();
            }
            void WindowReleased(object? sender, PointerReleasedEventArgs e)
            {
                releasedSource = (e.Source as Control)?.Name ?? e.Source?.GetType().Name;
                releasedPosition = e.GetPosition(window).ToString();
            }
            void ClosePressed(object? sender, PointerPressedEventArgs e) => closePresses++;
            void CloseReleased(object? sender, PointerReleasedEventArgs e) => closeReleases++;
            void CloseClicked(object? sender, RoutedEventArgs e) => closeClicks++;
            void CloseRequested() => closeRequests++;
            closeGallery.AddHandler(InputElement.PointerPressedEvent, ClosePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            closeGallery.AddHandler(InputElement.PointerReleasedEvent, CloseReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            closeGallery.Click += CloseClicked;
            viewer.CloseRequested += CloseRequested;
            window.AddHandler(InputElement.PointerPressedEvent, WindowPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerReleasedEvent, WindowReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                await input.ClickAsync(closeGallery);
                await Program.WaitUntilAsync(() => !galleryHost.IsVisible && window.FindControl<ContentControl>("ReaderHost")!.IsVisible
                    && ReturnedPostHasFocusAndAnchor());
                Require(closePresses == 1 && closeReleases == 1 && closeClicks == 1 && closeRequests == 1,
                    "Native GalleryClose did not deliver exactly one pointer press/release, click and close request.");
            }
            finally
            {
                Console.WriteLine("SMOKE reader-native-return " + JsonSerializer.Serialize(new {
                    closePresses, closeReleases, closeClicks, closeRequests, returnedTop,
                    pressedSource, releasedSource, pressedPosition, releasedPosition,
                    galleryVisible = galleryHost.IsVisible,
                    readerVisible = window.FindControl<ContentControl>("ReaderHost")!.IsVisible,
                    selectedPost = (posts.SelectedItem as ArchivePostRow)?.Id,
                    closeBounds = closeGallery.Bounds.ToString(),
                    closeOrigin = closeGallery.TranslatePoint(default, window)?.ToString(),
                    windowSize = window.Bounds.Size.ToString(), window.RenderScaling,
                    activationStatus = page.ReaderMediaActivationTask.Status.ToString(),
                    focus = (window.FocusManager?.GetFocusedElement() as Control)?.GetType().Name
                }));
                closeGallery.RemoveHandler(InputElement.PointerPressedEvent, ClosePressed);
                closeGallery.RemoveHandler(InputElement.PointerReleasedEvent, CloseReleased);
                closeGallery.Click -= CloseClicked;
                viewer.CloseRequested -= CloseRequested;
                window.RemoveHandler(InputElement.PointerPressedEvent, WindowPressed);
                window.RemoveHandler(InputElement.PointerReleasedEvent, WindowReleased);
            }
            Require((posts.SelectedItem as ArchivePostRow)?.Id == "1001", "Gallery return lost the selected reader post.");
            Program.CaptureWindow(window, "smoke-reader-media-native-return.png");
            Console.WriteLine("SMOKE reader media native: clicked body Enter/Space, selection, editor, button/link, slider/menu/dialog/foreign-window guards, Up/Down and production gallery return passed via " + input.Backend);
        }
        finally
        {
            pane.RemoveHandler(InputElement.PointerPressedEvent, ObservePointer);
            pane.RemoveHandler(InputElement.KeyDownEvent, ObserveKeyDown);
            pane.RemoveHandler(InputElement.KeyUpEvent, ObserveKeyUp);
            page.OpenGallery = realOpen;
        }
    }

    private static async Task CheckReaderShortcutsAsync(ArchivePage page)
    {
        ResourceId original = page.CurrentThread!.Summary.Id;
        ResourceId target = original with { Thread = "1001" };
        await page.LoadThreadAsync(target);
        await page.FocusPostAsync("1001");
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        Func<ResourceId, GalleryEntryKey?, Control?, Task>? previous = page.OpenGallery;
        ResourceId? galleryTarget = null;
        try
        {
            page.OpenGallery = (id, _, _) => { galleryTarget = id; return Task.CompletedTask; };
            KeyEventArgs galleryKey = Press(posts, Key.Enter, KeyModifiers.Shift);
            Require(galleryKey.Handled && galleryTarget == target, "Reader gallery shortcut used archive selection instead of the currently read thread.");
            Press(posts, Key.E, primary);
            await Program.WaitUntilAsync(() => page.IsReaderDialogOpen);
            Require(page.FindControl<TextBlock>("ArchiveExportSummary")!.Text == target.Source + " /" + target.Board + "/ " + target.Thread,
                "Reader export shortcut used archive selection instead of the currently read thread.");
            galleryTarget = null;
            TextBox destination = page.FindControl<TextBox>("ArchiveExportDestination")!;
            Press(destination, Key.Enter, KeyModifiers.Shift);
            Require(galleryTarget == null, "Reader shortcut escaped a focused dialog input.");
            Click(page.FindControl<Button>("ArchiveExportClose")!);
        }
        finally { page.OpenGallery = previous; await page.LoadThreadAsync(original); await page.FocusPostAsync("1000"); await page.MediaLoadTask; }
    }

    private static async Task CheckNativeMediaKeysAsync(ArchivePage page)
    {
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        Require(!page.ReaderArrowBelongsToNativeControl(page.FindControl<Button>("ReaderNextPost")), "Ordinary reader toolbar lost post navigation.");
        Require(!page.ReaderArrowBelongsToNativeControl(posts), "The posts list lost reader arrows.");
        Require(page.ReaderArrowBelongsToNativeControl(tabs.GetVisualDescendants().OfType<TabItem>().First()), "Tab headers lost native arrows.");
        Require(page.ReaderArrowBelongsToNativeControl(new Slider()), "Slider arrows were captured by the reader.");
        Require(page.ReaderArrowBelongsToNativeControl(new TextBox()), "Input arrows were captured by the reader.");
        Func<IReadOnlyList<GalleryEntry>, GalleryEntryKey, Control?, Task>? previous = page.OpenMediaGallery;
        GalleryEntryKey? entered = null;
        IReadOnlyList<GalleryEntry>? sequence = null;
        try
        {
            page.OpenMediaGallery = (entries, key, _) => { entered = key; sequence = entries; return Task.CompletedTask; };
            tabs.SelectedIndex = 1;
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            DataGrid table = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
            foreach (bool tileMode in new[] { true, false })
            {
                page.MediaTilesPreference = tileMode;
                Control list = tileMode ? tiles : table;
                ArchiveAssetRow row = (tileMode ? tiles.ItemsSource! : table.ItemsSource).Cast<ArchiveAssetRow>().First();
                if (tileMode) tiles.SelectedItem = row; else table.SelectedItem = row;
                Require(page.ReaderArrowBelongsToNativeControl(list), "Media list lost native arrows.");
                entered = null;
                KeyEventArgs key = Press(list, Key.Enter);
                await Program.WaitUntilAsync(() => entered != null);
                Require(key.Handled && entered!.Asset.AssetId == row.Asset.AssetId && entered.Asset.Directory == row.Manifest.Directory,
                    "Enter opened a different original/version.");
                Require(sequence!.Select(entry => entry.Key.AssetId).SequenceEqual(tiles.ItemsSource!.Cast<ArchiveAssetRow>().Select(asset => asset.Asset.AssetId)),
                    "Enter changed the frozen visible gallery sequence.");

            }
        }
        finally { page.OpenMediaGallery = previous; page.MediaTilesPreference = true; tabs.SelectedIndex = 0; }
    }

    private static async Task CheckFavoriteFailureAsync(ArchivePage page)
    {
        await page.FocusPostAsync("1000");
        await page.MediaLoadTask;
        await Program.WaitUntilAsync(() => page.FindControl<Grid>("ArchiveReaderPane")!.GetVisualDescendants().OfType<ArchiveAttachmentView>().Any());
        ArchiveAttachmentView inline = page.FindControl<Grid>("ArchiveReaderPane")!.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .First(view => view.GetVisualAncestors().OfType<ListBoxItem>().Any(item => item.DataContext is ArchivePostRow { Id: "1000" }));
        Func<GalleryEntry, bool>? isFavorite = page.IsArchiveFavorite;
        Func<GalleryEntry, Task>? toggle = page.ToggleArchiveFavoriteAsync;
        bool marked = false;
        try
        {
            page.IsArchiveFavorite = _ => marked;
            page.ToggleArchiveFavoriteAsync = _ => throw new IOException("Fixture favorite write denied");
            inline.RefreshFavorite();
            ToggleButton star = inline.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "ArchiveMediaFavorite");
            Click(star);
            page.RefreshArchiveFavoriteControls();
            Require(star.IsChecked == false && (AutomationProperties.GetName(star) ?? string.Empty).Contains("Fixture favorite write denied"), "Favorite refresh hid the failure or claimed success.");
            Require(star.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Fixture favorite write denied"), "Favorite failure is not visible at the medium.");
            Require(star.Focusable && AutomationProperties.GetLiveSetting(star) == AutomationLiveSetting.Assertive, "Favorite failure is not keyboard/automation reachable.");
            await FlushAsync();
            await Task.Delay(120);
            Require(star.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Fixture favorite write denied"
                    && text.Bounds.Height > 0 && text.Foreground?.Opacity > 0 && text.IsEffectivelyVisible),
                "Favorite failure has no laid-out visible text.");
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            Point? top = star.TranslatePoint(default, posts);
            Require(top != null && top.Value.Y >= 0 && top.Value.Y + star.Bounds.Height <= posts.Bounds.Height,
                "Favorite error was tested outside the visible post viewport.");
            Program.CaptureWindow((Window)TopLevel.GetTopLevel(star)!, "smoke-reader-favorite-error.png");
            page.ToggleArchiveFavoriteAsync = _ => { marked = true; return Task.CompletedTask; };
            Click(star);
            Require(star.IsChecked == true && !(AutomationProperties.GetName(star) ?? string.Empty).Contains("denied"), "The next successful favorite action did not clear the failure.");
        }
        finally { page.IsArchiveFavorite = isFavorite; page.ToggleArchiveFavoriteAsync = toggle; page.RefreshArchiveFavoriteControls(); }
    }

    private static async Task CheckHistoryAsync(ArchivePage page)
    {
        ResourceId thread = page.CurrentThread!.Summary.Id;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        await page.FocusPostAsync("1048");
        await FlushAsync();
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset += new Vector(0, 65);
        await FlushAsync();
        (string Id, double Top) before = Top(posts);
        await Quote("1054");
        Click(page.FindControl<Button>("ArchiveHistoryBack")!);
        await FlushAsync();
        (string Id, double Top) after = Top(posts);
        Require(before.Id == after.Id && Math.Abs(before.Top - after.Top) < 2 && (posts.SelectedItem as ArchivePostRow)?.Id == "1048",
            "Cross-page quote back did not restore the exact visible/selected post and offset.");
        await page.FocusPostAsync("1000");
        for (int index = 1; index <= 33; index++) await Quote((1000 + index).ToString());
        for (int index = 0; index < 32; index++)
        {
            Require(page.FindControl<Button>("ArchiveHistoryBack")!.IsVisible, "History overflow discarded recent entries.");
            Click(page.FindControl<Button>("ArchiveHistoryBack")!);
            await FlushAsync();
        }
        Require(!page.FindControl<Button>("ArchiveHistoryBack")!.IsVisible && (posts.SelectedItem as ArchivePostRow)?.Id == "1001", "History did not retain exactly the latest 32 entries.");
        await page.FocusPostAsync("1000");
        async Task Quote(string id)
        {
            ArchivePostId target = new(thread, id);
            await page.NavigateQuoteAsync(new ArchiveQuoteRow(new ArchiveQuote(target, target, ">>" + id, null)));
            await FlushAsync();
        }
    }

    private static async Task CheckFavoritesOverviewAsync(MainWindow window, ArchivePage page)
    {
        // Quote/history navigation can restart the asynchronous manifest scan.
        await page.MediaLoadTask;
        ArchiveAssetRow row = page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>().First();
        ResourceId thread = page.CurrentThread!.Summary.Id;
        GalleryEntry entry = new(new GalleryAssetKey(thread, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, false, true);
        GalleryEntry remaining = entry with { Key = entry.Key with { Thread = thread with { Thread = "1001" } } };
        await window.SessionHost.SetFavoriteAsync(entry, true);
        await window.SessionHost.SetFavoriteAsync(remaining, true);
        Task overview = page.OpenFavoritesOverviewAsync();
        await Program.WaitUntilAsync(() => window.OwnedWindows.Any(dialog => dialog.Name == "FavoritesOverviewDialog"));
        Window dialog = window.OwnedWindows.Single(dialog => dialog.Name == "FavoritesOverviewDialog");
        ListBox list = Named<ListBox>(dialog, "FavoritesList");
        await Program.WaitUntilAsync(() => list.ItemCount == 2 && Named<Button>(dialog, "FavoritesExportOriginals").IsEnabled);
        list.SelectedIndex = 0;
        Require(Equals(Named<Button>(dialog, "FavoritesExportOriginals").Content, Texts.T("viewer.favorites.exportShown", 2)),
            "Selecting one favorite hid the actual two-item export scope.");
        Named<TextBox>(dialog, "FavoritesSearch").Text = "absent favorite search";
        Require(Named<TextBlock>(dialog, "FavoritesEmpty").IsVisible
            && Named<TextBlock>(dialog, "FavoritesEmpty").Text == Texts.T("viewer.favorites.noResults")
            && Named<Button>(dialog, "FavoritesClearSearch").IsVisible,
            "A favorite search with no matches did not offer a local search reset.");
        Click(Named<Button>(dialog, "FavoritesClearSearch"));
        Require(list.ItemCount == 2 && Named<ComboBox>(dialog, "FavoritesScope").SelectedIndex == 3,
            "Clearing favorite search changed scope or lost visible favorites.");
        Named<TextBox>(dialog, "FavoritesSearch").Text = "reader.png";
        list.SelectedIndex = 0;
        dialog.UpdateLayout();
        Program.CaptureWindow(dialog, "smoke-favorites-review.png");
        Click(Named<Button>(dialog, "FavoritesView"));
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("GalleryHost")!.IsVisible);
        GalleryViewer viewer = (GalleryViewer)window.FindControl<ContentControl>("GalleryHost")!.Content!;
        Require(viewer.CaptureState().Key == entry.Key, "Favorite View opened a different identity.");
        Click(viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose"));
        await overview;
        await Program.WaitUntilAsync(() => window.OwnedWindows.Any(candidate => candidate.Name == "FavoritesOverviewDialog"));
        dialog = window.OwnedWindows.Single(candidate => candidate.Name == "FavoritesOverviewDialog");
        list = Named<ListBox>(dialog, "FavoritesList");
        await Program.WaitUntilAsync(() => Named<Button>(dialog, "FavoritesRemove").IsEnabled);
        Require(Named<TextBox>(dialog, "FavoritesSearch").Text == "reader.png" && list.SelectedItem != null, "Favorite return lost search or exact selection.");
        Click(Named<Button>(dialog, "FavoritesRemove"));
        await Program.WaitUntilAsync(() => list.ItemCount == 1 && Named<Button>(dialog, "FavoritesRemove").IsEnabled);
        Require(File.Exists(row.Asset.Path) && !window.SessionHost.IsFavorite(entry), "Removing a favorite deleted the original or kept the mark.");
        Require(window.SessionHost.IsFavorite(remaining) && Named<Button>(dialog, "FavoritesExportOriginals").IsEnabled,
            "Removing one of two visible favorites left the remaining original export disabled.");
        Require(Equals(Named<Button>(dialog, "FavoritesExportOriginals").Content, Texts.T("viewer.favorites.exportShown", 1)),
            "Removing a favorite left a stale export count.");
        await CheckRemainingFavoriteExportAsync(window, page, dialog, remaining);
        Named<ComboBox>(dialog, "FavoritesScope").SelectedIndex = 2;
        await Program.WaitUntilAsync(() => list.ItemCount == 0);
        Require(!Named<Button>(dialog, "FavoritesExportOriginals").IsEnabled, "Empty favorites retained an enabled original export.");
        Named<TextBox>(dialog, "FavoritesSearch").Text = string.Empty;
        Require(Named<TextBlock>(dialog, "FavoritesEmpty").Text == Texts.T("viewer.favorites.emptyScope")
            && !Named<Button>(dialog, "FavoritesClearSearch").IsVisible,
            "An empty favorite scope was presented as an active search.");
        dialog.Close();
        await FlushAsync();
    }

    private static async Task CheckRemainingFavoriteExportAsync(MainWindow window, ArchivePage page, Window overview, GalleryEntry remaining)
    {
        Func<IReadOnlyList<FavoriteEntry>?, CancellationToken, Task<FavoriteResolutionSnapshot>> resolve = page.ResolveFavoriteAssets!;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int resolutions = 0;
        FavoriteEntry[]? requested = null;
        string destination = TemporaryWorkspacePaths.CreateDirectory("meowchan-favorite-export-");
        string previousDirectory = page.LastExportDirectoryPreference;
        Window? exportDialog = null;
        try
        {
            page.LastExportDirectoryPreference = destination;
            page.ResolveFavoriteAssets = async (entries, token) =>
            {
                resolutions++;
                requested = entries?.ToArray();
                await gate.Task.WaitAsync(token);
                return await resolve(entries, token);
            };
            Button export = Named<Button>(overview, "FavoritesExportOriginals");
            Button remove = Named<Button>(overview, "FavoritesRemove");
            Button view = Named<Button>(overview, "FavoritesView");
            Click(export);
            Require(resolutions == 1 && requested is { Length: 1 }
                && requested[0].Key == FavoriteAssetKey.From(remaining.Key.Thread, remaining.Asset),
                "Original export did not freeze exactly the remaining favorite.");
            TextBox search = Named<TextBox>(overview, "FavoritesSearch");
            search.Text = "no matching favorite";
            search.Text = "reader.png";
            Named<ListBox>(overview, "FavoritesList").SelectedIndex = 0;
            Require(!export.IsEnabled && !remove.IsEnabled && !view.IsEnabled,
                "Search/selection re-enabled favorite actions while export resolution was pending.");
            Click(export); Click(remove); Click(view);
            Require(resolutions == 1 && window.SessionHost.IsFavorite(remaining),
                "A repeated favorite action escaped the pending original-export guard.");
            gate.TrySetResult();
            await Program.WaitUntilAsync(() => overview.OwnedWindows.Any());
            exportDialog = overview.OwnedWindows.Single();
            // The dialog owns a frozen set even if the favorite is removed before copying.
            await window.SessionHost.SetFavoriteAsync(remaining, false);
            Button start = Named<Button>(exportDialog, "OriginalExportStart");
            Require(start.IsEnabled, "The remaining favorite could not be exported immediately after removal of another.");
            Click(start);
            Button close = Named<Button>(exportDialog, "OriginalExportClose");
            await Program.WaitUntilAsync(() => Equals(close.Content, CommandCatalog.Label("export.close")));
            string[] copied = Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
            Require(copied.Length == 1 && File.ReadAllBytes(copied[0]).SequenceEqual(File.ReadAllBytes(remaining.Asset.Path)),
                "Original export lost its frozen target or changed the remaining original bytes.");
            Click(close);
            await Program.WaitUntilAsync(() => export.IsEnabled && remove.IsEnabled);
            Console.WriteLine("SMOKE favorites: remove one of two, immediate original export, pending-action guard and frozen original bytes passed.");
        }
        finally
        {
            gate.TrySetResult();
            page.ResolveFavoriteAssets = resolve;
            page.LastExportDirectoryPreference = previousDirectory;
            exportDialog?.Close();
            Directory.Delete(destination, recursive: true);
        }
    }

    private static (string Id, double Top) Top(ListBox posts) => posts.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is ArchivePostRow)
        .Select(item => (Id: ((ArchivePostRow)item.DataContext!).Id, Top: item.TranslatePoint(default, posts)!.Value.Y, Height: item.Bounds.Height))
        .Where(item => item.Top + item.Height > 0).OrderBy(item => item.Top).Select(item => (item.Id, item.Top)).First();
    private static KeyEventArgs Press(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        control.Focus();
        KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        control.RaiseEvent(args);
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        return args;
    }
    private static T Named<T>(Control parent, string name) where T : Control => parent.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(MenuItem item) => SmokeActionClick.Execute(item);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task FlushAsync() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Task.Delay(20); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
