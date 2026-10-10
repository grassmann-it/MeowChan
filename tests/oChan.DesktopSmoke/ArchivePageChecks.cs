using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using oChan.Archive;
using oChan.Desktop;
using oChan.Desktop.Localization;
using oChan.Storage;

namespace oChan.DesktopSmoke;

internal static class ArchivePageChecks
{
    internal static async Task RunAsync(MainWindow window, long watchId, ResourceId id)
    {
        // Removing monitoring must leave independently readable posts and saved media intact.
        await window.SessionHost.RemoveThreadAsync(watchId);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 0);
        OpenArchive(window);
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 1);
        ListBox threads = page.FindControl<ListBox>("ArchiveThreads")!;
        threads.SelectedIndex = 0;
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == id);
        Require(page.CurrentThread!.PostCount == 1, "Removing a restored watch lost its captured posts.");
        Require(page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().Single().Body
            == "Offline & readable\n>>124", "The reader did not render persisted source text as plain text.");
        await page.MediaLoadTask;
        DataGrid media = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Require(media.ItemsSource.Cast<ArchiveAssetRow>().Count() == 1, "The independent archive did not discover the saved manifest.");
        await FlushAsync();
        Program.CaptureWindow(window, "smoke-archive-restored-posts.png");

        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        media.SelectedItem = media.ItemsSource.Cast<ArchiveAssetRow>().Single();
        Click(page.FindControl<Button>("PreviewArchiveMediaButton")!);
        await Program.WaitUntilAsync(() => page.FindControl<Image>("ArchivePreviewImage")!.Source is Bitmap);
        Program.CaptureWindow(window, "smoke-archive-verified-preview.png");
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        OpenArchive(window);
        Click(page.FindControl<Button>("ClosePreviewButton")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == id);
        Require(!page.FindControl<Border>("ArchivePreview")!.IsVisible,
            "Reactivating the archive retained a stale preview or blocked its list load.");

        foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                window.WindowState = WindowState.Normal;
                double width = mode == "Barebones" ? 640 : 760;
                double height = mode == "Barebones" ? 480 : 500;
                window.Width = width;
                window.Height = height;
                page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1
                        && Fits(window, page.FindControl<Button>("CapturePostsButton")!)
                        && Fits(window, page.FindControl<Button>("ArchiveBackButton")!)
                        && page.FindControl<ListBox>("ArchivePosts")!.Bounds.Height >= 100
                        && Fits(window, window.FindControl<Button>("SettingsNavigationButton")!)
                        && Fits(window, window.FindControl<ComboBox>("WorkspaceModeComboBox")!);
                });
                Program.CaptureWindow(window, $"smoke-archive-{mode.ToLowerInvariant()}-{language}-minimum.png");
            }
        }
        Texts.SetLanguage("en");
        Click(window.FindControl<Button>("ThemeToggleButton")!);
        await Program.WaitUntilAsync(() => Application.Current?.RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            && window.FindControl<Button>("ThemeToggleButton")!.IsEnabled);
        Program.CaptureWindow(window, "smoke-archive-dark-compact.png");
        await CheckSlowMediaAsync(window, id);
        await ArchiveNavigationChecks.RunAsync(window);
        Console.WriteLine("SMOKE archive: real post capture, restart, watch removal, plain offline reader, verified image preview, independent media loading and compact EN/DE pages passed.");
    }

    private static async Task CheckSlowMediaAsync(MainWindow window, ResourceId id)
    {
        ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
        object? retained = host.Content;
        TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchivePage fixture = new ArchivePage(window.SessionHost.OpenArchiveReaderAsync,
            window.SessionHost.SetCaptureEnabledAsync, _ => { }, async (directory, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return await MediaAssetReader.OpenAsync(directory, cancellationToken);
            });
        host.Content = fixture;
        try
        {
            fixture.IsAvailable = true;
            await fixture.LoadThreadAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
            Require(fixture.CurrentThread?.PostCount == 1 && !fixture.IsBusy && fixture.IsMediaLoading
                && !fixture.MediaLoadTask.IsCompleted,
                "A delayed media directory blocked local posts or left local navigation busy.");
            await FlushAsync();
            Program.CaptureWindow(window, "smoke-archive-slow-media.png");
            gate.SetResult();
            await fixture.MediaLoadTask.WaitAsync(TimeSpan.FromSeconds(10));
            Require(fixture.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource.Cast<ArchiveAssetRow>().Count() == 1,
                "The delayed media snapshot did not populate after the local reader.");
        }
        finally
        {
            fixture.EndLifetime();
            gate.TrySetResult();
            host.Content = retained;
        }
    }

    private static void OpenArchive(MainWindow window) => window.FindControl<MenuItem>("ArchiveMenuItem")!
        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && point.HasValue
            && point.Value.X >= -1 && point.Value.Y >= -1 && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
