using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Real focused controls and routed keys on a scoped headless platform, not native OS input.
[Collection("Booru exact navigation")]
public sealed class EnterGalleryFocusTests
{
    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = AvaloniaLocator.EnterScope();
        typeof(AvaloniaHeadlessPlatform).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }]);
        TokenApplication.Bind();
        test();
    });

    private static void Press(Control source, KeyModifiers modifiers = KeyModifiers.None)
    {
        Assert.True(source.Focus(), "The tested source must really receive keyboard focus.");
        Assert.True(source.IsFocused);
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
            Source = source, Key = Key.Enter, KeyModifiers = modifiers });
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent,
            Source = source, Key = Key.Enter, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void FocusMovingMediumButtonTextMediumKeepsEachOwnerAndCommitsOneTag() => Run(() =>
    {
        using MediaTileEnterTests.Fixture fixture = new();
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSession target = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(fixture.Entries[1])!),
            () => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));
        using QuickTagSession session = new(QuickTagTarget.For(target));
        using QuickTagSurface tags = new(session);
        Button button = new() { Content = "Action" };
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        ListBoxItem medium = (ListBoxItem)fixture.Container(1);
        // Inner controls route through the very same grid tunnel as the medium.
        medium.ContentTemplate = null;
        medium.Content = new StackPanel { Children = { button, tags } };
        Window window = new() { Content = fixture.RootControl, Width = 900, Height = 700 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            fixture.Grid.SelectedIndex = 1;
            fixture.Opened.Clear();
            Press(medium);
            var opened = Assert.Single(fixture.Opened);
            Assert.Same(fixture.Entries[1], opened.Window[opened.Start]);

            Press(button);
            Assert.Equal(1, clicks);
            Assert.Single(fixture.Opened);
            Assert.True(button.IsFocused);

            tags.Input.Text = "ledger16_tag";
            tags.Input.Dismiss();
            Control editor = tags.Input.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? (Control)tags.Input;
            Press(editor);
            Assert.Single(session.UndoSteps);
            Assert.Equal("ledger16_tag", Assert.Single(store.ReadSnapshot().TagsOf(MediaLibraryFilterEngine.TagTarget(fixture.Entries[1])!)).Name.Display);
            Assert.True(string.IsNullOrEmpty(tags.Input.Text));
            Assert.Single(fixture.Opened);
            Assert.Equal(1, clicks);
            Assert.True(editor.IsFocused);

            Press(medium, KeyModifiers.Shift);
            Assert.Equal(2, fixture.Opened.Count);
            Assert.Same(fixture.Entries[1], fixture.Opened[1].Window[fixture.Opened[1].Start]);
            Assert.Single(session.UndoSteps);
        }
        finally { window.Content = null; window.Close(); }
    });

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]
    public void FilmstripEnterActivatesTheFocusedThirdTileNotTheCurrentFirstTile(KeyModifiers modifiers) => Run(() =>
    {
        var entries = Enumerable.Range(0, 3).Select(index =>
        {
            string file = "missing-" + index + ".png";
            string directory = Environment.GetEnvironmentVariable("COMMANDCODE_SCRATCHPAD")!;
            string hash = new((char)('a' + index), 64);
            MeowChan.Storage.MediaAssetReference asset = new(hash, file, "original", file, Path.Combine(directory, file), 3, hash, "https://offline.invalid/" + file, file, true);
            MeowChan.Storage.MediaAssetSnapshot snapshot = new(directory, MeowChan.Storage.MediaManifestStatus.Available, 3, "fixture", [asset]);
            return new MeowChan.Gallery.GalleryEntry(new MeowChan.Gallery.GalleryAssetKey(new ResourceId("test", "gallery", "1"), directory, asset.AssetId), snapshot, asset, null, null, false, false);
        }).ToArray();
        GalleryFilmstrip strip = new();
        strip.SetEntries(entries, entries.Length, false);
        strip.SetCurrent(0);
        int selected = -1, exits = 0;
        strip.TileActivated += index => selected = index;
        strip.ExitRequested += () => exits++;
        strip.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (strip.HandleKey(e)) e.Handled = true; }, RoutingStrategies.Tunnel);
        Window window = new() { Content = strip, Width = 600, Height = 200 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Control tile = strip.TileControl(2)!;
            Assert.NotNull(tile);
            Press(tile, modifiers);
            Assert.Equal(2, selected);
            Assert.Equal(1, exits);
        }
        finally { strip.Retire(); window.Content = null; window.Close(); }
    });

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]
    public void ReaderPreviewRouteOpensFocusedThirdAttachmentWithOneClickAndRejectsOtherButtons(KeyModifiers modifiers) => Run(() =>
    {
        ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        page.IsReadable = true;
        typeof(ArchivePage).GetField("_available", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
        page.OpenGallery = (_, _, _) => Task.CompletedTask;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ((Panel)posts.Parent!).Children.Remove(posts);
        int openedPosition = -1, clicks = 0, buttonClicks = 0;
        ArchiveAttachmentView view = new()
        {
            DataContext = new ArchiveAttachmentRow(new CapturedAttachment(2, "third", OriginalFileName: "third.jpg"))
                { OpenPreview = () => openedPosition = 2 }
        };
        Button other = new() { Content = "Unrelated action" };
        other.Click += (_, _) => buttonClicks++;
        view.PreviewButton.Click += (_, _) => clicks++;
        posts.Template = new Avalonia.Controls.Templates.FuncControlTemplate<ListBox>((_, _) =>
            new StackPanel { Children = { view, other } });
        posts.IsVisible = true;
        MethodInfo route = typeof(ArchivePage).GetMethod("TryOpenFocusedReaderMedium", BindingFlags.Instance | BindingFlags.NonPublic)!;
        posts.AddHandler(InputElement.KeyDownEvent, (_, e) => route.Invoke(page, [e]), RoutingStrategies.Tunnel);
        Window window = new() { Content = posts, Width = 500, Height = 500 };
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            Press(view.PreviewButton, modifiers);
            Assert.Equal(2, openedPosition);
            Assert.Equal(1, clicks);
            Assert.True(view.PreviewButton.IsFocused);
            Press(other);
            Assert.Equal(1, buttonClicks);
            Assert.Equal(1, clicks);
            Assert.True(other.IsFocused);
        }
        finally { window.Content = null; window.Close(); page.EndLifetime(); }
    });

    [Fact]
    public void PreviewGalleryActivationEmitsOneMediumButtonClickAndMouseClickStillPreviews() => Run(() =>
    {
        int opens = 0, previews = 0, clicks = 0;
        ArchiveAttachmentView view = new()
        {
            DataContext = new ArchiveAttachmentRow(new CapturedAttachment(2, "third", OriginalFileName: "third.jpg"))
                { OpenPreview = () => opens++ }
        };
        view.SetValue(ArchiveAttachmentView.TileActivationProperty, _ => { previews++; return true; });
        ReaderInlineVideoController controller = new();
        controller.ConfigureAsync(ReaderInlineVideoOptions.From(new Config())).GetAwaiter().GetResult();
        view.SetValue(ArchiveAttachmentView.InlineVideoProperty, controller);
        view.PreviewButton.Click += (_, _) => clicks++;
        Window window = new() { Content = view };
        try
        {
            window.Show();
            view.ActivateGalleryByKey();
            Assert.Equal((1, 0, 1), (opens, previews, clicks));
            view.PreviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((1, 1, 2), (opens, previews, clicks));
        }
        finally { window.Content = null; window.Close(); }
    });
}
