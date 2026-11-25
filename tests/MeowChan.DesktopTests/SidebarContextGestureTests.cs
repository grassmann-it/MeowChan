using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class SidebarContextGestureTests
{
    private static readonly ResourceId Thread = new("fixture", "g", "42");

    [Fact]
    public void MediaPanelMenuWritesOnlyToTheShownMediumOfAMultipleSelection() => Run(async (window, _) =>
    {
        MediaPage page = await OpenMediaAsync(window);
        MediaLibraryTileRow a = Assert.Single(page.Grid.Items.OfType<MediaLibraryTileRow>(), row => row.Entry.Name == "a.png");
        MediaLibraryTileRow b = Assert.Single(page.Grid.Items.OfType<MediaLibraryTileRow>(), row => row.Entry.Name == "b.png");
        page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
        page.Grid.SelectedItem = a;
        page.Grid.Selection.Select(b.Index);
        ReaderMediaSidebar sidebar = await MediaSidebarAsync(window, page, "b.png");
        Assert.Equal(2, page.Grid.Selection.Count);

        ContextGestures.RightClick(Picture(sidebar));
        QuickTagSurface surface = await OpenTagsAsync(page.MediaMenu);
        surface.Input.Text = "panel-tag";
        Assert.True(await surface.CommitTypedAsync());

        PrivateTagStoreSnapshot saved = ReadSavedTags(window);
        Assert.Empty(saved.TagsOf(Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(a.Entry))));
        Assert.Equal("panel-tag", Assert.Single(saved.TagsOf(Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(b.Entry)))).Name.Display);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MediaPanelGesturesFollowTheNewOccurrenceOfTheSameTile(bool useFilter) => Run(async (window, media) =>
    {
        MediaPage page = await OpenMediaAsync(window, tileCount: 1);
        MediaLibraryTileRow first = Assert.Single(page.Grid.Items.OfType<MediaLibraryTileRow>());
        Assert.Equal(2, first.Tile.Occurrences.Count);
        Assert.NotEqual(media[0].Subject, media[1].Subject);
        page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
        page.Grid.SelectedItem = first;
        ReaderMediaSidebar sidebar = await MediaSidebarAsync(window, page, "a.png");
        await page.PanelTask;
        Control frame = SidePanelDesignTests.ById<Control>(sidebar, "ReaderMediaSidebarFrame");
        Image picture = Picture(sidebar);
        List<string> galleryPaths = [];
        page.OpenGallery = async (entries, start) =>
        {
            IReadOnlyList<GalleryEntry> resolved = await MediaLibraryGallery.ResolveAsync([entries[start]],
                window.SessionHost.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync);
            galleryPaths.Add(Assert.Single(resolved).Asset.Path);
        };
        List<string> externalPaths = [];
        window.OpenFileForTests = path => { externalPaths.Add(path); return Task.CompletedTask; };

        if (useFilter)
        {
            page.FocusSearch();
            window.KeyTextInput("b.png");
        }
        else ContextGestures.Click(page.SortDirectionButton);
        await MediaSidebarAsync(window, page, "b.png");
        await page.PanelTask;
        Assert.Equal(1, page.Grid.ItemCount);
        Assert.Equal(first.Entry.Sha256, page.PanelRow!.Entry.Sha256);
        Assert.Equal(Path.GetDirectoryName(media[1].Path), page.PanelRow.Entry.Directory);

        Assert.True(page.NameFilter.Focus());
        ContextGestures.Click(picture);
        Assert.True(frame.IsFocused, "A single click must focus the panel after the occurrence changes.");
        Assert.Empty(galleryPaths);
        Point imageCenter = new Rect(picture.Bounds.Size).Center;
        ContextGestures.DoubleClick(picture, new Point(imageCenter.X + picture.Bounds.Width / 3, imageCenter.Y));
        await BooruTestSupport.WaitAsync(() => galleryPaths.Count != 0, "the new occurrence to open in the gallery");
        Assert.Equal(media[1].Path, Assert.Single(galleryPaths));

        ContextGestures.MiddleClick(picture);
        await BooruTestSupport.WaitAsync(() => externalPaths.Count != 0, "the new occurrence to open externally");
        Assert.Equal(media[1].Path, Assert.Single(externalPaths));

        ContextGestures.RightClick(picture);
        QuickTagSurface editor = await OpenTagsAsync(page.MediaMenu);
        editor.Input.Text = "new-occurrence";
        Assert.True(await editor.CommitTypedAsync());
        PrivateTagStoreSnapshot saved = ReadSavedTags(window);
        Assert.Empty(saved.TagsOf(media[0].Subject));
        Assert.Equal("new-occurrence", Assert.Single(saved.TagsOf(media[1].Subject)).Name.Display);
    }, identicalOccurrences: true);

    [Fact]
    public void ReaderPanelMenuRetiresTheTagEditorWhenTheAttachmentChangesInTheSamePost() => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        ContextGestures.RightClick(Picture(sidebar));
        QuickTagSurface surface = await OpenTagsAsync(page.ArchiveMediaMenu);
        QuickTagPopover editor = Assert.IsType<QuickTagPopover>(window.ArchiveTagPopover);
        surface.Input.Text = "stale-draft";

        page.SelectMediaSidebarIndex(1);
        Assert.Equal(Thread, page.CurrentThread?.Summary.Id);
        await BooruTestSupport.WaitAsync(() => sidebar.HeaderLine.Contains("b.png", StringComparison.Ordinal) && Picture(sidebar).IsVisible,
            "the Reader to show b.png");
        await surface.CommitTypedAsync();
        Assert.Equal(QuickTagPopoverCloseReason.Stale, editor.Reason);
        Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
        Assert.Empty(ReadSavedTags(window).TagsOf(media[1].Subject));

        ContextGestures.RightClick(Picture(sidebar));
        QuickTagSurface next = await OpenTagsAsync(page.ArchiveMediaMenu);
        next.Input.Text = "current-tag";
        Assert.True(await next.CommitTypedAsync());
        Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
        Assert.Equal("current-tag", Assert.Single(ReadSavedTags(window).TagsOf(media[1].Subject)).Name.Display);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutoHideTagChipOwnsRightClickMacControlClickAndBothMenuKeys(bool reader) => Run(async (window, media) =>
    {
        DesktopSessionHost host = window.SessionHost;
        TagId id = (await host.DefinePrivateTagAsync("sky")).Tag;
        await host.SetMediaSubjectTagAsync(id, media[0].Subject, true);

        ReaderMediaSidebar sidebar;
        Func<ContextMenu?> mediaMenu;
        if (reader)
        {
            (ArchivePage page, ReaderMediaSidebar panel) = await OpenReaderAsync(window);
            sidebar = panel;
            mediaMenu = () => page.ArchiveMediaMenu;
        }
        else
        {
            MediaPage page = await OpenMediaAsync(window);
            page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
            page.Grid.SelectedItem = Assert.Single(page.Grid.Items.OfType<MediaLibraryTileRow>(), row => row.Entry.Name == "a.png");
            sidebar = await MediaSidebarAsync(window, page, "a.png");
            mediaMenu = () => page.MediaMenu;
        }
        Image picture = Picture(sidebar);
        ContextGestures.Click(picture);
        await BooruTestSupport.WaitAsync(() => sidebar.GetVisualDescendants().OfType<Button>().Any(control => IsTag(control) && control.IsEffectivelyVisible),
            "the visible sky chip");
        Button chip = Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(), control => IsTag(control) && control.IsEffectivelyVisible);
        Assert.True(chip.IsTabStop);
        Assert.True(chip.Focus(NavigationMethod.Tab));
        Assert.False(SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarEditTags").Focusable);

        OpenChipMenu(() => ContextGestures.RightClick(chip));
        ContextClick.MacOSForTests = true;
        try { OpenChipMenu(() => ContextGestures.ControlClick(chip)); }
        finally { ContextClick.MacOSForTests = null; }
        OpenChipMenu(() => ContextGestures.MenuKey(chip));
        OpenChipMenu(() => ContextGestures.ShiftF10(chip));

        static bool IsTag(Button control) => AutomationProperties.GetAutomationId(control) == "ReaderMediaSidebarTag"
            && AutomationProperties.GetName(control) == "sky";

        void OpenChipMenu(Action gesture)
        {
            gesture();
            ContextMenu menu = Assert.IsType<ContextMenu>(chip.ContextMenu);
            Assert.True(menu.IsOpen);
            Assert.False(mediaMenu()?.IsOpen == true);
            Assert.Equal(new[] { "tags.filter-include", "tags.copy-name", "tags.quick.stamp.menuAdd" }, MenuProbe.Entries(menu).Select(entry => entry.Id));
            menu.Close();
            ContextGestures.Settle(window);
        }
    });

    [Fact]
    public void ReaderMiddleClickOnlyOpensOnThePictureAtBothEndsOfTheGesture() => Run(async (window, media) =>
    {
        (_, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        List<string> opened = [];
        window.OpenFileForTests = path => { opened.Add(path); return Task.CompletedTask; };
        Image picture = Picture(sidebar);
        ContextGestures.Click(picture);
        Button close = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarClose");
        Button edit = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarEditTags");

        ContextGestures.MiddleClick(close);
        ContextGestures.MiddleClick(edit);
        ContextGestures.Press(picture, MouseButton.Middle);
        ContextGestures.Release(close, MouseButton.Middle);
        ContextGestures.Press(edit, MouseButton.Middle);
        ContextGestures.Release(picture, MouseButton.Middle);
        Assert.Empty(opened);

        ContextGestures.MiddleClick(picture);
        await BooruTestSupport.WaitAsync(() => opened.Count != 0, "the shown file to open");
        Assert.Equal(media[0].Path, Assert.Single(opened));
    });

    [Fact]
    public void ReaderMiddleClickOnAnIdleVideoActivatorRequiresThePosterBounds() => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        List<string> opened = [];
        window.OpenFileForTests = path => { opened.Add(path); return Task.CompletedTask; };
        ReaderInlineVideoSlot slot = Assert.IsType<ReaderInlineVideoSlot>(page.SidebarInlineVideo);
        sidebar.LimitPicture(120, 120);
        slot.Offer(page.InlineVideo, "idle-video", _ => Task.FromResult<GalleryEntry?>(null), "Video with narrow poster");
        ContextGestures.Settle(window);
        Button activator = slot.Activator;
        Assert.True(activator.IsEffectivelyVisible);
        Image poster = Picture(sidebar);
        Point posterCenter = Assert.IsType<Point>(poster.TranslatePoint(new Rect(poster.Bounds.Size).Center, activator));
        Point margin = new(8, posterCenter.Y);
        Assert.True(posterCenter.X - poster.Bounds.Width / 2 > margin.X);

        ContextGestures.MiddleClick(activator, margin);
        ContextGestures.Press(activator, MouseButton.Middle, posterCenter);
        ContextGestures.Release(activator, MouseButton.Middle, margin);
        ContextGestures.Press(activator, MouseButton.Middle, margin);
        ContextGestures.Release(activator, MouseButton.Middle, posterCenter);
        Assert.Empty(opened);

        ContextGestures.MiddleClick(activator, posterCenter);
        await BooruTestSupport.WaitAsync(() => opened.Count != 0, "the file behind the displayed poster to open");
        Assert.Equal(media[0].Path, Assert.Single(opened));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingPreviewIgnoresClicksOnThePreviousPicture(bool reader) => Run(async (window, media) =>
    {
        (ReaderMediaSidebar sidebar, Action selectNext, Func<ContextMenu?> menu) = await OpenPreviewAsync(window, reader);
        Image picture = Picture(sidebar);
        object? oldBitmap = picture.Source;
        TaskCompletionSource decodeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeDecode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sidebar.BeforePreviewDecodeForTests = (path, token) =>
        {
            if (path != media[1].Path) return Task.CompletedTask;
            decodeStarted.TrySetResult();
            return resumeDecode.Task.WaitAsync(token);
        };
        List<string> opened = [];
        window.OpenFileForTests = path => { opened.Add(path); return Task.CompletedTask; };
        try
        {
            selectNext();
            await BooruTestSupport.WaitAsync(() => decodeStarted.Task.IsCompleted, "the decoder for b.png to be held");
            Assert.Contains("b.png", sidebar.HeaderLine);
            Assert.True(picture.IsEffectivelyVisible);
            Assert.True(sidebar.IsDimmed);
            Assert.Same(oldBitmap, picture.Source);

            ContextGestures.RightClick(picture);
            Assert.False(menu()?.IsOpen == true);
            ContextClick.MacOSForTests = true;
            try { ContextGestures.ControlClick(picture); }
            finally { ContextClick.MacOSForTests = null; }
            Assert.False(menu()?.IsOpen == true);
            ContextGestures.MiddleClick(picture);
            Assert.Empty(opened);
            Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
            Assert.Empty(ReadSavedTags(window).TagsOf(media[1].Subject));

            ContextGestures.Press(picture, MouseButton.Middle);
            resumeDecode.TrySetResult();
            await BooruTestSupport.WaitAsync(() => !sidebar.IsDimmed && picture.Source != null && !ReferenceEquals(oldBitmap, picture.Source),
                "the decoder to replace a.png with b.png");
            ContextGestures.Release(picture, MouseButton.Middle);
            Assert.Empty(opened);

            ContextGestures.MiddleClick(picture);
            await BooruTestSupport.WaitAsync(() => opened.Count != 0, "the displayed b.png to open externally");
            Assert.Equal(media[1].Path, Assert.Single(opened));
            ContextGestures.RightClick(picture);
            QuickTagSurface editor = await OpenTagsAsync(menu());
            editor.Input.Text = "displayed";
            Assert.True(await editor.CommitTypedAsync());
            Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
            Assert.Equal("displayed", Assert.Single(ReadSavedTags(window).TagsOf(media[1].Subject)).Name.Display);
        }
        finally
        {
            sidebar.BeforePreviewDecodeForTests = null;
            resumeDecode.TrySetResult();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabReachesTheLastOverflowTagWithoutPointerInput(bool reader) => Run(async (window, media) =>
    {
        string[] tags = Enumerable.Range(1, 12).Select(i => $"landscape-details-{i:00}").ToArray();
        foreach (string name in tags)
            await window.SessionHost.DefineAndSetMediaSubjectTagAsync(name, media[0].Subject);
        (ReaderMediaSidebar sidebar, _, Func<ContextMenu?> menu) = await OpenPreviewAsync(window, reader);
        Assert.True(SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarMoreTags").IsEffectivelyVisible);
        Assert.False(SidePanelDesignTests.ById<ScrollViewer>(sidebar, "ReaderMediaSidebarExpanded").IsEffectivelyVisible);
        Control frame = SidePanelDesignTests.ById<Control>(sidebar, "ReaderMediaSidebarFrame");
        Button expand = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarExpand");
        Assert.True(frame.Focus(NavigationMethod.Tab));
        for (int i = 0; i < tags.Length && !expand.IsFocused; i++) Tab();
        Assert.True(expand.IsFocused);
        ContextGestures.KeyStroke(expand, Key.Space, PhysicalKey.Space);
        Button last = Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(), control =>
            AutomationProperties.GetAutomationId(control) == "ReaderMediaSidebarExpandedTag"
            && AutomationProperties.GetName(control) == tags[^1]);
        for (int i = 0; i < tags.Length && !last.IsFocused; i++) Tab();
        Assert.True(last.IsFocused);
        ContextGestures.ShiftF10(last);
        ContextMenu tagMenu = Assert.IsType<ContextMenu>(last.ContextMenu);
        Assert.True(tagMenu.IsOpen);
        Assert.False(menu()?.IsOpen == true);
        Assert.Contains(MenuProbe.Entries(tagMenu), entry => entry.Id == "tags.copy-name");
        tagMenu.Close();

        void Tab()
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            ContextGestures.Settle(window);
        }
    });

    [Fact]
    public void SavedMediaSidebarTagsOnlyItsPreviewDuringMultipleSelection() => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar, _) = await OpenSavedPreviewAsync(window);
        page.SelectAllMedia();
        Assert.Equal(2, page.SelectedMediaRows().Count);
        Assert.Contains("a.png", sidebar.HeaderLine);
        ContextGestures.RightClick(Picture(sidebar));
        QuickTagSurface editor = await OpenTagsAsync(page.ArchiveMediaMenu);
        editor.Input.Text = "saved-preview";
        Assert.True(await editor.CommitTypedAsync());
        Assert.Equal("saved-preview", Assert.Single(ReadSavedTags(window).TagsOf(media[0].Subject)).Name.Display);
        Assert.Empty(ReadSavedTags(window).TagsOf(media[1].Subject));
    });

    [Fact]
    public void ClosingSavedMediaTagEditorCancelsAWriteAlreadyValidatingTheFile() => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar, ListBox tiles) = await OpenSavedPreviewAsync(window);
        ContextGestures.RightClick(Picture(sidebar));
        QuickTagSurface editor = await OpenTagsAsync(page.ArchiveMediaMenu);
        TaskCompletionSource<CancellationToken> checkStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.SessionHost.LocalMediaChecksForTests = async (observations, token) =>
        {
            checkStarted.TrySetResult(token);
            await resumeCheck.Task;
            return observations.Select(_ => LocalMediaCheck.Verified).ToArray();
        };
        editor.Input.Text = "closed-draft";
        Task<bool> write = editor.CommitTypedAsync();
        try
        {
            await BooruTestSupport.WaitAsync(() => checkStarted.Task.IsCompleted, "validation of the local file to be held");
            ContextGestures.Click(editor.CloseButton);
            bool canceledAtClose = (await checkStarted.Task).IsCancellationRequested;
            await WaitForSavedPictureAsync(window, sidebar, tiles, "b.png");
            resumeCheck.TrySetResult();
            await write;
            Assert.True(canceledAtClose);
            Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
            Assert.Empty(ReadSavedTags(window).TagsOf(media[1].Subject));
        }
        finally
        {
            resumeCheck.TrySetResult();
            await write;
            window.SessionHost.LocalMediaChecksForTests = null;
        }
    }, loose: true);

    [Fact]
    public void AdvancedSavedMediaTagDialogClosesWhenItsPreviewChanges() => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar, ListBox tiles) = await OpenSavedPreviewAsync(window);
        ContextGestures.RightClick(Picture(sidebar));
        QuickTagSurface editor = await OpenTagsAsync(page.ArchiveMediaMenu);
        ContextGestures.Click(editor.AdvancedButton);
        await BooruTestSupport.WaitAsync(() => window.GalleryMediaTagWindow?.IsVisible == true, "the advanced tag editor for a.png");
        await WaitForSavedPictureAsync(window, sidebar, tiles, "b.png");
        await BooruTestSupport.WaitAsync(() => window.GalleryMediaTagWindow == null, "the advanced tag editor to close on target change");
        Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
        Assert.Empty(ReadSavedTags(window).TagsOf(media[1].Subject));
    });

    [Theory]
    [InlineData("media")]
    [InlineData("posts")]
    [InlineData("saved")]
    public void SidebarContextGesturesWaitForThePictureDuringASelectionChange(string host) => Run(async (window, media) =>
    {
        TaskCompletionSource resumeDecode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (ReaderMediaSidebar sidebar, Action selectNext, Func<ContextMenu?> menu) = host == "saved"
            ? await OpenSavedPreviewWithHeldPresentationAsync(window, resumeDecode.Task)
            : await OpenPreviewAsync(window, reader: host == "posts");
        Image picture = Picture(sidebar);
        Control frame = SidePanelDesignTests.ById<Control>(sidebar, "ReaderMediaSidebarFrame");
        frame.Focusable = true;
        sidebar.LimitPicture(120, 120);
        ContextGestures.Settle(window);
        object? previous = picture.Source;
        if (host != "saved")
            sidebar.BeforePreviewDecodeForTests = (path, token) => path == media[1].Path ? resumeDecode.Task.WaitAsync(token) : Task.CompletedTask;
        List<string> opened = [];
        window.OpenFileForTests = path => { opened.Add(path); return Task.CompletedTask; };

        try
        {
            selectNext();
            await BooruTestSupport.WaitAsync(() => sidebar.IsDimmed && sidebar.HeaderLine.Contains("b.png", StringComparison.Ordinal),
                "the next medium to be described while its picture is pending");
            Assert.Same(previous, picture.Source);
            Assert.True(picture.IsEffectivelyVisible);

            Assert.All(ContextActions(), gesture =>
            {
                gesture.Run();
                bool visible = menu()?.IsOpen == true;
                if (visible) menu()!.Close();
                ContextGestures.Settle(window);
                Assert.False(visible, gesture.Name + " opened a menu while the previous picture was visible.");
            });
            ContextGestures.MiddleClick(picture);
            Assert.Empty(opened);
            Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
            Assert.Empty(ReadSavedTags(window).TagsOf(media[1].Subject));

            ContextGestures.Press(frame, MouseButton.Right, MarginPoint());
            Assert.False(menu()?.IsOpen == true);
            resumeDecode.TrySetResult();
            await BooruTestSupport.WaitAsync(() => !sidebar.IsDimmed && picture.IsEffectivelyVisible && !ReferenceEquals(previous, picture.Source),
                "the new picture to replace the previous picture");
            ContextGestures.Release(frame, MouseButton.Right, MarginPoint());
            Assert.False(menu()?.IsOpen == true);

            foreach (var (_, gesture) in ContextActions())
            {
                gesture();
                Assert.True(menu()?.IsOpen == true);
                menu()!.Close();
                ContextGestures.Settle(window);
            }
            ContextGestures.MiddleClick(picture);
            await BooruTestSupport.WaitAsync(() => opened.Count != 0, "the displayed b.png to open externally");
            Assert.Equal(media[1].Path, Assert.Single(opened));

            ContextGestures.ShiftF10(frame);
            QuickTagSurface editor = await OpenTagsAsync(menu());
            editor.Input.Text = "current-preview";
            Assert.True(await editor.CommitTypedAsync());
            Assert.Empty(ReadSavedTags(window).TagsOf(media[0].Subject));
            Assert.Equal("current-preview", Assert.Single(ReadSavedTags(window).TagsOf(media[1].Subject)).Name.Display);
        }
        finally
        {
            sidebar.BeforePreviewDecodeForTests = null;
            resumeDecode.TrySetResult();
        }

        Point MarginPoint()
        {
            sidebar.LimitPicture(120, 120);
            ContextGestures.Settle(window);
            Point middle = Assert.IsType<Point>(picture.TranslatePoint(new Rect(picture.Bounds.Size).Center, frame));
            Point margin = new(8, middle.Y);
            Assert.True(middle.X - picture.Bounds.Width / 2 > margin.X);
            return margin;
        }

        (string Name, Action Run)[] ContextActions() =>
        [
            ("Right click on the picture", () => ContextGestures.RightClick(picture)),
            ("Right click on the frame margin", () => ContextGestures.RightClick(frame, MarginPoint())),
            ("Control click on the picture", () => MacClick(picture)),
            ("Control click on the frame margin", () => MacClick(frame, MarginPoint())),
            ("Apps on the frame", () => ContextGestures.MenuKey(frame)),
            ("Shift+F10 on the frame", () => ContextGestures.ShiftF10(frame))
        ];

        static void MacClick(Control target, Point? point = null)
        {
            ContextClick.MacOSForTests = true;
            try { ContextGestures.ControlClick(target, point); }
            finally { ContextClick.MacOSForTests = null; }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedMenuKeyCannotOpenTheNextPictureOnRelease(bool reader) => Run(async (window, media) =>
    {
        var (sidebar, selectNext, menu) = await OpenPreviewAsync(window, reader);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Image picture = Picture(sidebar);
        object? first = picture.Source;
        Control frame = SidePanelDesignTests.ById<Control>(sidebar, "ReaderMediaSidebarFrame");
        frame.Focusable = true;
        sidebar.BeforePreviewDecodeForTests = (path, token) => path == media[1].Path ? resume.Task.WaitAsync(token) : Task.CompletedTask;
        try
        {
            selectNext();
            await BooruTestSupport.WaitAsync(() => sidebar.IsDimmed, "the old picture during the new decode");
            Assert.Same(first, picture.Source);
            Assert.True(frame.Focus());
            window.KeyPress(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            Assert.False(menu()?.IsOpen == true);
            resume.TrySetResult();
            await BooruTestSupport.WaitAsync(() => !sidebar.IsDimmed && picture.Source != first, "the next picture");
            Assert.True(frame.IsFocused);
            window.KeyRelease(Key.Apps, RawInputModifiers.None, PhysicalKey.ContextMenu, null);
            ContextGestures.Settle(window);
            Assert.False(menu()?.IsOpen == true, "Releasing the rejected Apps key opened the replacement's menu.");
            ContextGestures.MenuKey(frame);
            Assert.True(menu()?.IsOpen == true);
            menu()!.Close();
        }
        finally { resume.TrySetResult(); sidebar.BeforePreviewDecodeForTests = null; }
    });

    private static async Task<(ReaderMediaSidebar Sidebar, Action Next, Func<ContextMenu?> Menu)> OpenSavedPreviewWithHeldPresentationAsync(
        MainWindow window, Task resume)
    {
        (ArchivePage page, ReaderMediaSidebar sidebar, ListBox tiles) = await OpenSavedPreviewAsync(window);
        ArchiveAssetRow next = Assert.Single(tiles.Items.OfType<ArchiveAssetRow>(), item => item.Name == "b.png");
        long now = Environment.TickCount64;
        page.UseManualReaderTicks(() => now);
        return (sidebar, SelectNext, () => page.ArchiveMediaMenu);

        void SelectNext()
        {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new HeldPresentation(resume));
            try
            {
                now += ReaderMediaSidebarRules.FollowIntervalMs;
                tiles.SelectedItem = next;
                page.TickMediaSidebarFollow();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private sealed class HeldPresentation(Task resume) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => _ = PostAfterResumeAsync(callback, state);

        private async Task PostAfterResumeAsync(SendOrPostCallback callback, object? state)
        {
            await resume.ConfigureAwait(false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => callback(state));
        }
    }

    [Theory]
    [InlineData("right-click")]
    [InlineData("menu-key")]
    [InlineData("favorite")]
    [InlineData("mark")]
    [InlineData("unmark")]
    [InlineData("enter")]
    [InlineData("double-click")]
    public void ReaderThumbnailActsOnlyForThePostAndAttachmentItShows(string input) => Run(async (window, media) =>
    {
        long now = 0;
        window.ArchivePage.UseManualReaderTicks(() => now);
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        ListBox posts = SidePanelDesignTests.ById<ListBox>(page, "ArchivePosts");
        ArchivePostRow next = Assert.Single(posts.Items.OfType<ArchivePostRow>(), post => post.Id == "44");
        ListBoxItem previousThumbnail = SidebarSecondThumbnail(sidebar);
        Assert.Contains("b.png", AutomationProperties.GetName(previousThumbnail));
        List<GalleryEntryKey> gallery = [];
        page.OpenGallery = (_, entry, _) =>
        {
            gallery.Add(Assert.IsType<GalleryEntryKey>(entry));
            return Task.CompletedTask;
        };

        List<string> favorites = [];
        bool marked = input == "unmark";
        page.IsArchiveFavorite = _ => marked;
        page.EffectiveArchiveFavorite = _ => marked;
        page.ToggleArchiveFavoriteAsync = entry =>
        {
            favorites.Add(entry.Asset.Path);
            marked = !marked;
            return Task.CompletedTask;
        };
        page.SetArchiveFavoriteAsync = (entry, value) =>
        {
            favorites.Add(entry.Asset.Path);
            marked = value;
            return Task.CompletedTask;
        };

        posts.SelectedItem = next;
        ContextGestures.Settle(window);
        Assert.Same(next, posts.SelectedItem);
        Assert.Contains("a.png", sidebar.HeaderLine);
        Assert.Same(previousThumbnail, SidebarSecondThumbnail(sidebar));

        Invoke(previousThumbnail);
        await page.FavoriteKeyTask;
        Assert.False(page.ArchiveMediaMenu?.IsOpen == true);
        Assert.Empty(favorites);
        Assert.Empty(gallery);
        Assert.Contains("a.png", sidebar.HeaderLine);
        Assert.Equal(0, sidebar.StripCurrent);
        Assert.All(media, file => Assert.Empty(ReadSavedTags(window).TagsOf(file.Subject)));

        now += ReaderMediaSidebarRules.FollowIntervalMs;
        page.TickMediaSidebarFollow();
        await BooruTestSupport.WaitAsync(() => sidebar.HeaderLine.Contains("c.png", StringComparison.Ordinal)
            && Picture(sidebar).IsEffectivelyVisible && !sidebar.IsDimmed, "the Reader to present the next post");
        await page.MediaSidebarStripTask;
        ContextGestures.Settle(window);
        ListBoxItem thumbnail = SidebarSecondThumbnail(sidebar);
        Assert.Contains("d.png", AutomationProperties.GetName(thumbnail));
        Invoke(thumbnail);
        await page.FavoriteKeyTask;
        switch (input)
        {
            case "right-click":
            case "menu-key":
                QuickTagSurface editor = await OpenTagsAsync(page.ArchiveMediaMenu);
                editor.Input.Text = "shown-thumbnail";
                Assert.True(await editor.CommitTypedAsync());
                PrivateTagStoreSnapshot tags = ReadSavedTags(window);
                Assert.Equal("shown-thumbnail", Assert.Single(tags.TagsOf(media[3].Subject)).Name.Display);
                Assert.All(media.Take(3), file => Assert.Empty(tags.TagsOf(file.Subject)));
                break;
            case "enter":
            case "double-click":
                await BooruTestSupport.WaitAsync(() => gallery.Count != 0, "the thumbnail to open in the gallery");
                GalleryEntryKey entry = Assert.Single(gallery);
                Assert.Equal("44", entry.PostId);
                Assert.Equal(1, entry.AttachmentPosition);
                MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(entry.Asset.Directory);
                MediaAssetReference target = Assert.Single(manifest.GetPage(), candidate => candidate.AssetId == entry.Asset.AssetId);
                Assert.Equal(media[3].Path, target.Path);
                break;
            default:
                Assert.Equal(media[3].Path, Assert.Single(favorites));
                Assert.Equal(input != "unmark", marked);
                break;
        }

        void Invoke(ListBoxItem thumbnail)
        {
            switch (input)
            {
                case "right-click": ContextGestures.RightClick(thumbnail); break;
                case "menu-key": ContextGestures.MenuKey(thumbnail); break;
                case "favorite": ContextGestures.KeyStroke(thumbnail, Key.F, PhysicalKey.F); break;
                case "mark": ContextGestures.KeyStroke(thumbnail, Key.M, PhysicalKey.M); break;
                case "unmark": ContextGestures.KeyStroke(thumbnail, Key.U, PhysicalKey.U); break;
                case "enter": ContextGestures.KeyStroke(thumbnail, Key.Enter, PhysicalKey.Enter); break;
                case "double-click": ContextGestures.DoubleClick(thumbnail); break;
            }
        }
    }, readerStrip: true);

    [Fact]
    public void ReaderThumbnailWithAnOldAttachmentRevisionCannotOpenTheReplacement() => Run(async (window, _) =>
    {
        long now = 0;
        window.ArchivePage.UseManualReaderTicks(() => now);
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        await page.MediaSidebarStripTask;
        ListBox posts = SidePanelDesignTests.ById<ListBox>(page, "ArchivePosts");
        ArchivePostRow post = Assert.IsType<ArchivePostRow>(posts.SelectedItem);
        ArchiveAttachmentRow old = Assert.Single(post.Attachments, item => item.Attachment.Position == 1);
        ListBoxItem thumbnail = SidebarSecondThumbnail(sidebar);
        Assert.Contains("b.png", AutomationProperties.GetName(thumbnail));
        Assert.True(sidebar.HasStripPicture(1));

        List<string> opened = [];
        page.OpenGallery = (_, _, _) => { opened.Add("b.png"); return Task.CompletedTask; };
        TaskCompletionSource loading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchiveAttachmentRow replacement = old with
        {
            Attachment = old.Attachment with { OriginalFileName = "b-revised.png", AttachmentRevisionKey = "revised" },
            OpenPreview = () => opened.Add("b-revised.png"),
            LoadStripThumbnail = async (_, _, token) => { await loading.Task.WaitAsync(token); return null; }
        };
        ArchivePostRow revised = post with { Attachments = [.. post.Attachments.Select(row => ReferenceEquals(row, old) ? replacement : row)] };
        try
        {
            posts.ItemsSource = new[] { revised };
            posts.SelectedItem = revised;
            ContextGestures.Settle(window);
            Assert.Same(thumbnail, SidebarSecondThumbnail(sidebar));

            ContextGestures.DoubleClick(thumbnail);
            Assert.Empty(opened);

            now += ReaderMediaSidebarRules.FollowIntervalMs;
            page.TickMediaSidebarFollow();
            ContextGestures.Settle(window);
            ListBoxItem current = SidebarSecondThumbnail(sidebar);
            Assert.Contains("b-revised.png", AutomationProperties.GetName(current));
            Assert.False(sidebar.HasStripPicture(1));

            ContextGestures.KeyStroke(current, Key.Enter, PhysicalKey.Enter);
            Assert.Equal("b-revised.png", Assert.Single(opened));
        }
        finally { loading.TrySetResult(); }
    });

    [Theory]
    [InlineData("favorites.toggle")]
    [InlineData("media.copy-path")]
    [InlineData("media.open-external")]
    public void LegacyThumbnailCommandsFollowTheDisplayedFileWhenOnlyItsChecksumChanges(string command) => Run(async (window, media) =>
    {
        long now = 0;
        window.ArchivePage.UseManualReaderTicks(() => now);
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        await page.MediaSidebarStripTask;
        ListBox posts = SidePanelDesignTests.ById<ListBox>(page, "ArchivePosts");
        ArchivePostRow post = Assert.IsType<ArchivePostRow>(posts.SelectedItem);
        ArchiveAttachmentRow old = Assert.Single(post.Attachments, row => row.Attachment.Position == 1);
        ArchiveAttachmentRow other = Assert.Single(post.Attachments, row => row.Attachment.Position == 0);
        Assert.Null(old.Attachment.AttachmentRevisionKey);
        TaskCompletionSource loadReplacement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ArchiveAttachmentRow replacement = old with
        {
            Attachment = old.Attachment with { ExpectedMd5 = other.Attachment.ExpectedMd5 },
            LoadStripThumbnail = async (width, height, token) =>
            {
                await loadReplacement.Task.WaitAsync(token);
                return await other.LoadStripThumbnail!(width, height, token);
            }
        };
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(media[0].Path)!);
        Assert.Equal(media[1].Path, MediaAttachmentResolver.Resolve(manifest, Thread, post.Id, old.Attachment)?.Asset.Path);
        Assert.Equal(media[0].Path, MediaAttachmentResolver.Resolve(manifest, Thread, post.Id, replacement.Attachment)?.Asset.Path);

        List<string> targets = [];
        Task Record(string path) { targets.Add(path); return Task.CompletedTask; }
        page.IsArchiveFavorite = _ => false;
        page.ToggleArchiveFavoriteAsync = entry => Record(entry.Asset.Path);
        page.CopyFilePathAsync = Record;
        page.OpenMediaExternallyAsync = entry => Record(entry.Asset.Path);
        ListBoxItem thumbnail = SidebarSecondThumbnail(sidebar);
        Assert.True(sidebar.HasStripPicture(1));
        await InvokeIfOpenAsync(thumbnail);
        await BooruTestSupport.WaitAsync(() => targets.Count > 0, "the original thumbnail file command");
        Assert.Equal(media[1].Path, Assert.Single(targets));
        targets.Clear();

        ArchivePostRow revised = post with { Attachments = [other, replacement] };
        posts.ItemsSource = new[] { revised };
        posts.SelectedItem = revised;
        ContextGestures.Settle(window);
        Assert.Same(thumbnail, SidebarSecondThumbnail(sidebar));
        await InvokeIfOpenAsync(thumbnail);
        Assert.DoesNotContain(media[0].Path, targets);

        now += ReaderMediaSidebarRules.FollowIntervalMs;
        try
        {
            page.TickMediaSidebarFollow();
            ContextGestures.Settle(window);
            Assert.False(sidebar.HasStripPicture(1), "The new binding must release the old file's thumbnail.");
        }
        finally { loadReplacement.TrySetResult(); }
        await page.MediaSidebarStripTask;
        ContextGestures.Settle(window);
        Assert.True(sidebar.HasStripPicture(1));
        targets.Clear();
        await InvokeIfOpenAsync(SidebarSecondThumbnail(sidebar));
        await BooruTestSupport.WaitAsync(() => targets.Count > 0, "the replacement thumbnail file command");
        Assert.Equal(media[0].Path, Assert.Single(targets));

        async Task InvokeIfOpenAsync(ListBoxItem item)
        {
            ContextGestures.RightClick(item);
            if (page.ArchiveMediaMenu is not { IsOpen: true } menu) return;
            await BooruTestSupport.WaitAsync(() => MenuProbe.Entries(menu).Any(entry => entry.Id == command && entry.Enabled),
                "the file command to be ready");
            Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == command).Activate();
            ContextGestures.Settle(window);
            if (menu.IsOpen) menu.Close();
        }
    }, legacy: true);

    [Fact]
    public void ControlClickOnADetachedReaderThumbnailOpensThatFilesMenu() => Run(async (window, media) =>
    {
        (ArchivePage page, _) = await OpenReaderAsync(window);
        Control frame = page.DetachReader();
        // Drain the previous root's queued layout before the same controls join the new root.
        ContextGestures.Settle(window);
        Window detached = new() { Width = 1700, Height = 900, Content = frame };
        try
        {
            detached.Show();
            detached.PlatformImpl!.Activated?.Invoke();
            await BooruTestSupport.WaitAsync(() =>
            {
                detached.UpdateLayout();
                return page.MediaSidebar is { StripVisible: true } sidebar && sidebar.HasStripPicture(1);
            }, "the detached reader's thumbnail strip");
            List<string> paths = [];
            page.CopyFilePathAsync = path => { paths.Add(path); return Task.CompletedTask; };
            ContextClick.MacOSForTests = true;
            ContextGestures.ControlClick(SidebarSecondThumbnail(page.MediaSidebar!));
            ContextMenu menu = Assert.IsType<ContextMenu>(page.ArchiveMediaMenu);
            Assert.True(menu.IsOpen);
            Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "media.copy-path").Activate();
            Assert.Equal(media[1].Path, Assert.Single(paths));
        }
        finally
        {
            ContextClick.MacOSForTests = null;
            detached.Content = null;
            ContextGestures.Settle(detached);
            page.AttachReader(frame);
            detached.Close();
        }
    });

    [Fact]
    public void ReaderPictureOpensTheDisplayedAttachmentBeforeTheNextFollowTick() => Run(async (window, media) =>
    {
        long now = 0;
        window.ArchivePage.UseManualReaderTicks(() => now);
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        ListBox posts = SidePanelDesignTests.ById<ListBox>(page, "ArchivePosts");
        GalleryEntryKey? opened = null;
        page.OpenGallery = (_, entry, _) => { opened = entry; return Task.CompletedTask; };

        posts.SelectedItem = Assert.Single(posts.Items.OfType<ArchivePostRow>(), post => post.Id == "44");
        ContextGestures.Settle(window);
        Assert.Contains("a.png", sidebar.HeaderLine);
        ContextGestures.DoubleClick(Picture(sidebar));

        await BooruTestSupport.WaitAsync(() => opened != null, "the displayed picture to open in the gallery");
        GalleryEntryKey entry = Assert.IsType<GalleryEntryKey>(opened);
        Assert.Equal("43", entry.PostId);
        Assert.Equal(0, entry.AttachmentPosition);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(entry.Asset.Directory);
        Assert.Equal(media[0].Path, Assert.Single(manifest.GetPage(), asset => asset.AssetId == entry.Asset.AssetId).Path);
    }, readerStrip: true);

    private static ListBoxItem SidebarSecondThumbnail(ReaderMediaSidebar sidebar)
    {
        Border thumbnail = Assert.Single(sidebar.GetVisualDescendants().OfType<Border>(), control =>
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId() == "ReaderMediaSidebarThumbnail2");
        return Assert.IsType<ListBoxItem>(ItemActivation.ContainerAt(thumbnail, sidebar.Strip));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SidebarVideoPointerActivationBelongsToTheDisplayedMedium(bool crossesPresentation) => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        ReaderInlineVideoSlot slot = Assert.IsType<ReaderInlineVideoSlot>(page.SidebarInlineVideo);
        ReaderInlineVideoController controller = page.InlineVideo;
        await controller.ConfigureAsync(controller.Options with { Enabled = true, SidebarAutoplay = false });
        GalleryEntry video = await VideoEntryAsync(media[1].Path);
        MediaAssetReference poster = Assert.Single(video.Manifest.GetPage(), entry => entry.Path == media[1].Path);
        (Avalonia.Media.Imaging.Bitmap bitmap, GalleryImageInfo _) = await GalleryImageCache.DecodeAsync(video with { Asset = poster },
            new GalleryDecodeRequest.Fit(120, 120), CancellationToken.None);
        TaskCompletionSource<ReaderMediaSidebarContent> posterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Image picture = Picture(sidebar);
        object? old = picture.Source;
        int galleryRequests = 0;
        page.OpenGallery = (_, _, _) => Task.CompletedTask;
        sidebar.GalleryRequested += (_, _) => galleryRequests++;
        int resolved = 0;
        TestVideoPlayer fake = new(Task.CompletedTask);
        Task presentation = Task.CompletedTask;
        try
        {
            if (crossesPresentation) ContextGestures.Click(picture);
            ReaderMediaLoadTicket ticket = sidebar.Load.Begin("next-video", (120, 120));
            sidebar.Describe("43", "b.mp4", string.Empty, true, mediumKey: "next-video");
            slot.Offer(controller, "next-video", _ =>
            {
                resolved++;
                return Task.FromResult<GalleryEntry?>(video);
            }, "b.mp4", () => galleryRequests++);
            Assert.True(await controller.PlayAsync(slot, () => fake));

            ReaderMediaSidebarImageLoad load = new(sidebar, "next-video", ticket, new GalleryFitPreferences(), () => true, CancellationToken.None);
            presentation = load.RunAsync(_ => posterReady.Task);
            Assert.Same(old, picture.Source);
            Assert.True(sidebar.IsDimmed);
            if (!crossesPresentation)
            {
                ContextGestures.Click(picture);
                await slot.ActivationTask;
                Assert.Equal(0, galleryRequests);
                Assert.Equal(0, resolved);
            }

            posterReady.TrySetResult(new ReaderMediaSidebarContent.Picture(bitmap, GalleryImageCache.Shared.Release, Playable: true));
            await presentation;
            Assert.Same(bitmap, picture.Source);
            if (crossesPresentation)
            {
                ContextGestures.Click(picture);
                await slot.ActivationTask;
                Assert.Equal(0, galleryRequests);
                Assert.Equal(0, resolved);
            }

            Point center = new Rect(picture.Bounds.Size).Center;
            ContextGestures.Click(picture, point: new Point(center.X + picture.Bounds.Width / 3, center.Y));
            await slot.ActivationTask;
            Assert.Equal(1, resolved);
            Assert.Equal(1, fake.Plays);
            Assert.Equal(0, galleryRequests);
        }
        finally
        {
            if (posterReady.TrySetResult(new ReaderMediaSidebarContent.Picture(bitmap, GalleryImageCache.Shared.Release, Playable: true)))
                await presentation;
            await controller.ReleaseAllAsync();
        }
    });

    [Fact]
    public void RejectedControlClickDoesNotStartTheMediaPanelVideoOnRelease() => Run(async (window, media) =>
    {
        var (sidebar, selectNext, menu) = await OpenPreviewAsync(window, reader: false);
        MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
        ReaderInlineVideoSlot slot = Assert.IsType<ReaderInlineVideoSlot>(page.PanelVideo);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        Image picture = Picture(sidebar);
        object? old = picture.Source;
        sidebar.BeforePreviewDecodeForTests = (path, token) => path == media[1].Path ? resume.Task.WaitAsync(token) : Task.CompletedTask;
        try
        {
            selectNext();
            await BooruTestSupport.WaitAsync(() => sidebar.IsDimmed && page.PanelRow?.Entry.Name == "b.png", "the pending poster");
            slot.Offer(page.PanelVideoController, MediaPage.PanelVideoKey(page.PanelRow!.Entry), _ =>
            {
                starts++;
                return Task.FromResult<GalleryEntry?>(null);
            }, "b.mp4");
            Assert.True(slot.IsOffered);
            ContextClick.MacOSForTests = true;
            ContextGestures.Press(picture, MouseButton.Left, modifiers: RawInputModifiers.Control);
            Assert.False(menu()?.IsOpen == true);
            Assert.Equal(0, starts);
            resume.TrySetResult();
            await BooruTestSupport.WaitAsync(() => !sidebar.IsDimmed && picture.Source != old, "the new poster");
            ContextGestures.Release(picture, MouseButton.Left, modifiers: RawInputModifiers.Control);
            await slot.ActivationTask;
            Assert.Equal(0, starts);
            Assert.False(menu()?.IsOpen == true);
            ContextGestures.Click(slot.Activator);
            await slot.ActivationTask;
            Assert.Equal(1, starts);
        }
        finally { resume.TrySetResult(); sidebar.BeforePreviewDecodeForTests = null; ContextClick.MacOSForTests = null; }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedPlayClickReturnsFocusToTheWorkingList(bool reader) => Run(async (window, _) =>
    {
        var (sidebar, _, _) = await OpenPreviewAsync(window, reader);
        MediaPage? media = window.MediaPageForTests;
        await (reader ? window.ArchivePage.MediaSidebarTask : media!.PanelTask);
        ContextGestures.Settle(window);
        ReaderInlineVideoSlot slot = Assert.IsType<ReaderInlineVideoSlot>(reader ? window.ArchivePage.SidebarInlineVideo : media!.PanelVideo);
        ReaderInlineVideoController controller = reader ? window.ArchivePage.InlineVideo : media!.PanelVideoController;
        await controller.ConfigureAsync(controller.Options with { Enabled = true, SidebarAutoplay = false });
        Control work = reader ? SidePanelDesignTests.ById<ListBox>(window.ArchivePage, "ArchivePosts") : media!.Grid;
        Control frame = SidePanelDesignTests.ById<Control>(sidebar, "ReaderMediaSidebarFrame");
        frame.Focusable = true;
        Assert.True(frame.Focus());
        sidebar.Describe("43", "b.mp4", string.Empty, true, mediumKey: "pending-video");
        int starts = 0;
        slot.Offer(controller, "pending-video", _ => { starts++; return Task.FromResult<GalleryEntry?>(null); }, "b.mp4");
        Assert.True(sidebar.IsShowingStalePicture);
        ContextGestures.Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Point center = slot.Activator.TranslatePoint(new Rect(slot.Activator.Bounds.Size).Center, window)!.Value;
        Assert.Contains(slot.Activator, Assert.IsAssignableFrom<Visual>(window.InputHitTest(center)).GetSelfAndVisualAncestors());
        ContextGestures.Click(slot.Activator);
        await slot.ActivationTask;
        Assert.Equal(0, starts);
        Assert.True(work.IsKeyboardFocusWithin, "The rejected play click must return the keys to the working list.");
        Assert.False(slot.Activator.Focus());
        Assert.False(slot.Activator.IsTabStop);
    });

    [Fact]
    public void InlineVideoOfferChangingDuringFactoryYieldDoesNotRegisterTheOldPlayer() => Run(async (window, media) =>
    {
        (ArchivePage page, ReaderMediaSidebar sidebar) = await OpenReaderAsync(window);
        ReaderInlineVideoSlot slot = Assert.IsType<ReaderInlineVideoSlot>(page.SidebarInlineVideo);
        ReaderInlineVideoController controller = page.InlineVideo;
        await controller.ConfigureAsync(controller.Options with { Enabled = true, SidebarAutoplay = false, MaxPlayers = 2 });
        GalleryEntry a = await VideoEntryAsync(media[0].Path);
        GalleryEntry b = await VideoEntryAsync(media[1].Path);
        TaskCompletionSource finishPausing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestVideoPlayer blocker = new(finishPausing.Task);
        Assert.True(await controller.PlayAsync(new TestVideoOwner(), () => blocker));
        long created = controller.Created;
        TaskCompletionSource resumeFactory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task activation = Task.CompletedTask;
        try
        {
            sidebar.Describe("43", "a.mp4", string.Empty, true, mediumKey: "video-a");
            slot.Offer(controller, "video-a", _ => Task.FromResult<GalleryEntry?>(a), "a.mp4");
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new HeldPresentation(resumeFactory.Task));
            try { activation = slot.PlayAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.Equal(created, controller.Created);

            sidebar.Describe("43", "b.mp4", string.Empty, true, mediumKey: "video-b");
            slot.Offer(controller, "video-b", _ => Task.FromResult<GalleryEntry?>(b), "b.mp4");
            resumeFactory.TrySetResult();
            await BooruTestSupport.WaitAsync(() => activation.IsCompleted || controller.Created != created,
                "the activation to return from the factory yield");
            Assert.Equal(created, controller.Created);
            Assert.False(slot.IsLive);
            Assert.False(controller.IsLive(slot));
        }
        finally
        {
            resumeFactory.TrySetResult();
            await slot.ReleaseAsync();
            finishPausing.TrySetResult();
            await activation;
            await controller.ReleaseAllAsync();
        }
    });

    private static async Task<GalleryEntry> VideoEntryAsync(string path)
    {
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(path)!);
        MediaAssetReference asset = Assert.Single(manifest.GetPage(), entry => entry.Path == path);
        return new GalleryEntry(new GalleryAssetKey(Thread, manifest.Directory, asset.AssetId), manifest,
            asset with { OriginalFileName = Path.GetFileNameWithoutExtension(path) + ".mp4" }, null, null, false, true);
    }

    private sealed class TestVideoPlayer(Task finishPausing) : IReaderInlineVideoPlayer
    {
        internal int Plays { get; private set; }
        public bool IsPlaying { get; private set; }
        public Task StartAsync() { IsPlaying = true; return Task.CompletedTask; }
        public Task PlayAsync() { Plays++; IsPlaying = true; return Task.CompletedTask; }
        public async Task PauseAsync() { await finishPausing; IsPlaying = false; }
        public ValueTask DisposeAsync() { IsPlaying = false; return ValueTask.CompletedTask; }
    }

    private sealed class TestVideoOwner : IReaderInlineVideoOwner
    {
        public void PlayerReleased() { }
        public void CurrentChanged(bool current) { }
        public void PlaybackChanged(bool playing) { }
    }

    private static Image Picture(ReaderMediaSidebar sidebar) => SidePanelDesignTests.ById<Image>(sidebar, "ReaderMediaSidebarPicture");

    private static async Task<(ReaderMediaSidebar Sidebar, Action Next, Func<ContextMenu?> Menu)> OpenPreviewAsync(MainWindow window, bool reader)
    {
        if (reader)
        {
            (ArchivePage archive, ReaderMediaSidebar preview) = await OpenReaderAsync(window);
            return (preview, () => archive.SelectMediaSidebarIndex(1), () => archive.ArchiveMediaMenu);
        }
        MediaPage page = await OpenMediaAsync(window);
        MediaLibraryTileRow a = Assert.Single(page.Grid.Items.OfType<MediaLibraryTileRow>(), row => row.Entry.Name == "a.png");
        MediaLibraryTileRow b = Assert.Single(page.Grid.Items.OfType<MediaLibraryTileRow>(), row => row.Entry.Name == "b.png");
        page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
        page.Grid.SelectedItem = a;
        ReaderMediaSidebar sidebar = await MediaSidebarAsync(window, page, "a.png");
        return (sidebar, () => page.Grid.SelectedItem = b, () => page.MediaMenu);
    }

    private static async Task<(ArchivePage Page, ReaderMediaSidebar Sidebar, ListBox Tiles)> OpenSavedPreviewAsync(MainWindow window)
    {
        ArchivePage page = await OpenArchiveAsync(window);
        page.MediaTilesPreference = true;
        window.UpdateLayout();
        TabControl tabs = SidePanelDesignTests.ById<TabControl>(page, "ArchiveContentTabs");
        tabs.SelectedItem = Assert.Single(tabs.Items.OfType<TabItem>(),
            tab => AutomationProperties.GetAutomationId(tab) == "ArchiveMediaTab");
        ContextGestures.Settle(window);
        ListBox tiles = SidePanelDesignTests.ById<ListBox>(page, "ArchiveMediaTiles");
        await BooruTestSupport.WaitAsync(() => tiles.ItemCount == 2 && page.IsMediaSidebarShown, "both saved media tiles and the sidebar");
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        await WaitForSavedPictureAsync(window, sidebar, tiles, "a.png");
        return (page, sidebar, tiles);
    }

    private static async Task WaitForSavedPictureAsync(MainWindow window, ReaderMediaSidebar sidebar, ListBox tiles, string name)
    {
        ArchiveAssetRow row = Assert.Single(tiles.Items.OfType<ArchiveAssetRow>(), item => item.Name == name);
        if (!ReferenceEquals(tiles.SelectedItem, row)) tiles.SelectedItem = row;
        await BooruTestSupport.WaitAsync(() =>
        {
            window.UpdateLayout();
            return sidebar.HeaderLine.Contains(name, StringComparison.Ordinal) && Picture(sidebar).IsEffectivelyVisible && !sidebar.IsDimmed;
        }, "the Saved Media sidebar to show " + name);
    }

    private static async Task<QuickTagSurface> OpenTagsAsync(ContextMenu? activeMenu)
    {
        ContextMenu menu = Assert.IsType<ContextMenu>(activeMenu);
        Assert.True(menu.IsOpen);
        Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "media.rating.edit").Activate();
        await BooruTestSupport.WaitAsync(() => QuickTagPopover.Current is { IsOpen: true, Surface: not null }, "the tag editor");
        return QuickTagPopover.Current!.Surface!;
    }

    private static PrivateTagStoreSnapshot ReadSavedTags(MainWindow window) =>
        Assert.IsType<PrivateTagStoreSnapshot>(window.SessionHost.GetPrivateTagSnapshot().Snapshot);

    private static void Navigate(MainWindow window, Key key, PhysicalKey physicalKey, MainWindow.WorkspacePage page)
    {
        RawInputModifiers modifiers = (OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control) | RawInputModifiers.Shift;
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Assert.Equal(page, window.SelectedPage);
    }

    private static async Task<MediaPage> OpenMediaAsync(MainWindow window, int tileCount = 2)
    {
        Navigate(window, Key.E, PhysicalKey.E, MainWindow.WorkspacePage.Media);
        MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
        await page.BuildTask;
        await BooruTestSupport.WaitAsync(() => page.Grid.ItemCount == tileCount, $"{tileCount} saved tiles");
        return page;
    }

    private static async Task<ReaderMediaSidebar> MediaSidebarAsync(MainWindow window, MediaPage page, string name)
    {
        await BooruTestSupport.WaitAsync(() =>
        {
            window.UpdateLayout();
            return page.PanelRow?.Entry.Name == name && page.Panel is { } sidebar && Picture(sidebar).IsEffectivelyVisible;
        }, "the Media panel to show " + name);
        return page.Panel!;
    }

    private static async Task<ArchivePage> OpenArchiveAsync(MainWindow window)
    {
        Navigate(window, Key.A, PhysicalKey.A, MainWindow.WorkspacePage.Archive);
        ArchivePage page = window.ArchivePage;
        await page.ActivateAsync();
        await page.LoadThreadAsync(Thread);
        await page.MediaLoadTask;
        return page;
    }

    private static async Task<(ArchivePage Page, ReaderMediaSidebar Sidebar)> OpenReaderAsync(MainWindow window)
    {
        ArchivePage page = await OpenArchiveAsync(window);
        await BooruTestSupport.WaitAsync(() =>
        {
            window.UpdateLayout();
            return page.IsMediaSidebarShown && page.MediaSidebar is { } sidebar
                && sidebar.HeaderLine.Contains("a.png", StringComparison.Ordinal) && Picture(sidebar).IsEffectivelyVisible;
        }, "the Reader panel to show a.png");
        return (page, page.MediaSidebar!);
    }

    internal sealed record SavedFile(string Path, MediaTagSubject Subject);

    internal static void Run(Func<MainWindow, SavedFile[], Task> test, bool loose = false, bool identicalOccurrences = false, bool readerStrip = false, bool legacy = false,
        bool copies = false, bool unnamedSecond = false) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            using TempDirectory profile = new();
            using TempDirectory directory = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "downloads"), FirstRunSetupVersion = 1, DownloadsPaused = true,
                WorkspaceMode = "Advanced", Language = "en", ReaderMediaSidebarScope = "Always", ReaderMediaSidebarMinimumWidth = 1000,
                MediaSidebarAutoHide = true, MediaSidebarInfo = "Always", MediaSidebarPinned = false
            }.SaveConfig(profile.Path);
            SavedFile[] media = await SeedAsync(profile.Path, directory.Path, loose, identicalOccurrences, readerStrip, legacy, copies, unnamedSecond);
            await using DesktopSessionHost host = new(profile.Path, [],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(host) { Width = 1700, Height = 900, Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            try { await test(window, media); }
            finally { QuickTagPopover.Current?.Close(); }
        }
    });

    private static async Task<SavedFile[]> SeedAsync(string profile, string directory, bool loose, bool identicalOccurrences, bool readerStrip, bool legacy,
        bool copies, bool unnamedSecond)
    {
        const string first = "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg==";
        const string second = "iVBORw0KGgoAAAANSUhEUgAAABMAAAANCAIAAADE7sJwAAAAFklEQVR4nGMwdgklDzGM6hzVOQR1AgDPR8TVSHrz+wAAAABJRU5ErkJggg==";
        if (legacy)
        {
            byte[] a = Convert.FromBase64String(first), b = Convert.FromBase64String(second);
            await ArchiveCleanupFixtures.AssetsAsync(directory, new("777", "original", "a.png", "a.png", a),
                new("777", "original", "b.png", "b.png", b));
            CapturedAttachment Attachment(int position, string name, byte[] bytes) =>
                new(position, "777", OriginalFileName: name, ExpectedMd5: Convert.ToBase64String(MD5.HashData(bytes)));
            CapturedPost post = new("43", 0, PostFieldPresence.All, Html: "Legacy pictures",
                Attachments: [Attachment(0, "a.png", a), Attachment(1, "b.png", b)]);
            ArchivedThreadView view = new(new(Thread, "https://fixture.invalid/g/thread/42", "Legacy files", directory,
                ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1), [post], [directory], true);
            await RateAndTagWindowFixture.SeedAsync(profile, view);
            // Today's writer stamps every capture. Restore the two rows as they were stored by a legacy archive.
            using (var database = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadWrite))
            using (var unstamped = database.CreateCommand())
            {
                unstamped.CommandText = "UPDATE archive_attachments SET attachment_revision_key = NULL WHERE post = '43'";
                Assert.Equal(2, unstamped.ExecuteNonQuery());
            }
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
            return GallerySelection.Build(view, [manifest], allSavedVersions: true).Entries.OrderBy(entry => entry.Asset.Path)
                .Select(entry => new SavedFile(entry.Asset.Path, Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry)))).ToArray();
        }
        if (readerStrip)
        {
            const string third = "iVBORw0KGgoAAAANSUhEUgAAABcAAAARCAIAAAC5EaDqAAAAGklEQVR4nGMw36hIOWIYNWXUlFFTRk2hkSkAFVCUz4rmaxkAAAAASUVORK5CYII=";
            const string fourth = "iVBORw0KGgoAAAANSUhEUgAAAB0AAAATCAIAAADj+5EoAAAAHklEQVR4nGO4J69NC8Qwau6ouaPmjpo7au6ouUQgAIQrfTfBcPqEAAAAAElFTkSuQmCC";
            return await SeedThreadAsync(profile, directory, Thread, loose: false, unnamed: null,
                ("43", [("a.png", first), ("b.png", second)]), ("44", [("c.png", third), ("d.png", fourth)]));
        }
        if (!identicalOccurrences)
            return await SeedThreadAsync(profile, directory, Thread, loose, unnamedSecond ? "b.png" : null,
                ("43", [("a.png", first), ("b.png", copies ? first : second)]));

        SavedFile[] original = await SeedThreadAsync(profile, Path.Combine(directory, "first"), Thread, loose: false, unnamed: null, ("43", [("a.png", first)]));
        SavedFile[] duplicate = await SeedThreadAsync(profile, Path.Combine(directory, "second"), new("fixture", "g", "44"),
            loose: false, unnamed: null, ("45", [("b.png", first)]));
        return [.. original, .. duplicate];
    }

    // unnamed: the file whose capture carries no original file name (an attachment the board sent without one).
    private static async Task<SavedFile[]> SeedThreadAsync(string profile, string directory, ResourceId threadId, bool loose, string? unnamed,
        params (string PostId, (string Name, string Encoded)[] Pictures)[] fixtures)
    {
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        List<CapturedPost> posts = [];
        foreach (var (postId, pictures) in fixtures)
        {
            List<CapturedAttachment> attachments = [];
            for (int index = 0; index < pictures.Length; index++)
            {
                (string name, string encoded) = pictures[index];
                string id = (777 + entries.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string url = "https://fixture.invalid/" + name;
                byte[] bytes = Convert.FromBase64String(encoded);
                string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                string md5 = Convert.ToBase64String(MD5.HashData(bytes));
                await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
                entries.Add(name, new { MediaIdentifier = id, Variant = "original", SourceUrl = url, FileName = name,
                    Length = bytes.Length, Sha256 = sha256, Md5 = md5, OriginalFileName = name });
                string? capturedName = name == unnamed ? null : name;
                MediaProviderMetadata provider = new(id, "original", url, capturedName, bytes.Length, md5, null);
                attachments.Add(new(index, id, Url: url, OriginalFileName: capturedName, ExpectedSize: bytes.Length, ExpectedMd5: md5)
                { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(threadId, postId, index, provider) });
            }
            posts.Add(new(postId, posts.Count, PostFieldPresence.All, Html: "Two pictures", Attachments: attachments));
        }
        string manifestPath = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        MediaAssetSnapshot legacy = await MediaAssetReader.OpenAsync(directory);
        Dictionary<string, object> assets = new();
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        foreach (CapturedPost post in posts)
        foreach (CapturedAttachment attachment in post.Attachments!)
        {
            MediaAssetReference saved = Assert.Single(legacy.GetPage(), asset => asset.MediaIdentifier == attachment.MediaId);
            assets.Add(saved.AssetId, new { RequestedFileName = saved.RequestedFileName, Entry = entries[saved.RequestedFileName] });
            MediaProviderMetadata provider = new(saved.MediaIdentifier, saved.Variant, saved.SourceUrl, attachment.OriginalFileName,
                attachment.ExpectedSize, attachment.ExpectedMd5, null);
            string key = attachment.AttachmentRevisionKey!;
            if (!loose)
                bindings.Add(key, new(new(threadId, post.PostId, attachment.Position, key), MediaRevisionKeys.CreateAcquisitionKey(threadId, provider), saved.AssetId,
                    provider, new(saved.Length, saved.Md5, saved.Sha256), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch)));
        }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        ArchivedThreadView thread = new(new(threadId, "https://fixture.invalid/g/thread/" + threadId.Thread, "Sidebar gesture fixture", directory,
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, posts.Count, 1),
            loose ? posts.Select(post => post with { Attachments = null }).ToArray() : posts, [directory], decimalPostOrder: true);
        await RateAndTagWindowFixture.SeedAsync(profile, thread);
        return GallerySelection.Build(thread, [manifest], allSavedVersions: true, includeOtherMedia: loose).Entries.OrderBy(entry => entry.Asset.RequestedFileName)
            .Select(entry => new SavedFile(entry.Asset.Path, Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry)))).ToArray();
    }
}
