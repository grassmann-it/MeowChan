using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

internal static class ReaderInteractionChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        await CheckNativeMediaKeysAsync(page);
        await CheckFavoriteFailureAsync(page);
        await CheckHistoryAsync(page);
        await CheckFavoritesOverviewAsync(window, page);
        Console.WriteLine("SMOKE reader interactions: native focus, exact media Enter, persistent favorite failure, exact bounded quote return and favorite removal/view passed.");
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
        ArchiveAttachmentView inline = page.FindControl<Grid>("ArchiveReaderPane")!.GetVisualDescendants().OfType<ArchiveAttachmentView>().First();
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
        ArchiveAssetRow row = page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>().First();
        ResourceId thread = page.CurrentThread!.Summary.Id;
        GalleryEntry entry = new(new GalleryAssetKey(thread, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, false, true);
        await window.SessionHost.SetFavoriteAsync(entry, true);
        Task overview = page.OpenFavoritesOverviewAsync();
        await Program.WaitUntilAsync(() => window.OwnedWindows.Any(dialog => dialog.Name == "FavoritesOverviewDialog"));
        Window dialog = window.OwnedWindows.Single(dialog => dialog.Name == "FavoritesOverviewDialog");
        ListBox list = dialog.FindControl<ListBox>("FavoritesList")!;
        await Program.WaitUntilAsync(() => list.ItemCount == 1 && dialog.FindControl<Button>("FavoritesExportOriginals")!.IsEnabled);
        dialog.FindControl<TextBox>("FavoritesSearch")!.Text = "reader.png";
        list.SelectedIndex = 0;
        Click(dialog.FindControl<Button>("FavoritesView")!);
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("GalleryHost")!.IsVisible);
        GalleryViewer viewer = (GalleryViewer)window.FindControl<ContentControl>("GalleryHost")!.Content!;
        Require(viewer.CaptureState().Key == entry.Key, "Favorite View opened a different identity.");
        Click(viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose"));
        await overview;
        await Program.WaitUntilAsync(() => window.OwnedWindows.Any(candidate => candidate.Name == "FavoritesOverviewDialog"));
        dialog = window.OwnedWindows.Single(candidate => candidate.Name == "FavoritesOverviewDialog");
        list = dialog.FindControl<ListBox>("FavoritesList")!;
        await Program.WaitUntilAsync(() => dialog.FindControl<Button>("FavoritesRemove")!.IsEnabled);
        Require(dialog.FindControl<TextBox>("FavoritesSearch")!.Text == "reader.png" && list.SelectedItem != null, "Favorite return lost search or exact selection.");
        Click(dialog.FindControl<Button>("FavoritesRemove")!);
        await Program.WaitUntilAsync(() => list.ItemCount == 0);
        Require(File.Exists(row.Asset.Path) && !window.SessionHost.IsFavorite(entry), "Removing a favorite deleted the original or kept the mark.");
        dialog.Close();
        await FlushAsync();
    }

    private static (string Id, double Top) Top(ListBox posts) => posts.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is ArchivePostRow)
        .Select(item => (Id: ((ArchivePostRow)item.DataContext!).Id, Top: item.TranslatePoint(default, posts)!.Value.Y, Height: item.Bounds.Height))
        .Where(item => item.Top + item.Height > 0).OrderBy(item => item.Top).Select(item => (item.Id, item.Top)).First();
    private static KeyEventArgs Press(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        control.Focus();
        KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        control.RaiseEvent(args); return args;
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task FlushAsync() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Task.Delay(20); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
