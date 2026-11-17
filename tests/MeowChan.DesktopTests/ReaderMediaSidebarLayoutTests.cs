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
            Assert.Equal("Media sidebar: Off", button.Content);
            Assert.Equal(button.Content, AutomationProperties.GetName(button));
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
            bool initial = largeReader;
            Assert.Equal(initial, button.IsChecked);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(!initial, button.IsChecked);
            Assert.Equal("Media sidebar: " + (!initial ? "On" : "Off"), button.Content);
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
