using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class MediaPageSelectionLayoutTests
{
    [Theory]
    [InlineData(760)]
    [InlineData(1100)]
    [InlineData(1440)]
    public void CommandsLeadAndClearAndReservedLiveCountTrailWithoutChangingRowHeight(int width)
    {
        using IDisposable platform = AvaloniaTestPlatform.EnterSkia();
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<Avalonia.Platform.ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            Texts.Initialize();
            Texts.SetLanguage("en");
            using MediaTileEnterTests.Fixture fixture = new();
            MediaPage page = fixture.Page;
            // The fixture's bare visual root has no application resource parent. Supply the same
            // control token locally so the product's DynamicResource bindings resolve in this host.
            page.Resources["Workspace.Size.Control"] = 32d;
            typeof(MediaPage).GetField("_statusAttention", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(page, false);
            DockPanel row = page.GetLogicalDescendants().OfType<DockPanel>().Single(control => control.Name == "MediaSelectionRow");
            StackPanel actions = page.GetLogicalDescendants().OfType<StackPanel>().Single(control => control.Name == "MediaSelectionActions");
            TextBlock reason = page.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "MediaSelectionReason");
            ReservedTextBlock count = page.GetLogicalDescendants().OfType<ReservedTextBlock>().Single(control => control.Name == "MediaSelectionStatus");
            Assert.Equal(new[] { "MediaSelectionStatus", "MediaSelectionQueue", "MediaSelectionTag", "MediaSelectionGallery", "MediaSelectionExport", "MediaSelectionMore", "MediaSelectionClear" },
                actions.Children.Select(child => child.Name));
            Assert.Equal(Dock.Left, DockPanel.GetDock(actions));
            Assert.Equal("MediaSelectionReason", row.Children.Last().Name);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(count));

            void Layout()
            {
                foreach (Layoutable child in page.GetVisualDescendants().OfType<Layoutable>().Reverse()) child.InvalidateMeasure();
                page.InvalidateMeasure();
                page.Measure(new Size(width, 900));
                page.Arrange(new Rect(0, 0, width, 900));
            }
            fixture.Grid.Selection.Clear();
            Layout();
            double before = fixture.Grid.TranslatePoint(default, page)!.Value.Y;
            fixture.Grid.SelectedIndex = 0;
            Layout();
            Assert.Equal(32, row.Bounds.Height);
            Assert.Equal(before, fixture.Grid.TranslatePoint(default, page)!.Value.Y);
            Assert.Equal(0, actions.Bounds.X);
            Assert.True(actions.Bounds.Right <= row.Bounds.Right);
            Assert.Equal(Texts.T("media.selection.count", MediaPage.CounterReserveSample(fixture.Rows.Length)), count.ReserveText);
            double reserved = count.DesiredSize.Width;
            fixture.Grid.Selection.SelectRange(0, 2);
            Layout();
            Assert.Equal(reserved, count.DesiredSize.Width);
            Assert.Equal(32, row.Bounds.Height);
            // Exercise digit boundaries on the actual status control with the four-digit
            // reservation used for larger libraries, without manufacturing thousands of tiles.
            count.ReserveText = Texts.T("media.selection.count", MediaPage.CounterReserveSample(1000));
            count.Text = Texts.T("media.selection.count", 1);
            Layout();
            double fourDigitWidth = count.DesiredSize.Width;
            foreach (int selected in new[] { 9, 10, 99, 100, 999, 1000 })
            {
                count.Text = Texts.T("media.selection.count", selected);
                Layout();
                Assert.Equal(fourDigitWidth, count.DesiredSize.Width);
                Assert.Equal(32, row.Bounds.Height);
            }
            fixture.Grid.Selection.Clear();
            Layout();
            Assert.Equal(before, fixture.Grid.TranslatePoint(default, page)!.Value.Y);
        });
    }
}
