using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

internal static class RateAndTagWindowFixture
{
    internal static ArchivedThreadView View(ArchiveAttachmentMatchingTests.Fixture media)
    {
        CapturedPost[] posts =
        [
            new("43", 0, PostFieldPresence.All, Html: "Saved picture", Attachments: [media.Attachment]),
            new("44", 1, PostFieldPresence.All, Html: "Text only")
        ];
        return new(new(media.Binding!.Reference.Resource, "https://offline.invalid/thread/42", "Rate and tag fixture",
            media.Directory, ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, posts.Length, 1),
            posts, [media.Directory], decimalPostOrder: true);
    }

    internal static async Task SeedAsync(string profile, ArchivedThreadView view)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new(view.Summary.Id, view.Summary.Url, view.Summary.Title,
            ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, view.Summary.MediaDirectory, view.GetPosts()));
    }

    internal static void RunMediaPage(Func<MainWindow, MediaPage, Task> test) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruComponentDispatcher.Wait(Exercise());

            async Task Exercise()
            {
                using TempDirectory profile = new();
                using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false);
                new Config
                {
                    DownloadPath = Path.Combine(profile.Path, "downloads"), FirstRunSetupVersion = 1, DownloadsPaused = true,
                    WorkspaceMode = "Advanced", Language = "en"
                }.SaveConfig(profile.Path);
                await SeedAsync(profile.Path, View(media));

                await using DesktopSessionHost session = new(profile.Path, Array.Empty<IImageBoard>(),
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await using MainWindow window = new(session) { Width = 1440, Height = 900, Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                window.FindControl<Button>("MediaNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(MainWindow.WorkspacePage.Media, window.SelectedPage);
                MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
                await page.BuildTask;
                await BooruTestSupport.WaitAsync(() =>
                {
                    window.UpdateLayout();
                    return page.Grid.ItemCount == 1 && page.Grid.ContainerFromIndex(0) != null;
                }, "the Media page to show the saved picture");
                page.Grid.SelectedIndex = 0;
                try { await test(window, page); }
                finally { QuickTagPopover.Current?.Close(); }
            }
        });

    internal static async Task<QuickTagPopover> OpenEditorAsync(Window window)
    {
        PressR(window);
        await BooruTestSupport.WaitAsync(() =>
            QuickTagPopover.Current is { IsOpen: true, Surface.Input.IsKeyboardFocusWithin: true }, "the focused tag input");
        return QuickTagPopover.Current!;
    }

    internal static void PressR(Window window)
    {
        window.KeyPress(Key.R, RawInputModifiers.None, PhysicalKey.R, "r");
        window.KeyRelease(Key.R, RawInputModifiers.None, PhysicalKey.R, "r");
    }

    internal static void Escape(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
    }
}
