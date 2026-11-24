using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

public sealed partial class MenuIconLayoutTests
{
    [Fact]
    public void CompactGalleryOverflowShowsActionMasksAndUpdatesTheMarksOnKeyPress() => OnUi(() =>
        BooruComponentDispatcher.Wait(ExerciseCompactGalleryIconsAsync()));

    [Fact]
    public void GalleryLayoutFlyoutAndContextMenuShareActionMasks() => OnUi(() =>
        BooruComponentDispatcher.Wait(ExerciseLayoutIconsAsync()));

    private static async Task ExerciseCompactGalleryIconsAsync()
    {
        await using var viewer = new GalleryViewer();
        await using var player = new GalleryVideoPlayer();
        viewer.ApplyWorkView(WorkView.Advanced);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(Mode: GalleryPlaybackMode.Slideshow, OverlayControls: false));
        viewer.SetPosition("1 of 8", true, true);
        GalleryPlaybackPreferences? preferences = null;
        viewer.PlaybackPreferencesChanged += next => preferences = next;

        Texts.SetLanguage("de");
        WithWindow(viewer, window =>
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.SetRenderScaling(1.5);
            BooruReviewTestPlatform.Resize(window, 760);
            Assert.True(viewer.IsCompactToolbar);
            Button more = viewer.OverflowButton;
            Assert.True(more.Focus(NavigationMethod.Tab));
            Press(more, Key.Enter);
            Assert.True(viewer.IsOverflowOpen);
            Assert.True(viewer.LayoutSection.IsEffectivelyVisible);
            TopLevel popup = TopLevel.GetTopLevel(viewer.OverflowMenuContent)!;
            popup.SetRenderScaling(1.5);
            Settle(viewer.OverflowMenuContent);
            Assert.Equal(1.5, popup.RenderScaling);
            var viewport = Assert.IsType<ScrollContentPresenter>(viewer.OverflowMenuContent.FindAncestorOfType<ScrollContentPresenter>());

            foreach (Button row in viewer.OverflowItems.Where(row => row.IsEffectivelyVisible))
            {
                var entry = Assert.IsType<GalleryViewer.OverflowItem>(row.Tag);
                string expected = entry.Id switch
                {
                    "gallery.slideshow-pause-on-focus-loss" => "pause-circle",
                    "gallery.wheel-browse" => "mouse-pointer",
                    "gallery.filmstrip-toggle" => "film",
                    "gallery.post-overlay-toggle" => "layers",
                    _ => CommandCatalog.Get(entry.Id).Icon
                };
                Assert.Equal(expected, entry.IconKey);
                var icon = Assert.IsType<Border>(entry.Icon.Content);
                AssertMask(row, icon, expected);
                Assert.Equal(new PixelSize(48, 48), CommandBindings.IconMask(expected).PixelSize);
                AssertBefore(icon, entry.Label, row, 8);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(row)), entry.Id);
                Assert.InRange(RelativeBounds(entry.Label, viewport).Right, 0, viewport.Bounds.Width + 1);
                if (!string.IsNullOrEmpty(entry.Gesture.Text))
                {
                    AssertShortcut(row, entry.Label, entry.Gesture);
                    Assert.InRange(RelativeBounds(entry.Gesture, viewport).Right, 0, viewport.Bounds.Width + 1);
                }
            }

            ToggleButton Toggle(string id) => Assert.IsType<ToggleButton>(
                Assert.Single(viewer.OverflowItems, row => row.Tag is GalleryViewer.OverflowItem entry && entry.Id == id));

            void Flip(string id, string key, bool wasChecked)
            {
                ToggleButton row = Toggle(id);
                Border icon = AssertFlyout(row, key, wasChecked);
                Assert.True(row.Focus(NavigationMethod.Directional));
                Press(row, Key.Space);
                Assert.Same(icon, AssertFlyout(row, key, !wasChecked));
            }

            ToggleButton pause = Toggle("gallery.slideshow-pause-on-focus-loss");
            Assert.True(pause.Focus(NavigationMethod.Directional));
            var wheel = Assert.IsType<GalleryViewer.OverflowItem>(viewer.WheelBrowseToggle.Tag);
            Assert.Equal(12, Texts.Languages.Count);
            foreach (var language in Texts.Languages)
            {
                Texts.SetLanguage(language.Code);
                Settle(pause);
                Assert.Equal(Texts.T("gallery.wheelBrowse"), wheel.Label.Text);
                Assert.Equal(wheel.Label.Text, AutomationProperties.GetName(viewer.WheelBrowseToggle));
                Assert.Equal(Texts.T("gallery.wheelBrowse.help"), ToolTip.GetTip(viewer.WheelBrowseToggle));
                Assert.Equal(GalleryPlaybackCatalog.Get("viewer.playback.pauseOnFocusLoss"), AutomationProperties.GetName(pause));
                Assert.True(pause.IsFocused);
            }
            Texts.SetLanguage("de");
            Settle(pause);
            CaptureGalleryMenu(popup, "gallery-overflow-unchecked.png");
            Flip("gallery.slideshow-pause-on-focus-loss", "pause-circle", false);
            Assert.True(preferences?.PauseOnFocusLoss);
            CaptureGalleryMenu(popup, "gallery-overflow-checked.png");

            Flip("gallery.wheel-browse", "mouse-pointer", false);
            Assert.Equal(Config.GalleryWheelModeBrowse, viewer.WheelMode);
            Flip("gallery.filmstrip-toggle", "film", true);
            Assert.False(viewer.LayoutChoice(GalleryChromeMode.View).Filmstrip);
            AssertFlyout(Assert.IsType<ToggleButton>(viewer.LayoutSectionRows.ButtonOf(GalleryLayoutMenu.FilmstripId)), "film", false);

            ToggleButton repeat = Toggle("video.loop");
            string repeatKey = Assert.IsType<GalleryViewer.OverflowItem>(repeat.Tag).IconKey!;
            Flip("video.loop", "repeat", false);
            Assert.True(preferences?.VideoRepeat);
            MenuItem playerRepeat = Assert.Single(player.PlayerMenu.Items.OfType<MenuItem>(), row => row.ToggleType == MenuItemToggleType.CheckBox);
            Assert.Equal(repeatKey, MenuIcons.KeyOf(playerRepeat));
            Assert.Same(CommandBindings.IconMask(repeatKey), Assert.IsType<ImageBrush>(Assert.IsType<Border>(playerRepeat.Icon).OpacityMask).Source);

            foreach ((string id, string key) in new[] { ("gallery.post-overlay-toggle", "layers"), ("gallery.overlay-toggle", "sliders") })
            {
                ToggleButton row = Toggle(id);
                AssertFlyout(row, key, false);
                Assert.True(row.Focus(NavigationMethod.Directional));
                Press(row, Key.Space);
                Assert.False(viewer.IsOverflowOpen);
                Assert.True(more.IsFocused);
                Press(more, Key.Enter);
                AssertFlyout(row, key, true);
            }
            Assert.True(preferences?.OverlayPostContext);
            Assert.True(preferences?.OverlayControls);
            Assert.True(preferences?.AutoHideControls);
            viewer.SetOverflowOpen(false);
        });
    }

    private static async Task ExerciseLayoutIconsAsync()
    {
        await using var viewer = new GalleryViewer();
        viewer.ApplyWorkView(WorkView.Advanced);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: false));
        WithWindow(viewer, _ =>
        {
            Button layout = viewer.LayoutButton;
            var anchorIcon = Assert.IsType<Border>(Assert.IsType<StackPanel>(layout.Content).Children[0]);
            AssertMask(layout, anchorIcon, CommandCatalog.Get(GalleryLayoutMenu.OpenCommandId).Icon);
            Assert.True(layout.Focus(NavigationMethod.Tab));
            Press(layout, Key.Enter);
            Assert.True(viewer.IsLayoutMenuOpen);

            LayoutMenuPanel panel = viewer.LayoutMenuRows;
            var keys = new Dictionary<string, string>
            {
                [GalleryLayoutMenu.FilmstripId] = "film",
                [GalleryLayoutMenu.ToolsId] = "tool",
                [GalleryLayoutMenu.InfoId] = "info",
                [GalleryLayoutMenu.HintsId] = "help",
                [GalleryLayoutMenu.ResetId] = "rotate-ccw"
            };
            foreach (LayoutMenuRow model in panel.Model!.Interactive)
            {
                Button row = panel.ButtonOf(model.Id)!;
                Assert.Equal(model.Label, AutomationProperties.GetName(row));
                if (model.Kind == LayoutMenuRowKind.Choice)
                {
                    // Top and bottom are values in the filmstrip group.
                    Assert.Null(IconSlot(row).Content);
                    continue;
                }
                string key = keys[model.Id];
                if (row is ToggleButton toggle) AssertFlyout(toggle, key, model.Checked);
                else AssertMask(row, Assert.IsType<Border>(IconSlot(row).Content), key);
            }

            var info = Assert.IsType<ToggleButton>(panel.ButtonOf(GalleryLayoutMenu.InfoId));
            Assert.True(info.Focus(NavigationMethod.Directional));
            Press(info, Key.Space);
            Assert.False(viewer.LayoutChoice(GalleryChromeMode.View).Info);
            AssertFlyout(info, "info", false);
            Press(info, Key.Escape);
            Assert.True(layout.IsFocused);

            SendMenuKey(layout, Key.F10, KeyModifiers.Shift);
            ContextMenu menu = Assert.IsType<ContextMenu>(viewer.LayoutContextMenu);
            Settle(menu);
            foreach (LayoutMenuRow model in viewer.LayoutMenuNow().Interactive)
            {
                MenuItem row = Item(menu, model.Id);
                if (model.Kind == LayoutMenuRowKind.Choice)
                {
                    Assert.Equal(MenuItemToggleType.Radio, row.ToggleType);
                    Assert.Equal(model.Checked, row.IsChecked);
                    Assert.Null(MenuIcons.KeyOf(row));
                }
                else if (model.Kind == LayoutMenuRowKind.Toggle)
                    AssertSemi(row, MenuItemToggleType.CheckBox, keys[model.Id], model.Checked);
                else
                    AssertMask(row, Assert.IsType<Border>(row.Icon), keys[model.Id]);
            }
            MenuItem reset = Item(menu, GalleryLayoutMenu.ResetId);
            Assert.True(reset.Focus(NavigationMethod.Directional));
            SendMenuKey(reset, Key.Enter);
            Assert.Equal(GalleryLayoutChoice.Default, viewer.LayoutChoice(GalleryChromeMode.View));
            Assert.False(menu.IsOpen);
        });
    }

    private static void CaptureGalleryMenu(TopLevel popup, string name)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_ICON_CAPTURES") is not { Length: > 0 } output) return;
        double scale = popup.RenderScaling;
        var pixels = PixelSize.FromSizeWithDpi(popup.ClientSize, 96 * scale);
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(popup);
        bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
    }
}
