using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderMediaSidebarLayoutTests
{
    private static void OnDispatcher(Action test)
    {
        using IDisposable platform = AvaloniaTestPlatform.EnterSkia();
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            Texts.Initialize();
            Texts.SetLanguage("en");
            try { test(); }
            finally { Texts.SetLanguage("en"); }
        });
    }

    private static ArchivePage Page() => new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });

    [Fact]
    public void DefaultToolbarStateMatchesTheDefaultScopeWithoutChangingPreferences() => OnDispatcher(() =>
    {
        ArchivePage page = Page();
        try
        {
            ToggleButton button = page.FindControl<ToggleButton>("ReaderMediaSidebarToggle")!;
            Assert.True(button.IsVisible);
            Assert.True(button.IsEnabled);
            Assert.False(button.IsChecked);
            Assert.Equal("Media", button.Content);
            Assert.Equal("Media sidebar: Off", AutomationProperties.GetName(button));
            Assert.StartsWith("Media sidebar", Assert.IsType<string>(ToolTip.GetTip(button)));
            ToggleButton threads = page.FindControl<ToggleButton>("ArchiveThreadListToggle")!;
            Assert.Equal("Threads", threads.Content);
            Assert.Equal("panel-left", IconButtons.KeyOf(threads));
            Assert.Equal("Thread list: On", AutomationProperties.GetName(threads));
            Assert.StartsWith("Hide the thread list", Assert.IsType<string>(ToolTip.GetTip(threads)));
            // 071e43347 deliberately registered this tool as Direct in every work
            // view. Direct tools stay discoverable but overflow when they do not fit.
            var policy = MeowChan.Desktop.WorkViews.WorkViewPolicy.Find("reader.toolbar.media-sidebar")!;
            Assert.Equal(MeowChan.Desktop.WorkViews.Presence.Direct, policy.Barebones);
            Assert.Equal(policy.Barebones, policy.Simple);
            Assert.Equal(policy.Simple, policy.Advanced);
            Assert.Equal(Ursa.Controls.OverflowMode.AsNeeded, Ursa.Controls.ToolBar.GetOverflowMode(button));
            Assert.Equal(ReaderMediaPreferences.From(new Config()), page.ReaderMedia);
            Assert.True(ReaderMediaSidebarRules.IsOn(page.ReaderMedia, largeReader: true));
            Assert.False(ReaderMediaSidebarRules.IsOn(page.ReaderMedia, largeReader: false));
            Assert.False(page.IsMediaSidebarShown);
            Texts.SetLanguage("pl");
            Assert.Equal("Multimedia", button.Content);
            Assert.Equal("Wątki", threads.Content);
            Assert.StartsWith("Panel boczny mediów", AutomationProperties.GetName(button));
        }
        finally { page.EndLifetime(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolbarShortcutAndCloseShareThePersistedPerHostToggle(bool largeReader) => OnDispatcher(() =>
    {
        ArchivePage page = Page();
        ArchivePage restored = Page();
        FieldInfo large = typeof(ArchivePage).GetField("_largeReader", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Config saved = new() { ReaderMediaSidebarSide = "Right", ReaderMediaSidebarWidth = 620 };
        int writes = 0;
        try
        {
            large.SetValue(page, largeReader);
            page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(saved));
            page.SaveReaderMediaSwitches = update => { update(saved); writes++; return Task.CompletedTask; };
            ToggleButton button = page.FindControl<ToggleButton>("ReaderMediaSidebarToggle")!;
            Assert.Equal("panel-right", IconButtons.KeyOf(button));
            bool initial = largeReader;
            Assert.Equal(initial, button.IsChecked);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(!initial, button.IsChecked);
            Assert.Equal("Media sidebar: " + (!initial ? "On" : "Off"), AutomationProperties.GetName(button));
            Assert.Equal(1, writes);
            Assert.Equal(page.ReaderMedia.Sidebar, saved.ReaderMediaSidebar);
            Assert.Equal(page.ReaderMedia.SidebarAlways, saved.ReaderMediaSidebarScope == Config.ReaderMediaSidebarScopeAlways);
            // Reload a serialized preference snapshot, not just the in-memory value.
            restored.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(saved))!));
            Assert.Equal(page.ReaderMedia, restored.ReaderMedia);
            Assert.True(page.ToggleReaderViewOption("reader.sidebar.toggle"));
            Assert.Equal(initial, button.IsChecked);
            Assert.Equal(2, writes);
            if (!initial) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            page.CloseMediaSidebar();
            Assert.False(button.IsChecked);
            Assert.False(ReaderMediaSidebarRules.IsOn(ReaderMediaPreferences.From(saved), largeReader));
            Assert.Equal("Right", saved.ReaderMediaSidebarSide);
            Assert.Equal(620, saved.ReaderMediaSidebarWidth);
            Assert.True(saved.ReaderMediaSidebar || largeReader); // Embedded Off preserves the large reader's choice.
        }
        finally { large.SetValue(page, false); page.EndLifetime(); restored.EndLifetime(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SidebarUsesItsHostHeightIndependentlyOfPosts(bool largeReader, bool right) => OnDispatcher(() =>
    {
        ArchivePage page = Page();
        FieldInfo large = typeof(ArchivePage).GetField("_largeReader", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        Control root = page.FindControl<Grid>("ArchiveRoot")!;
        static void Layout(Control root, Size size)
        {
            // No window/layout manager in this fixture: settle the real visual tree.
            for (int pass = 0; pass < 4; pass++)
            {
                foreach (Layoutable child in root.GetVisualDescendants().OfType<Layoutable>().Reverse()) child.InvalidateMeasure();
                root.InvalidateMeasure();
                root.Measure(size);
                root.Arrange(new Rect(size));
            }
        }
        try
        {
            page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(new Config
            {
                ReaderMediaSidebar = true,
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways,
                ReaderMediaSidebarSide = right ? "Right" : "Left",
                ReaderMediaSidebarMinimumWidth = Config.ReaderMediaSidebarMinimumWidthMinimum
            }));
            if (largeReader)
            {
                ((Grid)pane.Parent!).Children.Remove(pane);
                large.SetValue(page, true);
                root = (Control)typeof(ArchivePage).GetMethod("WrapLargeReader", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, new object[] { pane })!;
            }
            Size[] geometries = { new(1440, 720), new(1440, 900), new(1920, 1080) };
            Layout(root, geometries[0]);
            Assert.True(page.IsMediaSidebarShown);
            ReaderMediaSidebar sidebar = page.MediaSidebar!;
            Grid host = (Grid)sidebar.Parent!;
            ToggleButton toggle = page.FindControl<ToggleButton>("ReaderMediaSidebarToggle")!;
            Assert.Equal(right ? "panel-right" : "panel-left", IconButtons.KeyOf(toggle));
            Assert.Equal(right ? 2 : 0, Grid.GetColumn(sidebar));
            Assert.Equal(largeReader ? 1 : 2, Grid.GetRowSpan(sidebar));
            foreach (bool withChrome in new[] { false, true })
            {
                if (withChrome)
                {
                    sidebar.ShowStrip(3, 0, 3, index => "file" + index, samePost: false);
                    sidebar.VideoRow.Children.Add(new TextBlock { Text = "Transport" });
                }
                double previousRatio = 0, previousFrameHeight = 0, previousHostHeight = 0;
                double? chromeHeight = null;
                foreach (Size size in geometries)
                {
                    // First measure the normal stretching neighbour, then make it short.
                    // Its content, scroll extent and header rows must not size the media.
                    page.ReaderBox.MaxHeight = double.PositiveInfinity;
                    page.ReaderBox.VerticalAlignment = VerticalAlignment.Stretch;
                    Layout(root, size);
                    double independentFrameHeight = sidebar.Frame.Bounds.Height;
                    page.ReaderBox.MaxHeight = size.Height / 2;
                    page.ReaderBox.VerticalAlignment = VerticalAlignment.Top;
                    Layout(root, size);
                    Assert.True(page.ReaderBox.Bounds.Height < host.Bounds.Height / 2);
                    Assert.Equal(independentFrameHeight, sidebar.Frame.Bounds.Height);
                    Assert.True(host.Bounds.Height > 0);
                    Assert.Equal(host.Bounds.Height, sidebar.Bounds.Height);
                    Assert.Equal(0, sidebar.TranslatePoint(default, host)!.Value.Y);
                    Assert.Equal(0, sidebar.Padding.Top);
                    Assert.Equal(0, sidebar.Padding.Bottom);
                    Assert.Equal(ReaderMediaSidebar.HeaderLineHeight, sidebar.HeaderRow.Bounds.Height);
                    Assert.Equal(8, sidebar.HeaderRow.TranslatePoint(default, sidebar)!.Value.Y);
                    Assert.Equal(sidebar.Bounds.Height - 8 - ReaderMediaSidebar.HeaderLineHeight - ReaderMediaSidebar.HeaderGap, sidebar.Card.Bounds.Height);
                    Assert.Equal(sidebar.Bounds.Height, sidebar.Card.TranslatePoint(default, sidebar)!.Value.Y + sidebar.Card.Bounds.Height);
                    double chrome = sidebar.Bounds.Height - sidebar.Frame.Bounds.Height;
                    chromeHeight ??= chrome;
                    Assert.Equal(chromeHeight.Value, chrome);
                    double ratio = sidebar.Frame.Bounds.Width * sidebar.Frame.Bounds.Height / (sidebar.Bounds.Width * sidebar.Bounds.Height);
                    Assert.True(ratio >= previousRatio, $"Media/pane area fell from {previousRatio} to {ratio} at {size}");
                    if (previousHostHeight > 0)
                        Assert.Equal(host.Bounds.Height - previousHostHeight, sidebar.Frame.Bounds.Height - previousFrameHeight);
                    Console.WriteLine($"HEIGHT large={largeReader} right={right} chrome={withChrome} size={size} host={host.Bounds.Height} sidebar={sidebar.Bounds.Height} frame={sidebar.Frame.Bounds.Height} ratio={ratio:F6}");
                    previousRatio = ratio;
                    previousFrameHeight = sidebar.Frame.Bounds.Height;
                    previousHostHeight = host.Bounds.Height;
                }
            }
            ReaderMediaPreferences saved = page.ReaderMedia;
            page.ApplyReaderMediaPreferences(saved with
            {
                SidebarWidth = ReaderDisplayPreferences.MediaSidebarWidthMaximum,
                SidebarMinimumReaderWidth = ReaderDisplayPreferences.MediaSidebarReaderWidthMaximum
            });
            Layout(root, geometries[0]);
            Assert.False(page.IsMediaSidebarShown);
            Assert.True(toggle.IsVisible);
            Assert.False(toggle.IsEnabled);
            Assert.Equal(Ursa.Controls.OverflowMode.Always, Ursa.Controls.ToolBar.GetOverflowMode(toggle));
            page.ApplyReaderMediaPreferences(saved with { SidebarWidth = 620 });
            Layout(root, geometries[0]);
            Assert.True(page.IsMediaSidebarShown);
            Assert.True(toggle.IsEnabled);
            Assert.Equal(Ursa.Controls.OverflowMode.AsNeeded, Ursa.Controls.ToolBar.GetOverflowMode(toggle));
            Assert.Equal(right ? 2 : 0, Grid.GetColumn(sidebar));
            Assert.Equal(ReaderMediaSidebarRules.EffectiveWidth(620, host.Bounds.Width, largeReader ? 0 : 2 * ReaderMediaSidebarRules.ArchiveViewGap), sidebar.Bounds.Width);
            Assert.Equal(host.Bounds.Height, sidebar.Bounds.Height);
        }
        finally { large.SetValue(page, false); page.EndLifetime(); }
    });

    [Theory]
    [InlineData(8, 512, 240, 720)]
    [InlineData(8, 512, 360, 720)]
    [InlineData(512, 8, 360, 720)]
    [InlineData(64, 64, 500, 900)]
    public void HeaderIsFixedAndCardFillsTheAvailableHeightAcrossImageAspects(int pixelWidth, int pixelHeight, int width, int height) => OnDispatcher(() =>
    {
        ReaderMediaSidebar view = new();
        using WriteableBitmap picture = new(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        void Layout()
        {
            // This standalone control has no layout root to schedule invalidated ancestors.
            foreach (Layoutable child in view.GetVisualDescendants().OfType<Layoutable>().Reverse()) child.InvalidateMeasure();
            view.InvalidateMeasure();
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            Assert.Equal(ReaderMediaSidebar.HeaderLineHeight, view.HeaderRow.Bounds.Height);
            Assert.Equal(height, view.Bounds.Height);
            Assert.Equal(height - view.Padding.Top - view.Padding.Bottom - ReaderMediaSidebar.HeaderLineHeight - ReaderMediaSidebar.HeaderGap, view.Card.Bounds.Height);
        }
        Layout(); // Empty state reserves exactly the same title row.
        Grid headerRow = Assert.IsType<Grid>(view.HeaderRow);
        // ActualWidth includes the gap after the first grid column.
        double titleWidth = headerRow.ColumnDefinitions[0].ActualWidth - headerRow.ColumnSpacing;
        Assert.InRange(Math.Abs(headerRow.Children[0].Bounds.Center.X - titleWidth / 2), 0, 0.5);
        Rect header = view.HeaderRow.Bounds;
        Rect card = view.Card.Bounds;
        view.Describe("No. 123", new string('x', 500) + ".png", "100 MiB · 50000×100", true, "Copy post reference");
        view.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity);
        Layout();
        Assert.Equal(header, view.HeaderRow.Bounds);
        Assert.Equal(card, view.Card.Bounds);
        Assert.Equal(view.Card.Bounds.Height, view.Frame.Bounds.Height);
        view.ShowStrip(3, 0, 3, index => "file" + index, samePost: false);
        Layout();
        Assert.Equal(header, view.HeaderRow.Bounds);
        Assert.Equal(card, view.Card.Bounds);
        Assert.True(view.Frame.Bounds.Height > 0);
        Assert.True(view.Frame.Bounds.Height < view.Card.Bounds.Height);
        view.ShowEmpty();
        Layout();
        Assert.Equal(header, view.HeaderRow.Bounds);
        Assert.Equal(card, view.Card.Bounds);
    });
}
