using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveMediaCommandChecks
{
    internal static async Task RunAsync(ArchivePage page, ArchiveAttachmentView inline)
    {
        var oldMarked = page.IsArchiveFavorite;
        var oldToggle = page.ToggleArchiveFavoriteAsync;
        var oldGallery = page.OpenMediaGallery;
        bool marked = false;
        GalleryEntryKey? opened = null;
        IReadOnlyList<GalleryEntry>? openedEntries = null;
        try
        {
            page.IsArchiveFavorite = _ => marked;
            page.ToggleArchiveFavoriteAsync = _ => { marked = !marked; return Task.CompletedTask; };
            page.OpenMediaGallery = (entries, key, _) => { openedEntries = entries; opened = key; return Task.CompletedTask; };
            inline.RefreshFavorite();
            Button star = inline.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ArchiveMediaFavorite");
            Require(star.IsEnabled, "Saved inline media has no enabled favorite control.");
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(marked && star is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true } && AutomationProperties.GetName(star) == Texts.T("viewer.favorite.remove"), "Inline favorite did not update its accessible state.");
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            await Program.WaitUntilAsync(() => posts.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .Any(view => view.DataContext is ArchiveAttachmentRow));
            ArchiveAttachmentView current = posts.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .First(view => view.DataContext is ArchiveAttachmentRow);
            Require(current.DataContext is ArchiveAttachmentRow, "Current inline media has no attachment context.");
            Grid readerPane = page.FindControl<Grid>("ArchiveReaderPane")!;
            Require(current.GetVisualAncestors().Contains(readerPane), "Current inline media is not in the reader pane.");
            // Native context input originates on the attachment. The detached reader pane is
            // now in that route; raising at the pane itself would replace Source with Grid.
            current.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            ContextMenu menu = page.ArchiveMediaMenu!;
            Require(menu.IsOpen, "Inline keyboard context request did not open a media menu.");
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Console.WriteLine("SMOKE archive media menu: " + JsonSerializer.Serialize(items
                .Select(item => new { item.Tag, header = item.Header?.ToString(), item.IsEnabled,
                    canExecute = item.Command?.CanExecute(item.CommandParameter) })));
            Require(page.EditPostBookmark != null && items.Length == 8 && items[0].Tag == null
                && Equals(items[0].Header, CommandCatalog.Label("post-bookmarks.edit-post")),
                "The real reader media menu lost its leading post-bookmark action or contains unexpected actions.");
            MenuItem bookmark = items[0];
            Require(bookmark.IsEnabled && bookmark.Command?.CanExecute(bookmark.CommandParameter) == true,
                "The captured inline post has no available shared bookmark action.");
            // Check every remaining item; do not hide the untagged bookmark or any unexpected action.
            MenuItem[] commands = items.Skip(1).ToArray();
            string[] actualTags = commands.Select(item => item.Tag as string ?? "<untagged>").ToArray();
            Require(actualTags.SequenceEqual(new[]
            { "gallery.open", "favorites.toggle", "media.rating.edit", "media.export-original", "media.show-in-folder", "gallery.show-in-thread", "media.details" }),
                "Inline media menu lacks the shared media actions or order: " + string.Join(", ", actualTags));
            MenuItem rating = commands.Single(item => (string)item.Tag! == "media.rating.edit");
            Require(rating.IsEnabled && rating.Command?.CanExecute(rating.CommandParameter) == true,
                "Saved inline media has no available shared rating action.");
            commands.Single(item => (string)item.Tag! == "gallery.open").Command!.Execute(null);
            Require(opened?.PostId == page.CurrentThread!.GetPosts(0, 1).Single().PostId,
                "Inline context gallery lost the clicked attachment's post association.");
            ArchiveAssetRow[] visible = page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>().ToArray();
            Require(openedEntries?.Select(entry => (entry.Manifest.Directory, entry.Asset.AssetId))
                .SequenceEqual(visible.Select(row => (row.Manifest.Directory, row.Asset.AssetId))) == true,
                "Inline context gallery did not use the visible media sequence.");
            commands.Single(item => (string)item.Tag! == "favorites.toggle").Command!.Execute(null);
            Require(!marked, "Inline context favorite did not use the same favorite action as its visible star.");
            menu.Close();
            // An already opened menu retains its visible scope even when the favorites
            // filter changes. A newly opened menu cannot navigate to the hidden row.
            current.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            ContextMenu frozenMenu = page.ArchiveMediaMenu!;
            Button favorites = page.FindControl<Button>("ArchiveFavoritesFilter")!;
            favorites.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>().Any(),
                "Favorites filter did not hide the unmarked archive asset.");
            opened = null; openedEntries = null;
            frozenMenu.Items.OfType<MenuItem>().Single(item => (string?)item.Tag == "gallery.open").Command!.Execute(null);
            Require(openedEntries?.Count == visible.Length && opened?.Asset.AssetId == visible[0].Asset.AssetId,
                "An open media menu lost its frozen visible gallery scope.");
            frozenMenu.Close();
            current.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            MenuItem hiddenGallery = page.ArchiveMediaMenu!.Items.OfType<MenuItem>()
                .Single(item => (string?)item.Tag == "gallery.open");
            Require(!hiddenGallery.IsEnabled, "A hidden inline asset remained navigable in the visible gallery.");
            page.ArchiveMediaMenu.Close();
            favorites.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            page.ArchiveMediaMenu?.Close();
            page.IsArchiveFavorite = oldMarked; page.ToggleArchiveFavoriteAsync = oldToggle; page.OpenMediaGallery = oldGallery;
            page.RefreshArchiveFavoriteControls();
        }
        Console.WriteLine("SMOKE archive media commands: inline star, accessible favorite state, keyboard menu, shared media actions and post association passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
