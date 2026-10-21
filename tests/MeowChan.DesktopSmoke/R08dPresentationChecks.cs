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
                fixture.IsAvailable = true;
                ListBox threads = fixture.FindControl<ListBox>("ArchiveThreads")!;
                await Program.WaitUntilAsync(() => !fixture.IsBusy && threads.ItemCount == 1);
                Button list = fixture.FindControl<Button>("ArchiveLayoutList")!;
                Button tiles = fixture.FindControl<Button>("ArchiveLayoutTiles")!;
                Button table = fixture.FindControl<Button>("ArchiveLayoutTable")!;
                Button favorites = fixture.FindControl<Button>("ArchiveFavoritesOverview")!;
                Require(threads.IsEffectivelyVisible && tiles.IsEffectivelyVisible && table.IsEffectivelyVisible
                    && favorites.IsEffectivelyVisible && favorites.Bounds.Width > 0,
                    "Archive layout choices or favorites entry were absent from the real page.");
                Program.CaptureWindow(window, "smoke-r08d-archive-list.png");
                Click(tiles);
                await Program.WaitUntilAsync(() => fixture.FindControl<ListBox>("ArchiveThreadTiles")!.IsEffectivelyVisible);
                Program.CaptureWindow(window, "smoke-r08d-archive-tiles.png");
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
                Program.CaptureWindow(window, "smoke-r08d-reader-fullscreen-action.png");
            }
            finally { fixture.EndLifetime(); host.Content = retained; }
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        }
        finally { Directory.Delete(profile, recursive: true); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
