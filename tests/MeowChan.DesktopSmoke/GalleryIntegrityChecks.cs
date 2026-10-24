using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

// Consumer UI fixture, not evidence of production consent/transfer/commit.
// Uses the real gallery host, action wiring, resolver, verified decoder and details dialog.
internal static class GalleryIntegrityChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        double expectedScale = double.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_INTEGRITY_EXPECT_SCALE")
            ?? throw new InvalidOperationException("Specify the actual gallery scale as 1, 1.5 or 2."), CultureInfo.InvariantCulture);
        Require(new[] { 1d, 1.5d, 2d }.Contains(expectedScale) && Math.Abs(window.RenderScaling - expectedScale) < .025,
            "Actual native gallery scale differs from the requested cell.");
        string directory = Path.Combine(window.SessionHost.ProfileDirectory, "gallery-integrity-fixture");
        GalleryEntry selected = await CreateAsync(directory);
        MediaAttachmentBinding binding = selected.AttachmentResolution!.Binding!;
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        double originalWidth = window.Width, originalHeight = window.Height;
        string originalMode = window.SessionHost.Config.WorkspaceMode;
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.WindowState = WindowState.Normal; window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));
            foreach (string language in new[] { "en", "de" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Texts.SetLanguage(language); Application.Current.RequestedThemeVariant = theme;
                await window.OpenMediaGalleryAsync([selected], selected.EntryKey);
                ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
                await Program.WaitUntilAsync(() => host.Content is GalleryViewer current && Find<Image>(current, "GalleryImage").Source is Bitmap
                    && window.ActualThemeVariant == theme);
                GalleryViewer viewer = (GalleryViewer)host.Content!;
                SetContext(viewer, false); await Settle(window);
                Require(Math.Abs(window.RenderScaling - expectedScale) < .025, "Native scale changed during gallery matrix.");
                TextBlock summary = Find<TextBlock>(viewer, "GalleryProvenance");
                string expectedSummary = language == "de"
                    ? "Mit Quellenfreigabe gespeichert; Anbietergröße oder Prüfsumme weicht ab."
                    : "Saved with source permission; source size or checksum differs.";
                Require(summary.Text == expectedSummary, "Gallery provenance summary has stale or missing translation.");
                AssertVisibleText(summary, viewer);
                AssertNoRawValues(viewer, binding);
                Require(!window.OwnedWindows.Any(child => child.IsVisible), "Details appeared without explicit invocation.");
                SetContext(viewer, true); await Settle(window);
                Border context = Find<Border>(viewer, "GalleryContext");
                SelectableTextBlock[] text = context.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray();
                string localLabel = (language == "de" ? "Gespeicherte Datei: " : "Saved file: ")
                    + binding.Actual.Length.ToString("N0", Texts.Culture) + " bytes";
                string sourceLabel = (language == "de" ? "Anbieterangabe: " : "Source reported: ")
                    + binding.Provider.ExpectedSize!.Value.ToString("N0", Texts.Culture) + " bytes";
                SelectableTextBlock local = text.Single(item => item.Text == localLabel);
                SelectableTextBlock provider = text.Single(item => item.Text == expectedSummary + Environment.NewLine + sourceLabel);
                AssertVisibleText(summary, viewer); AssertVisibleText(local, viewer); AssertVisibleText(provider, viewer);
                AssertNoRawValues(viewer, binding);
                Rect caption = Bounds(Find<Border>(viewer, "GalleryOverlayInfo"), viewer);
                Rect contextBounds = Bounds(context, viewer), chrome = Bounds(Find<Border>(viewer, "GalleryOverlayControls"), viewer);
                Require(!Overlaps(caption, contextBounds) && !Overlaps(chrome, contextBounds),
                    "Gallery provenance caption or toolbar overlaps the context panel.");
                string cell = language + "-" + theme.ToString().ToLowerInvariant();
                Console.WriteLine("GALLERY_INTEGRITY_GUI " + JsonSerializer.Serialize(new
                {
                    cell, sourceDigest = Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_DIGEST"), actualScale = window.RenderScaling,
                    client = window.ClientSize.ToString(), gallery = viewer.Bounds.ToString(), caption = caption.ToString(),
                    chrome = chrome.ToString(), context = contextBounds.ToString(), summary = State(summary, viewer),
                    local = State(local, viewer), provider = State(provider, viewer), rawValuesHidden = true,
                    assetId = selected.Asset.AssetId, revision = binding.Reference.RevisionKey
                }));
                string scaleKey = expectedScale.ToString("0.0##", CultureInfo.InvariantCulture).Replace('.', '_');
                Program.CaptureWindow(window, "smoke-gallery-integrity-" + scaleKey + "-" + cell + ".png");
                // Open the genuine media menu and its genuine DetailsRequested action.
                SetContext(viewer, false); await Settle(window);
                Grid viewport = Find<Grid>(viewer, "GalleryViewport");
                viewport.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                await Program.WaitUntilAsync(() => viewport.ContextMenu?.IsOpen == true);
                ContextMenu menu = viewport.ContextMenu!;
                MenuItem details = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, CommandCatalog.Label("media.details")));
                menu.Close();
                Require(viewer.Focus(NavigationMethod.Tab) && ReferenceEquals(window.FocusManager?.GetFocusedElement(), viewer),
                    "The details action has no focused gallery origin to restore.");
                details.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Program.WaitUntilAsync(() => window.OwnedWindows.Any(child => child.IsVisible));
                Window dialog = window.OwnedWindows.Single(child => child.IsVisible);
                bool dialogClosed = false;
                dialog.Closed += (_, _) => dialogClosed = true;
                await Settle(dialog);
                SelectableTextBlock detailsText = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
                string raw = detailsText.Text ?? string.Empty;
                Require(raw.Contains(binding.Actual.Sha256, StringComparison.Ordinal)
                    && raw.Contains(binding.Actual.Md5, StringComparison.Ordinal)
                    && raw.Contains(binding.Provider.ExpectedMd5!, StringComparison.Ordinal)
                    && raw.Contains(binding.Provider.Url, StringComparison.Ordinal)
                    && raw.Contains(localLabel, StringComparison.Ordinal) && raw.Contains(sourceLabel, StringComparison.Ordinal),
                    "Explicit details lost raw provider/local values or per-binding provenance.");
                Require(detailsText.TextLayout.WidthIncludingTrailingWhitespace <= detailsText.Bounds.Width + 1,
                    "Explicit details text overflows horizontally.");
                ScrollViewer scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single();
                scroll.ScrollToEnd(); await Settle(dialog);
                Require(scroll.Offset.Y >= Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height) - 1,
                    "Explicit details cannot scroll to their final raw values.");
                Button close = dialog.GetVisualDescendants().OfType<Button>().Single();
                Require(close.IsEffectivelyVisible && Bounds(close, dialog).Bottom <= dialog.Bounds.Height + 1,
                    "Explicit details Close action is outside the dialog.");
                Console.WriteLine("GALLERY_INTEGRITY_DETAILS " + JsonSerializer.Serialize(new
                {
                    cell, actualScale = dialog.RenderScaling, dialog = dialog.Bounds.ToString(), text = detailsText.Bounds.ToString(),
                    scrollOffset = scroll.Offset.ToString(), extent = scroll.Extent.ToString(), viewport = scroll.Viewport.ToString(),
                    rawValuesPresent = true, closedBeforeExplicitInvocation = true
                }));
                Require(close.IsEnabled && close.Command?.CanExecute(close.CommandParameter) == true,
                    "The real details Close command is unavailable.");
                // Routed Click alone bypasses Button.OnClick's ICommand dispatch.
                SmokeActionClick.Execute(close);
                await Program.WaitUntilAsync(() => dialogClosed && !dialog.IsVisible && !window.OwnedWindows.Contains(dialog)
                    && window.IsEnabled && host.IsVisible && ReferenceEquals(host.Content, viewer)
                    && ReferenceEquals(window.FocusManager?.GetFocusedElement(), viewer));
                Console.WriteLine("GALLERY_INTEGRITY_DETAILS_RETURN " + JsonSerializer.Serialize(new
                {
                    cell, dialogClosed, dialogVisible = dialog.IsVisible, stillOwned = window.OwnedWindows.Contains(dialog),
                    ownerEnabled = window.IsEnabled, galleryVisible = host.IsVisible,
                    sameGallery = ReferenceEquals(host.Content, viewer),
                    focusReturned = ReferenceEquals(window.FocusManager?.GetFocusedElement(), viewer)
                }));
                Find<Button>(viewer, "GalleryClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.GalleryCleanupTask;
            }
        }
        finally
        {
            foreach (Window dialog in window.OwnedWindows.Where(child => child.IsVisible).ToArray()) dialog.Close();
            if (window.FindControl<ContentControl>("GalleryHost")?.Content is GalleryViewer viewer)
                Find<Button>(viewer, "GalleryClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.GalleryCleanupTask;
            Texts.SetLanguage(originalLanguage); Application.Current.RequestedThemeVariant = originalTheme;
            await PresentationChecks.SwitchModeAsync(window, originalMode);
            window.Width = originalWidth; window.Height = originalHeight;
            Directory.Delete(directory, true);
        }
        Console.WriteLine("SMOKE gallery integrity: four EN/DE Dark/Light cells, exact accepted binding, verified PNG, summary/context geometry, explicit real details and four original captures passed.");
    }

    private static async Task<GalleryEntry> CreateAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        ResourceId resource = new("integrity-fixture", "g", "42");
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(Path.Combine(directory, "saved.png"), bytes);
        object entry = new { MediaIdentifier = "777", Variant = "original", SourceUrl = "https://offline.invalid/source.png",
            FileName = "saved.png", OriginalFileName = "saved.png", Length = bytes.Length,
            Md5 = Convert.ToBase64String(MD5.HashData(bytes)), Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        string path = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { ["saved.png"] = entry } }));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = snapshot.GetPage().Single();
        MediaProviderMetadata provider = new("777", "original", "https://offline.invalid/source.png", "saved.png",
            bytes.Length + 17, Convert.ToBase64String(MD5.HashData(new byte[] { 3, 1, 4 })), asset.Sha256);
        string revision = MediaRevisionKeys.CreateAttachmentKey(resource, "43", 0, provider);
        MediaAttachmentBinding binding = new(new(resource, "43", 0, revision), MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
            asset.AssetId, provider, new(asset.Length, asset.Md5, asset.Sha256),
            new(MediaAcceptanceKind.ExplicitProviderMismatch, true, true, DateTimeOffset.UtcNow, "fixture-profile", "fixture-owner", resource.Source, 1));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 3,
            Entries = new Dictionary<string, object> { ["saved.png"] = entry },
            Assets = new Dictionary<string, object> { [asset.AssetId] = new { RequestedFileName = "saved.png", Entry = entry } },
            AttachmentBindings = new Dictionary<string, MediaAttachmentBinding> { [revision] = binding } }));
        snapshot = await MediaAssetReader.OpenAsync(directory); asset = snapshot.GetPage().Single();
        CapturedAttachment attachment = new(0, "777", Url: provider.Url, OriginalFileName: provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = revision };
        ResolvedMediaAttachment resolution = MediaAttachmentResolver.Resolve(snapshot, resource, "43", attachment)
            ?? throw new InvalidOperationException("The explicit-mismatch fixture must resolve its exact binding.");
        Require(resolution.Binding?.Acceptance.Kind == MediaAcceptanceKind.ExplicitProviderMismatch && !resolution.IsLegacy,
            "The gallery fixture did not retain its exact accepted provenance.");
        await using (Stream verified = await snapshot.OpenVerifiedReadAsync(asset.AssetId))
        {
            using MemoryStream actual = new(); await verified.CopyToAsync(actual);
            Require(actual.ToArray().SequenceEqual(bytes), "The gallery fixture PNG failed verified byte comparison.");
        }
        return new(new(resource, directory, asset.AssetId), snapshot, asset,
            new CapturedPost("43", 0, PostFieldPresence.All, Html: "<p>Saved image.</p>", Attachments: [attachment]), attachment, false, false)
            { AttachmentResolution = resolution, ThreadUrl = "https://offline.invalid/g/thread/42" };
    }

    private static void AssertNoRawValues(GalleryViewer viewer, MediaAttachmentBinding binding)
    {
        string text = string.Join("\n", viewer.GetGalleryVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
        Require(new[] { binding.Actual.Sha256, binding.Actual.Md5, binding.Provider.ExpectedMd5!, binding.Provider.Url }
            .All(raw => !text.Contains(raw, StringComparison.Ordinal)), "Raw hashes or provider URL leaked into normal gallery/context.");
    }
    private static void AssertVisibleText(TextBlock text, GalleryViewer viewer)
    {
        Rect bounds = Bounds(text, viewer), visible = bounds;
        foreach (Control ancestor in text.GetVisualAncestors().OfType<Control>())
        {
            if (ancestor.ClipToBounds) visible = visible.Intersect(Bounds(ancestor, viewer));
            if (ReferenceEquals(ancestor, viewer)) break;
        }
        Require(text.IsEffectivelyVisible && bounds.Width > 0 && bounds.Height > 0
            && text.TextLayout.WidthIncludingTrailingWhitespace <= bounds.Width + 1 && text.TextLayout.Height <= bounds.Height + 1
            && visible.Width >= bounds.Width - 1 && visible.Height >= bounds.Height - 1
            && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewer.Bounds.Width + 1 && bounds.Bottom <= viewer.Bounds.Height + 1,
            "Gallery provenance text is clipped or outside its visible viewport: " + text.Text);
    }
    private static object State(TextBlock text, Control root) => new { text.Text, bounds = Bounds(text, root).ToString(),
        layoutWidth = text.TextLayout.WidthIncludingTrailingWhitespace, layoutHeight = text.TextLayout.Height, text.IsEffectivelyVisible };
    private static Rect Bounds(Control control, Control root) => control.TranslatePoint(default, root) is { } point
        ? new Rect(point, control.Bounds.Size) : throw new InvalidOperationException("Gallery control has no root coordinates.");
    private static bool Overlaps(Rect first, Rect second) { Rect area = first.Intersect(second); return area.Width > 1 && area.Height > 1; }
    private static void SetContext(GalleryViewer viewer, bool open)
    {
        if (viewer.CaptureState().ContextOpen != open) Find<Button>(viewer, "GalleryToggleContext").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static async Task Settle(Window window) => await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
