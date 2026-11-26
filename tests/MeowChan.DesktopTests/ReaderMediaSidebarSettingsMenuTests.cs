using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderMediaSidebarSettingsMenuTests
{
    [Theory]
    [InlineData("reader", true, "de", "Leisten automatisch ausblenden", "Fakten anzeigen")]
    [InlineData("media", false, "en", "Auto-hide sidebar controls", "Show sidebar facts")]
    public void GearRendersCatalogIconsAndActiveChipsWithAllShortcuts(string keyContext, bool autoHide,
        string language, string autoHideLabel, string factsLabel) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        string previousLanguage = Texts.Language;
        ShortcutBindingsSection previousBindings = ShortcutService.CurrentSection;
        Texts.SetLanguage(language);
        Config saved = new() { MediaSidebarAutoHide = autoHide, MediaSidebarFacts = true };
        int writes = 0;
        ReaderMediaSidebar sidebar = new()
        {
            DecisionKeyContext = keyContext,
            SaveSidebarSetting = update => { update(saved); writes++; },
            SidebarVideoSettings = new QuickSettingsHost
            {
                CurrentConfig = () => saved,
                SaveAsync = update => { update(saved); writes++; return Task.CompletedTask; }
            }
        };
        sidebar.Describe("No. 42", "fixture.jpg", "640 × 480", true);
        sidebar.ConfigureInfo(ReaderMediaPreferences.From(saved));
        Window window = new() { Width = 480, Height = 640, Content = sidebar,
            RequestedThemeVariant = autoHide ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            const string autoHideCommand = "media.sidebar.autohide";
            const string factsCommand = "media.sidebar.info";
            var autoHideAlias = ShortcutGesture.Parse("Primary+Shift+F8");
            var factsAlias = ShortcutGesture.Parse("Primary+Shift+F9");
            var bindings = new ShortcutBindingsSection()
                .Set(autoHideCommand, [ShortcutGesture.Parse("Shift+A"), autoHideAlias])
                .Set(factsCommand, [ShortcutGesture.Parse("Shift+I"), factsAlias]);
            ShortcutService.Publish(bindings);
            window.Show();
            Settle(window);
            var gear = SidePanelDesignTests.ById<ToggleButton>(sidebar, "ReaderMediaSidebarSettings");
            var menu = Assert.IsType<MenuFlyout>(gear.Flyout);
            var mac = EffectiveShortcutBindings.Build(bindings, ShortcutPlatform.MacOS);
            Assert.Equal("⇧A / ⇧⌘F8 (Fn)", CommandCatalog.ShortcutTooltipDisplay(autoHideCommand, macOS: true, keyContext, mac));
            Assert.Equal("⇧I / ⇧⌘F9 (Fn)", CommandCatalog.ShortcutTooltipDisplay(factsCommand, macOS: true, keyContext, mac));
            var displayPlatform = OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows;

            (MenuItem AutoHide, MenuItem Facts) Open()
            {
                Click(gear);
                Assert.True(menu.IsOpen);
                Assert.True(gear.IsChecked);
                Popup popup = Assert.Single(window.OpenedPopups, popup => popup.PlacementTarget == gear);
                Control presenter = Assert.IsAssignableFrom<Control>(popup.Child);
                TopLevel root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(presenter));
                Settle(root);
                MenuItem[] rows = presenter.GetVisualDescendants().OfType<MenuItem>()
                    .Where(row => row.IsEffectivelyVisible && row.Bounds.Width > 0 && row.Bounds.Height > 0).ToArray();
                Assert.Equal(5, rows.Length);
                VerifyVideoCheck(SettingsFieldId.ReaderSidebarVideoAutoplay, "settings.reader.sidebarVideoAutoplay", saved.ReaderSidebarVideoAutoplay);
                VerifyVideoCheck(SettingsFieldId.ReaderSidebarVideoLoop, "settings.reader.sidebarVideoLoop", saved.ReaderSidebarVideoLoop);
                VerifyVideoCheck(SettingsFieldId.ReaderSidebarVideoStartMuted, "settings.reader.sidebarVideoMuted", saved.ReaderSidebarVideoStartMuted);
                return (FindRow("ReaderMediaSidebarSettingsAutoHide", Key.A, autoHideLabel, "eye-off", autoHideAlias),
                    FindRow("ReaderMediaSidebarSettingsFacts", Key.I, factsLabel, "info", factsAlias));

                void VerifyVideoCheck(string field, string labelKey, bool isChecked)
                {
                    MenuItem row = SidePanelDesignTests.ById<MenuItem>(presenter, "ReaderMediaSidebarSettings" + field);
                    Assert.Contains(row, rows);
                    Assert.Equal(MenuItemToggleType.CheckBox, row.ToggleType);
                    Assert.Equal(Texts.T(labelKey), row.Header);
                    Assert.Equal(isChecked, row.IsChecked);
                    Assert.Null(row.InputGesture);
                }

                MenuItem FindRow(string id, Key key, string label, string mask, ShortcutGesture alias)
                {
                    MenuItem row = Assert.Single(rows, item => AutomationProperties.GetAutomationId(item) == id);
                    Assert.Equal(MenuItemToggleType.CheckBox, row.ToggleType);
                    Assert.Equal(new KeyGesture(key, KeyModifiers.Shift), row.InputGesture);
                    Assert.Equal(label, Assert.IsType<string>(row.Header));
                    Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text =>
                        text.Text == label && text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.Bounds.Height > 0);
                    string tip = Assert.IsType<string>(ToolTip.GetTip(row));
                    string primary = ShortcutGesture.Parse("Shift+" + key).Display(displayPlatform);
                    Assert.Equal($"{label} · {primary} / {alias.Display(displayPlatform)}", tip.Split('\n')[0]);
                    Assert.Equal(tip, AutomationProperties.GetHelpText(row));
                    AssertChip(row, mask);
                    return row;
                }
            }

            var initial = Open();
            Assert.Equal(autoHide, initial.AutoHide.IsChecked);
            Assert.True(initial.Facts.IsChecked);
            Click(gear);
            AssertClosed();

            Click(Open().Facts);
            AssertClosed();
            Assert.Equal(1, writes);
            Assert.False(saved.SidebarFactsVisible);
            Assert.False(sidebar.CopyBarSettingsTo(ReaderMediaPreferences.Default).MediaSidebarFacts);

            var changed = Open();
            Assert.False(changed.Facts.IsChecked);
            Click(changed.AutoHide);
            AssertClosed();
            Assert.Equal(2, writes);
            Assert.Equal(!autoHide, saved.MediaSidebarAutoHide);
            Assert.Equal(!autoHide, sidebar.CopyBarSettingsTo(ReaderMediaPreferences.Default).MediaSidebarAutoHide);

            var reopened = Open();
            Assert.Equal(!autoHide, reopened.AutoHide.IsChecked);
            Assert.False(reopened.Facts.IsChecked);
            TopLevel popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(reopened.AutoHide));
            popupRoot.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            popupRoot.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(window);
            AssertClosed();
            Assert.Equal(2, writes);

            void AssertClosed()
            {
                Assert.False(menu.IsOpen);
                Assert.False(gear.IsChecked);
                Assert.DoesNotContain(window.OpenedPopups, popup => popup.PlacementTarget == gear);
            }
        }
        finally
        {
            window.Close();
            ShortcutService.Publish(previousBindings);
            Texts.SetLanguage(previousLanguage);
        }
    });

    private static void AssertChip(MenuItem row, string mask)
    {
        var chip = Assert.Single(row.GetVisualDescendants().OfType<MenuStateChip>());
        var frame = Assert.IsType<Border>(Assert.Single(chip.GetVisualChildren()));
        var icon = Assert.IsType<Border>(row.Icon);
        Assert.Equal(mask, MenuIcons.KeyOf(icon));
        Assert.Contains(icon, frame.GetVisualDescendants());
        Assert.True(icon.IsEffectivelyVisible);
        Assert.InRange(chip.TranslatePoint(default, row)!.Value.X, 15, 17);
        Assert.InRange(frame.Bounds.Width, 23, 25);
        Assert.InRange(frame.Bounds.Height, 23, 25);
        if (row.IsChecked)
        {
            Assert.True(row.TryFindResource("Workspace.AccentSurface", row.ActualThemeVariant, out object? accent));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(accent).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color);
            Assert.NotNull(frame.BorderBrush);
            Assert.Equal(new Thickness(1), frame.BorderThickness);
        }
        else
        {
            Assert.Null(frame.Background);
            Assert.Null(frame.BorderBrush);
        }
    }

    private static void Click(Control control)
    {
        TopLevel root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(control));
        Point point = control.TranslatePoint(new Rect(control.Bounds.Size).Center, root)!.Value;
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Settle(root);
    }

    private static void Settle(TopLevel root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }
}
