using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Realized Avalonia containers without a native window, profile, fonts or provider requests.
public sealed class ReaderConversationLayoutTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(16)]
    public void ResizingRetainsRowsAndReadingMarkRoomWhileReclampingTheIndent(int depth)
    {
        ArchivePostRow[] rows =
        [
            new("700", "", "", []) { UnreadCount = 3 },
            new("701", "", "", []) { Depth = depth, ParentId = "700", UnreadCount = 2 },
            new("702", "", "", []) { Depth = depth, ParentId = "701" }
        ];
        ListBox posts = new()
        {
            ItemsSource = rows,
            Template = new FuncControlTemplate<ListBox>((owner, scope) =>
            {
                ItemsPresenter presenter = new() { Name = "PART_ItemsPresenter", ItemsPanel = owner.ItemsPanel };
                scope.Register("PART_ItemsPresenter", presenter);
                return presenter;
            }),
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
            ItemTemplate = new FuncDataTemplate<ArchivePostRow>((_, _) => new Border { Height = 24 })
        };
        ArchivePage.AttachConversationLayout(posts);
        posts.SelectedItem = rows[1];
        posts.ApplyTemplate();
        Control[]? original = null;
        foreach (double width in new[] { 1440d, 900d, 400d, 1440d })
        {
            posts.Measure(new Size(width, 800));
            posts.Arrange(new Rect(0, 0, width, 800));
            Control[] containers = posts.GetRealizedContainers().ToArray();
            Assert.Equal(rows.Length, containers.Length);
            original ??= containers;
            Assert.Same(rows, posts.ItemsSource);
            Assert.Same(rows[1], posts.SelectedItem);
            for (int index = 0; index < containers.Length; index++)
            {
                Assert.Same(original[index], containers[index]);
                Assert.Same(rows[index], posts.ItemFromContainer(containers[index]));
                double lineRoom = rows[index].HasUnreadLine ? ArchivePage.ReaderUnreadLineRoom : 0;
                Assert.Equal(ArchivePage.ReaderPostIndentMargin(rows[index].Depth, width, lineRoom), containers[index].Margin);
                Assert.InRange(containers[index].Margin.Left - 8, 0, width * ReaderNavigationLayout.ConversationIndentMaximumShare);
            }
        }
    }
}
