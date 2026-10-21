using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Legal;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

internal static class R08dPresentationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        await CheckSettingsAndAboutAsync(window);
        await CheckArchivePresentationAsync(window);
    }

    private static async Task CheckSettingsAndAboutAsync(MainWindow window)
    {
        Click(window.FindControl<MenuItem>("SettingsMenuItem")!);
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        ComboBox font = settings.FindControl<ComboBox>("ReadingFontSizeInput")!;
        Require(font.IsEnabled && font.SelectedItem != null && font.ItemCount >= 3,
            "The reading font size setting is unavailable in the real settings page.");
        font.BringIntoView();
        await Program.WaitUntilAsync(() => font.IsEffectivelyVisible && font.Bounds.Width > 0);
        Program.CaptureWindow(window, "smoke-r08d-settings-font.png");

        Button aboutButton = settings.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content?.ToString() == Texts.T("r08d.settings.about"));
        aboutButton.BringIntoView();
        await Program.WaitUntilAsync(() => aboutButton.IsEffectivelyVisible && aboutButton.Bounds.Width > 0);
        Click(aboutButton);
        await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(about => about.IsVisible));
        AboutWindow about = window.OwnedWindows.OfType<AboutWindow>().Single();
        try
        {
            TabControl tabs = about.FindControl<TabControl>("AboutTabs")!;
            Require(tabs.ItemCount == 3 && about.FindControl<SelectableTextBlock>("VersionText")!.Text?.Contains("Version") == true,
                "About did not show three tabs and a readable version.");
            Program.CaptureWindow(about, "smoke-r08d-about-overview.png");
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => about.FindControl<TextBlock>("VideoCreditsText")!.IsEffectivelyVisible);
            Require(about.FindControl<TextBlock>("IconsText")!.Text?.Contains("Cole Bemis") == true,
                "Credits omitted the named Feather author.");
            Program.CaptureWindow(about, "smoke-r08d-about-credits.png");

            tabs.SelectedIndex = 2;
            TextBox search = about.FindControl<TextBox>("LicenseSearchBox")!;
            await Program.WaitUntilAsync(() => search.IsEffectivelyVisible);
            search.Text = "Cole Bemis";
            ListBox list = about.FindControl<ListBox>("LicenseList")!;
            await Program.WaitUntilAsync(() => list.Items.OfType<OfflineLegalCatalog.Entry>()
                .Any(entry => entry.Name == "Feather Icons"));
            list.SelectedItem = list.Items.OfType<OfflineLegalCatalog.Entry>().Single(entry => entry.Name == "Feather Icons");
            Require(about.FindControl<TextBox>("LicenseFullText")!.Text?.Contains("THE SOFTWARE IS PROVIDED") == true
                && about.FindControl<SelectableTextBlock>("LicenseMetadataText")!.Text?.Contains("v4.29.2") == true,
                "Offline Feather search did not expose its complete local MIT text and revision.");
            Program.CaptureWindow(about, "smoke-r08d-about-licenses.png");
        }
        finally { Click(about.FindControl<Button>("CloseButton")!); }
        await Program.WaitUntilAsync(() => !about.IsVisible);
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
    }

    private static async Task CheckArchivePresentationAsync(MainWindow window)
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-r08d-presentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ResourceId id = new("4chan", "g", "808");
        double originalWidth = window.Width;
        double originalHeight = window.Height;
        WindowState originalState = window.WindowState;
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                    "https://boards.4chan.org/g/thread/808", "Offline presentation fixture",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                    new[] { new CapturedPost("808", 0, PostFieldPresence.All, Html: "<p>Offline reader fixture</p>") }));

            Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
            ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
            await Program.WaitUntilAsync(() => host.IsEffectivelyVisible);
            object? retained = host.Content;
            ArchivePage fixture = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            host.Content = fixture;
            try
            {
                window.WindowState = WindowState.Normal;
                await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal);
                Size desired = new(1360, 820);
                Size client = window.Bounds.Size;
                Size frame = window.FrameSize ?? client;
                Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                    ?? throw new InvalidOperationException("The archive fixture has no native screen.");
                // Hosted macOS desktops can be shorter than the intended wide
                // viewport. Request a real client size that fits this desktop,
                // reserving measured native frame insets; keep the size assertion
                // exact rather than accepting an OS-clamped 820-DIP request.
                WindowStartupFit wide = WindowStartupGeometry.Fit(desired,
                    new Size(desired.Width + Math.Max(0, frame.Width - client.Width),
                        desired.Height + Math.Max(0, frame.Height - client.Height)),
                    new Size(window.MinWidth, window.MinHeight), screen.WorkingArea,
                    window.RenderScaling, window.Position);
                window.Width = wide.Client.Width;
                window.Height = wide.Client.Height;
                window.Position = wide.Position;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window,
                    wide.Client.Width, wide.Client.Height));
                fixture.IsAvailable = true;
                await fixture.ActivateAsync();
                ListBox threads = fixture.FindControl<ListBox>("ArchiveThreads")!;
                await Program.WaitUntilAsync(() => !fixture.IsBusy && threads.ItemCount == 1);
                Button list = fixture.FindControl<Button>("ArchiveLayoutList")!;
                Button table = fixture.FindControl<Button>("ArchiveLayoutTable")!;
                Button favorites = fixture.FindControl<Button>("ArchiveFavoritesOverview")!;
                Require(threads.IsEffectivelyVisible && list.IsEffectivelyVisible && table.IsEffectivelyVisible
                    && favorites.IsEffectivelyVisible && favorites.Bounds.Width > 0,
                    "Archive layout choices or favorites entry were absent from the real page.");
                Program.CaptureWindow(window, "smoke-r08d-archive-list.png");
                Click(table);
                await Program.WaitUntilAsync(() => fixture.FindControl<DataGrid>("ArchiveThreadTable")!.IsEffectivelyVisible);
                Program.CaptureWindow(window, "smoke-r08d-archive-table.png");
                Click(list);
                await Program.WaitUntilAsync(() => threads.IsEffectivelyVisible);
                threads.SelectedIndex = 0;
                Button read = fixture.FindControl<Button>("ArchiveReadPosts")!;
                await Program.WaitUntilAsync(() => read.IsEffectivelyEnabled);
                Click(read);
                await Program.WaitUntilAsync(() => fixture.CurrentThread?.Summary.Id == id
                    && fixture.FindControl<Button>("ArchiveReaderFullscreen")!.IsEffectivelyVisible);
                Button full = fixture.FindControl<Button>("ArchiveReaderFullscreen")!;
                Require(full.IsEnabled && full.Bounds.Width > 0 && full.Content?.ToString()?.Length > 0,
                    "The reader's visible OS full-screen action is missing.");
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window,
                    wide.Client.Width, wide.Client.Height));
                Button back = fixture.FindControl<Button>("ArchiveBackButton")!;
                Require(FitsWindow(window, full) && (!back.IsVisible || FitsWindow(window, back)),
                    "The reader's native full-screen or back action overflows the fitted wide viewport.");
                Console.WriteLine($"SMOKE archive native viewport: requested={wide.Client}; actual={window.Bounds.Size}; "
                    + $"workingArea={screen.WorkingArea}; scale={window.RenderScaling}; "
                    + $"readerLayout={(fixture.FindControl<GridSplitter>("ArchiveSplitter")!.IsEffectivelyVisible ? "split" : "narrow on this desktop")}.");
                Program.CaptureWindow(window, "smoke-r08d-reader-wide.png");

                window.Width = 760;
                window.Height = 500;
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return PresentationChecks.MatchesRequestedSize(window, 760, 500)
                        && FitsWindow(window, full) && FitsWindow(window, back);
                });
                Program.CaptureWindow(window, "smoke-r08d-reader-narrow.png");
                Click(fixture.FindControl<Button>("ArchiveBackButton")!);
                await Program.WaitUntilAsync(() => threads.IsEffectivelyVisible);
                Program.CaptureWindow(window, "smoke-r08d-archive-narrow-list.png");
            }
            finally
            {
                fixture.EndLifetime(); host.Content = retained;
                window.Width = originalWidth;
                window.Height = originalHeight;
                window.WindowState = originalState;
            }
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static bool FitsWindow(Window window, Control control)
    {
        Point? origin = control.TranslatePoint(default, window);
        return control.IsEffectivelyVisible && origin.HasValue && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && origin.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
