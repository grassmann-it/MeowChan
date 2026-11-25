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
        SidePanelDesignTests.ById<TabItem>(page, "ArchiveMediaTab").IsSelected = true;
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

    private static async Task<MediaPage> OpenMediaAsync(MainWindow window)
    {
        Navigate(window, Key.E, PhysicalKey.E, MainWindow.WorkspacePage.Media);
        MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
        await page.BuildTask;
        await BooruTestSupport.WaitAsync(() => page.Grid.ItemCount == 2, "both saved tiles");
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

    private sealed record SavedFile(string Path, MediaTagSubject Subject);

    private static void Run(Func<MainWindow, SavedFile[], Task> test, bool loose = false) => BooruComponentDispatcher.Run(() =>
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
            SavedFile[] media = await SeedAsync(profile.Path, directory.Path, loose);
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

    private static async Task<SavedFile[]> SeedAsync(string profile, string directory, bool loose)
    {
        string[] pictures =
        [
            "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg==",
            "iVBORw0KGgoAAAANSUhEUgAAABMAAAANCAIAAADE7sJwAAAAFklEQVR4nGMwdgklDzGM6hzVOQR1AgDPR8TVSHrz+wAAAABJRU5ErkJggg=="
        ];
        Dictionary<string, object> entries = new();
        List<CapturedAttachment> attachments = [];
        for (int i = 0; i < pictures.Length; i++)
        {
            string name = i == 0 ? "a.png" : "b.png";
            string id = i == 0 ? "777" : "778";
            string url = "https://fixture.invalid/" + name;
            byte[] bytes = Convert.FromBase64String(pictures[i]);
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string md5 = Convert.ToBase64String(MD5.HashData(bytes));
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
            entries.Add(name, new { MediaIdentifier = id, Variant = "original", SourceUrl = url, FileName = name,
                Length = bytes.Length, Sha256 = sha256, Md5 = md5, OriginalFileName = name });
            MediaProviderMetadata provider = new(id, "original", url, name, bytes.Length, md5, null);
            attachments.Add(new(i, id, Url: url, OriginalFileName: name, ExpectedSize: bytes.Length, ExpectedMd5: md5)
            { AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Thread, "43", i, provider) });
        }
        string manifestPath = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        MediaAssetSnapshot legacy = await MediaAssetReader.OpenAsync(directory);
        Dictionary<string, object> assets = new();
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        foreach (CapturedAttachment attachment in attachments)
        {
            MediaAssetReference saved = Assert.Single(legacy.GetPage(), asset => asset.MediaIdentifier == attachment.MediaId);
            assets.Add(saved.AssetId, new { RequestedFileName = saved.RequestedFileName, Entry = entries[saved.RequestedFileName] });
            MediaProviderMetadata provider = new(saved.MediaIdentifier, saved.Variant, saved.SourceUrl, attachment.OriginalFileName,
                attachment.ExpectedSize, attachment.ExpectedMd5, null);
            string key = attachment.AttachmentRevisionKey!;
            if (!loose)
                bindings.Add(key, new(new(Thread, "43", attachment.Position, key), MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), saved.AssetId,
                    provider, new(saved.Length, saved.Md5, saved.Sha256), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch)));
        }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        ArchivedThreadView thread = new(new(Thread, "https://fixture.invalid/g/thread/42", "Sidebar gesture fixture", directory,
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1),
            [new CapturedPost("43", 0, PostFieldPresence.All, Html: "Two pictures", Attachments: loose ? null : attachments)], [directory], decimalPostOrder: true);
        await RateAndTagWindowFixture.SeedAsync(profile, thread);
        return GallerySelection.Build(thread, [manifest], allSavedVersions: true, includeOtherMedia: loose).Entries.OrderBy(entry => entry.Asset.RequestedFileName)
            .Select(entry => new SavedFile(entry.Asset.Path, Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry)))).ToArray();
    }
}
