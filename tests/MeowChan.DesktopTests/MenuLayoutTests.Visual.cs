using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed partial class MenuIconLayoutTests
{
    [Theory]
    [InlineData("de", "Bedienelemente beim Öffnen der Galerie über das aktuell ausgewählte Medium legen")]
    [InlineData("ja", "ギャラリーで現在選択しているメディアの上に操作ボタンを表示する")]
    [InlineData("zh-Hans", "在图库中打开当前选中的媒体时将操作按钮显示在媒体上方")]
    public void ContextMenuTogglesAndSubmenuKeepActionIconsBesideTheSemiMarks(string language, string phrase) => OnUi(() =>
    {
        Texts.SetLanguage(language);
        string label = phrase + " · " + phrase;
        bool enabled = false, second = false;
        ContextMenuBuilder Compose()
        {
            var choices = new ContextMenuBuilder()
                .Radio("mode", "first", Texts.T("gallery.layout.filmstripTop"), !second, () => second = false)
                .Radio("mode", "second", Texts.T("gallery.layout.filmstripBottom"), second, () => second = true, icon: "columns");
            var builder = new ContextMenuBuilder()
                .Check("overlay", label, enabled, () => enabled = !enabled, icon: "sliders")
                .Check("unavailable", Texts.T("gallery.layout.tools"), true, () => throw new InvalidOperationException(),
                    available: false, reason: Texts.T("gallery.layout.whyTag"), icon: "tool")
                .Submenu("mode", Texts.T("gallery.layout.filmstrip"), choices.ToModel(), icon: "columns");
            Assert.Equal("sliders", builder.ToModel().Entries.First().Icon);
            Assert.Equal("columns", choices.ToModel().Entries.Last().Icon);
            return builder;
        }

        var anchor = new Button { Content = "Menu", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        anchor.PropertyChanged += (_, change) =>
        {
            if (change.Property == Control.ContextMenuProperty && anchor.ContextMenu is { } menu)
                Item(menu, "overlay").InputGesture = new KeyGesture(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift);
        };
        using var host = new ContextMenuHost(anchor, Compose);
        WithWindow(anchor, _ =>
        {
            Assert.True(anchor.Focus(NavigationMethod.Tab));
            SendMenuKey(anchor, Key.F10, KeyModifiers.Shift);
            ContextMenu menu = Assert.IsType<ContextMenu>(host.OpenMenu);
            MenuItem check = Item(menu, "overlay");
            MenuItem unavailable = Item(menu, "unavailable");
            MenuItem submenu = Item(menu, "mode");
            AssertSemi(check, MenuItemToggleType.CheckBox, "sliders", isChecked: false);
            AssertSemi(unavailable, MenuItemToggleType.CheckBox, "tool", isChecked: true);
            Assert.False(unavailable.IsEffectivelyEnabled);
            Assert.Contains(Texts.T("gallery.layout.whyTag"), Assert.IsType<string>(ToolTip.GetTip(unavailable)));
            AssertLongLabel(check, LabelOf(check), Part<TextBlock>(check, "PART_InputGestureText"), label);

            Assert.True(submenu.Focus(NavigationMethod.Directional));
            Assert.False(submenu.IsSubMenuOpen);
            SendMenuKey(submenu, Key.Right);
            Assert.True(submenu.IsSubMenuOpen);
            MenuItem first = Item(submenu, "first"), radio = Item(submenu, "second");
            Assert.Null(MenuIcons.KeyOf(first)); // Filmstrip positions are values in a choice group.
            AssertSemi(radio, MenuItemToggleType.Radio, "columns", isChecked: false);
            Assert.True(radio.Focus(NavigationMethod.Directional));
            SendMenuKey(radio, Key.Enter);
            Assert.True(second);
            Assert.False(menu.IsOpen);
            Assert.True(anchor.IsFocused);

            SendMenuKey(anchor, Key.F10, KeyModifiers.Shift);
            menu = Assert.IsType<ContextMenu>(host.OpenMenu);
            check = Item(menu, "overlay");
            submenu = Item(menu, "mode");
            Assert.True(submenu.Focus(NavigationMethod.Directional));
            SendMenuKey(submenu, Key.Right);
            AssertSemi(Item(submenu, "second"), MenuItemToggleType.Radio, "columns", isChecked: true);
            SendMenuKey(Item(submenu, "first"), Key.Left);
            Assert.False(submenu.IsSubMenuOpen);
            Assert.True(check.Focus(NavigationMethod.Directional));
            SendMenuKey(check, Key.Enter);
            Assert.True(enabled);
            Assert.True(anchor.IsFocused);

            SendMenuKey(anchor, Key.F10, KeyModifiers.Shift);
            menu = Assert.IsType<ContextMenu>(host.OpenMenu);
            AssertSemi(Item(menu, "overlay"), MenuItemToggleType.CheckBox, "sliders", isChecked: true);
            host.Close();
        });

    });

    [Fact]
    public void GalleryMenusKeepTheirMarksIconsAndFocusAcrossAllBundledLanguages() => OnUi(() =>
        BooruComponentDispatcher.Wait(ExerciseGalleryAsync()));

    [Fact]
    public void LayoutFlyoutTrimsLongLabelsAndKeepsTheShortcutWholeAt150Percent() => OnUi(() =>
    {
        const string id = "gallery.filmstrip-toggle";
        string german = string.Concat(Enumerable.Repeat("Filmstreifen für das aktuell angezeigte Medium einblenden ", 3));
        string cjk = string.Concat(Enumerable.Repeat("現在選択しているメディアのフィルムストリップを表示する", 3));
        string label = german;
        string key = "tool";
        var panel = new LayoutMenuPanel("Layout", () => new("Layout",
        [
            new(LayoutMenuRowKind.Toggle, id, label) { CommandId = id, IconKey = key },
            new(LayoutMenuRowKind.Toggle, "dynamic-column", "Original title") // Profile-defined column name, no stable action mask.
        ]), _ => { });
        var flyout = new Flyout { Content = panel, FlyoutPresenterClasses = { "gallery-overflow" } };
        var anchor = new Button { Content = "Layout", Flyout = flyout };
        ShortcutService.Publish(new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("Primary+Shift+F7")]));
        WithWindow(anchor, window =>
        {
            window.SetRenderScaling(1.5);
            Assert.True(anchor.Focus(NavigationMethod.Tab));
            flyout.Opening += (_, _) => panel.Refresh();
            Press(anchor, Key.Enter);
            Assert.True(flyout.IsOpen);
            TopLevel.GetTopLevel(panel)!.SetRenderScaling(1.5);
            Assert.Equal(1.5, TopLevel.GetTopLevel(panel)!.RenderScaling);
            var row = Assert.IsType<ToggleButton>(panel.ButtonOf(id));
            Assert.True(panel.FocusFirst());
            Settle(row);
            Assert.True(row.IsFocused);
            var grid = Assert.IsType<Grid>(row.Content);
            var text = Assert.IsType<StackPanel>(grid.Children.Single(child => Grid.GetColumn(child) == 2));
            var shortcut = Assert.IsType<TextBlock>(grid.Children.Single(child => Grid.GetColumn(child) == 3));
            var labelBlock = Assert.IsType<TextBlock>(text.Children[0]);
            Control firstIcon = AssertFlyout(row, "tool", isChecked: false);
            Assert.Null(IconSlot(panel.ButtonOf("dynamic-column")!).Content);
            AssertLongLabel(row, labelBlock, shortcut, german);
            Assert.Equal(CommandCatalog.ShortcutDisplay(id), shortcut.Text);
            Assert.Equal("⇧⌘F7 (Fn)", CommandCatalog.ShortcutDisplay(id, macOS: true));

            label = cjk;
            key = "image";
            panel.Refresh();
            Settle(row);
            Assert.True(row.IsFocused);
            AssertLongLabel(row, labelBlock, shortcut, cjk);
            Assert.NotSame(firstIcon, AssertFlyout(row, "image", isChecked: false));
            flyout.Hide();
        });
    });

    private static async Task ExerciseGalleryAsync()
    {
        await using var viewer = new GalleryViewer();
        viewer.ApplyWorkView(WorkView.Advanced);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: true, AutoHideControls: false, VideoRepeat: false));
        viewer.SetPosition("1 of 8", true, true);
        WithWindow(viewer, _ =>
        {
            string[] languages = ["en", "de", "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans"];
            Button more = viewer.OverflowButton;
            Assert.True(more.Focus(NavigationMethod.Tab));
            Press(more, Key.Enter);
            Assert.True(viewer.IsOverflowOpen);
            Assert.Contains(viewer.OverflowMenuContent, Assert.IsAssignableFrom<Visual>(Focused(more)).GetVisualAncestors());

            ToggleButton repeat = Assert.IsType<ToggleButton>(Assert.Single(viewer.OverflowItems, row => row.Name == "GalleryVideoRepeatToggle"));
            var entry = Assert.IsType<GalleryViewer.OverflowItem>(repeat.Tag);
            Assert.Equal("repeat", entry.IconKey);
            Control repeatIcon = AssertFlyout(repeat, "repeat", isChecked: false);
            Assert.True(repeat.Focus(NavigationMethod.Directional));
            Press(repeat, Key.Space);
            Assert.True(viewer.IsOverflowOpen);
            Assert.True(repeat.IsFocused);
            Assert.Same(repeatIcon, AssertFlyout(repeat, "repeat", isChecked: true));
            foreach (string language in languages)
            {
                Texts.SetLanguage(language);
                Settle(repeat);
                string label = GalleryPlaybackCatalog.Get("viewer.playback.repeat");
                Assert.Equal(language, Texts.Language);
                Assert.Equal(label, entry.Label.Text);
                Assert.Equal(label, AutomationProperties.GetName(repeat));
                Assert.Equal(CommandCatalog.ShortcutDisplay("video.loop"), entry.Gesture.Text);
                AssertShortcut(repeat, entry.Label, entry.Gesture);
                var viewport = Assert.IsType<ScrollContentPresenter>(viewer.OverflowMenuContent.FindAncestorOfType<ScrollContentPresenter>());
                foreach (Button row in viewer.OverflowItems.Where(row => row.IsEffectivelyVisible))
                    if (row.Tag is GalleryViewer.OverflowItem item && item.Gesture.Text is { Length: > 0 })
                        Assert.True(RelativeBounds(item.Gesture, viewport).Right <= viewport.Bounds.Width + 1, item.Id);
                Assert.Same(repeatIcon, AssertFlyout(repeat, "repeat", isChecked: true));
            }
            Press(repeat, Key.Escape);
            Assert.False(viewer.IsOverflowOpen);
            Assert.True(more.IsFocused);

            Button layout = viewer.LayoutButton;
            Assert.True(layout.Focus(NavigationMethod.Tab));
            Press(layout, Key.Enter);
            Assert.True(viewer.IsLayoutMenuOpen);
            LayoutMenuPanel panel = viewer.LayoutMenuRows;
            var filmstrip = Assert.IsType<ToggleButton>(panel.ButtonOf(GalleryLayoutMenu.FilmstripId));
            foreach (string language in languages)
            {
                Texts.SetLanguage(language);
                Settle(filmstrip);
                Assert.Same(filmstrip, panel.ButtonOf(GalleryLayoutMenu.FilmstripId));
                var model = panel.Model!.Find(GalleryLayoutMenu.FilmstripId)!;
                Assert.Equal(Texts.T("gallery.layout.filmstrip"), AutomationProperties.GetName(filmstrip));
                Assert.Equal("film", model.ActionIcon);
                Assert.True(filmstrip.IsFocused);
                Control filmIcon = AssertFlyout(filmstrip, "film", model.Checked);
                Press(filmstrip, Key.Space);
                Assert.True(viewer.IsLayoutMenuOpen);
                Assert.True(filmstrip.IsFocused);
                Assert.Same(filmIcon, AssertFlyout(filmstrip, "film", !model.Checked));
                Button dock = Assert.IsType<ToggleButton>(panel.ButtonOf(GalleryLayoutMenu.FilmstripTopId));
                Assert.Null(IconSlot(dock).Content);
                if (!filmstrip.IsChecked.GetValueOrDefault())
                {
                    Assert.False(dock.IsEffectivelyEnabled);
                    Assert.Contains(Texts.T("gallery.layout.whyFilmstripOff"), Assert.IsType<string>(ToolTip.GetTip(dock)));
                }
                Press(filmstrip, Key.Down);
                Assert.Equal(filmstrip.IsChecked == true ? dock : panel.ButtonOf(GalleryLayoutMenu.ToolsId), Focused(filmstrip));
                Press((Control)Focused(filmstrip)!, Key.Home);
            }
            Press(filmstrip, Key.Escape);
            Assert.False(viewer.IsLayoutMenuOpen);
            Assert.True(layout.IsFocused);

            viewer.ApplyWorkView(WorkView.Barebones);
            Settle(viewer);
            Assert.True(more.Focus(NavigationMethod.Tab));
            Press(more, Key.Enter);
            var slideshow = Assert.Single(viewer.OverflowItems, row => row.Name == "GallerySlideshowToggle");
            var slideshowEntry = Assert.IsType<GalleryViewer.OverflowItem>(slideshow.Tag);
            AssertMask(slideshow, Assert.IsType<Border>(slideshowEntry.Icon.Content), "play");
            AssertBefore(Assert.IsType<Border>(slideshowEntry.Icon.Content), slideshowEntry.Label, slideshow, 8);
            viewer.SetOverflowOpen(false);
        });
    }

    private static Border AssertFlyout(ToggleButton row, string key, bool isChecked)
    {
        var grid = Assert.IsType<Grid>(row.Content);
        Assert.Equal(isChecked, row.IsChecked);
        Assert.Equal(new GridLength(16), grid.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(16), grid.ColumnDefinitions[1].Width);
        Control mark = Assert.Single(grid.Children, child => Grid.GetColumn(child) == 0);
        Border icon = Assert.IsType<Border>(IconSlot(row).Content);
        Assert.Equal(isChecked ? 1 : 0, mark.Opacity);
        Assert.InRange(RelativeBounds(mark, row).Width, 15, 17);
        AssertMask(row, icon, key);
        AssertBefore(mark, icon, row, 8);
        AssertBefore(icon, Assert.Single(grid.Children, child => Grid.GetColumn(child) == 2), row, 8);
        return icon;
    }

    private static ContentControl IconSlot(Button row) =>
        Assert.IsType<ContentControl>(Assert.Single(Assert.IsType<Grid>(row.Content).Children, child => Grid.GetColumn(child) == 1));

    private static void AssertSemi(MenuItem row, MenuItemToggleType kind, string key, bool isChecked)
    {
        Assert.Equal(kind, row.ToggleType);
        Assert.Equal(isChecked, row.IsChecked);
        Assert.Equal(key, MenuIcons.KeyOf(row));
        var icon = Assert.IsType<Border>(row.Icon);
        Assert.Contains(icon, row.GetVisualDescendants());
        AssertMask(row, icon, key);
        Control? mark = row.GetVisualDescendants().OfType<Control>().FirstOrDefault(child => child.Name == "PART_ToggleIconPresenter");
        bool visible = mark is { IsEffectivelyVisible: true, Opacity: > 0, Bounds.Width: > 0 }
            && mark is not ContentPresenter { Child: null }
            && mark is not ContentControl { Content: null };
        Assert.Equal(isChecked, visible);
        if (isChecked) AssertBefore(mark!, icon, row);
        AssertBefore(icon, LabelOf(row), row);
    }

    private static void AssertMask(Control row, Border icon, string key)
    {
        Assert.Equal(key, MenuIcons.KeyOf(icon));
        Assert.Same(CommandBindings.IconMask(key), Assert.IsType<ImageBrush>(icon.OpacityMask).Source);
        Assert.NotNull(icon.Background);
        Assert.True(icon.IsEffectivelyVisible);
        Rect bounds = RelativeBounds(icon, row);
        Assert.InRange(bounds.Width, 15, 17);
        Assert.InRange(bounds.Height, 15, 17);
    }

    private static void AssertLongLabel(Control row, TextBlock label, TextBlock shortcut, string fullText)
    {
        Assert.Equal(fullText, label.Text);
        Assert.Equal(fullText, AutomationProperties.GetName(row));
        Assert.Contains(fullText, Assert.IsType<string>(ToolTip.GetTip(row)));
        Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        Assert.True(MenuLayout.TextWidth(label, fullText) > label.Bounds.Width);
        AssertShortcut(row, label, shortcut);
    }

    private static void AssertShortcut(Control row, TextBlock label, TextBlock shortcut)
    {
        Assert.False(string.IsNullOrWhiteSpace(shortcut.Text));
        AssertBefore(label, shortcut, row, MenuLayout.MinimumShortcutGap);
        Assert.True(MenuLayout.TextWidth(shortcut, shortcut.Text!) <= shortcut.Bounds.Width + 1);
        Assert.True(RelativeBounds(shortcut, row).Right <= row.Bounds.Width + 1);
    }

    private static void AssertBefore(Control left, Control right, Control row, double gap = 0) =>
        Assert.True(RelativeBounds(left, row).Right + gap <= RelativeBounds(right, row).Left + 1);

    private static Rect RelativeBounds(Control part, Control row) =>
        new(part.TranslatePoint(default, row)!.Value, part.Bounds.Size);

    private static T Part<T>(MenuItem row, string name) where T : Control =>
        Assert.Single(row.GetVisualDescendants().OfType<T>(), child => child.Name == name);

    private static TextBlock LabelOf(MenuItem row) => Assert.IsAssignableFrom<TextBlock>(Part<ContentPresenter>(row, "PART_HeaderPresenter").Child);

    private static MenuItem Item(ItemsControl menu, string tag) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), row => Equals(row.Tag, tag));

    private static void OnUi(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        string language = Texts.Language;
        Texts.Initialize();
        try { test(); }
        finally { Texts.SetLanguage(language); }
    });

    private static void WithWindow(Control content, Action<Window> test)
    {
        var window = new Window { Width = 1440, Height = 900, Content = content, Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            Settle(window);
            test(window);
        }
        finally { window.Close(); }
    }

    private static void Settle(Control control)
    {
        Dispatcher.UIThread.RunJobs();
        TopLevel.GetTopLevel(control)?.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static IInputElement? Focused(Control control) => TopLevel.GetTopLevel(control)?.FocusManager?.GetFocusedElement();

    private static void SendMenuKey(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Settle(target);
    }

    private static void Press(Control control, Key key)
    {
        var top = TopLevel.GetTopLevel(control)!;
        top.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        top.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle(control);
    }
}
