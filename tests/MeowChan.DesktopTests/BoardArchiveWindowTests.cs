using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BoardArchiveWindowTests
{
    [Fact]
    public void TheOpenedListDiscardsLateBoardsAndConfirmsOneWatchAtATimeInAllLanguages()
        => BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(ReaderFonts.ManagerOptions());
            var oldBoard = new BoardArchiveLocation("g", new Uri("https://boards.4chan.org/g/archive"));
            var newBoard = new BoardArchiveLocation("gif", new Uri("https://boards.4chan.org/gif/archive"));
            var existing = new BoardArchiveThread("123", "https://boards.4chan.org/gif/thread/123");
            var fresh = new BoardArchiveThread("124", "https://boards.4chan.org/gif/thread/124");
            var gone = new BoardArchiveThread("125", "https://boards.4chan.org/gif/thread/125");
            TaskCompletionSource<IReadOnlyList<BoardArchiveThread>?> late = new();
            TaskCompletionSource<DesktopAdmissionResult> adding = new();
            CancellationToken oldToken = default;
            List<string> detailRequests = new();
            List<string> watchRequests = new();
            List<string> opened = new();
            BoardArchiveWindow dialog = new([oldBoard, newBoard], (board, token) =>
            {
                if (board == newBoard) return Task.FromResult<IReadOnlyList<BoardArchiveThread>?>([existing, fresh, gone]);
                oldToken = token;
                return late.Task;
            }, (thread, _) =>
            {
                detailRequests.Add(thread.Id);
                return Task.FromResult(thread == gone ? null
                    : new BoardArchiveThreadDetails("Preview", "Opening post", SourceThreadState.Archived, thread == existing));
            }, thread =>
            {
                watchRequests.Add(thread.Id);
                return adding.Task;
            }, thread => opened.Add(thread.Id));
            dialog.Show();
            dialog.PlatformImpl!.Activated?.Invoke();
            dialog.SetRenderScaling(1.5);
            try
            {
                Layout(dialog);
                ComboBox boards = Control<ComboBox>(dialog, "BoardArchiveBoards");
                ListBox rows = Control<ListBox>(dialog, "BoardArchiveThreads");
                TextBlock status = Control<TextBlock>(dialog, "BoardArchiveStatus");
                Button watch = Control<Button>(dialog, "BoardArchiveWatch");
                Button open = Control<Button>(dialog, "BoardArchiveOpen");
                Assert.True(oldToken.CanBeCanceled);
                Assert.Empty(detailRequests);

                boards.SelectedIndex = 1;
                Layout(dialog);
                Assert.True(oldToken.IsCancellationRequested);
                Assert.Equal(3, rows.ItemCount);
                late.SetResult([new BoardArchiveThread("999", "https://boards.4chan.org/g/thread/999")]);
                Layout(dialog);
                Assert.Equal(3, rows.ItemCount);
                Assert.Empty(detailRequests);
                Assert.Equal("#123", rows.Items[0]!.ToString());

                Click(dialog, rows.ContainerFromIndex(0)!);
                Assert.Equal(new[] { "123" }, detailRequests);
                Assert.False(watch.IsEnabled);
                Assert.Equal(Texts.T("admission.alreadyPresent"), status.Text);
                Click(dialog, open);
                Assert.Equal(new[] { "123" }, opened);
                Assert.Empty(watchRequests);

                Click(dialog, rows.ContainerFromIndex(2)!);
                Assert.Equal(Texts.T("boardArchive.gone"), status.Text);
                Assert.False(open.IsEnabled);
                Assert.False(watch.IsEnabled);

                Click(dialog, rows.ContainerFromIndex(1)!);
                Assert.Equal(new[] { "123", "125", "124" }, detailRequests);
                Assert.True(watch.IsEnabled);
                Assert.Empty(watchRequests);
                string[] languages = ["en", "de", .. Texts.BundledPackCodes()];
                Assert.Equal(12, languages.Length);
                foreach (string language in languages)
                {
                    ReaderFonts.Apply(Application.Current!, language);
                    Texts.SetLanguage(language);
                    Layout(dialog);
                    Assert.Equal(1.5, dialog.RenderScaling);
                    Assert.Equal(Texts.Own("boardArchive.title"), dialog.Title);
                    Assert.Equal(Texts.Own("boardArchive.threads"), AutomationProperties.GetName(rows));
                    Assert.Equal(Texts.Own("boardArchive.watch"), watch.Content);
                    Assert.All(BoardAdmissionCatalog.Entries.Where(entry => entry.Key.StartsWith("boardArchive.", StringComparison.Ordinal)),
                        entry => Assert.False(string.IsNullOrWhiteSpace(Texts.Own(entry.Key)), language + "/" + entry.Key));
                    Point listTop = rows.TranslatePoint(default, dialog)!.Value;
                    Point buttonTop = watch.TranslatePoint(default, dialog)!.Value;
                    Assert.True(rows.Bounds.Height > 100, language);
                    Assert.True(listTop.Y + rows.Bounds.Height <= buttonTop.Y + 1, language);
                    Assert.True(buttonTop.X + watch.Bounds.Width <= dialog.Bounds.Width + 1, language);
                    Assert.Same(fresh, rows.SelectedItem);
                }

                Assert.True(watch.Focus(NavigationMethod.Tab));
                dialog.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                dialog.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
                Layout(dialog);
                Assert.Equal(new[] { "124" }, watchRequests);
                Click(dialog, watch);
                Assert.Equal(new[] { "124" }, watchRequests);
                adding.SetResult(new DesktopAdmissionResult(AdmissionStatus.Added, 42));
                Layout(dialog);
                Assert.Equal(Texts.T("admission.added"), status.Text);
                Assert.False(watch.IsEnabled);
                Assert.True(open.IsEnabled);
            }
            finally
            {
                dialog.Close();
                Layout(dialog);
            }
        });

    [Fact]
    public async Task ReturningToABoardAndThreadWaitsForTheCanceledReadAndShowsTheLatestResult()
    {
        using TempDirectory profile = new();
        BoardArchiveHostTests.Configure(profile.Path);
        DelayedRead<string[]> boardRead = new(["999"], ["123", "124"]);
        DelayedRead<SourcePost[]> threadRead = new([Post("old opening")], [Post("latest opening")]);
        await using var worker = new BoardArchiveHostTests.ArchiveWorker
        {
            ReadIds = (board, token) => board == "a" ? boardRead.RunAsync(token) : Task.FromResult<string[]>(["777"]),
            ReadPosts = (thread, token) => thread == "123" ? threadRead.RunAsync(token) : Task.FromResult<SourcePost[]>([Post("other opening")])
        };
        using PluginSourceImageBoard source = worker.CreateSource();
        await using DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new DesktopSessionHost(profile.Path, [source],
            updateSessionFactory: (directory, config) => new UpdateSession(directory, config, disableEnvironment: () => "1")));
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        BoardArchiveLocation a = Assert.IsType<BoardArchiveLocation>(host.FindBoardArchive("https://archive.test/a/archive"));
        BoardArchiveLocation b = Assert.IsType<BoardArchiveLocation>(host.FindBoardArchive("https://archive.test/b/archive"));

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruComponentDispatcher.Wait(ExerciseAsync());
        });

        async Task ExerciseAsync()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
            BoardArchiveWindow dialog = new([a, b], host.ReadBoardArchiveAsync, host.ReadBoardArchiveThreadAsync,
                _ => throw new InvalidOperationException("Unexpected watch"), _ => { });
            dialog.Show();
            try
            {
                ComboBox boards = Control<ComboBox>(dialog, "BoardArchiveBoards");
                ListBox rows = Control<ListBox>(dialog, "BoardArchiveThreads");
                TextBlock status = Control<TextBlock>(dialog, "BoardArchiveStatus");
                Button retry = Control<Button>(dialog, "BoardArchiveRetry");
                Button watch = Control<Button>(dialog, "BoardArchiveWatch");
                await boardRead.Started.Task.WaitAsync(deadline.Token);
                boards.SelectedIndex = 1;
                await boardRead.Canceled.Task.WaitAsync(deadline.Token);
                await BooruTestSupport.WaitAsync(() => rows.ItemCount == 1, "board B archive");
                Assert.Equal("777", Assert.IsType<BoardArchiveThread>(rows.Items[0]).Id);

                Task superseded = host.ReadBoardArchiveAsync(a, deadline.Token);
                boards.SelectedIndex = 0;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => superseded.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, boardRead.Calls);
                Assert.Equal(Texts.T("wizard.loading"), status.Text);
                Assert.False(retry.IsVisible);
                boardRead.Release.SetResult();
                await BooruTestSupport.WaitAsync(() => rows.ItemCount == 2, "latest board A archive");
                Assert.Equal(new[] { "123", "124" }, rows.Items.Cast<BoardArchiveThread>().Select(thread => thread.Id));
                Assert.Equal(2, boardRead.Calls);

                rows.SelectedIndex = 0;
                await threadRead.Started.Task.WaitAsync(deadline.Token);
                rows.SelectedIndex = 1;
                await threadRead.Canceled.Task.WaitAsync(deadline.Token);
                await BooruTestSupport.WaitAsync(() => watch.IsEnabled, "thread B preview");
                rows.SelectedIndex = 0;
                Assert.Equal(1, threadRead.Calls);
                Assert.Equal(Texts.T("boardArchive.loadingThread"), status.Text);
                Assert.False(watch.IsEnabled);
                Assert.False(retry.IsVisible);
                threadRead.Release.SetResult();
                await BooruTestSupport.WaitAsync(() => watch.IsEnabled, "latest thread A preview");
                Assert.Equal(2, threadRead.Calls);
                Assert.Equal("123", Assert.IsType<BoardArchiveThread>(rows.SelectedItem).Id);
                Assert.Equal(Texts.T("sources.archived"), status.Text);
                Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == "latest opening");
                Assert.False(retry.IsVisible);
            }
            finally
            {
                dialog.Close();
                boardRead.Release.TrySetResult();
                threadRead.Release.TrySetResult();
            }
        }

        static SourcePost Post(string text) => new("123", 0, SourceFieldPresence.Text, Text: text, TextFormat: SourceTextFormat.PlainText);
    }

    private sealed class DelayedRead<T>(T first, T latest)
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task<T> RunAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) != 1) return latest;
            using CancellationTokenRegistration registration = token.Register(() => Canceled.TrySetResult());
            Started.SetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            return first;
        }
    }

    [Fact]
    public async Task TheDownloadsBoardsMenuAndUrlFieldOpenTheSameArchiveDialog()
    {
        using TempDirectory profile = new();
        using BoardArchiveHostTests.ArchiveTransport transport = new() { EmptyLiveCatalog = true };
        BoardArchiveHostTests.Configure(profile.Path);
        await using DesktopSessionHost session = OwnedUiDispatcher.CreateHost(() => BoardArchiveHostTests.CreateHost(profile.Path, transport));
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(session.State == DesktopSessionState.Ready, session.StartupError);
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruComponentDispatcher.Wait(Exercise());

            async Task Exercise()
            {
                await using MainWindow window = new(session) { Width = 1000, Height = 700, Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                Button add = window.FindControl<Button>("AddUrlButton")!;
                await BooruTestSupport.WaitAsync(() => add.IsEffectivelyEnabled, "Downloads URL admission", 20000);
                Assert.Equal(AdmissionStatus.Added, (await session.AddAsync(BoardArchiveHostTests.BoardUrl)).Status);
                await BooruTestSupport.WaitAsync(() => window.VisibleBoards.Count == 1, "board watch in Downloads");
                window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 1;
                Layout(window);
                DataGrid boards = window.FindControl<DataGrid>("BoardsDataGrid")!;
                boards.SelectedIndex = 0;
                Assert.True(boards.Focus());
                EffectiveShortcutBindings mac = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
                Assert.True(mac.Matches(new KeyEventArgs { Key = Key.F10, KeyModifiers = KeyModifiers.Shift }, "menu.context-open", "downloads"));
                window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
                Layout(window);
                ContextMenu menu = Assert.IsType<ContextMenu>(boards.ContextMenu);
                Assert.True(menu.IsOpen);
                MenuItem browse = Assert.Single(menu.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, Texts.T("boardArchive.browse")));
                Click(TopLevel.GetTopLevel(browse)!, browse);
                await BooruTestSupport.WaitAsync(() => window.OwnedWindows.OfType<BoardArchiveWindow>().Any(), "board archive from the board menu");
                BoardArchiveWindow fromMenu = Assert.Single(window.OwnedWindows.OfType<BoardArchiveWindow>());
                await WaitForArchive(fromMenu);
                Assert.Equal("#123", Control<ListBox>(fromMenu, "BoardArchiveThreads").Items[0]!.ToString());
                fromMenu.Close();
                await BooruTestSupport.WaitAsync(() => add.IsEffectivelyEnabled, "Downloads after leaving the archive");
                Layout(window);
                Assert.Same(boards, window.FocusManager!.GetFocusedElement());

                TextBox input = window.FindControl<TextBox>("UrlInput")!;
                await window.Clipboard!.SetTextAsync("https://boards.4chan.org/G/archive/");
                Assert.True(input.Focus());
                window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, null);
                await BooruTestSupport.WaitAsync(() => input.Text == "https://boards.4chan.org/G/archive/", "pasted archive URL");
                Click(window, add);
                await BooruTestSupport.WaitAsync(() => window.OwnedWindows.OfType<BoardArchiveWindow>().Any(), "board archive from Paste");
                BoardArchiveWindow fromPaste = Assert.Single(window.OwnedWindows.OfType<BoardArchiveWindow>());
                await WaitForArchive(fromPaste);
                Assert.Equal("#123", Control<ListBox>(fromPaste, "BoardArchiveThreads").Items[0]!.ToString());
                fromPaste.Close();
                Assert.Empty(session.GetView().Threads);
                Assert.Single(session.GetView().Boards);
                Assert.Equal(new DateTimeOffset?[] { null, BoardArchiveHostTests.Modified },
                    transport.Requests.Where(call => call.Path == "/g/archive.json").Select(call => call.Modified));
            }
        });
    }

    private static Task WaitForArchive(BoardArchiveWindow dialog) => BooruTestSupport.WaitAsync(() =>
    {
        Layout(dialog);
        return Control<ListBox>(dialog, "BoardArchiveThreads").ItemCount == 2;
    }, "two archive IDs in the real dialog");

    private static T Control<T>(Visual root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static void Layout(TopLevel window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(TopLevel window, Control control)
    {
        window.UpdateLayout();
        Point center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Layout(window);
    }
}
