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
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
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
        "BooruView:"
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
        Assert.Contains("⌥", Texts.T("booru.ui.tags.clickHint", PlatformKeyNames.Alt(ShortcutPlatform.MacOS)));
        Assert.Contains("Alt", Texts.T("booru.ui.tags.clickHint", PlatformKeyNames.Alt(ShortcutPlatform.Linux)));
        using Fixture fixture = new();
        foreach ((int width, double frameWidth, double frameHeight, double budget) in new[]
        {
            (800, 160d, 128d, 248d), (1280, 192d, 182d, 200d), (1600, 224d, 150d, 168d), (1920, 224d, 150d, 168d)
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
            AssertBands(fixture, frameWidth, frameHeight, budget, tagsBelow: width < 900);
        }

        fixture.ResizeViewport(1840, 700);
        Rect first = Bounds(fixture.Card("101"), fixture.Results);
        Rect fourth = Bounds(fixture.Card("104"), fixture.Results);
        Assert.InRange(fourth.Bottom - first.Top, 0, 700);
        ScrollViewer scroll = FindById<ScrollViewer>(fixture.Window, "BooruResultScroll");
        Control footer = FindById<Control>(fixture.Window, "BooruResultsFooter");
        Near(8, Bounds(footer, fixture.Window).Top - Bounds(scroll, fixture.Window).Bottom);

        fixture.ResizeViewport(1152);
        Border enlarged = fixture.Card("101");
        foreach (TextBlock text in enlarged.GetVisualDescendants().OfType<TextBlock>())
            text.FontSize = 20;
        fixture.Settle();
        Rect tags = Bounds(FindById<Control>(enlarged, "BooruListTags:101"), enlarged);
        Rect actions = Bounds(FindById<Control>(enlarged, "BooruResultActions:101"), enlarged);
        Assert.True(tags.Bottom + 7 <= actions.Top);
        Near(8, enlarged.Bounds.Height - enlarged.BorderThickness.Bottom - actions.Bottom);
        Assert.All(ShownTags(enlarged), chip => Assert.True(Bounds(chip, enlarged).Right <= enlarged.Bounds.Width));
    });

    [Fact]
    public void SeparateWorkbenchUsesTheSameWidthsAtBothSidesOfEachBreakpoint() => Run(() =>
    {
        using Fixture fixture = new(separate: true);
        foreach ((int width, int expected, int height) in new[]
        {
            (800, 160, 128), (899, 160, 128), (900, 192, 182), (1152, 192, 182),
            (1400, 192, 182), (1401, 224, 150), (1840, 224, 150)
        })
        {
            fixture.ResizeViewport(width);
            Border card = fixture.Card("101");
            Near(width, card.Bounds.Width);
            AssertBands(fixture, expected, height, width < 900 ? 248 : width <= 1400 ? 200 : 168,
                tagsBelow: width < 900);
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
        AssertBands(fixture, 224, 150, 168, tagsBelow: false);
        Image scaled = Assert.IsType<Image>(fixture.Frame("101").Child);
        Near(224, scaled.Bounds.Width);
        Assert.Equal(336, Assert.IsAssignableFrom<Bitmap>(scaled.Source).PixelSize.Width);
        Border gifBadge = Find<Border>(fixture.Card("102"), "BooruResultKind:102");
        Rect mark = Bounds(gifBadge, fixture.Card("102"));
        Rect frame = Bounds(fixture.Frame("102"), fixture.Card("102"));
        Assert.True(frame.Contains(mark));
        Assert.Single(fixture.Harness.Search.Searches);
    });

    // Fable B keeps category priority and the visible chips as tab stops, with keyboard
    // access to folding in the card menu. Astra W112-69 B §4 preserves a new explicit 12.
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(200)]
    public void TagFoldAccountsForEveryOmittedTagAndReturnsFocusToTheSameHit(int tagLimit) => Run(() =>
    {
        using Fixture fixture = new(tagCount: 200, tagLimit: tagLimit);
        fixture.ResizeViewport(800);
        Border card = fixture.Card("101");
        Button[] visible = ShownTags(card);
        if (tagLimit == 0) Assert.Empty(visible);
        else
        {
            if (tagLimit == 12) Assert.Equal(12, visible.Length);
            else Assert.InRange(visible.Length, 13, 199);
            Button longChip = visible[0];
            Assert.Contains(new string('n', 300), AutomationProperties.GetName(longChip));
            Assert.InRange(longChip.Bounds.Width, 1, 240);
            Assert.Contains(visible, chip => chip.Name!.EndsWith("meta_tag_199", StringComparison.Ordinal));
        }

        Button more = Toggle(card, "101");
        Assert.Equal(Texts.T("booru.ui.tags.more", 200 - visible.Length), more.Content);
        Assert.False(more.IsTabStop);
        Assert.False(more.Focusable);
        Assert.True(card.Focus());
        ContextMenu menu = BooruResultActionsTests.OpenContext(fixture.Window, card);
        BooruResultActionsTests.Click(BooruResultActionsTests.Item(menu, "booru.menu.fold-tags"));
        fixture.Settle();
        Border expanded = fixture.Card("101");
        Assert.Same(card, expanded);
        Assert.Equal(200, ShownTags(expanded).Length);
        Assert.Equal(Texts.T("booru.ui.tags.less"), Toggle(expanded, "101").Content);
        Assert.Same(card, fixture.Window.FocusManager!.GetFocusedElement());
        Assert.Equal(Texts.T("booru.ui.tags.more", 200 - ShownTags(fixture.Card("102")).Length), Toggle(fixture.Card("102"), "102").Content);
        Assert.True(expanded.Bounds.Height > 248);
        fixture.ResizeViewport(1600);
        Rect expandedTags = Bounds(FindById<Control>(expanded, "BooruListTags:101"), expanded);
        Rect expandedActions = Bounds(FindById<Control>(expanded, "BooruResultActions:101"), expanded);
        Assert.True(expandedTags.Bottom + 7 <= expandedActions.Top);
        Near(8, expanded.Bounds.Height - expanded.BorderThickness.Bottom - expandedActions.Bottom);
        fixture.ResizeViewport(800);

        BooruResultActionsTests.Click(Toggle(expanded, "101"));
        fixture.Settle();
        Assert.Equal(visible.Length, ShownTags(fixture.Card("101")).Length);
        Assert.Same(card, fixture.Window.FocusManager!.GetFocusedElement());
        Assert.Single(fixture.Harness.Search.Searches);
    });

    [Theory]
    [InlineData(12)]
    [InlineData(29)]
    public void AllTagsFitWithoutAFoldButtonAtWideWidth(int tagCount) => Run(() =>
    {
        using Fixture fixture = new(tagCount: tagCount);
        fixture.ResizeViewport(1920);
        Border card = fixture.Card("101");
        Assert.Equal(tagCount, ShownTags(card).Length);
        Assert.False(Toggle(card, "101").IsTabStop);
        Assert.Equal(0, Toggle(card, "101").Opacity);
        Assert.False(Toggle(card, "101").IsHitTestVisible);
    });

    [Fact]
    public void CategoriesContinueOnTheSameLineAndTheirFirstChipTravelsWithTheMark() => Run(() =>
    {
        using Fixture fixture = new(tagCount: 29);
        fixture.ResizeViewport(1600);
        Border card = fixture.Card("101");
        Control tags = Find<Control>(card, "BooruListTags:101");
        string source = Texts.T("booru.ui.tags.label");
        Assert.Equal(source, AutomationProperties.GetName(tags));
        Assert.DoesNotContain(tags.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == source);

        Button artistChip = ShownTags(card)[0];
        Button copyrightChip = ShownTags(card)[1];
        TextBlock copyright = Assert.Single(tags.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == BooruText.TagCategory(BooruTagCategory.Copyright) + ":");
        Near(Bounds(artistChip, card).Right + 12, Bounds(copyright, card).Left);
        Near(Bounds(artistChip, card).Center.Y, Bounds(copyright, card).Center.Y);
        Near(Bounds(copyrightChip, card).Center.Y, Bounds(copyright, card).Center.Y);
        Assert.Equal(source, ToolTip.GetTip(copyright));
        Assert.Equal(29, ShownTags(card).Length);
        Assert.Contains(ShownTags(card), chip => Bounds(chip, card).Left > Bounds(tags, card).Center.X);
    });

    [Fact]
    public void DenseRowsUseTheAvailableHeightAndCanBeRestrictedByTagLines() => Run(() =>
    {
        using Fixture automatic = new(tagCount: 200, singleCategory: true);
        foreach ((int width, int lines) in new[] { (1280, 5), (1600, 4), (1920, 4) })
        {
            automatic.ResizeViewport(width);
            Border card = automatic.Card("101");
            Button[] chips = ShownTags(card);
            Assert.Equal(lines, chips.Select(chip => Bounds(chip, card).Top).Distinct().Count());
            Assert.Equal(Texts.T("booru.ui.tags.more", 200 - chips.Length), Toggle(card, "101").Content);
            Assert.True(Toggle(card, "101").IsHitTestVisible);
        }
        using Fixture limited = new(tagCount: 200, config: new Config { BooruListTagLines = 1 });
        limited.ResizeViewport(1920);
        Border limitedCard = limited.Card("101");
        Button[] limitedChips = ShownTags(limitedCard);
        Assert.Single(limitedChips.Select(chip => Bounds(chip, limitedCard).Top).Distinct());
        Assert.Equal(Texts.T("booru.ui.tags.more", 200 - limitedChips.Length), Toggle(limitedCard, "101").Content);
    });

    // Astra W112-69 B §8 keeps the complete hidden count and limits the names in the tooltip.
    [Theory]
    [InlineData(60, "")]
    [InlineData(61, "…")]
    [InlineData(200, "…")]
    public void FoldTooltipLimitsNamesButCountsEveryHiddenTag(int tagCount, string suffix) => Run(() =>
    {
        using Fixture fixture = new(tagCount: tagCount, config: new Config { BooruResultTagsShown = 0 });
        Button more = Toggle(fixture.Card("101"), "101");
        Assert.Equal(Texts.T("booru.ui.tags.more", tagCount), more.Content);
        var tags = fixture.Model.Search.View.Rows[0].Post.Tags;
        string hint = Texts.T("booru.ui.tags.showAll", string.Join(", ", tags.Take(60).Select(tag => tag.Name)) + suffix);
        Assert.Equal(hint, ToolTip.GetTip(more));
        Assert.Equal(hint, AutomationProperties.GetHelpText(more));
    });

    [Fact]
    public void ResizingClosesTheFoldedChipsMenuAndEscapeLeavesFocusOnTheHit() => Run(() =>
    {
        using Fixture fixture = new(tagCount: 200, singleCategory: true);
        fixture.ResizeViewport(1920);
        Border card = fixture.Card("101");
        Button chip = ShownTags(card)[^1];
        Assert.True(chip.Focus());
        fixture.Window.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
        fixture.Window.KeyReleaseQwerty(PhysicalKey.F10, RawInputModifiers.Shift);
        fixture.Settle();
        ContextMenu menu = Assert.IsType<ContextMenu>(chip.ContextMenu);
        Assert.True(menu.IsOpen);

        fixture.ResizeViewport(800);
        Assert.DoesNotContain(chip, ShownTags(card));
        Assert.False(menu.IsOpen);
        Assert.Null(chip.ContextMenu);
        Assert.False(chip.Focus());
        Press(fixture.Window, PhysicalKey.Escape);
        fixture.Settle();
        Assert.Same(card, fixture.Window.FocusManager!.GetFocusedElement());
        Assert.Equal("1boy", fixture.Model.Search.View.Input);
        Assert.Single(fixture.Harness.Search.Searches);
    });

    [Fact]
    public void TabDuringFoldKeepsTheNewFocus() => Run(() =>
    {
        using Fixture fixture = new(tagCount: 200, singleCategory: true);
        fixture.ResizeViewport(1920);
        Border card = fixture.Card("101");
        Button chip = ShownTags(card)[^1];
        Assert.True(chip.Focus());
        IInputElement? moved = null;
        chip.PropertyChanged += OnChipFolded;
        try { fixture.ResizeViewport(800); }
        finally { chip.PropertyChanged -= OnChipFolded; }
        Assert.NotNull(moved);
        Assert.Same(moved, fixture.Window.FocusManager!.GetFocusedElement());
        Assert.False(chip.Focus());
        Assert.Equal("1boy", fixture.Model.Search.View.Input);

        void OnChipFolded(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property != InputElement.FocusableProperty || chip.Focusable) return;
            chip.PropertyChanged -= OnChipFolded;
            // Send Tab after arrangement hides this chip, while the focus return is still queued.
            Dispatcher.UIThread.Post(() =>
            {
                Assert.DoesNotContain(chip, ShownTags(card));
                CheckBox other = FindById<CheckBox>(fixture.Card("102"), "BooruSelect:102");
                Assert.True(other.Focus());
                Press(fixture.Window, PhysicalKey.Tab);
                moved = fixture.Window.FocusManager!.GetFocusedElement();
                Assert.NotSame(chip, moved);
                Assert.NotSame(card, moved);
                Assert.NotSame(other, moved);
            }, DispatcherPriority.Normal);
        }
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
        fixture.ResizeViewport(400);
        foreach (TextBlock text in fixture.Card("101").GetVisualDescendants().OfType<TextBlock>())
            text.FontSize = 24;
        fixture.Settle();
        Border card = fixture.Card("101");
        Rect tags = Bounds(FindById<Control>(card, "BooruListTags:101"), card);
        Rect actions = Bounds(FindById<Control>(card, "BooruResultActions:101"), card);
        Assert.True(actions.Top >= Bounds(fixture.Frame("101"), card).Bottom);
        Assert.True(tags.Top > actions.Bottom);
        Assert.True(card.Bounds.Height - card.BorderThickness.Bottom - tags.Bottom >= 7);
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
        TextBlock details = Find<TextBlock>(retry, BooruWorkbenchView.DetailsPrefix + "102");
        Assert.Equal(4, details.Text!.Split(" · ").Length - 1); // File size has its own separator.
        Assert.DoesNotContain(Texts.T("booru.ui.post.field.score"), AutomationProperties.GetName(details));
        CheckAction(local, BooruWorkbenchView.ShowInMediaPrefix + "101", "101");
        CheckAction(retry, BooruWorkbenchView.RetryLoadPrefix + "102", "102");
        // Astra W112-69 B §5: the disabled original action keeps its regular label and explains the local state.
        Button localLoad = FindById<Button>(local, BooruWorkbenchView.LoadPrefix + "101");
        Assert.Equal(Texts.T("booru.ui.result.short.load"), localLoad.Content);
        Assert.False(localLoad.IsEffectivelyEnabled);
        Assert.Equal(BooruText.Load(fixture.Model.Search.View.Rows[0]), AutomationProperties.GetHelpText(localLoad));
        Button showInMedia = FindById<Button>(local, BooruWorkbenchView.ShowInMediaPrefix + "101");
        Assert.Equal(Texts.T("booru.ui.result.short.media"), showInMedia.Content);
        Assert.True(showInMedia.IsEffectivelyEnabled);
        Button retryLoad = FindById<Button>(retry, BooruWorkbenchView.RetryLoadPrefix + "102");
        Assert.Equal(Texts.T("booru.ui.result.short.again"), retryLoad.Content);
        Assert.True(retryLoad.IsEffectivelyEnabled);
        Assert.Contains("KiB", Find<TextBlock>(local, "BooruListStatus:101").Text);
        Assert.True(Find<Border>(local, "BooruResultLocal:101").IsVisible);
        Border withdrawn = fixture.Card("104");
        Rect badge = Bounds(Find<Border>(withdrawn, "BooruResultWithdrawn:104"), withdrawn);
        Assert.True(Bounds(fixture.Frame("104"), withdrawn).Contains(badge));
        Assert.Contains(withdrawn.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("workspace-warning"));
        Rect notice = Bounds(FindById<Control>(withdrawn, "BooruResultNotices:104"), withdrawn);
        Rect withdrawnActions = Bounds(FindById<Control>(withdrawn, "BooruResultActions:104"), withdrawn);
        Near(8, notice.Top - withdrawnActions.Bottom);
        Near(8, withdrawn.Bounds.Height - withdrawn.BorderThickness.Bottom - notice.Bottom);
        Assert.True(local.Focus());
        Press(fixture.Window, PhysicalKey.Enter);
        Assert.Equal("101", Assert.Single(fixture.ShownLocally));
        BooruResultActionsTests.Click(showInMedia);
        Assert.Equal(new[] { "101", "101" }, fixture.ShownLocally);

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
    public void SavingARefusalKeepsTheSameActionTierAndAlignmentAsAnIdleNeighbor() => Run(() =>
    {
        Texts.SetLanguage("de");
        using Fixture fixture = new(refusalPending: true);
        Border idle = fixture.Card("101");
        Border refusal = fixture.Card("102");
        Assert.Equal(BooruRowLoad.RefusalPending, fixture.Model.Search.View.Rows[1].Load);
        Button idleOpen = FindById<Button>(idle, BooruWorkbenchView.OpenPostPrefix + "101");
        Button refusalOpen = FindById<Button>(refusal, BooruWorkbenchView.OpenPostPrefix + "102");

        int narrow = 460, wide = 800;
        fixture.ResizeViewport(narrow);
        Assert.False(HasLabel(idleOpen));
        fixture.ResizeViewport(wide);
        Assert.True(HasLabel(idleOpen));
        while (wide - narrow > 1)
        {
            int middle = (narrow + wide) / 2;
            fixture.ResizeViewport(middle);
            if (HasLabel(idleOpen)) wide = middle;
            else narrow = middle;
        }

        foreach (int width in new[] { narrow, wide })
        {
            fixture.ResizeViewport(width);
            Assert.Equal(width == wide, HasLabel(idleOpen));
            Assert.Equal(width == wide, HasLabel(refusalOpen));
            foreach (string prefix in ActionPrefixes.Skip(1))
            {
                Button first = FindById<Button>(idle, prefix + "101");
                Button second = FindById<Button>(refusal, prefix + "102");
                Assert.True(first.IsHitTestVisible && second.IsHitTestVisible);
                Near(Bounds(first, idle).Left, Bounds(second, refusal).Left);
                Near(first.Bounds.Width, second.Bounds.Width);
            }
        }

        Button saveRefusal = FindById<Button>(refusal, BooruWorkbenchView.RetryLoadPrefix + "102");
        Assert.Equal(Texts.T("booru.ui.load.saveRefusal"), saveRefusal.Content);
        Assert.True(saveRefusal.IsEffectivelyEnabled);

        static bool HasLabel(Button button) => button.Opacity > 0 && button.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.IsEffectivelyVisible && text.Text == (string?)button.Content);
    });

    [Fact]
    public void ProfileDefaultsLimitsTransferAndEditorResetsRoundTrip() => Run(() =>
    {
        Config old = JsonSerializer.Deserialize("""{"PreferenceDefaultsRevision":1,"BooruResultTagsShown":12}""", ConfigJsonContext.Default.Config)!;
        Assert.Equal(Config.BooruResultTagsShownAutomatic, old.BooruResultTagsShown);
        Assert.Equal(Config.BooruResultTagsShownAutomatic, new Config().BooruResultTagsShown);
        using (Fixture legacy = new(config: old))
        {
            legacy.ResizeViewport(1920);
            Assert.Equal(29, ShownTags(legacy.Card("101")).Length);
        }
        Assert.Equal(BooruListLayoutSettings.Default, old.BooruListLayout);
        Assert.Equal(0, old.BooruListTagLines);
        old.BooruListTagLines = -1;
        Assert.Throws<ArgumentOutOfRangeException>(old.Validate);
        old.BooruListTagLines = 13;
        Assert.Throws<ArgumentOutOfRangeException>(old.Validate);
        old.BooruListTagLines = 0;
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

        Config config = new() { BooruListPreviewWidth = 256, BooruListWideAbove = 1360, BooruListWideRowHeight = 196, BooruListTagLines = 2,
            BooruResultTagsShown = 20 };
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
            NumericUpDown tagLines = Assert.Single(editor.GetLogicalDescendants().OfType<NumericUpDown>(),
                control => control.Name == "BooruListTagLinesInput");
            Assert.Equal(2, tagLines.Value);
            editor.ComposeRowMenu(tagLines)!.ToModel().Entries.Single(entry => entry.CommandId == "settings.reset-default").Invoke!();
            Assert.Equal(0, tagLines.Value);
            editor.ComposeRowMenu(input)!.ToModel().Entries.Single(entry => entry.CommandId == "settings.reset-default").Invoke!();
            Assert.Equal(192, input.Value);
            NumericUpDown tagLimit = Assert.Single(editor.GetLogicalDescendants().OfType<NumericUpDown>(),
                control => AutomationProperties.GetAutomationId(control) == "BooruResultTagsShownInput");
            Assert.Equal(20, tagLimit.Value);
            editor.ResetDraft(SettingsEditor.SettingsResetScope.WorkerBudgets);
            Assert.Equal(Config.BooruResultTagsShownAutomatic, tagLimit.Value);
            tagLimit.Value = 12;
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
            Assert.Equal(0, saved.BooruListTagLines);
            Assert.Equal(12, saved.BooruResultTagsShown);
            Config reopened = JsonSerializer.Deserialize(JsonSerializer.Serialize(saved, ConfigJsonContext.Default.Config),
                ConfigJsonContext.Default.Config)!;
            Assert.Equal(12, reopened.BooruResultTagsShown);
            Assert.Equal(12, SettingsTransfer.Export(saved).Deserialize(ConfigJsonContext.Default.Config)!.BooruResultTagsShown);
            using Fixture explicitLimit = new(config: reopened);
            explicitLimit.ResizeViewport(1920);
            Assert.Equal(12, ShownTags(explicitLimit.Card("101")).Length);
        }
        finally { editor.Dispose(); }
    });

    // Fable B §2 and §4 replace the shared facts axis and split columns with full-width text.
    // W112-69 §B2, §B4 place the reserved action strip at the image edge and the checkbox on the image.
    private static void AssertBands(Fixture fixture, double width, double height, double budget, bool tagsBelow)
    {
        Border first = fixture.Card("101");
        Border second = fixture.Card("102");
        Assert.Contains("workspace-card", first.Classes);
        TextBlock title = Assert.Single(first.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("card-title"));
        Near(width, fixture.Frame("101").Bounds.Width);
        Near(height, fixture.Frame("101").Bounds.Height);
        Assert.InRange(first.Bounds.Height, height, budget + 1);
        Assert.InRange(second.Bounds.Height, height, budget + 1);

        Rect frame = Bounds(fixture.Frame("101"), first);
        Rect actions = Bounds(FindById<Panel>(first, "BooruResultActions:101"), first);
        Rect facts = Bounds(FindById<Control>(first, "BooruResultFacts:101"), first);
        Rect details = Bounds(FindById<TextBlock>(first, BooruWorkbenchView.DetailsPrefix + "101"), first);
        Rect tags = Bounds(FindById<Control>(first, "BooruListTags:101"), first);
        Near(actions.Bottom, frame.Bottom);
        Near(facts.Left, actions.Left);
        Near(24, actions.Height);
        Assert.True(frame.Contains(Bounds(FindById<CheckBox>(first, "BooruSelect:101"), first)));
        Assert.True(details.Top >= facts.Top);
        if (!tagsBelow)
        {
            Assert.True(details.Left > Bounds(title, first).Right);
            Near(facts.Left, tags.Left);
            Near(facts.Right, tags.Right);
            // Astra's 8 DIP clearance and Opus' common action/preview edge both apply inside the frame.
            Near(8, first.Bounds.Height - first.BorderThickness.Bottom - actions.Bottom);
            Near(8, frame.Top - first.BorderThickness.Top);
            Near(budget, first.Bounds.Height);
            Assert.True(tags.Top >= facts.Bottom && tags.Bottom + 7 <= actions.Top);
        }
        else
        {
            Assert.True(tags.Top >= frame.Bottom && tags.Top >= facts.Bottom);
            Near(frame.Left, tags.Left);
            Assert.True(first.Bounds.Height - first.BorderThickness.Bottom - tags.Bottom >= 7);
        }
        foreach (string prefix in ActionPrefixes) CheckAction(first, prefix + "101", "101");
        Assert.All(ShownTags(first), chip =>
        {
            ChipOutline outline = Assert.IsType<ChipOutline>(chip.Content);
            Assert.Contains(WorkspaceChips.Source, outline.Classes);
            Assert.Contains(WorkspaceChips.Compact, outline.Classes);
            Assert.True(chip.IsTabStop);
            Assert.True(chip.Focusable);
            Assert.Equal(Texts.T("booru.ui.tags.clickHint", PlatformKeyNames.Alt()), AutomationProperties.GetHelpText(chip));
            Assert.True(tags.Contains(Bounds(chip, first)));
        });
    }

    private static void CheckAction(Border card, string name, string postId)
    {
        Panel row = FindById<Panel>(card, "BooruResultActions:" + postId);
        Button button = FindById<Button>(row, name);
        Assert.True(button.IsEffectivelyVisible);
        Assert.False(button.Focusable);
        Assert.False(button.IsTabStop);
        Rect action = Bounds(button, card);
        Assert.True(Bounds(row, card).Contains(action), $"{name}: {action} in {Bounds(row, card)}");
        TextBlock label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == (string?)button.Content);
        Rect labelBounds = Bounds(label, card);
        Assert.True(action.Contains(labelBounds));
        Assert.NotEmpty(AutomationProperties.GetName(button)!);
        if (name.StartsWith(BooruWorkbenchView.LoadPrefix, StringComparison.Ordinal))
        {
            Assert.Equal("download-cloud", IconButtons.KeyOf(button));
            Border icon = Assert.Single(button.GetVisualDescendants().OfType<Border>(), child =>
                child.OpacityMask is ImageBrush brush && ReferenceEquals(brush.Source, CommandBindings.IconMask("download-cloud")));
            Assert.True(action.Contains(Bounds(icon, card)));
        }
    }

    private static Button[] ShownTags(Control card) => card.GetVisualDescendants().OfType<Button>()
        .Where(button => AutomationProperties.GetAutomationId(button)?.StartsWith(BooruWorkbenchView.ResultTagPrefix, StringComparison.Ordinal) == true
            && button.IsEffectivelyVisible && button.Opacity > 0 && button.Bounds.Width > 0
            && button.GetVisualAncestors().TakeWhile(ancestor => !ReferenceEquals(ancestor, card)).All(ancestor => ancestor.Opacity > 0))
        .ToArray();

    private static Button Toggle(Border card, string id) => Find<Button>(card, BooruWorkbenchView.MoreTagsPrefix + id);
    private static T Find<T>(Visual root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static T FindById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

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

        internal Fixture(bool separate = false, int tagCount = 29, int tagLimit = Config.BooruResultTagsShownAutomatic, bool states = false,
            Config? config = null, bool singleCategory = false, bool refusalPending = false)
        {
            config ??= new Config { BooruResultTagsShown = tagLimit };
            BooruPostView[] posts = Enumerable.Range(101, 4).Select(id => Post(id, tagCount, states, singleCategory)).ToArray();
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
            if (refusalPending) Harness.Queue.States["102"] = BooruLocalState.RefusalPending;
            if (separate)
            {
                BooruWorkbenchWindow workbench = new(Harness.Services);
                workbench.ApplyListProfile(config);
                Window = workbench;
                Model = workbench.Model;
                Window.Classes.Add(MotionPolicy.OffClass);
                Window.RequestedThemeVariant = ThemeVariant.Dark;
                Window.Show();
                View = Assert.Single(Window.GetVisualDescendants().OfType<BooruWorkbenchView>());
            }
            else
            {
                _page = new(Harness.Services);
                View = _page.View;
                Model = _page.Model;
                View.ApplyListProfile(config);
                Window = new Window { Content = _page, Classes = { MotionPolicy.OffClass }, RequestedThemeVariant = ThemeVariant.Dark };
                Window.Show();
            }

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

        internal Border Card(string id) => FindById<Border>(Results, "BooruResult:" + id);
        internal Border Frame(string id) => View.ResultView == BooruResultView.List
            ? FindById<Border>(Card(id), "BooruPreviewFrame:" + id)
            : Find<Border>(Card(id), "BooruPreviewFrame:" + id);

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

        private static BooruPostView Post(int id, int tagCount, bool states, bool singleCategory)
        {
            string[] names =
            [
                "aoi_studio", "iron_road", "steel_ball_run", "gyro_zeppeli", "johnny_joestar", "tusk_act4",
                "1boy", "blue_hat", "english_text", "green_nails", "long_hair", "closed_eyes", "retro_coloring", "wide_shot"
            ];
            BooruTag[] tags = Enumerable.Range(0, tagCount).Select(index => new BooruTag(
                tagCount == 200 && index == 0 ? new string('n', 300) : index < names.Length ? names[index] :
                    (index >= tagCount - 2 && !singleCategory ? "meta_tag_" : "general_tag_") + index,
                singleCategory ? BooruTagCategory.General : index switch
                {
                    0 => BooruTagCategory.Artist,
                    1 or 2 => BooruTagCategory.Copyright,
                    3 or 4 or 5 => BooruTagCategory.Character,
                    _ when index >= tagCount - 2 => BooruTagCategory.Meta,
                    _ => BooruTagCategory.General
                })).ToArray();
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
