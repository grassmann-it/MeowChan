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
        var app = Application.Current!;
        ThemePaletteBase ReadBase(ThemeTokenTheme baseTheme) => ThemeRuntime.ReadBase(app.Styles, baseTheme);
        var model = new ThemeConfiguratorModel(ThemeColorOverrides.Empty, ReadBase);
        model.ApplyPreset(ThemeAccentPresets.Find(presetId)!);
        var runtime = new ThemeRuntimeHost(app.Resources, ReadBase(ThemeTokenTheme.Dark), ReadBase(ThemeTokenTheme.Light));
        runtime.Use(model.Preview(theme).Palette);

        var menu = new ContextMenuBuilder()
            .Check("video.loop", true, () => { })
            .Check("on", "Status", true, () => { })
            .Check("off", "Size", false, () => { })
            .Check("locked-on", "Name", true, () => { }, available: false)
            .Check("locked-off", "Date", false, () => { }, available: false)
            .Build()!;
        var button = new ToggleButton { IsChecked = true, Classes = { "gallery-overflow-item" } };
        var flyoutChip = MenuLayout.CreateFlyoutChip(button, MenuItemToggleType.CheckBox);
        MenuLayout.SetFlyoutIcon(flyoutChip, "repeat");
        var shortcut = new TextBlock { Text = CommandCatalog.ShortcutDisplay("video.loop") };
        button.Content = MenuLayout.CreateFlyoutRow(flyoutChip, new TextBlock { Text = "Repeat video" }, shortcut);
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
            Color menuSurface = BrushColor(menu.Background);
            AssertContrast(IconSlot(item), menuSurface);
            AssertSwitchContrast(Item(menu, "on"), Item(menu, "off"), menuSurface, locked: false);
            AssertSwitchContrast(Item(menu, "locked-on"), Item(menu, "locked-off"), menuSurface, locked: true);
            menu.Close();

            flyout.ShowAt(anchor);
            Settle(button);
            Assert.Equal(ThemeRuntime.Variant(theme), button.ActualThemeVariant);
            Color flyoutSurface = BrushColor(button.FindAncestorOfType<FlyoutPresenter>()!.Background);
            AssertContrast(flyoutChip, flyoutSurface);
            Assert.Equal(ToggleState.On, ControlAutomationPeer.CreatePeerForElement(button)!.GetProvider<IToggleProvider>()!.ToggleState);

            var presenter = Assert.Single(button.GetVisualDescendants().OfType<ContentPresenter>(),
                part => ReferenceEquals(part.TemplatedParent, button) && part.Name == "PART_ContentPresenter");
            Assert.True(shortcut.IsEffectivelyVisible);
            Assert.False(string.IsNullOrWhiteSpace(shortcut.Text));
            AssertShortcutContrast("rest");
            IPseudoClasses states = button.Classes;
            foreach (string state in new[] { ":pointerover", ":focus-visible" })
            {
                states.Set(state, true);
                Settle(button);
                Assert.Equal(ResourceBrush(button, "Workspace.Hover"), Paint(presenter.Background, flyoutSurface));
                AssertShortcutContrast(state);
                states.Set(state, false);
            }
            flyout.Hide();

            void AssertShortcutContrast(string state)
            {
                Color surface = Paint(presenter.Background, flyoutSurface);
                double ratio = Ratio(Paint(shortcut.Foreground, surface, shortcut.Opacity), surface);
                Assert.True(ratio >= 4.5, $"{presetId} {theme}: shortcut {state} {ratio:F2}:1");
            }
        });

        void AssertContrast(MenuStateChip chip, Color surface)
        {
            var paintedChip = Assert.IsType<Border>(Assert.Single(chip.GetVisualChildren()));
            var mask = Assert.IsType<Border>(chip.Content);
            Assert.Contains(mask, paintedChip.GetVisualDescendants());
            Assert.True(mask.IsEffectivelyVisible);
            double frame = Ratio(BrushColor(paintedChip.BorderBrush), surface);
            double icon = Ratio(BrushColor(mask.Background), BrushColor(paintedChip.Background));
            Assert.True(frame >= 3, $"{presetId} {theme}: frame {frame:F2}:1");
            Assert.True(icon >= 4.5, $"{presetId} {theme}: mask {icon:F2}:1");
        }

        void AssertSwitchContrast(MenuItem on, MenuItem off, Color surface, bool locked)
        {
            Assert.Equal(!locked, on.IsEffectivelyEnabled);
            Assert.Equal(!locked, off.IsEffectivelyEnabled);
            var bar = Part<Border>(on, "PART_CheckBar");
            Assert.True(bar.IsEffectivelyVisible);
            Assert.True(bar.Bounds.Width > bar.Bounds.Height && bar.Bounds.Height > 0);
            Assert.False(Part<Border>(off, "PART_CheckBar").IsEffectivelyVisible);
            var onChip = Assert.IsType<Border>(Assert.Single(IconSlot(on).GetVisualChildren()));
            var offChip = Assert.IsType<Border>(Assert.Single(IconSlot(off).GetVisualChildren()));
            Color onInterior = Paint(onChip.Background, surface);
            Color offInterior = Paint(offChip.Background, surface);
            Color ink = Paint(bar.Background, onInterior, bar.Opacity);
            double marker = Ratio(ink, offInterior);
            double markerOnFill = Ratio(ink, onInterior);
            Color onFrame = Paint(onChip.BorderBrush, surface);
            Color offFrame = Paint(offChip.BorderBrush, surface);
            double frames = Ratio(onFrame, offFrame);
            Assert.True(marker >= 3, $"{presetId} {theme} locked={locked}: bar against empty chip {marker:F2}:1");
            Assert.True(markerOnFill >= 3, $"{presetId} {theme} locked={locked}: bar against filled chip {markerOnFill:F2}:1");
            Assert.True(Ratio(offFrame, surface) >= 3, $"{presetId} {theme} locked={locked}: empty frame against menu");
            if (locked)
                Assert.Equal(1, frames);
            else
                Assert.True(frames >= 3, $"{presetId} {theme}: checked against unchecked frame {frames:F2}:1");
        }
    });

    private static Color Paint(IBrush? brush, Color backdrop, double opacity = 1)
    {
        if (brush == null) return backdrop;
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        return Color.Parse(ContrastRules.Blend(ThemeColorMath.Format(solid.Color), ThemeColorMath.Format(backdrop),
            solid.Color.A / 255d * solid.Opacity * opacity));
    }

    private static double Ratio(Color a, Color b) => ContrastRules.Contrast(ThemeColorMath.Format(a), ThemeColorMath.Format(b));
}
