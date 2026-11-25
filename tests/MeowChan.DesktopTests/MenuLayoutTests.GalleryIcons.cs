using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    [Theory]
    [InlineData("en", "Overlay / dock post panel", "Auto-hide sidebar controls")]
    [InlineData("de", "Post-Panel überlagern / andocken", "Leisten automatisch ausblenden")]
    public void PostPanelPlacementAgreesAcrossHelpMenuAndTheBoundKey(string language, string placement, string hide) => OnUi(() =>
    {
        const string shared = "media.sidebar.autohide";
        ShortcutBindingsSection original = ShortcutService.CurrentSection;
        try
        {
            Texts.SetLanguage(language);
            ShortcutService.Publish(new ShortcutBindingsSection());
            BooruComponentDispatcher.Wait(CheckAsync());
        }
        finally { ShortcutService.Publish(original); }

        async Task CheckAsync()
        {
            await using var viewer = new GalleryViewer();
            viewer.Configure(contextOpen: true, showShortcutHelp: true, videoAutoplay: false, videoStartMuted: true, videoVolume: 0);
            viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: false, AutoHideControls: false));
            List<GalleryPlaybackPreferences> saved = [];
            viewer.PlaybackPreferencesChanged += saved.Add;
            WithWindow(viewer, _ =>
            {
                var toggle = Assert.IsType<ToggleButton>(Assert.Single(viewer.OverflowItems,
                    button => AutomationProperties.GetAutomationId(button) == "gallery.post-overlay-toggle"));
                GalleryViewer.OverflowItem item = Assert.IsType<GalleryViewer.OverflowItem>(toggle.Tag);
                Check(Key.A, KeyModifiers.Shift);
                Assert.True(toggle.IsChecked);
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(toggle.IsChecked);

                ShortcutService.Publish(new ShortcutBindingsSection().Set(shared, [ShortcutGesture.Parse("J")]));
                Settle(viewer);
                Check(Key.J, KeyModifiers.None);
                Assert.True(toggle.IsChecked);
                bool[] expectedPlacement = [true, false, true];
                Assert.Equal(expectedPlacement, saved.Select(preference => preference.OverlayPostContext));
                Assert.All(saved, preference => Assert.False(preference.AutoHideControls));

                void Check(Key key, KeyModifiers modifiers)
                {
                    var help = viewer.ShortcutHelp();
                    Assert.Contains(shared, help.Preferred);
                    var gallery = ShortcutTableModel.Build(ShortcutService.CurrentSection, ShortcutService.Current, help.Area, help.Preferred);
                    var reader = ShortcutTableModel.Build(ShortcutService.CurrentSection, ShortcutService.Current, ShortcutGroups.Reader);
                    ShortcutTableRow row = Assert.Single(gallery.Rows, command => command.CommandId == shared);
                    Assert.Equal(placement, row.Label);
                    Assert.Equal(hide, Assert.Single(reader.Rows, command => command.CommandId == shared).Label);
                    Assert.Equal(placement, item.Label.Text);
                    Assert.Equal(Assert.Single(row.GestureTexts), item.Gesture.Text);
                    KeyEventArgs press = new() { Key = key, KeyModifiers = modifiers, Source = viewer };
                    viewer.RouteKeyForTests(press);
                    Assert.True(press.Handled);
                }
            });
        }
    });

    [Fact]
    public void OverflowMenuRowsShareOneInsetAndTheTooltipIsTheLabelNotTheRowGrid() => OnUi(() =>
        BooruComponentDispatcher.Wait(ExerciseSettingsRowsAsync()));

    private static async Task ExerciseSettingsRowsAsync()
    {
        await using var viewer = new GalleryViewer();
        viewer.ApplyWorkView(WorkView.Advanced);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(Mode: GalleryPlaybackMode.Slideshow, OverlayControls: false));
        viewer.SetPosition("1 of 8", true, true);
        Texts.SetLanguage("en");
        WithWindow(viewer, window =>
        {
            window.SetRenderScaling(1.5);
            BooruReviewTestPlatform.Resize(window, 760);
            viewer.SetOverflowOpen(true);
            var menu = viewer.OverflowMenuContent;
            TopLevel.GetTopLevel(menu)!.SetRenderScaling(1.5);
            Settle(menu);
            ToggleButton repeat = ById<ToggleButton>(menu, "video.loop");
            var repeatEntry = Assert.IsType<GalleryViewer.OverflowItem>(repeat.Tag);
            Assert.Contains(repeatEntry.Label.Text!, Assert.IsType<string>(ToolTip.GetTip(repeat)));
            AssertAt(48, repeatEntry.Label, repeat);
            TextBlock playback = ById<TextBlock>(menu, "GalleryMenuPlayback");
            TextBlock view = ById<TextBlock>(menu, "GalleryMenuView");
            AssertAt(16, playback, menu);
            AssertAt(16, view, menu);
            Assert.False(playback.Focusable);
            Assert.False(view.Focusable);
            ComboBox mode = ById<ComboBox>(menu, "GalleryPlaybackModeSelector");
            var seconds = ById<NumericUpDown>(menu, "GallerySlideshowSecondsInput");
            ComboBox videos = ById<ComboBox>(menu, "GallerySlideshowVideoRuleSelector");
            foreach (var language in Texts.Languages)
            {
                Texts.SetLanguage(language.Code);
                Settle(menu);
                Assert.Equal(Texts.T("viewer.playback.group.playback"), playback.Text);
                Assert.Equal(Texts.T("viewer.playback.group.view"), view.Text);
                CheckSetting(mode, "viewer.playback.menu.mode");
                CheckSetting(seconds, "viewer.playback.menu.seconds");
                CheckSetting(videos, "viewer.playback.menu.videos");
            }
            Assert.True(repeat.Focus(NavigationMethod.Directional));
            Press(repeat, Key.Down);
            Assert.True(mode.IsKeyboardFocusWithin);
            Press(mode, Key.Down);
            Assert.True(seconds.IsKeyboardFocusWithin);
            decimal value = seconds.Value!.Value;
            SendMenuKey((Control)Focused(seconds)!, Key.Up, KeyModifiers.Alt);
            Assert.Equal(value + 1, seconds.Value);
            Press(seconds, Key.Down);
            Assert.True(videos.IsKeyboardFocusWithin);
            Press(videos, Key.Up);
            Assert.True(seconds.IsKeyboardFocusWithin);
            Press(seconds, Key.Escape);
            Assert.False(viewer.IsOverflowOpen);

            Texts.SetLanguage("en");
            menu.MaxWidth = 280;
            viewer.SetOverflowOpen(true);
            Settle(menu);
            TextBlock shortLabel = ById<TextBlock>(menu, "GallerySlideshowSecondsInputLabel");
            Assert.Equal(TextTrimming.CharacterEllipsis, shortLabel.TextTrimming);
            Assert.True(MenuLayout.TextWidth(shortLabel, shortLabel.Text!) > shortLabel.Bounds.Width);
            Assert.Equal(shortLabel.Text, ToolTip.GetTip(shortLabel));
            Assert.InRange(seconds.Bounds.Width, 143, 145);
            Assert.Equal(Texts.T("gallery.slideshow.secondsLabel"), AutomationProperties.GetName(seconds));
            viewer.SetOverflowOpen(false);

            Assert.True(viewer.SetSlideshowMenuOpen(true));
            Settle(viewer.SlideshowSettingsPanel);
            Assert.Same(mode, ById<ComboBox>(viewer.SlideshowSettingsPanel, "GalleryPlaybackModeSelector"));
            Assert.Same(playback, ById<TextBlock>(viewer.SlideshowSettingsPanel, "GalleryMenuPlayback"));
            viewer.SetSlideshowMenuOpen(false);

            void CheckSetting(Control input, string resource)
            {
                var row = ById<Grid>(menu, input.Name + "Row");
                var label = ById<TextBlock>(row, input.Name + "Label");
                Assert.Equal(Texts.T(resource), label.Text);
                AssertAt(48, label, menu);
                Assert.InRange(row.Bounds.Height, 39, 41);
                Assert.InRange(input.Bounds.Width, 143, 145);
                Assert.InRange(input.Bounds.Height, 31, 33);
                double right = RelativeBounds(input, menu).Right - RelativeBounds(repeatEntry.Gesture, menu).Right;
                Assert.InRange(right, -1, 1);
            }
        });
    }

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
        WithGalleryWindow(viewer, window =>
        {
            window.RequestedThemeVariant = ThemeVariant.Dark;
            window.SetRenderScaling(1.5);
            BooruReviewTestPlatform.Resize(window, 760);
            Assert.True(viewer.IsCompactToolbar);
            Assert.False(viewer.OverflowButton.Focusable);
            Assert.True(viewer.Focus(NavigationMethod.Tab));
            WorkspacePopupToggleTests.PressCommand(window, GalleryLayoutMenu.OpenCommandId);
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

            ToggleButton Toggle(string id) => ById<ToggleButton>(viewer.OverflowMenuContent, id);

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
                Assert.Equal(GalleryWheelCatalog.Text("gallery.wheelBrowse.help"), ToolTip.GetTip(viewer.WheelBrowseToggle));
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
                Settle(window);
                Assert.False(viewer.IsOverflowOpen, id);
                Assert.True(viewer.IsFocused, $"{id}: expected picture focus, got {Focused(viewer)}.");
                WorkspacePopupToggleTests.PressCommand(window, GalleryLayoutMenu.OpenCommandId);
                Assert.True(viewer.IsOverflowOpen, id);
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
            Assert.False(layout.Focusable);
            BooruResultActionsTests.Click(layout);
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
            Assert.False(viewer.IsLayoutMenuOpen);
            Assert.Same(viewer, Focused(viewer));

            Assert.True(viewer.ModeViewButton.IsTabStop && viewer.ModeViewButton.Focus(NavigationMethod.Tab));
            SendMenuKey(viewer.ModeViewButton, Key.F10, KeyModifiers.Shift);
            ContextMenu menu = Assert.IsType<ContextMenu>(viewer.LayoutContextMenu);
            Settle(menu);
            foreach (LayoutMenuRow model in viewer.LayoutMenuNow().Interactive)
            {
                MenuItem row = Item(menu, model.Id);
                if (model.Kind == LayoutMenuRowKind.Choice)
                {
                    AssertMenuChip(row, MenuItemToggleType.Radio, null, model.Checked);
                }
                else if (model.Kind == LayoutMenuRowKind.Toggle)
                    AssertMenuChip(row, MenuItemToggleType.CheckBox, keys[model.Id], model.Checked);
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
