using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

public sealed partial class MenuIconLayoutTests
{
    [Fact]
    public void TheMenuTokensStyleAndGapAreDefinedOnTheEightDipGrid() => OnUi(() =>
    {
        var menu = new ContextMenuBuilder()
            .GroupHeading("heading", "Columns")
            .Item("plain", "Reset", () => { }).Build()!;
        var flyout = new MenuFlyout();
        var anchor = new Button { ContextMenu = menu, Flyout = flyout };
        WithWindow(anchor, _ =>
        {
            menu.Open(anchor);
            Settle(menu);
            Assert.True(MenuLayout.GetIsManaged(menu));
            Assert.Equal(MenuLayout.DefaultMinimumWidth, menu.MinWidth);
            Assert.Equal(MenuLayout.DefaultMaximumWidth, menu.MaxWidth);
            MenuItem plain = Item(menu, "plain");
            MenuItem heading = Item(menu, "heading");
            Assert.Equal(32, plain.MinHeight);
            AssertAt(16, LabelOf(plain), plain);
            AssertAt(16, LabelOf(heading), heading);
            Assert.False(IconSlot(plain).IsEffectivelyVisible);
            Assert.False(heading.Focusable);
            Assert.False(heading.IsEnabled);
            Assert.True(plain.TryFindResource("MenuItemInputGestureTextMargin", out object? inset));
            Assert.Equal(MenuLayout.MinimumShortcutGap, Assert.IsType<Thickness>(inset).Left);
            foreach (double size in new[] { menu.MinWidth, menu.MaxWidth, MenuLayout.ChipColumn,
                MenuLayout.ColumnGap, MenuLayout.RowInset, MenuLayout.SettingHeight, MenuLayout.SettingControlWidth })
                Assert.Equal(0, size % 8);
            menu.Close();

            var row = new MenuItem { Header = "Plain" };
            flyout.Items.Add(row);
            flyout.ShowAt(anchor);
            Settle(row);
            Assert.True(MenuLayout.GetIsManaged(Assert.IsType<MenuFlyoutPresenter>(row.FindAncestorOfType<MenuBase>())));
            flyout.Hide();
        });
    });

    [Fact]
    public void IconlessSwitchesKeepAnOutlineWhileSelectionAndPressLeaveTheirStateAlone() => OnUi(() =>
    {
        var menu = new ContextMenuBuilder()
            .Check("title", "Title", true, () => { })
            .Check("size", "Size", false, () => { })
            .Check("status", "Status", true, () => { }, available: false, reason: "Required")
            .Item("reset", "Reset", () => { }).Build()!;
        var anchor = new Button { ContextMenu = menu };
        WithWindow(anchor, _ =>
        {
            menu.Open(anchor);
            Settle(menu);
            var on = Item(menu, "title");
            var off = Item(menu, "size");
            AssertMenuChip(on, MenuItemToggleType.CheckBox, null, isChecked: true);
            AssertMenuChip(off, MenuItemToggleType.CheckBox, null, isChecked: false);
            AssertMenuChip(Item(menu, "status"), MenuItemToggleType.CheckBox, null, isChecked: true);
            Assert.Contains("Required", Assert.IsType<string>(ToolTip.GetTip(Item(menu, "status"))));
            AssertAt(48, LabelOf(Item(menu, "reset")), Item(menu, "reset"));
            MenuStateChip chip = IconSlot(off);
            IBrush? outline = chip.BorderBrush;
            ((IPseudoClasses)off.Classes).Set(":selected", true);
            ((IPseudoClasses)off.Classes).Set(":pressed", true);
            Settle(off);
            Assert.Same(outline, chip.BorderBrush);
            Assert.Null(chip.Background);
            Assert.False(off.IsChecked);
            menu.Close();
        });
    });

    [Theory, MemberData(nameof(ThemeAccentPresetTests.PresetsAndThemes), MemberType = typeof(ThemeAccentPresetTests))]
    public void ResolvedActiveChipAndMaskMeetContrastInEveryPreset(string presetId, string themeName) => OnUi(() =>
    {
        var theme = Enum.Parse<ThemeTokenTheme>(themeName);
        var model = new ThemeConfiguratorModel(ThemeColorOverrides.Empty, ThemePaletteTests.Base);
        model.ApplyPreset(ThemeAccentPresets.Find(presetId)!);
        var runtime = new ThemeRuntimeHost(Application.Current!.Resources,
            ThemePaletteTests.Base(ThemeTokenTheme.Dark), ThemePaletteTests.Base(ThemeTokenTheme.Light));
        runtime.Use(model.Preview(theme).Palette);

        var menu = new ContextMenuBuilder().Check("video.loop", true, () => { }).Build()!;
        var button = new ToggleButton { IsChecked = true, Classes = { "gallery-overflow-item" } };
        var flyoutChip = MenuLayout.CreateFlyoutChip(button, MenuItemToggleType.CheckBox);
        MenuLayout.SetFlyoutIcon(flyoutChip, "repeat");
        button.Content = MenuLayout.CreateFlyoutRow(flyoutChip, new TextBlock { Text = "Repeat video" }, new TextBlock());
        var flyout = new Flyout { Content = button, FlyoutPresenterClasses = { "gallery-overflow" } };
        var anchor = new Button { ContextMenu = menu, Flyout = flyout };
        WithWindow(anchor, window =>
        {
            window.RequestedThemeVariant = ThemeRuntime.Variant(theme);
            menu.Open(anchor);
            Settle(menu);
            MenuItem item = Item(menu, "video.loop");
            Assert.Equal(ThemeRuntime.Variant(theme), item.ActualThemeVariant);
            Assert.Equal(CommandCatalog.ShortcutDisplay("video.loop"), Part<TextBlock>(item, "PART_InputGestureText").Text);
            AssertContrast(IconSlot(item), BrushColor(menu.Background));
            menu.Close();

            flyout.ShowAt(anchor);
            Settle(button);
            Assert.Equal(ThemeRuntime.Variant(theme), button.ActualThemeVariant);
            AssertContrast(flyoutChip, BrushColor(button.FindAncestorOfType<FlyoutPresenter>()!.Background));
            Assert.Equal(ToggleState.On, ControlAutomationPeer.CreatePeerForElement(button)!.GetProvider<IToggleProvider>()!.ToggleState);
            flyout.Hide();
        });

        void AssertContrast(MenuStateChip chip, Color surface)
        {
            var mask = Assert.IsType<Border>(chip.Content);
            double frame = Ratio(BrushColor(chip.BorderBrush), surface);
            double icon = Ratio(BrushColor(mask.Background), BrushColor(chip.Background));
            Assert.True(frame >= 3, $"{presetId} {theme}: frame {frame:F2}:1");
            Assert.True(icon >= 4.5, $"{presetId} {theme}: mask {icon:F2}:1");
        }
    });

    private static double Ratio(Color a, Color b) => ContrastRules.Contrast(ThemeColorMath.Format(a), ThemeColorMath.Format(b));
}
