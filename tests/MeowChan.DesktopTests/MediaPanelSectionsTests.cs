using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaPanelSectionsTests
{
    [Theory]
    [InlineData(240)]
    [InlineData(280)]
    [InlineData(360)]
    [InlineData(480)]
    public void SectionsHaveOneSeparatorAndActionsFillTheColumn(double width) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Button show = new() { Content = "Show in thread" };
        Button edit = new() { Content = "Edit tags" };
        PanelActionStack.Present(show, "file-text");
        PanelActionStack.Present(edit, "edit-2");
        PanelActionStack actions = new("Actions") { Children = { show, edit } };
        Control rating = new Border { Height = 72 };
        Control tags = new Border { Height = 40 };
        Control info = new Border { Height = 80 };
        MediaPanelSections sections = new() { Width = width - 32, Rating = rating, Tags = tags, Info = info, Actions = actions };
        Window window = new() { Width = 520, Height = 500, Content = sections };
        try
        {
            window.Show();
            window.UpdateLayout();
            Rect BoundsIn(Control control) => new(control.TranslatePoint(default, sections)!.Value, control.Bounds.Size);
            Assert.True(BoundsIn(rating).Bottom < BoundsIn(tags).Top);
            Assert.True(BoundsIn(tags).Bottom < BoundsIn(info).Top);
            Assert.True(BoundsIn(info).Bottom < BoundsIn(actions).Top);
            Assert.Equal(3, sections.Children.OfType<Border>().Count(divider => divider.IsVisible));
            Assert.Equal(sections.Bounds.Width, show.Bounds.Width, 0.5);
            Assert.Equal(sections.Bounds.Width, edit.Bounds.Width, 0.5);
            Assert.Equal(WorkspaceMetrics.InputHeight, show.Bounds.Height, 0.5);
            Assert.Equal(show.Bounds.Bottom + 8, edit.Bounds.Top, 0.5);
            sections.Tags = null;
            window.UpdateLayout();
            Assert.Equal(2, sections.Children.OfType<Border>().Count(divider => divider.IsVisible));
        }
        finally { window.Close(); }
    });
}
