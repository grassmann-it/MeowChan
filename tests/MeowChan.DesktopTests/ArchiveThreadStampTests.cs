using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveThreadStampTests
{
    private const int Saved = 40;
    private static readonly ResourceId Thread = new("fixture", "g", "42");
    private static readonly ResourceId EmptyThread = new("fixture", "g", "99");

    [Fact]
    public void ThreadMenuTargetsSavedFilesFromPostsAndOneUndoPreservesPreexistingTags() =>
        WithArchive(async (page, window, store, stamp, subjects) =>
        {
            DataGrid threads = ById<DataGrid>(window, "ArchiveThreadTable");
            TabControl tabs = ById<TabControl>(window, "ArchiveContentTabs");
            TabItem posts = ById<TabItem>(window, "ArchivePostsTab");
            Assert.Same(posts, tabs.SelectedItem);

            ContextMenu menu = OpenThreadMenu(threads);
            MenuItem stampAll = MenuItem(menu, "thread.stamp-media");
            Assert.False(stampAll.IsEnabled);
            Assert.Contains(Texts.T("tags.quick.archive.emptyStamp"), AutomationProperties.GetHelpText(stampAll));
            ExecuteAndClose(menu, "thread.tag-media");
            await BooruTestSupport.WaitAsync(() => page.MediaSelectionTagPopover?.Surface?.Shown.Any(tag => tag.Name.Display == "tabby") == true,
                "the opening post's own tag suggestion");
            QuickTagPopover popover = Assert.IsType<QuickTagPopover>(page.MediaSelectionTagPopover);
            Assert.Contains(popover.Surface!.Shown, tag => tag.Name.Display == "/ptg/" && tag.CreatesTag);
            Assert.DoesNotContain(popover.Surface.Shown, tag => tag.Name.Display is "unmatched" or "123");
            Assert.Same(posts, tabs.SelectedItem);
            Assert.Empty(page.SelectedMediaRows());
            RateAndTagWindowFixture.Escape(window);
            await BooruTestSupport.WaitAsync(() => page.MediaSelectionTagPopover == null && threads.IsKeyboardFocusWithin, "Esc to restore thread focus");

            TagId a = store.Define("a").Tag;
            store.Define("b");
            store.Define("c");
            store.SetMediaSubjects(a, [subjects[0]], true);
            stamp.Replace(["a", "b", "c"]);

            menu = OpenThreadMenu(threads);
            Assert.Contains("3 tags", MenuItem(menu, "thread.stamp-media").Header?.ToString());
            Assert.Contains("40 of 42", MenuItem(menu, "thread.stamp-media").Header?.ToString());
            ExecuteAndClose(menu, "thread.stamp-media");
            Button undo = ById<Button>(window, "ArchiveMediaStampUndo");
            await BooruTestSupport.WaitAsync(() => undo.IsEffectivelyVisible && undo.IsEnabled, "the stamp result and Undo");
            Assert.Same(posts, tabs.SelectedItem);
            Assert.Empty(page.SelectedMediaRows());
            TextBlock status = ById<TextBlock>(window, "ArchiveMediaStampStatus");
            Assert.Contains("a, b, c", status.Text);
            Assert.Contains("40 of 42", status.Text);
            PrivateTagStoreSnapshot stamped = store.ReadSnapshot();
            Assert.All(subjects, subject => Assert.Equal(3, stamped.TagsOf(subject).Count));

            Click(undo);
            await BooruTestSupport.WaitAsync(() => !undo.IsEffectivelyVisible, "the stamp to be undone");
            PrivateTagStoreSnapshot restored = store.ReadSnapshot();
            Assert.Equal(a, Assert.Single(restored.TagsOf(subjects[0])).Id);
            Assert.All(subjects[1..], subject => Assert.Empty(restored.TagsOf(subject)));

            ArchiveThreadRow empty = threads.ItemsSource!.Cast<ArchiveThreadRow>().Single(row => row.Summary.Id == EmptyThread);
            threads.SelectedItems.Add(empty);
            menu = OpenThreadMenu(threads);
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetAutomationId(item) is "thread.tag-media" or "thread.stamp-media");
            menu.Close();
            threads.SelectedItem = empty;
            await page.LoadThreadAsync(EmptyThread);
            await page.MediaLoadTask;
            menu = OpenThreadMenu(threads);
            Assert.All(new[] { "thread.tag-media", "thread.stamp-media" }, id =>
            {
                MenuItem item = MenuItem(menu, id);
                Assert.False(item.IsEnabled);
                Assert.Contains(Texts.T("tags.quick.archive.noSaved"), AutomationProperties.GetHelpText(item));
            });
            menu.Close();
            Assert.False(undo.IsEffectivelyVisible);
        });

    [Fact]
    public void MediaListGestureStampsOnlyTheSelectionAndTheMenuOffersTheWholeThread() =>
        WithArchive(async (page, window, store, stamp, subjects) =>
        {
            TabControl tabs = ById<TabControl>(window, "ArchiveContentTabs");
            tabs.SelectedItem = ById<TabItem>(window, "ArchiveMediaTab");
            Layout(window);
            DataGrid media = ById<DataGrid>(window, "ArchiveMediaGrid");
            Button selectionStamp = ById<Button>(window, "ArchiveMediaSelectionStamp");
            Button undo = ById<Button>(window, "ArchiveMediaStampUndo");
            Assert.True(media.Focus());
            TagId tag = store.Define("tabby").Tag;
            PressStamp(window);
            PrivateTagStoreSnapshot untouched = store.ReadSnapshot();
            Assert.All(subjects, subject => Assert.False(untouched.Carries(tag, subject)));
            Assert.False(undo.IsEffectivelyVisible);

            ArchiveAssetRow row = media.ItemsSource!.Cast<ArchiveAssetRow>().First();
            media.SelectedItem = row;
            Layout(window);
            Assert.True(selectionStamp.IsEffectivelyVisible);
            Assert.False(selectionStamp.IsEnabled);
            Assert.Contains(Texts.T("tags.quick.archive.emptyStamp"), AutomationProperties.GetHelpText(selectionStamp));
            stamp.Replace(["tabby"]);
            Assert.True(selectionStamp.IsEnabled);
            PressStamp(window);
            await BooruTestSupport.WaitAsync(() => undo.IsEffectivelyVisible && undo.IsEnabled, "a stamp on the selected file");
            PrivateTagStoreSnapshot selected = store.ReadSnapshot();
            Assert.Single(subjects.Where(subject => selected.Carries(tag, subject)));
            Assert.Same(row, Assert.Single(page.SelectedMediaRows()));
            Assert.Contains("1 media file", ById<TextBlock>(window, "ArchiveMediaStampStatus").Text);

            Button toolbar = ById<Button>(window, "ArchiveThreadMediaMenuButton");
            Click(toolbar);
            ContextMenu mediaMenu = Assert.IsType<ContextMenu>(toolbar.ContextMenu);
            Assert.True(MenuItem(mediaMenu, "thread.tag-media").IsEnabled);
            ExecuteAndClose(mediaMenu, "thread.stamp-media");
            TextBlock status = ById<TextBlock>(window, "ArchiveMediaStampStatus");
            await BooruTestSupport.WaitAsync(() => undo.IsEnabled && undo.IsEffectivelyVisible
                && status.Text?.Contains("40 of 42", StringComparison.Ordinal) == true, "Undo after the whole-thread stamp");
            PrivateTagStoreSnapshot stamped = store.ReadSnapshot();
            Assert.All(subjects, subject => Assert.True(stamped.Carries(tag, subject)));
            Click(undo);
            await BooruTestSupport.WaitAsync(() => !undo.IsEffectivelyVisible, "Undo to remove the whole-thread stamp");
            PrivateTagStoreSnapshot restored = store.ReadSnapshot();
            Assert.Single(subjects.Where(subject => restored.Carries(tag, subject)));
        });

    [Fact]
    public void MacResolvesTheExistingStampGestureAndTheThreadCommandsCanBeBound()
    {
        EffectiveShortcutBindings defaults = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        ShortcutGesture stamp = Assert.Single(defaults.For(QuickTagCommands.StampApply));
        Assert.Equal("⇧⌘↩", stamp.Display(ShortcutPlatform.MacOS));
        Assert.True(defaults.Matches(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift },
            QuickTagCommands.StampApply, "reader"));
        Assert.Empty(defaults.For("thread.tag-media"));
        Assert.Empty(defaults.For("thread.stamp-media"));
        ShortcutBindingsSection user = new ShortcutBindingsSection()
            .Set("thread.tag-media", [ShortcutGesture.Parse("Primary+Alt+Shift+F8")])
            .Set("thread.stamp-media", [ShortcutGesture.Parse("Primary+Alt+Shift+F9")]);
        EffectiveShortcutBindings custom = EffectiveShortcutBindings.Build(user, ShortcutPlatform.MacOS);
        Assert.DoesNotContain(custom.Issues, issue => issue.BlocksSave);
        Assert.Single(custom.For("thread.tag-media"));
        Assert.Single(custom.For("thread.stamp-media"));
    }

    private static void WithArchive(Func<ArchivePage, Window, PrivateTagStore, QuickTagStamp, MediaTagSubject[], Task> test) =>
        BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
            using TempDirectory profile = new();
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            string mediaPath = Path.Combine(profile.Path, "media");
            Directory.CreateDirectory(mediaPath);
            MediaAssetReference[] files = Enumerable.Range(0, Saved).Select(index =>
                new MediaAssetReference(index.ToString("x64", CultureInfo.InvariantCulture), "m" + index, "original", index + ".png", Path.Combine(mediaPath, index + ".png"),
                    3, new string('a', 64), "https://fixture.invalid/" + index, index + ".png", true)).ToArray();
            MediaAssetSnapshot manifest = new(mediaPath, MediaManifestStatus.Available, 3, "fixture", files);
            CapturedPost[] posts = Enumerable.Range(0, Saved + 2).Select(index => new CapturedPost((42 + index).ToString(), 42 + index,
                PostFieldPresence.All, Subject: index == 0 ? "Pet thread" : null,
                Html: index == 0 ? "<a href='#123'>&gt;&gt;123</a><br><span>&gt;tabby unmatched /ptg/</span>" : "A reply",
                Attachments: [new CapturedAttachment(0, "m" + index, Url: "https://fixture.invalid/" + index)])).ToArray();
            await using (MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile.Path, lease))
            {
                await catalog.CapturePreservingRevisionsAsync(new(Thread, "https://fixture.invalid/g/thread/42", "Pet thread", ThreadSourceState.Archived,
                    DateTimeOffset.UnixEpoch, mediaPath, posts));
                await catalog.CaptureAsync(new(EmptyThread, "https://fixture.invalid/g/thread/99", "Empty", ThreadSourceState.Archived,
                    DateTimeOffset.UnixEpoch, Path.Combine(profile.Path, "empty"), [new CapturedPost("99", 99, PostFieldPresence.All, Html: "Empty")]));
            }
            PrivateTagStore store = new(profile.Path, lease);
            store.Define("tabby");
            store.Define("123");
            QuickTagStamp stamp = new();
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask, _ => { },
                (path, _) => Task.FromResult(path == mediaPath ? manifest : new MediaAssetSnapshot(path, MediaManifestStatus.Missing, null, null, [])))
            {
                ArchiveLayoutPreference = "Table",
                MediaTilesPreference = false,
                MediaTagServices = new(() => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
                    (name, targets, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, targets, revision, token)),
                    (tag, targets, assigned, revision, token) => Task.FromResult(store.SetMediaSubjects(tag, targets, assigned, revision, token))),
                TagPopoverOptions = context => QuickTagPopoverOptions.Defaults(context) with { Stamp = stamp },
                IsAvailable = true
            };
            Window window = new() { Content = page, Width = 1720, Height = 900 };
            string language = Texts.Language;
            try
            {
                Texts.SetLanguage("en");
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await page.ActivateAsync();
                DataGrid threads = ById<DataGrid>(window, "ArchiveThreadTable");
                threads.SelectedItem = threads.ItemsSource!.Cast<ArchiveThreadRow>().Single(row => row.Summary.Id == Thread);
                await page.LoadThreadAsync(Thread);
                await page.MediaLoadTask;
                Layout(window);
                ArchivedThreadView thread = Assert.IsType<ArchivedThreadView>(page.CurrentThread);
                MediaTagSubject[] subjects = GallerySelection.Build(thread, [manifest], includeOtherMedia: true).Entries
                    .Select(entry => Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry))).ToArray();
                Assert.Equal(Saved, subjects.Length);
                await test(page, window, store, stamp, subjects);
            }
            finally
            {
                QuickTagPopover.Current?.Close();
                page.EndLifetime();
                window.Close();
                Texts.SetLanguage(language);
            }
        });

    private static T ById<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static ContextMenu OpenThreadMenu(DataGrid table)
    {
        Assert.True(table.Focus());
        table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        ContextMenu menu = Assert.IsType<ContextMenu>(table.ContextMenu);
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem MenuItem(ContextMenu menu, string id) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetAutomationId(item) == id);

    private static void ExecuteAndClose(ContextMenu menu, string id)
    {
        MenuItem item = MenuItem(menu, id);
        Assert.True(item.IsEnabled);
        item.Command!.Execute(null);
        menu.Close();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressStamp(Window window)
    {
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control | RawInputModifiers.Shift);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Control | RawInputModifiers.Shift);
    }

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
