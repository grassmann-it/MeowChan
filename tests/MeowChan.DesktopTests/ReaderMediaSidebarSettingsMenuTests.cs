using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderMediaSidebarSettingsMenuTests
{
    [Theory]
    [InlineData("reader", true)]
    [InlineData("media", false)]
    public void GearRendersTwoChecksThatSaveAndCanBeDismissed(string keyContext, bool autoHide) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        string previousLanguage = Texts.Language;
        Texts.SetLanguage("en");
        Config saved = new() { MediaSidebarAutoHide = autoHide, MediaSidebarFacts = true };
        int writes = 0;
        ReaderMediaSidebar sidebar = new()
        {
            DecisionKeyContext = keyContext,
            SaveSidebarSetting = update => { update(saved); writes++; }
        };
        sidebar.Describe("No. 42", "fixture.jpg", "640 × 480", true);
        sidebar.ConfigureInfo(ReaderMediaPreferences.From(saved));
        Window window = new() { Width = 480, Height = 640, Content = sidebar, RequestedThemeVariant = ThemeVariant.Dark };
        try
        {
            window.Show();
            Settle(window);
            var gear = SidePanelDesignTests.ById<ToggleButton>(sidebar, "ReaderMediaSidebarSettings");
            var menu = Assert.IsType<MenuFlyout>(gear.Flyout);

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
                Assert.True(rows.Length == 2, $"Expected two laid-out menu rows, found {rows.Length} with {menu.Items.Count} flyout entries.");
                return (FindRow("ReaderMediaSidebarSettingsAutoHide", Key.A), FindRow("ReaderMediaSidebarSettingsFacts", Key.I));

                MenuItem FindRow(string id, Key key)
                {
                    MenuItem row = Assert.Single(rows, item => AutomationProperties.GetAutomationId(item) == id);
                    Assert.Equal(MenuItemToggleType.CheckBox, row.ToggleType);
                    Assert.Equal(new KeyGesture(key, KeyModifiers.Shift), row.InputGesture);
                    string label = Assert.IsType<string>(row.Header);
                    Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text =>
                        text.Text == label && text.IsEffectivelyVisible && text.Bounds.Width > 0 && text.Bounds.Height > 0);
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
            Texts.SetLanguage(previousLanguage);
        }
    });

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
