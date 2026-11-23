using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;
using SkiaSharp;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruListLayoutTests
{
    private static readonly string[] ActionPrefixes =
    [
        BooruWorkbenchView.LoadPrefix, BooruWorkbenchView.OpenPostPrefix, BooruWorkbenchView.CopyLinkPrefix,
        "BooruView:", "BooruSelect:"
    ];

    public static TheoryData<string> Languages()
    {
        TheoryData<string> languages = new() { "en", "de" };
        foreach (string code in Texts.BundledPackCodes()) languages.Add(code);
        return languages;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void RealPageFitsTheThreeViewportBandsInEveryLanguage(string language) => Run(() =>
    {
        Texts.SetLanguage(language);
        using Fixture fixture = new();
        foreach ((int width, double frameWidth, double frameHeight, double budget) in new[]
        {
            (800, 160d, 104d, 248d), (1152, 192d, 128d, 200d), (1840, 224d, 144d, 168d)
        })
        {
            fixture.ResizeViewport(width);
            if (language == "en" && Environment.GetEnvironmentVariable("MEOWCHAN_CAPTURE_BOORU_LIST") == "1")
            {
                fixture.Settle();
                using WriteableBitmap rendered = fixture.Window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("The headless window did not render.");
                rendered.Save($"/tmp/112-booru-{width}.png", PngBitmapEncoderOptions.Default);
            }
            AssertBands(fixture, frameWidth, frameHeight, budget, tagsBelow: width < 900, twoColumns: width > 1400);
        }

        fixture.ResizeViewport(1840, 700);
        Rect first = Bounds(fixture.Card("101"), fixture.Results);
        Rect fourth = Bounds(fixture.Card("104"), fixture.Results);
        Assert.InRange(fourth.Bottom - first.Top, 0, 700);

        fixture.ResizeViewport(1152);
        Border enlarged = fixture.Card("101");
        foreach (TextBlock text in enlarged.GetVisualDescendants().OfType<TextBlock>())
            text.FontSize = 20;
        fixture.Settle();
        Assert.True(Bounds(Find<Control>(enlarged, "BooruListTags:101"), enlarged).Bottom <= enlarged.Bounds.Height);
        Assert.All(ShownTags(enlarged), chip => Assert.True(Bounds(chip, enlarged).Right <= enlarged.Bounds.Width));
    });

    [Fact]
    public void SeparateWorkbenchUsesTheSameWidthsAtBothSidesOfEachBreakpoint() => Run(() =>
    {
        using Fixture fixture = new(separate: true);
        foreach ((int width, int expected, int height) in new[]
        {
            (800, 160, 104), (899, 160, 104), (900, 192, 128), (1152, 192, 128),
            (1400, 192, 128), (1401, 224, 144), (1840, 224, 144)
        })
        {
            fixture.ResizeViewport(width);
            Border card = fixture.Card("101");
            Near(width, card.Bounds.Width);
            AssertBands(fixture, expected, height, width < 900 ? 248 : width <= 1400 ? 200 : 168,
                tagsBelow: width < 900, twoColumns: width > 1400);
        }
    });

    [Fact]
    public void PreviewKeepsAspectAndNaturalSizeAndDecodesForTheMonitor() => Run(() =>
    {
        using Fixture fixture = new();
        foreach (int width in new[] { 800, 1152, 1840 })
        {
            fixture.ResizeViewport(width);
            Border landscape = fixture.Frame("101");
            Image image = Assert.IsType<Image>(landscape.Child);
            Near(landscape.Bounds.Width, image.Bounds.Width);
            Near(landscape.Bounds.Width * 1291 / 2048, image.Bounds.Height);
            Image portrait = Assert.IsType<Image>(fixture.Frame("102").Child);
            Near(fixture.Frame("102").Bounds.Height, portrait.Bounds.Height);
            Near(portrait.Bounds.Height * 600 / 950, portrait.Bounds.Width);
            Image small = Assert.IsType<Image>(fixture.Frame("103").Child);
            Near(56, small.Bounds.Width);
            Near(36, small.Bounds.Height);
            Border placeholder = fixture.Frame("104");
            Assert.Contains("workspace-thumb-placeholder", placeholder.Classes);
        }

        fixture.Window.SetRenderScaling(1.5);
        fixture.Settle();
        Assert.Equal(1.5, fixture.Window.RenderScaling);
        fixture.ResizeViewport(1840);
        Image scaled = Assert.IsType<Image>(fixture.Frame("101").Child);
        Near(224, scaled.Bounds.Width);
        Assert.Equal(336, Assert.IsAssignableFrom<Bitmap>(scaled.Source).PixelSize.Width);
        Border gifBadge = Find<Border>(fixture.Card("102"), "BooruResultKind:102");
        Rect mark = Bounds(gifBadge, fixture.Card("102"));
        Rect frame = Bounds(fixture.Frame("102"), fixture.Card("102"));
        Assert.True(frame.Contains(mark));
        Assert.Single(fixture.Harness.Search.Searches);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(200)]
    public void TagFoldAccountsForEveryOmittedTagAndReturnsFocusToTheSameHit(int limit) => Run(() =>
    {
        using Fixture fixture = new(tagCount: 200, tagLimit: limit);
        fixture.ResizeViewport(800);
        Border card = fixture.Card("101");
        Button[] visible = ShownTags(card);
        if (limit == 0) Assert.Empty(visible);
        else Assert.InRange(visible.Length, 1, limit);
        if (limit != 0)
        {
            Button longChip = visible[0];
            Assert.Contains(new string('n', 300), AutomationProperties.GetName(longChip));
            Assert.InRange(longChip.Bounds.Width, 1, 240);
        }

        Button more = Toggle(card, "101");
        Assert.Equal(Texts.T("booru.ui.tags.more", 200 - visible.Length), more.Content);
        Assert.True(more.Focus());
        Press(fixture.Window, PhysicalKey.Enter);
        fixture.Settle();
        Border expanded = fixture.Card("101");
        Assert.Equal(200, ShownTags(expanded).Length);
        Assert.Equal(Texts.T("booru.ui.tags.less"), Toggle(expanded, "101").Content);
        Assert.Same(Toggle(expanded, "101"), fixture.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(Texts.T("booru.ui.tags.more", 200 - ShownTags(fixture.Card("102")).Length), Toggle(fixture.Card("102"), "102").Content);
        Assert.True(expanded.Bounds.Height > 248);

        Press(fixture.Window, PhysicalKey.Enter);
        fixture.Settle();
        Assert.Equal(visible.Length, ShownTags(fixture.Card("101")).Length);
        Assert.Same(Toggle(fixture.Card("101"), "101"), fixture.Window.FocusManager!.GetFocusedElement());
    });

    [Fact]
    public void ExactlyTwelveTagsFitWithoutAFoldButtonAtWideWidth() => Run(() =>
    {
        using Fixture fixture = new(tagCount: 12);
        fixture.ResizeViewport(1840);
        Border card = fixture.Card("101");
        Assert.Equal(12, ShownTags(card).Length);
        Assert.False(Toggle(card, "101").IsTabStop);
        Assert.Equal(0, Toggle(card, "101").Opacity);
    });

    [Fact]
    public void EmptyTagsAndLargeTextStayReachableAndResizeKeepsTheCheckboxFocused() => Run(() =>
    {
        using Fixture fixture = new(tagCount: 0);
        fixture.ResizeViewport(1840);
        Assert.Empty(ShownTags(fixture.Card("101")));
        Assert.Contains(fixture.Card("101").GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Texts.T("booru.ui.tags.none"));
        CheckBox select = Find<CheckBox>(fixture.Card("101"), "BooruSelect:101");
        Assert.True(select.Focus());
        fixture.ResizeViewport(800);
        Assert.Same(select, fixture.Window.FocusManager!.GetFocusedElement());
        Press(fixture.Window, PhysicalKey.Space);
        fixture.Settle();
        select = Find<CheckBox>(fixture.Card("101"), "BooruSelect:101");
        Assert.True(select.IsChecked);
        Assert.Same(select, fixture.Window.FocusManager.GetFocusedElement());
        Assert.False(Find<CheckBox>(fixture.Card("102"), "BooruSelect:102").IsChecked);
        foreach (TextBlock text in fixture.Card("101").GetVisualDescendants().OfType<TextBlock>())
            text.FontSize = 24;
        fixture.Settle();
        Border card = fixture.Card("101");
        Control tags = Find<Control>(card, "BooruListTags:101");
        Assert.True(Bounds(tags, card).Bottom <= card.Bounds.Height);
        Assert.Same(select, fixture.Window.FocusManager.GetFocusedElement());
        Assert.Empty(fixture.Harness.Queue.Loads);
    });

    [Fact]
    public void LocalRetryAndWarningFitTheirZonesAndTheGridRoutesTheSamePost() => Run(() =>
    {
        using Fixture fixture = new(states: true);
        fixture.ResizeViewport(1840);
        Border local = fixture.Card("101");
        Border retry = fixture.Card("102");
        Assert.Null(fixture.Model.Search.View.Rows[1].Post.Score);
        Assert.Equal(4, Find<Grid>(retry, BooruWorkbenchView.DetailsPrefix + "102").RowDefinitions.Count);
        CheckAction(local, BooruWorkbenchView.ShowInMediaPrefix + "101", "101");
        CheckAction(retry, BooruWorkbenchView.RetryLoadPrefix + "102", "102");
        Border withdrawn = fixture.Card("104");
        Rect badge = Bounds(Find<Border>(withdrawn, "BooruResultWithdrawn:104"), withdrawn);
        Assert.True(Bounds(fixture.Frame("104"), withdrawn).Contains(badge));
        Assert.Contains(withdrawn.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("workspace-warning"));
        Assert.True(local.Focus());
        Press(fixture.Window, PhysicalKey.Enter);
        Assert.Equal("101", Assert.Single(fixture.ShownLocally));

        foreach (MediaTileSize size in Enum.GetValues<MediaTileSize>())
        {
            fixture.View.RestoreResultView(BooruResultView.Grid, size);
            fixture.Settle();
            Rect tile = fixture.Frame("102").Bounds;
            Near(BooruTileMetrics.PictureSize(size), tile.Width);
            Near(tile.Width, tile.Height);
        }
        Border tileWithMenu = fixture.Card("102");
        ContextMenu menu = BooruResultActionsTests.OpenContext(fixture.Window, tileWithMenu);
        BooruResultActionsTests.Click(BooruResultActionsTests.Item(menu, BooruWorkbenchView.MenuOpenPost));
        BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => fixture.Harness.Links.Opened.Count == 1, "grid menu opens the correct post"));
        Assert.EndsWith("/102", fixture.Harness.Links.Opened[0], StringComparison.Ordinal);

        fixture.View.SetResultView(BooruResultView.List, save: false);
        fixture.Settle();
        Assert.True(fixture.Card("103").Focus());
        Press(fixture.Window, PhysicalKey.Enter);
        fixture.Settle();
        Assert.Equal(BooruResultView.Gallery, fixture.View.ResultView);
        Assert.Equal("103", fixture.View.Gallery.CurrentPostId);
        Assert.True(fixture.View.Gallery.Stage.Bounds.Height >= 320);
        Assert.Empty(fixture.Harness.Queue.Loads);
    });

    [Fact]
    public void ProfileDefaultsLimitsTransferAndEditorResetsRoundTrip() => Run(() =>
    {
        Config old = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal(BooruListLayoutSettings.Default, old.BooruListLayout);
        old.BooruListPreviewWidth = 127;
        Assert.Throws<ArgumentOutOfRangeException>(old.Validate);
        old.BooruListPreviewWidth = 321;
        Assert.Throws<ArgumentOutOfRangeException>(old.Validate);
        old.BooruListPreviewWidth = 128;
        old.Validate();
        Assert.Equal(128, old.BooruListLayout.At(800).Width);
        old.BooruListPreviewWidth = 320;
        old.Validate();
        Assert.Equal(320, old.BooruListLayout.At(1840).Width);
        old.BooruListWideRowHeight = 167;
        Assert.Throws<ArgumentOutOfRangeException>(old.Validate);

        Config config = new() { BooruListPreviewWidth = 256, BooruListWideAbove = 1360, BooruListWideRowHeight = 196 };
        config.Validate();
        Config restarted = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config),
            ConfigJsonContext.Default.Config)!;
        Assert.Equal(config.BooruListLayout, restarted.BooruListLayout);
        Assert.Equal(config.BooruListLayout, config.Clone().BooruListLayout);
        Config imported = SettingsTransfer.Export(config).Deserialize(ConfigJsonContext.Default.Config)!;
        Assert.Equal(config.BooruListLayout, imported.BooruListLayout);
        using Fixture custom = new(separate: true, config: restarted);
        custom.ResizeViewport(1152);
        Near(256, custom.Frame("101").Bounds.Width);
        custom.ResizeViewport(1400);
        Near(288, custom.Frame("101").Bounds.Width);

        Config? saved = null;
        SettingsEditor editor = new(config, candidate => { saved = candidate; return Task.CompletedTask; });
        editor.IsAvailable = true;
        try
        {
            NumericUpDown input = Assert.Single(editor.GetLogicalDescendants().OfType<NumericUpDown>(),
                control => control.Name == "BooruListPreviewWidthInput");
            Assert.Equal(256, input.Value);
            editor.ComposeRowMenu(input)!.ToModel().Entries.Single(entry => entry.CommandId == "settings.reset-default").Invoke!();
            Assert.Equal(192, input.Value);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.WorkerBudgets);
            NumericUpDown advanced = Assert.Single(editor.GetLogicalDescendants().OfType<NumericUpDown>(),
                control => control.Name == "BooruListWideAboveInput");
            Assert.Equal(1400, advanced.Value);

            Button save = editor.FindControl<Button>("SaveSettingsButton")!;
            input.Value = 321;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(editor.HasError);
            Assert.Null(saved);
            TextBlock error = Assert.Single(editor.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Name == "BooruListPreviewWidthInputError");
            Assert.Contains(Texts.T("settings.booruList.invalid.Width"), error.Text);
            input.Value = 300;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !editor.IsBusy, "Booru layout settings save"));
            Assert.False(editor.HasError, editor.ErrorMessage);
            Assert.Equal(300, saved!.BooruListPreviewWidth);
        }
        finally { editor.EndLifetime(); }
    });

    private static void AssertBands(Fixture fixture, double width, double height, double budget, bool tagsBelow, bool twoColumns)
    {
        Border first = fixture.Card("101");
        Border second = fixture.Card("102");
        Assert.Contains("workspace-card", first.Classes);
        Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("card-title"));
        Near(width, fixture.Frame("101").Bounds.Width);
        Near(height, fixture.Frame("101").Bounds.Height);
        Assert.InRange(first.Bounds.Height, height, budget + 1);
        Assert.InRange(second.Bounds.Height, height, budget + 1);
        foreach (string prefix in ActionPrefixes) CheckAction(first, prefix + "101", "101");
        double[] axes = new[] { first, second }.SelectMany(card =>
            card.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name?.StartsWith(BooruWorkbenchView.DetailsPrefix, StringComparison.Ordinal) == true)
                .SelectMany(grid => grid.Children.OfType<TextBlock>()).Where(text => Grid.GetColumn(text) == 1)
                .Select(text => Bounds(text, card).Left)).ToArray();
        Assert.Equal(10, axes.Length);
        Assert.All(axes, x => Near(axes[0], x));
        Assert.All(ShownTags(first), chip =>
        {
            ChipOutline outline = Assert.IsType<ChipOutline>(chip.Content);
            Assert.Contains(WorkspaceChips.Source, outline.Classes);
            Assert.Contains(WorkspaceChips.Compact, outline.Classes);
        });
        Rect facts = Bounds(Find<Control>(first, "BooruResultFacts:101"), first);
        Rect tags = Bounds(Find<Control>(first, "BooruListTags:101"), first);
        if (tagsBelow) Assert.True(tags.Top >= facts.Bottom);
        else
        {
            Assert.True(tags.Left >= facts.Right && tags.Top < facts.Bottom);
            if (twoColumns)
            {
                Rect[] chips = ShownTags(first).Select(chip => Bounds(chip, first)).ToArray();
                Assert.True(chips.Any(chip => chip.Left > tags.Center.X && chip.Top < facts.Bottom),
                    $"No chips in the right half of tag area {tags} above {facts.Bottom}: {string.Join("; ", chips)}");
                Assert.Contains(chips, chip => chip.Right < tags.Center.X && chip.Top < facts.Bottom);
            }
        }
    }

    private static void CheckAction(Border card, string name, string postId)
    {
        Rect preview = Bounds(Find<Border>(card, "BooruPreviewFrame:" + postId), card);
        Control button = Find<Control>(card, name);
        Assert.True(button.IsEffectivelyVisible);
        Rect action = Bounds(button, card);
        Assert.True(action.Top >= preview.Bottom - 1 && action.Left >= preview.Left - 1 && action.Right <= preview.Right + 1,
            $"{name}: {action} must be under {preview}");
    }

    private static Button[] ShownTags(Control card) => card.GetVisualDescendants().OfType<Button>()
        .Where(button => button.Name?.StartsWith(BooruWorkbenchView.ResultTagPrefix, StringComparison.Ordinal) == true && button.IsTabStop)
        .ToArray();

    private static Button Toggle(Border card, string id) => Find<Button>(card, BooruWorkbenchView.MoreTagsPrefix + id);
    private static T Find<T>(Visual root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Rect Bounds(Control control, Visual ancestor) =>
        new(control.TranslatePoint(default, ancestor) ?? throw new InvalidOperationException(control.Name), control.Bounds.Size);

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1, expected + 1);

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
    }

    private static void Run(Action action) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        action();
    });

    private sealed class Fixture : IDisposable
    {
        private readonly BooruPage? _page;
        internal BooruUiHarness Harness { get; } = new();
        internal List<string> ShownLocally { get; } = new();
        internal Window Window { get; }
        internal BooruWorkbenchModel Model { get; }
        internal BooruWorkbenchView View { get; }
        internal Panel Results => Find<Panel>(Window, BooruWorkbenchView.ResultsName);
        private ScrollViewer Scroll => Find<ScrollViewer>(Window, "BooruResultScroll");

        internal Fixture(bool separate = false, int tagCount = 29, int tagLimit = 12, bool states = false, Config? config = null)
        {
            config ??= new Config { BooruResultTagsShown = tagLimit };
            BooruPostView[] posts = Enumerable.Range(101, 4).Select(id => Post(id, tagCount, states)).ToArray();
            byte[] landscape = Preview(2048, 1291, new SKColor(53, 100, 132));
            byte[] portrait = Preview(600, 950, new SKColor(117, 65, 125));
            byte[] small = Preview(56, 36, new SKColor(88, 122, 79));
            Harness.Previews.OnPreview = post => post.PostId switch { "101" => landscape, "102" => portrait, "103" => small, _ => null };
            Harness.Search.OnSearch = (q, p, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(q, p, true, posts)));
            if (states)
            {
                Harness.Queue.States["101"] = BooruLocalState.Loaded;
                Harness.Queue.States["102"] = BooruLocalState.DownloadFailed;
            }
            if (separate)
            {
                BooruWorkbenchWindow workbench = new(Harness.Services);
                workbench.ApplyListProfile(config);
                Window = workbench;
                Model = workbench.Model;
                Window.Show();
                View = Assert.Single(Window.GetVisualDescendants().OfType<BooruWorkbenchView>());
            }
            else
            {
                _page = new(Harness.Services);
                View = _page.View;
                Model = _page.Model;
                View.ApplyListProfile(config);
                Window = new Window { Content = _page };
                Window.Show();
            }

            Window.RequestedThemeVariant = ThemeVariant.Dark;
            Window.Activate();
            View.ShowInMedia = id => { ShownLocally.Add(id); return Task.FromResult(true); };
            View.Gallery.SampleDelay = TimeSpan.Zero;
            ResizeViewport(1152);
            BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(Model.Search, "1boy"));
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => Model.Search.View.Rows.Length == 4
                && Model.Search.View.Rows.All(row => row.Preview != BooruPreviewState.Loading)
                && Results.Children.Count == 4, "four result cards and completed previews"));
            Settle();
        }

        internal Border Card(string id) => Find<Border>(Results, "BooruResult:" + id);
        internal Border Frame(string id) => Find<Border>(Card(id), "BooruPreviewFrame:" + id);

        internal void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        internal void ResizeViewport(int width, int height = 820)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                Size viewport = Scroll.Viewport;
                if (Math.Abs(viewport.Width - width) <= 0.1 && Math.Abs(viewport.Height - height) <= 0.1) break;
                Window.PlatformImpl!.Resize(new Size(Window.ClientSize.Width + width - viewport.Width,
                    Window.ClientSize.Height + height - viewport.Height), WindowResizeReason.User);
                Settle();
            }
            Near(width, Scroll.Viewport.Width);
            Near(height, Scroll.Viewport.Height);
        }

        public void Dispose()
        {
            Window.Close();
            _page?.Dispose();
            Settle();
        }

        private static BooruPostView Post(int id, int tagCount, bool states)
        {
            string[] names =
            [
                "aoi_studio", "iron_road", "steel_ball_run", "gyro_zeppeli", "johnny_joestar", "tusk_act4",
                "1boy", "blue_hat", "english_text", "green_nails", "long_hair", "closed_eyes", "retro_coloring", "wide_shot"
            ];
            BooruTag[] tags = Enumerable.Range(0, tagCount).Select(index => new BooruTag(
                tagCount == 200 && index == 0 ? new string('n', 300) : index < names.Length ? names[index] : "general_tag_" + index,
                index switch { 0 => BooruTagCategory.Artist, 1 or 2 => BooruTagCategory.Copyright, 3 or 4 or 5 => BooruTagCategory.Character, _ => BooruTagCategory.General })).ToArray();
            var post = BooruUiFixtures.Post(id.ToString(System.Globalization.CultureInfo.InvariantCulture), rawRating: "General").Post;
            var original = post.Media!.Single(media => media.Role == BooruMediaRole.Original);
            return BooruPostView.From(post with
            {
                Tags = tags,
                Fields = states && id == 102 ? post.Fields & ~BooruFieldPresence.Score : post.Fields,
                IsDeleted = states && id == 104,
                Score = id - 101,
                Media = [post.Media!.First(media => media.Role == BooruMediaRole.Preview),
                    original with { Width = 2048, Height = 1291, ExpectedSize = 472064, Extension = id == 102 ? "gif" : "png" }]
            });
        }

        private static byte[] Preview(int width, int height, SKColor color)
        {
            using SKBitmap bitmap = new(width, height);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(color);
            using SKPaint paint = new() { Color = new SKColor(243, 195, 148), IsAntialias = true };
            canvas.DrawCircle(width * 0.68f, height * 0.31f, Math.Min(width, height) * 0.16f, paint);
            paint.Color = new SKColor(33, 42, 65);
            canvas.DrawOval(new SKRect(-width * 0.2f, height * 0.56f, width * 1.3f, height * 1.4f), paint);
            paint.Color = new SKColor(172, 184, 177);
            canvas.DrawOval(new SKRect(width * 0.34f, height * 0.72f, width * 1.3f, height * 1.2f), paint);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
