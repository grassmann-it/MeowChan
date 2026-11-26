using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

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
        // Exercise full result rows without scheduling image decodes and card rebuilds for each preview.
        harness.Search.OnSearch = (query, number, _) =>
        {
            BooruPostView[] posts = Enumerable.Range(1, number == 9 ? 1 : 20)
                .Select(index => BooruUiFixtures.Post((100 * number + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    tags: [], preview: false)).ToArray();
            return Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, number, number == 9, posts)));
        };
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

    [Fact]
    public void TheSelectedSourceBoundsThePagerAndRequestsForTheWholeSearch() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruUiHarness harness = new();
        const string sourceId = "test-source";
        SourceDescription source = new(sourceId, "Fixture", new("https://booru.test/"),
            SourceCapabilities.PostSearch, [], [new("booru.test", HttpHostPurpose.Metadata)], new("1", []))
        {
            BooruProfile = BooruEndpointPolicy.Danbooru with { MaximumPage = 2, MaximumLimit = 10 }
        };
        BooruPostView post = BooruPostView.From(BooruUiFixtures.Post("42", tags: [], preview: false).Post with
        {
            Id = new(sourceId, "42"), PostUrl = new("https://booru.test/posts/42"), Media = []
        });
        FakeBooruSearchPort bounded = new() { ProviderId = sourceId, ProviderName = source.Name };
        bounded.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, isEnd: false, post)));
        BooruWorkbenchServices services = new(bounded, harness.Queue, harness.Watchers, harness.Previews, harness.Clock)
        { Source = source };
        using BooruPage booru = new(harness.Services, [harness.Services, services]);
        Config profile = new() { BooruSearchPages = 5, BooruSearchPostsPerPage = 20 };
        booru.Model.Search.ReadPagerLimits = () => new(profile.BooruSearchPages, profile.BooruSearchPostsPerPage);
        Window window = new() { Content = booru, Width = 960, Height = 720 };
        try
        {
            window.Show();
            ComboBox picker = Find<ComboBox>("BooruSourcePicker");
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(item => ReferenceEquals(item.Tag, bounded));
            booru.ShowQuery("cat");
            BooruComponentDispatcher.Wait(booru.Model.Search.SubmitAsync());
            Layout();

            TextBox number = Find<TextBox>("BooruPageNumber");
            Button jump = Find<Button>("BooruPageJump");
            TextBlock info = Find<TextBlock>("BooruPageInfo");
            Assert.Equal(Texts.T("booru.ui.page.current", "1+", 10), info.Text);
            number.Text = "3";
            Layout();
            Assert.False(jump.IsEnabled);
            number.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent });
            BooruComponentDispatcher.Wait(booru.Model.Search.GoToPageAsync(3));
            Assert.Equal(("cat", 1), Assert.Single(bounded.Searches));

            profile.BooruSearchPages = 1;
            profile.BooruSearchPostsPerPage = 8;
            number.Text = "2";
            Layout();
            Assert.True(jump.IsEnabled);
            jump.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Assert.Equal(new[] { ("cat", 1), ("cat", 2) }, bounded.Searches);
            Assert.Equal(new[] { 10, 10 }, bounded.SearchLimits);
            Assert.False(Find<Button>(BooruWorkbenchView.NextName).IsEnabled);
            Assert.Equal(Texts.T("booru.ui.page.current", "2+", 10), info.Text);

            booru.ShowQuery("dog");
            BooruComponentDispatcher.Wait(booru.Model.Search.SubmitAsync());
            Layout();
            Assert.Equal(("dog", 1), bounded.Searches.Last());
            Assert.Equal(8, bounded.SearchLimits.Last());
            Assert.Equal(Texts.T("booru.ui.page.current", "1+", 8), info.Text);
            Assert.False(Find<Button>(BooruWorkbenchView.NextName).IsEnabled);
            Assert.Empty(harness.Search.Searches);

            BooruSearchTabTarget saved = new(sourceId, null, null, "cat", IsEnd: false,
                Rows: [new(post.Post, null)], MaximumPage: 5, PostsPerPage: 20);
            booru.View.SearchTabs.RestoreTargets(new(0, [saved]));
            Layout();
            Assert.Equal(Texts.T("booru.ui.page.current", "1+", 10), info.Text);
            number.Text = "3";
            Layout();
            Assert.False(jump.IsEnabled);
            Assert.Equal(3, bounded.Searches.Count);

            booru.View.SearchTabs.RestoreTargets(new(0, [saved with { Page = 3 }]));
            Layout();
            Assert.Empty(booru.Model.Search.View.Rows);
            Assert.Equal(1, booru.Model.Search.View.Page);
            BooruComponentDispatcher.Wait(booru.Model.Search.RetryAsync());
            Assert.Equal(("cat", 1), bounded.Searches.Last());
            Assert.Equal(10, bounded.SearchLimits.Last());
        }
        finally { window.Close(); }

        T Find<T>(string id) where T : Control => SidePanelDesignTests.ById<T>(booru, id);
        void Layout()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
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

    [Theory]
    [InlineData(BooruWorkbenchView.InputName, PhysicalKey.J, "J", "j")]
    [InlineData("BooruPageNumber", PhysicalKey.Digit2, "D2", "2")]
    public void SavedBarePageBindingLeavesNativeTextEntryToTheFocusedField(string automationId, PhysicalKey key, string gesture, string text)
        => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        ShortcutBindingsSection previous = ShortcutService.CurrentSection;
        ShortcutService.Publish(new ShortcutBindingsSection().Set("pagination.next",
            [ShortcutGesture.Parse(gesture), ShortcutGesture.Parse(OperatingSystem.IsMacOS() ? "Primary+Alt+Right" : "Alt+PageDown")]));
        BooruUiHarness harness = new();
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, isEnd: false, BooruUiFixtures.Post("101", tags: [], preview: false))));
        using BooruPage booru = new(harness.Services);
        Window window = new() { Content = booru, Width = 960, Height = 720 };
        try
        {
            Assert.Empty(ShortcutService.Current.Issues);
            window.Show();
            booru.ShowQuery("cat");
            BooruComponentDispatcher.Wait(booru.Model.Search.SubmitAsync());
            Layout();

            TextBox field = Find<TextBox>(automationId);
            Assert.True(Find<Button>(BooruWorkbenchView.NextName).IsEnabled);
            Assert.True(field.Focus());
            field.SelectAll();
            window.KeyPressQwerty(key, RawInputModifiers.None);
            window.KeyTextInput(text);
            window.KeyReleaseQwerty(key, RawInputModifiers.None);
            Layout();
            Assert.Equal(new[] { ("cat", 1) }, harness.Search.Searches);
            Assert.Equal(text, field.Text);

            bool mac = OperatingSystem.IsMacOS();
            PhysicalKey pageKey = mac ? PhysicalKey.ArrowRight : PhysicalKey.PageDown;
            RawInputModifiers modifiers = mac ? RawInputModifiers.Meta | RawInputModifiers.Alt : RawInputModifiers.Alt;
            window.KeyPressQwerty(pageKey, modifiers);
            window.KeyReleaseQwerty(pageKey, modifiers);
            Layout();
            Assert.Equal(new[] { ("cat", 1), ("cat", 2) }, harness.Search.Searches);

            T Find<T>(string id) where T : Control => booru.GetVisualDescendants().OfType<T>()
                .Single(control => AutomationProperties.GetAutomationId(control) == id);
            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }
        finally
        {
            window.Close();
            ShortcutService.Publish(previous);
        }
    });
}
