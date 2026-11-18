using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan;

namespace MeowChan.DesktopTests;

public sealed class ReaderGalleryOriginRoutedEventTests
{
    [Fact]
    public void SpaceRoutedFromPostsListResolvesTheSelectedPostContainerAsGalleryOrigin()
    {
        var posts = new ListBox();
        var selectedRow = new ListBoxItem { DataContext = "selected" };
        Control? routedSource = null;
        posts.AddHandler(InputElement.KeyDownEvent, (_, e) => routedSource = e.Source as Control,
            RoutingStrategies.Bubble);

        // This is the actual routed event shape from the failing path: the tunnel/bubble
        // handler sees the posts ListBox as Source, while the selected row is realized.
        posts.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });

        Assert.Same(posts, routedSource);
        Control? origin = ArchivePage.ResolveReaderMediaGalleryOrigin(routedSource!, selectedRow, posts);
        Assert.Same(selectedRow, origin);
    }

    [Fact]
    public void SourceAlreadyInsideSelectedPostRemainsTheFocusReturnOrigin()
    {
        var posts = new ListBox();
        var selectedRow = new ListBoxItem();
        Control source = selectedRow;

        Assert.Same(source, ArchivePage.ResolveReaderMediaGalleryOrigin(source, selectedRow, posts));
    }
}
