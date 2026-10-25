using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Gallery;

namespace MeowChan.DesktopSmoke;

internal static class VideoContrastChecks
{
    private static readonly ResourceId Id = new("4chan", "g", "2500");

    internal static async Task SeedAsync(string profile)
    {
        string fixture = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")
            ?? throw new InvalidOperationException("video-contrast requires a short, real WebM in MEOWCHAN_VIDEO_FIXTURE.");
        string directory = Path.Combine(profile, "Video contrast");
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        Dictionary<string, CapturedAttachment> attachments = new();
        foreach ((string name, byte[] bytes) in new[] { ("clip.webm", await File.ReadAllBytesAsync(fixture)),
            ("corrupt.webm", "This verified fixture is intentionally not a decodable WebM."u8.ToArray()) })
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
            string sourceUrl = "https://offline.invalid/" + name;
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            string md5 = Convert.ToBase64String(MD5.HashData(bytes));
            entries[name] = new { MediaIdentifier = name, SourceUrl = sourceUrl,
                FileName = name, Length = bytes.Length, Sha256 = sha256, Md5 = md5 };
            attachments[name] = new CapturedAttachment(0, name, Url: sourceUrl, OriginalFileName: name,
                ExpectedSize: bytes.Length, ExpectedMd5: md5) { ExpectedSha256 = sha256 };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        // CaptureAsync stamps observed attachment revisions. Bind those exact
        // revisions to the saved assets before capture, as VideoGalleryChecks does.
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        Dictionary<string, object> assets = new();
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        foreach ((string name, string postId) in new[] { ("clip.webm", "2500"), ("corrupt.webm", "2501") })
        {
            CapturedAttachment attachment = attachments[name];
            ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(manifest, Id, postId, attachment);
            if (resolved == null || resolved.Asset.RequestedFileName != name)
                throw new InvalidOperationException("The video contrast fixture lost its exact saved asset: " + name);
            MediaAssetReference asset = resolved.Asset;
            MediaProviderMetadata provider = new(attachment.MediaId!, attachment.Variant, attachment.Url!,
                attachment.OriginalFileName, attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256);
            string revision = MediaRevisionKeys.CreateAttachmentKey(Id, postId, attachment.Position, provider);
            assets[asset.AssetId] = new { RequestedFileName = name, Entry = entries[name] };
            bindings[revision] = new(new(Id, postId, attachment.Position, revision),
                MediaRevisionKeys.CreateAcquisitionKey(Id, provider), asset.AssetId, provider,
                new(asset.Length, asset.Md5, asset.Sha256),
                new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
            attachments[name] = attachment with { AttachmentRevisionKey = revision };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
            Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://boards.4chan.org/g/thread/2500", "Video contrast",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory, new[] {
                new CapturedPost("2500", 0, PostFieldPresence.All, Html: "Real decoded video", Attachments: new[] { attachments["clip.webm"] }),
                new CapturedPost("2501", 1, PostFieldPresence.All, Html: "Verified undecodable video", Attachments: new[] { attachments["corrupt.webm"] }) }));
        // Validate the same persisted snapshot and selection used by OpenGalleryAsync,
        // after capture has stamped/reloaded the host revisions.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchivedThreadView thread = await reader.ReadThreadAsync(Id)
            ?? throw new InvalidOperationException("The video contrast fixture archive was not captured.");
        manifest = await MediaAssetReader.OpenAsync(directory);
        GalleryThreadResult selected = GallerySelection.Build(thread, new[] { manifest }, includeOtherMedia: true);
        if (selected.Entries.Count != 2 || selected.Entries[0].Post?.PostId != "2500"
            || selected.Entries[0].Asset.RequestedFileName != "clip.webm"
            || selected.Entries[1].Post?.PostId != "2501" || selected.Entries[1].Asset.RequestedFileName != "corrupt.webm"
            || selected.Entries.Any(entry => entry.Unassigned || entry.Kind != SavedMediaKind.Video
                || entry.AttachmentResolution?.Binding?.Acceptance.Kind != MediaAcceptanceKind.Strict))
            throw new InvalidOperationException("The captured video contrast fixture must retain two exact strict post bindings in gallery order.");
        Console.WriteLine("SMOKE video contrast fixture: persisted posts 2500/2501 retain exact strict bindings in production gallery order.");
    }

    internal static async Task RunAsync(MainWindow window)
    {
        List<string> failures = new();
        Texts.SetLanguage("en");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (Application.Current!.RequestedThemeVariant != theme)
                window.FindControl<Button>("ThemeToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => Application.Current.ActualThemeVariant == theme);
            await window.OpenGalleryAsync(Id);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            GalleryViewer viewer = (GalleryViewer)host.Content!;
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Any());
            GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            GalleryVideoPlayer.PlaybackEvidence playback;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                playback = await player.InspectPlaybackAsync();
                if (playback.SurfaceFrames >= 3 && playback.SurfaceNonUniform && playback.Time > 100) break;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Real video did not decode frames and advance its clock.");
                await Task.Delay(50);
            } while (true);
            Console.WriteLine("SMOKE video playback evidence: " + JsonSerializer.Serialize(playback));
            await Program.WaitUntilAsync(() => player.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains(" / ") == true));
            TextBlock time = player.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text?.Contains(" / ") == true);
            Check(window, viewer, time, theme, "time", failures);
            await GalleryFocusChecks.CheckVideoAsync(window, viewer, player, theme);
            if (theme == ThemeVariant.Light) await GalleryCaptionChecks.RunAsync(window, viewer, video: true);
            TextBlock status = player.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GalleryVideoStatus");
            if (!ReferenceEquals(player, viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single())
                || viewer.CaptureState().PostId != "2500")
                throw new InvalidOperationException("Caption checks changed the verified video player or post identity.");
            Slider seek = player.GetVisualDescendants().OfType<Slider>().Single(control => control.Name == "GalleryVideoSeek");
            if (!seek.IsEnabled || seek.Minimum != 0 || seek.Maximum != 1000)
                throw new InvalidOperationException("The real video timeline is unavailable or has changed its range.");
            Console.WriteLine("SMOKE video before end seek: " + JsonSerializer.Serialize(new
                { theme = theme.ToString(), status.Text, playback = await player.InspectPlaybackAsync(), targetPermille = 900 }));
            // The shared fixture is twelve seconds long. Exercise the real timeline
            // before waiting for its real EndReached event; keep the five-second guard.
            seek.Focus();
            seek.Value = 900;
            try { await Program.WaitUntilAsync(() => status.Text == "Playback ended."); }
            catch (TimeoutException)
            {
                Console.WriteLine("SMOKE video end seek timeout: " + JsonSerializer.Serialize(new
                    { theme = theme.ToString(), status.Text, playback = await player.InspectPlaybackAsync() }));
                throw;
            }
            Console.WriteLine("SMOKE video after end seek: " + JsonSerializer.Serialize(new
                { theme = theme.ToString(), status.Text, playback = await player.InspectPlaybackAsync() }));
            // Allow queued native reset notifications to arrive after EndReached.
            await Task.Delay(100);
            var endedPlayback = await player.InspectPlaybackAsync();
            string endedClock = TimeSpan.FromMilliseconds(endedPlayback.Length)
                .ToString(endedPlayback.Length >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
            if (endedPlayback.Length <= 0 || time.Text != endedClock + " / " + endedClock)
                throw new InvalidOperationException("Ended video did not retain its terminal clock: " + time.Text);
            Check(window, viewer, status, theme, "ended", failures);
            if ((await player.InspectPlaybackAsync()).LoopEnabled)
                throw new InvalidOperationException("Repeat did not default off for the verified video.");
            Button playbackButton = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryVideoPlayPause");
            playbackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitNativeAsync(player, evidence => evidence.State == "Playing" && evidence.Time > 100);
            Button mute = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryVideoMute");
            var audio = await player.InspectPlaybackAsync();
            Slider volume = viewer.GetGalleryVisualDescendants().OfType<Slider>().Single(control => control.Name == "GalleryVideoVolume");
            // This pinned fixture is intentionally silent. It proves unavailable
            // audio controls, never a native mute/unmute transition.
            if (audio.AudioTracks != 0 || audio.AudioAvailable)
                throw new InvalidOperationException("The silent fixture unexpectedly reported native audio.");
            await Program.WaitUntilAsync(() => !mute.IsEnabled && !volume.IsEnabled);
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(mute))
                || string.IsNullOrWhiteSpace(ToolTip.GetTip(volume)?.ToString()))
                throw new InvalidOperationException("Unavailable audio controls lack an accessible explanation.");
            Console.WriteLine("SMOKE video no-audio-track evidence: " + JsonSerializer.Serialize(audio));
            Button loop = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryVideoLoop");
            loop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            seek.Value = 980;
            var repeated = await WaitNativeAsync(player, evidence => evidence.LoopEnabled && evidence.LoopRestarts > 0
                && evidence.State == "Playing" && evidence.Time < evidence.Length / 2);
            loop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            seek.Value = 970;
            var finalEnd = await WaitNativeAsync(player, evidence => evidence.State == "Ended");
            if (finalEnd.LoopEnabled || finalEnd.LoopRestarts != repeated.LoopRestarts)
                throw new InvalidOperationException("Repeat continued after it was disabled.");
            Console.WriteLine("SMOKE native video repeat evidence: " + JsonSerializer.Serialize(new { theme = theme.ToString(), repeated, finalEnd }));
            await GalleryToolbarContrastChecks.RunAsync(window, viewer, theme, failures);
            Button next = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryNext");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => status.Text?.StartsWith("Video playback is unavailable", StringComparison.Ordinal) == true);
            Check(window, viewer, status, theme, "error", failures);
            Button close = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !host.IsVisible);
            await window.GalleryCleanupTask;
        }
        if (failures.Count > 0) throw new InvalidOperationException("Video or gallery toolbar contrast failed: " + string.Join(", ", failures));
    }

    private static async Task<GalleryVideoPlayer.PlaybackEvidence> WaitNativeAsync(GalleryVideoPlayer player,
        Func<GalleryVideoPlayer.PlaybackEvidence, bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var evidence = await player.InspectPlaybackAsync();
            if (predicate(evidence)) return evidence;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native transport state did not settle: " + JsonSerializer.Serialize(evidence));
            await Task.Delay(25);
        }
    }

    private static void Check(Window window, GalleryViewer viewer, TextBlock text, ThemeVariant theme, string state, List<string> failures)
    {
        window.UpdateLayout();
        Color foreground = (text.Foreground as ISolidColorBrush)?.Color ?? throw new InvalidOperationException("Expected a measurable solid text foreground.");
        Border transport = viewer.GetGalleryVisualDescendants().OfType<Border>().Single(border => border.Name == "GalleryVideoTransport");
        if (!text.GetVisualAncestors().Contains(transport))
            throw new InvalidOperationException("Video text is no longer carried by its measured transport surface.");
        Color background = (transport.Background as ISolidColorBrush)?.Color ?? throw new InvalidOperationException("Expected the production video transport backdrop.");
        double ratio = Contrast(foreground, background);
        Point origin = text.TranslatePoint(default, viewer) ?? throw new InvalidOperationException("Video text is detached.");
        Rect textBounds = new(origin, text.Bounds.Size);
        bool chromeOverlap = viewer.GetGalleryVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Name is "GalleryClose" or "GalleryNext" or "GalleryFavorite" or "GalleryExport")
            .Any(button => button.TranslatePoint(default, viewer) is Point point && new Rect(point, button.Bounds.Size).Intersects(textBounds));
        Console.WriteLine("SMOKE video contrast: " + JsonSerializer.Serialize(new { theme = theme.ToString(), state,
            text.Text, foreground = foreground.ToString(), background = background.ToString(), ratio,
            text.IsEffectivelyVisible, chromeOverlap, bounds = text.Bounds.ToString(), client = window.ClientSize.ToString(), window.RenderScaling }));
        Program.CaptureWindow(window, $"smoke-video-contrast-{theme.ToString().ToLowerInvariant()}-{state}.png");
        if (!text.IsEffectivelyVisible || text.Bounds.Width <= 0 || ratio < 4.5 || chromeOverlap) failures.Add($"{theme}/{state}={ratio:0.###}, chromeOverlap={chromeOverlap}");
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
