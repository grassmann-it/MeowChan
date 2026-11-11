using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_GALLERY_MEDIA_TAGS_ONLY. Real main window gallery, action wiring,
// host and tag store with a synthetic bound and an unbound media file. Managed input only.
internal static class GalleryMediaTagChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Resource = new("media-tag-fixture", "g", "42");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");

    internal static async Task RunAsync(MainWindow window)
    {
        string root = Path.Combine(window.SessionHost.ProfileDirectory, "gallery-media-tag-fixture");
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            GalleryEntry bound = await BoundAsync(Path.Combine(root, "bound"));
            GalleryEntry unbound = await UnboundAsync(Path.Combine(root, "unbound"));
            PrivateMediaTagTarget target = GalleryMediaTagTarget.Resolve(bound, out _)
                ?? throw new InvalidOperationException("Fixture entry did not resolve to a tag target.");
            await window.SessionHost.DefinePrivateTagAsync("existing");

            await window.OpenMediaGalleryAsync([bound, unbound], bound.EntryKey);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            await UntilAsync(() => host.Content is GalleryViewer);
            GalleryViewer viewer = (GalleryViewer)host.Content!;
            Button tags = viewer.TagsButton;
            await UntilAsync(() => tags.IsEffectivelyEnabled);
            Require(Equals(tags.Content, Texts.T("tags.media.button")), "Initial tags button label is wrong: " + tags.Content);

            tags.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => window.GalleryMediaTagWindow?.IsVisible == true);
            MediaTagDialog dialog = window.GalleryMediaTagWindow!;
            Require(dialog.Session.Target == target, "Dialog is not bound to the shown media version.");
            Require(Labels(dialog.AvailablePanel).SequenceEqual(new[] { "+ existing" }), "Existing tags are not offered.");
            Require(dialog.AssignedPanel.Children.OfType<TextBlock>().Single().Text == Texts.T("tags.media.none"), "Empty state missing.");

            // New tag by Enter, then an existing tag by one click.
            dialog.Input.Focus();
            dialog.Input.Text = "my_media";
            var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
            dialog.Input.RaiseEvent(enter);
            Require(enter.Handled, "Enter was not handled by the tag field.");
            await IdleAsync(dialog);
            Button existing = dialog.AvailablePanel.Children.OfType<Button>().Single();
            existing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(dialog);
            Require(Labels(dialog.AssignedPanel).SequenceEqual(new[] { "existing  ✕", "my_media  ✕" }),
                "Assigned tags are wrong: " + string.Join("|", Labels(dialog.AssignedPanel)));
            Require(string.IsNullOrEmpty(dialog.Input.Text) && !dialog.AvailablePanel.IsVisible, "Field or offers did not update.");
            Program.CaptureWindow(dialog, "smoke-gallery-media-tags.png");

            // An invalid name keeps the draft and changes nothing.
            long revision = window.SessionHost.GetPrivateTagSnapshot().Snapshot!.Revision;
            dialog.Input.Text = "bad\tname";
            dialog.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(dialog);
            Require(dialog.Status.Text == Texts.T("tags.error.invalidName") && dialog.Input.Text == "bad\tname"
                && window.SessionHost.GetPrivateTagSnapshot().Snapshot!.Revision == revision, "Invalid name was not refused cleanly.");
            dialog.Input.Text = string.Empty;

            Button first = dialog.AssignedPanel.Children.OfType<Button>().First();
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await IdleAsync(dialog);
            Require(Labels(dialog.AssignedPanel).SequenceEqual(new[] { "my_media  ✕" }) && Labels(dialog.AvailablePanel).SequenceEqual(new[] { "+ existing" }),
                "Removing a chip did not move the tag back to the offers.");

            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            dialog.Input.RaiseEvent(escape);
            await UntilAsync(() => window.GalleryMediaTagWindow == null);
            Require(escape.Handled && Equals(tags.Content, Texts.T("tags.media.buttonCount", 1)), "Closing did not update the gallery button: " + tags.Content);

            PrivateTagStoreSnapshot stored = window.SessionHost.GetPrivateTagSnapshot().Snapshot!;
            Require(stored.TagsOf(target).Select(tag => tag.Name.Display).SequenceEqual(new[] { "my_media" }) && stored.TagsOf(Resource).Count == 0,
                "Store does not hold exactly one media tag and no thread tag.");

            // The next entry has no bound post version: no tagging, with an explanation.
            GalleryViewerChecksAccess.Click(viewer, "GalleryNext");
            await UntilAsync(() => !tags.IsEffectivelyEnabled && ToolTip.GetTip(tags) as string == Texts.T("tags.media.unbound"));
            Require(Equals(tags.Content, Texts.T("tags.media.button")), "Unbound entry shows another medium's tags.");
            GalleryViewerChecksAccess.Click(viewer, "GalleryPrevious");
            await UntilAsync(() => tags.IsEffectivelyEnabled && Equals(tags.Content, Texts.T("tags.media.buttonCount", 1)));

            GalleryViewerChecksAccess.Click(viewer, "GalleryClose");
            await window.GalleryCleanupTask.WaitAsync(Deadline);
            Console.WriteLine("SMOKE gallery media tags [en]: bound target, new and existing tag, invalid name, remove, Escape, button count, unbound entry checked.");
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.Where(child => child.IsVisible).ToArray()) owned.Close();
            if (window.FindControl<ContentControl>("GalleryHost")?.Content is GalleryViewer open)
                GalleryViewerChecksAccess.Click(open, "GalleryClose");
            await window.GalleryCleanupTask;
            Texts.SetLanguage(language);
        }
    }

    private static string[] Labels(Panel panel) => panel.Children.OfType<Button>()
        .Select(button => button.Content is TextBlock text ? text.Text ?? "" : button.Content?.ToString() ?? "").ToArray();

    private static async Task IdleAsync(MediaTagDialog dialog)
    {
        await UntilAsync(() => !dialog.Session.Busy);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task<GalleryEntry> BoundAsync(string directory)
    {
        (MediaAssetSnapshot snapshot, MediaAssetReference asset, object entry, string path) = await WriteAsync(directory, "bound.png", "901");
        MediaProviderMetadata provider = new("901", "original", "https://offline.invalid/bound.png", "bound.png",
            Png.Length, asset.Md5, asset.Sha256);
        string revision = MediaRevisionKeys.CreateAttachmentKey(Resource, "43", 0, provider);
        MediaAttachmentBinding binding = new(new(Resource, "43", 0, revision), MediaRevisionKeys.CreateAcquisitionKey(Resource, provider),
            asset.AssetId, provider, new(asset.Length, asset.Md5, asset.Sha256),
            new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch, "fixture-profile", "fixture-owner", Resource.Source, 1));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3,
            Entries = new Dictionary<string, object> { ["bound.png"] = entry },
            Assets = new Dictionary<string, object> { [asset.AssetId] = new { RequestedFileName = "bound.png", Entry = entry } },
            AttachmentBindings = new Dictionary<string, MediaAttachmentBinding> { [revision] = binding } }));
        snapshot = await MediaAssetReader.OpenAsync(directory); asset = snapshot.GetPage().Single();
        CapturedAttachment attachment = new(0, "901", Url: provider.Url, OriginalFileName: provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = revision };
        ResolvedMediaAttachment resolution = MediaAttachmentResolver.Resolve(snapshot, Resource, "43", attachment)
            ?? throw new InvalidOperationException("The bound fixture must resolve its exact binding.");
        return new(new(Resource, directory, asset.AssetId), snapshot, asset,
            new CapturedPost("43", 0, PostFieldPresence.All, Html: "<p>Bound image.</p>", Attachments: [attachment]), attachment, false, false)
            { AttachmentResolution = resolution };
    }

    private static async Task<GalleryEntry> UnboundAsync(string directory)
    {
        (MediaAssetSnapshot snapshot, MediaAssetReference asset, _, _) = await WriteAsync(directory, "loose.png", "902");
        return new(new(Resource, directory, asset.AssetId), snapshot, asset, null, null, false, true);
    }

    private static async Task<(MediaAssetSnapshot, MediaAssetReference, object, string)> WriteAsync(string directory, string file, string mediaId)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, file), Png);
        object entry = new { MediaIdentifier = mediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + file,
            FileName = file, OriginalFileName = file, Length = Png.Length,
            Md5 = Convert.ToBase64String(MD5.HashData(Png)), Sha256 = Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant() };
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { [file] = entry } }));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        return (snapshot, snapshot.GetPage().Single(), entry, path);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Gallery media tag UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal static class GalleryViewerChecksAccess
{
    internal static void Click(GalleryViewer viewer, string name)
    {
        Button button = viewer.GetGalleryVisualDescendants().OfType<Button>().FirstOrDefault(item => item.Name == name)
            ?? throw new InvalidOperationException("Missing gallery control " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
