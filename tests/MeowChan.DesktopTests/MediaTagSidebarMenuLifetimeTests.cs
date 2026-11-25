using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class MediaTagSidebarMenuLifetimeTests
{
    [Fact]
    public void DetachingTheTagListClosesItsMenuAndReattachingInstallsOneHandler() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        List<string> copied = [];
        MediaTagSidebar sidebar = new() { CopyName = copied.Add };
        sidebar.SetCounts(new MediaTagCountResult([], [new(MediaTagGroup.Source, "cat", "cat", 1)], 1, 1, true, true), false);
        Window window = new() { Width = 400, Height = 560, Content = sidebar };
        try
        {
            window.Show();
            ContextGestures.Settle(window);
            ListBoxItem Tag() => Assert.Single(sidebar.List.GetVisualDescendants().OfType<ListBoxItem>(),
                item => item.DataContext is MediaTagListItem { Kind: MediaTagListKind.Tag });
            ContextGestures.MenuKey(Tag());
            ContextMenu retired = Assert.IsType<ContextMenu>(sidebar.List.ContextMenu);

            window.Content = null;
            ContextGestures.Settle(window);
            Assert.False(retired.IsOpen);
            Assert.Null(sidebar.List.ContextMenu);

            window.Content = sidebar;
            ContextGestures.Settle(window);
            int openings = 0;
            sidebar.List.PropertyChanged += (_, e) =>
            {
                if (e.Property == Control.ContextMenuProperty && sidebar.List.ContextMenu != null) openings++;
            };
            ContextGestures.MenuKey(Tag());
            Assert.Equal(1, openings);
            Assert.Single(MenuProbe.Entries(Assert.IsType<ContextMenu>(sidebar.List.ContextMenu)),
                entry => entry.Id == "tags.copy-name").Activate();
            Assert.Equal("cat", Assert.Single(copied));
        }
        finally { window.Close(); }
    });
}
