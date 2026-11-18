using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class MediaPageRoutedShortcutRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RatingReachesThePopoverBeforeGridBubbleConsumption(bool tile) => MediaTileEnterTests.Run(f =>
    {
        f.Grid.SelectedIndex = 1;
        List<MediaLibraryEntry> opened = new();
        f.Page.TagPopoverAsync = (entry, _, _, _, _) => { opened.Add(entry); return Task.CompletedTask; };
        // Model a consuming ListBox bubble handler as well as exercising its real class route.
        f.Grid.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        f.Press(tile ? f.Container(1) : f.Grid, Key.R);
        Assert.True(f.LastHandled);
        Assert.Same(f.Entries[1], Assert.Single(opened));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelRatingAndTagsIgnoreAnUnrelatedMultipleSelection(bool tags) => MediaTileEnterTests.Run(f =>
    {
        f.Grid.Selection.Select(0);
        f.Grid.Selection.Select(2);
        Control open = f.ShowPanelOn(1);
        List<MediaLibraryEntry> opened = new();
        f.Page.TagPopoverAsync = (entry, _, _, _, _) => { opened.Add(entry); return Task.CompletedTask; };
        KeyGesture gesture = ShortcutGesture.Parse(tags ? "Primary+Shift+T" : "R")
            .Resolve(EffectiveShortcutBindings.CurrentPlatform);
        f.Press(open, gesture.Key, gesture.KeyModifiers);
        Assert.True(f.LastHandled);
        Assert.Same(f.Entries[1], Assert.Single(opened));
        Assert.Equal(2, f.Grid.Selection.Count);
    });

    [Fact]
    public void RatingFromVideoControlsUsesTheDisplayedMedium() => MediaTileEnterTests.Run(f =>
    {
        f.Grid.SelectedIndex = 0;
        f.ShowPanelOn(1);
        Button videoControl = new();
        f.PanelView!.VideoRow.Children.Add(videoControl);
        List<MediaLibraryEntry> opened = new();
        f.Page.TagPopoverAsync = (entry, _, _, _, _) => { opened.Add(entry); return Task.CompletedTask; };
        f.Press(videoControl, Key.R);
        Assert.True(f.LastHandled);
        Assert.Same(f.Entries[1], Assert.Single(opened));
        Assert.Equal(0, f.Grid.SelectedIndex);
    });

    [Fact]
    public void SidebarSiblingCommandUsesTheSelection() => MediaTileEnterTests.Run(f =>
    {
        f.Grid.SelectedIndex = 1;
        f.Page.ApplyTemplate();
        foreach (ContentPresenter presenter in f.Page.GetVisualDescendants().OfType<ContentPresenter>().ToArray())
            presenter.UpdateChild();
        ListBox sidebar = new();
        Assert.IsType<Grid>(f.Page.Content).Children.Add(sidebar);
        sidebar.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        List<MediaLibraryEntry> actions = new();
        f.Page.ExecuteMediaMenuActionAsync = (_, entry, _) => { actions.Add(entry); return Task.CompletedTask; };
        f.Press(sidebar, Key.I);
        Assert.True(f.LastHandled);
        Assert.Same(f.Entries[1], Assert.Single(actions));
    });

    [Fact]
    public void GridDetailsUsesTheSelectionAndCaptureSuppressesIt() => MediaTileEnterTests.Run(f =>
    {
        f.Grid.SelectedIndex = 1;
        List<MediaLibraryEntry> actions = new();
        f.Page.ExecuteMediaMenuActionAsync = (_, entry, _) => { actions.Add(entry); return Task.CompletedTask; };
        using (ShortcutService.BeginCapture()) f.Press(f.Container(1), Key.I);
        Assert.Empty(actions);
        f.Press(f.Container(1), Key.I);
        Assert.Same(f.Entries[1], Assert.Single(actions));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelNativeEditorsKeepRatingAndTaggingKeys(bool combo) => MediaTileEnterTests.Run(f =>
    {
        f.ShowPanelOn(1);
        Control editor = combo ? new ComboBox() : new TextBox();
        f.PanelView!.VideoRow.Children.Add(editor);
        int calls = 0;
        f.Page.TagPopoverAsync = (_, _, _, _, _) => { calls++; return Task.CompletedTask; };
        f.Press(editor, Key.R);
        Assert.Equal(0, calls);
    });
}
