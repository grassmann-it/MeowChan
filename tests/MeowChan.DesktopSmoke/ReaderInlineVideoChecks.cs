using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

// R10: inline video in the reader, in an isolated page and profile. Always: with the default choice "Show in the sidebar" and no sidebar on
// screen a click on a video opens the gallery path and creates no player; with "Open in the post" a broken video shows its error under the
// frame (never hidden) and announces nothing as playing. With the real fixture (MEOWCHAN_VIDEO_FIXTURE): click plays in the post, a second video pauses the first (one plays),
// the bound of live players holds, scrolling out pauses (software surface) or releases (native), the media sidebar plays the
// selected post's video and gives its player back on another medium, and a thread switch releases every player.
internal static class ReaderInlineVideoChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task CheckEmbeddedShortcutFocusAsync(Window host, ArchivePage page, ReaderInlineVideoSlot slot, GalleryVideoPlayer player)
    {
        List<string> failures = new();
        await page.FocusPostAsync("1");
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        Control post = (Control)posts.ContainerFromItem(posts.SelectedItem!)!;
        foreach ((string name, Control source) in new[] { ("post", post), ("inline-video", player.SurfaceControl) })
        foreach ((Key key, KeyModifiers mods) in new[] { (Key.Space, KeyModifiers.None), (Key.M, KeyModifiers.Shift), (Key.L, KeyModifiers.None) })
        {
            string scene = name + "-" + key;
            try
            {
                bool focusable = source.Focusable;
                source.Focusable = true;
                try
                {
                    Require(source.Focus() && ReferenceEquals(host.FocusManager?.GetFocusedElement(), source), scene + ": focus unavailable");
                    bool before = key == Key.Space ? slot.IsPlaying : key == Key.M ? player.Muted : player.LoopEnabled;
                    var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods };
                    source.RaiseEvent(e);
                    source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = mods });
                    await Program.WaitUntilAsync(() => before != (key == Key.Space ? slot.IsPlaying : key == Key.M ? player.Muted : player.LoopEnabled));
                    Require(e.Handled && ReferenceEquals(host.FocusManager?.GetFocusedElement(), source), scene + ": shortcut stole focus");
                    // Restore through the same shortcut (not by assigning expected player state).
                    source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods });
                    source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = mods });
                    await Program.WaitUntilAsync(() => before == (key == Key.Space ? slot.IsPlaying : key == Key.M ? player.Muted : player.LoopEnabled));
                    Console.WriteLine("SMOKE embedded-video-shortcut PASS " + scene);
                }
                finally { source.Focusable = focusable; }
            }
            catch (Exception error) { failures.Add(scene + ": " + error.Message); Console.WriteLine("SMOKE embedded-video-shortcut FAIL " + scene + ": " + error); }
        }
        if (failures.Count > 0) throw new InvalidOperationException("Embedded video shortcuts (all scenes ran): " + string.Join("; ", failures));
    }
    private static async Task Flush() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static async Task RunAsync(MainWindow owner)
    {
        Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE")),
            "The inline video smoke requires the isolated runner profile.");
        string? fixture = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE");
        byte[]? real = string.IsNullOrEmpty(fixture) ? null : await File.ReadAllBytesAsync(fixture);
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-inline-video-" + Guid.NewGuid().ToString("N"));
        string media = Path.Combine(profile, "media");
        Directory.CreateDirectory(media);
        byte[] broken = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.Concat(Guid.NewGuid().ToByteArray()).ToArray();
        Dictionary<string, byte[]> files = new()
        {
            ["first.webm"] = real ?? broken,
            ["second.webm"] = real != null ? real.Concat(new byte[] { 0 }).ToArray() : broken.Reverse().ToArray(),
            ["broken.webm"] = broken.Concat(new byte[] { 7 }).ToArray()
        };
        foreach ((string name, byte[] bytes) in files) await File.WriteAllBytesAsync(Path.Combine(media, name), bytes);
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = files.ToDictionary(file => file.Key, file => (object)new
            {
                MediaIdentifier = "inline-" + file.Key, SourceUrl = "https://offline.invalid/" + file.Key, FileName = file.Key,
                OriginalFileName = file.Key, Length = file.Value.Length, Sha256 = Convert.ToHexString(SHA256.HashData(file.Value)),
                Md5 = Convert.ToBase64String(MD5.HashData(file.Value))
            })
        }));
        ResourceId videos = new("offline", "inline", "videos"), other = new("offline", "inline", "other");
        CapturedPost Post(string id, int ordinal, string? file) => new(id, ordinal, PostFieldPresence.All, Html: "<p>Inline video post " + id + "</p>",
            Attachments: file == null ? Array.Empty<CapturedAttachment>() : new[] { new CapturedAttachment(0, "inline-" + file, OriginalFileName: file) });
        CapturedPost[] posts = new[] { Post("1", 0, "first.webm"), Post("2", 1, "second.webm"), Post("3", 2, "broken.webm") }
            .Concat(Enumerable.Range(4, 40).Select(index => Post(index.ToString(), index - 1, null))).ToArray();
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
        {
            await catalog.CaptureAsync(new CapturedThreadSnapshot(videos, "https://offline.invalid/videos", "Inline videos",
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, await StrictMediaFixture.BindAsync(media, videos, posts)));
            await catalog.CaptureAsync(new CapturedThreadSnapshot(other, "https://offline.invalid/other", "Other thread",
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, new[] { Post("1", 0, null) }));
        }

        ArchivePage? page = null;
        Window? host = null;
        int galleryOpens = 0;
        try
        {
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            page.OpenGallery = (_, _, _) => { galleryOpens++; return Task.CompletedTask; };
            ContentControl readerHost = new() { IsVisible = false };
            host = new Window { Width = 1600, Height = 960, Content = new Grid { Children = { page, readerHost } }, Title = "Inline video — diagnostic", ShowInTaskbar = false };
            host.Show(owner);
            await page.ActivateAsync();
            await page.LoadThreadAsync(videos);
            await page.MediaLoadTask;
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            ArchiveAttachmentView View(string file) => list.GetVisualDescendants().OfType<ArchiveAttachmentView>()
                .First(view => view.DataContext is ArchiveAttachmentRow row && row.Attachment.OriginalFileName == file);
            await Program.WaitUntilAsync(() => { host.UpdateLayout(); return list.GetVisualDescendants().OfType<ArchiveAttachmentView>().Count() >= 3; });

            // The default choice is the sidebar (autoplay off); with no sidebar on screen a click is the old path (gallery), no player exists in the post.
            Require(page.ReaderMedia.Video.Mode == ReaderMediaClickMode.Sidebar && !page.ReaderMedia.Video.InPost && !page.ReaderMedia.Video.AutoplayMuted,
                "The click choice is not the sidebar by default, or autoplay is not off.");
            Click(View("first.webm").PreviewButton);
            await Flush();
            Require(page.InlineVideo.Created == 0 && View("first.webm").InlineVideo?.IsLive != true, "A player was created with the setting off.");

            // "Open in the post": videos play inline (the minimal control), images expand there.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Video = page.ReaderMedia.Video with { Enabled = true, InPost = true } });
            int opensBefore = galleryOpens;

            // The broken video: its error stays visible under the frame (in every work view: it is post content, never reduced).
            ArchiveAttachmentView brokenView = View("broken.webm");
            Click(brokenView.PreviewButton);
            ReaderInlineVideoSlot brokenSlot = brokenView.InlineVideo!;
            await brokenSlot.ActivationTask;
            await Program.WaitUntilAsync(() => brokenSlot.ErrorText.Length > 0);
            Require(brokenSlot.Below.IsEffectivelyVisible && !brokenSlot.IsPlaying, "A failed inline video hid its error or claims to play.");
            Require(galleryOpens == opensBefore, "With the setting on, a click on a video still opened the gallery.");
            Program.CaptureWindow(host, "smoke-reader-inline-video-error.png");
            await page.InlineVideo.ReleaseAllAsync();

            if (real == null)
            {
                Console.WriteLine("SMOKE reader inline video: sidebar choice by default (no player in the post), in the post: click plays here, a broken video shows its error; real playback needs MEOWCHAN_VIDEO_FIXTURE.");
                return;
            }

            // Click plays in the post; the activator is named; the transport and the live region follow.
            ArchiveAttachmentView firstView = View("first.webm"), secondView = View("second.webm");
            Click(firstView.PreviewButton);
            ReaderInlineVideoSlot first = firstView.InlineVideo!;
            await first.ActivationTask;
            await Program.WaitUntilAsync(() => first.IsPlaying);
            Require(AutomationProperties.GetName(first.Activator).Contains("first.webm", StringComparison.Ordinal), "The video control has no name.");
            Require(first.LiveText == Texts.T("reader.video.playing", "first.webm"), "Play was not announced.");
            Require(first.Below.IsEffectivelyVisible && first.Player?.TransportControl.IsEffectivelyVisible == true, "The current video shows no transport.");
            // The minimal control: exactly play, timeline, time, mute (here no "Open in gallery" icon is missing: the post offers it), as wide as the picture.
            GalleryVideoPlayer firstPlayer = first.Player!;
            Require(firstPlayer.HasMinimalTransport && firstPlayer.MinimalBar != null, "The post's video does not use the minimal control.");
            Require(firstPlayer.MinimalBar!.Children.All(child => child.Name != null && InlineVideoBarLayout.ControlNames.Contains(child.Name)),
                "The minimal control holds something besides play, timeline, time, mute and open.");
            Require(Math.Abs(firstPlayer.TransportControl.Bounds.Width - firstView.PreviewButton.Bounds.Width) <= 2.5,
                "The video's control is not as wide as its picture: " + firstPlayer.TransportControl.Bounds.Width + " vs " + firstView.PreviewButton.Bounds.Width);
            Require(first.Activator.Classes.Contains("video-activator"), "The play control over the poster lost its transparent style (hover would hide the poster).");

            await CheckEmbeddedShortcutFocusAsync(host, page, first, firstPlayer);

            // KB-03: a mouse click on the bar's mute gives the keys back to the post, so Space never presses mute again; keyboard activation keeps the bar's focus.
            Button muteButton = (Button)firstPlayer.MinimalBar!.Mute;
            bool mutedAtStart = firstPlayer.Muted;
            await GalleryPointerFocusChecks.ClickAsync(host, muteButton);
            await Program.WaitUntilAsync(() => firstPlayer.Muted != mutedAtStart);
            Require(GalleryPointerFocusChecks.Focused(host) is Visual afterMute && !afterMute.GetSelfAndVisualAncestors().OfType<Button>().Any(),
                "A mouse click on the video bar's mute left the keys on a button: " + GalleryPointerFocusChecks.Focused(host));
            bool mutedAfterClick = firstPlayer.Muted;
            await GalleryPointerFocusChecks.SpaceAsync(host);
            Require(firstPlayer.Muted == mutedAfterClick, "Space after a mouse click on mute pressed mute again.");
            await GalleryPointerFocusChecks.ClickAsync(host, muteButton);
            await Program.WaitUntilAsync(() => firstPlayer.Muted == mutedAtStart);
            Require(muteButton.Focus(Avalonia.Input.NavigationMethod.Tab), "The bar's mute could not be focused by keyboard.");
            await GalleryPointerFocusChecks.SpaceAsync(host);
            await Program.WaitUntilAsync(() => firstPlayer.Muted != mutedAtStart);
            Require(ReferenceEquals(GalleryPointerFocusChecks.Focused(host), muteButton), "Keyboard activation of mute moved the focus off the bar.");
            await GalleryPointerFocusChecks.SpaceAsync(host); // and back to where the run started
            await Program.WaitUntilAsync(() => firstPlayer.Muted == mutedAtStart);

            // One at a time: the second pauses the first, whose transport gives way (one set of video controls in the reader).
            Click(secondView.PreviewButton);
            ReaderInlineVideoSlot second = secondView.InlineVideo!;
            await second.ActivationTask;
            await Program.WaitUntilAsync(() => second.IsPlaying && !first.IsPlaying);
            Require(page.InlineVideo.PlayingCount == 1, "More than one inline video plays.");
            Require(first.LiveText == Texts.T("reader.video.paused", "first.webm"), "The pause was not announced.");
            Require(first.Activator.IsVisible && first.Player?.TransportControl.IsEffectivelyVisible != true, "The paused older video kept its transport.");
            Program.CaptureWindow(host, "smoke-reader-inline-video-posts.png");

            // The bound: one player only, the older one is released.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Video = page.ReaderMedia.Video with { MaxPlayers = 1 } });
            await page.InlineVideo.WhenReleasedAsync();
            Require(page.InlineVideo.LiveCount == 1 && !first.IsLive && second.IsLive, "The player bound was not applied.");
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Video = page.ReaderMedia.Video with { MaxPlayers = ReaderInlineVideoOptions.MaxPlayersDefault } });

            // Scrolling the playing video out of view pauses it (software surface) or releases it (native surface).
            list.ScrollIntoView(list.ItemCount - 1);
            await Program.WaitUntilAsync(() => { host.UpdateLayout(); return !second.IsPlaying; });
            // A recycled post container gives its player back in any case; a native surface does so already when partly out.
            Require(!ReaderInlineVideoRules.NativeSurface || !second.IsLive, "A native video surface stayed live outside the posts.");
            list.ScrollIntoView(0);
            await Flush();

            // The media sidebar of the large reader plays the selected post's video and gives it back on another medium.
            Control frame = page.DetachReader();
            readerHost.Content = frame; readerHost.IsVisible = true; page.IsVisible = false;
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Sidebar = true });
            await Program.WaitUntilAsync(() => { host.UpdateLayout(); return page.IsMediaSidebarShown; });
            list.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => page.SidebarInlineVideo?.IsOffered == true);
            ReaderInlineVideoSlot sidebar = page.SidebarInlineVideo!;
            Require(sidebar.Activator.IsVisible && sidebar.Activator.Focusable, "The sidebar's video has no focusable play control.");
            Click(sidebar.Activator);
            await sidebar.ActivationTask;
            await Program.WaitUntilAsync(() => sidebar.IsPlaying);
            Program.CaptureWindow(host, "smoke-reader-inline-video-sidebar.png");
            list.SelectedIndex = 4; // a post without media
            await Program.WaitUntilAsync(() => !sidebar.IsLive);

            // "Show in the sidebar": a click on a tile of a post shows it there and plays it there; the post's own player stays unused.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Video = page.ReaderMedia.Video with { InPost = false } });
            await page.InlineVideo.ReleaseAllAsync();
            Click(View("second.webm").PreviewButton);
            await Program.WaitUntilAsync(() => page.SidebarInlineVideo?.IsPlaying == true);
            Require(View("second.webm").InlineVideo?.IsLive != true && page.InlineVideo.LiveCount == 1, "A tile click with the sidebar choice played in the post, not in the sidebar.");
            // "Open in the gallery": the click opens the gallery and plays nothing.
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Video = page.ReaderMedia.Video with { Enabled = false } });
            await page.InlineVideo.ReleaseAllAsync();
            int opensBeforeGallery = galleryOpens;
            Click(View("second.webm").PreviewButton);
            await Flush();
            Require(galleryOpens == opensBeforeGallery + 1 && page.InlineVideo.LiveCount == 0, "A click with the gallery choice did not open the gallery or played a video.");
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Video = page.ReaderMedia.Video with { Enabled = true, InPost = true } });
            page.ApplyReaderMediaPreferences(page.ReaderMedia with { Sidebar = false });
            page.AttachReader(frame);
            readerHost.Content = null; readerHost.IsVisible = false; page.IsVisible = true;
            Require(page.InlineVideo.LiveCount == 0, "Closing the large reader kept a player.");

            // A thread switch releases every player.
            await page.LoadThreadAsync(videos);
            await page.MediaLoadTask;
            await Program.WaitUntilAsync(() => { host.UpdateLayout(); return list.GetVisualDescendants().OfType<ArchiveAttachmentView>().Count() >= 3; });
            Click(View("first.webm").PreviewButton);
            await View("first.webm").InlineVideo!.ActivationTask;
            await Program.WaitUntilAsync(() => page.InlineVideo.LiveCount == 1);
            await page.LoadThreadAsync(other);
            await page.InlineVideo.WhenReleasedAsync();
            Require(page.InlineVideo.LiveCount == 0 && page.InlineVideo.Created == page.InlineVideo.Released, "A thread switch kept a player.");
            Console.WriteLine("SMOKE reader inline video: sidebar choice by default, in the post click plays here with the minimal control at picture width, named control, announced play/pause, one plays, bound, scroll-out pause, sidebar play and give-back, reader close and thread switch release, broken video error visible.");
        }
        finally
        {
            if (page != null)
            {
                await page.ReleaseInlineVideos();
                page.EndLifetime();
            }
            host?.Close();
            await Flush();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
