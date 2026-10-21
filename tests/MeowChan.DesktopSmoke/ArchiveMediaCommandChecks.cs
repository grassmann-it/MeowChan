using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
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
        try
        {
            page.IsArchiveFavorite = _ => marked;
            page.ToggleArchiveFavoriteAsync = _ => { marked = !marked; return Task.CompletedTask; };
            page.OpenMediaGallery = (_, key, _) => { opened = key; return Task.CompletedTask; };
            inline.RefreshFavorite();
            Button star = inline.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ArchiveMediaFavorite");
            Require(star.IsEnabled, "Saved inline media has no enabled favorite control.");
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(marked && star is Avalonia.Controls.Primitives.ToggleButton { IsChecked: true } && AutomationProperties.GetName(star) == Texts.T("viewer.favorite.remove"), "Inline favorite did not update its accessible state.");
            inline.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = inline });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            ContextMenu menu = page.ArchiveMediaMenu!;
            Require(menu.IsOpen, "Inline keyboard context request did not open a media menu.");
            MenuItem[] commands = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
            Require(commands.Select(item => (string)item.Tag!).SequenceEqual(new[]
            { "gallery.open", "favorites.toggle", "media.export-original", "media.show-in-folder", "gallery.show-in-thread", "media.details" }),
                "Inline media menu lacks the shared media actions or order.");
            commands.Single(item => (string)item.Tag! == "gallery.open").Command!.Execute(null);
            Require(opened?.PostId == page.CurrentThread!.GetPosts(0, 1).Single().PostId,
                "Inline context gallery lost the clicked attachment's post association.");
            commands.Single(item => (string)item.Tag! == "favorites.toggle").Command!.Execute(null);
            Require(!marked, "Inline context favorite did not use the same favorite action as its visible star.");
            menu.Close();
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
