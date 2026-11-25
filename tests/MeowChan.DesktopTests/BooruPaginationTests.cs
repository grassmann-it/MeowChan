using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruPaginationTests
{
    [Theory]
    [InlineData(640, 640)]
    [InlineData(960, 900)]
    [InlineData(1920, 1920)]
    public void ResultsScrollIndependentlyAndThePagerOperatesOnTheExecutedSearch(int width, int height)
        => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        harness.Serve(pages: 9, perPage: 20);
        using BooruPage page = new(harness.Services);
        page.Model.Search.ReadPagerLimits = () => new(12, 20);
        Window window = new() { Content = page, Width = width, Height = height };
        try
        {
            window.Show();
            page.View.SetResultView(BooruResultView.List, save: false);
            page.ShowQuery("cat");
            BooruComponentDispatcher.Wait(page.Model.Search.SubmitAsync());
            Layout();

            Button previous = Find<Button>(BooruWorkbenchView.PreviousName);
            Button next = Find<Button>(BooruWorkbenchView.NextName);
            Button jump = Find<Button>("BooruPageJump");
            TextBox number = Find<TextBox>("BooruPageNumber");
            TextBlock label = Find<TextBlock>("BooruPageInfo");
            ScrollViewer hits = Find<ScrollViewer>("BooruResultScroll");
            Rect footer = Bounds(Find<Control>("BooruResultsFooter"));
            Assert.True(new Rect(window.ClientSize).Contains(footer));
            Assert.True(footer.Top >= Bounds(hits).Bottom - 1);
            foreach (Button button in new[] { previous, next, jump })
            {
                Assert.True(button.IsEffectivelyVisible && footer.Contains(Bounds(button)));
                Assert.False(button.Focusable || button.IsTabStop);
            }
            Assert.Equal(0, hits.Offset.Y);
            Assert.True(hits.ScrollBarMaximum.Y > 0);
            hits.Offset = new Vector(0, hits.ScrollBarMaximum.Y);
            Layout();
            Assert.True(hits.Offset.Y > 0);
            Assert.Equal(footer, Bounds(Find<Control>("BooruResultsFooter")));
            Assert.False(previous.IsEnabled);
            Assert.True(next.IsEnabled);
            Assert.Equal(Texts.T("booru.ui.page.current", "1+", 20), label.Text);

            TextBox input = Find<TextBox>(BooruWorkbenchView.InputName);
            input.Text = "dog";
            number.Text = "13";
            Layout();
            Assert.False(jump.IsEnabled);
            number.Text = "9";
            Layout();
            Assert.True(jump.IsEnabled);
            jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Assert.Equal(("cat", 9), harness.Search.Searches.Last());
            Assert.Equal(Texts.T("booru.ui.page.info", 9, 9, 20), label.Text);
            Assert.False(next.IsEnabled);
            var back = new KeyEventArgs { Key = Key.PageUp, KeyModifiers = KeyModifiers.Alt, RoutedEvent = InputElement.KeyDownEvent };
            input.RaiseEvent(back);
            Layout();
            Assert.True(back.Handled);
            Assert.Equal(Texts.T("booru.ui.page.info", 8, 9, 20), label.Text);
            number.Text = "2";
            number.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent });
            Layout();
            Assert.Equal(new[] { ("cat", 1), ("cat", 9), ("cat", 8), ("cat", 2) }, harness.Search.Searches);

            T Find<T>(string id) where T : Control => page.GetVisualDescendants().OfType<T>()
                .Single(control => AutomationProperties.GetAutomationId(control) == id);
            Rect Bounds(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(ShortcutPlatform.MacOS, Key.Left, Key.Right, KeyModifiers.Meta | KeyModifiers.Alt)]
    [InlineData(ShortcutPlatform.Windows, Key.PageUp, Key.PageDown, KeyModifiers.Alt)]
    [InlineData(ShortcutPlatform.Linux, Key.PageUp, Key.PageDown, KeyModifiers.Alt)]
    public void PageCommandsResolveInTheBooruContext(ShortcutPlatform platform, Key back, Key next, KeyModifiers modifiers)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = back, KeyModifiers = modifiers }, "pagination.previous", "booru"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = next, KeyModifiers = modifiers }, "pagination.next", "booru"));
    }
}
