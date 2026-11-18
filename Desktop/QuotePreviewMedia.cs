using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>What the quote preview does with the first medium of the quoted post.</summary>
internal enum QuotePreviewMediaKind { None, Image, Gif, Video }

/// <summary>The room for the medium of a quote preview and whether the preview opens below the reference (otherwise above it).</summary>
internal readonly record struct QuotePreviewMediaBox(Size Box, bool Below)
{
    internal bool HasRoom => Box.Width > 0 && Box.Height > 0;
}

/// <summary>
/// The window-free rules of the medium in a quote preview (Tom, 16.11.2026: "if a post has media and someone replies to it, hovering the
/// reference shows the medium LARGE; videos autoplay muted"). The medium is the quoted post's first one; it takes at most
/// <see cref="ViewportShare"/> of the reader's width and height, keeps its aspect, is never enlarged beyond its own pixels and is never
/// placed outside the window: the side of the reference with more room is chosen and the text below the medium is subtracted first.
/// The proportions are fixed parts of the layout, not resource limits; the decode is made at the shown size.
/// </summary>
internal static class QuotePreviewMediaRules
{
    /// <summary>The medium takes at most this share of the reader's width and of its height.</summary>
    internal const double ViewportShare = 0.6;
    /// <summary>Distance the preview keeps to the window's edges.</summary>
    internal const double WindowMargin = 8;
    /// <summary>Below this many pixels on either side there is no room for a medium (the preview shows the text only).</summary>
    internal const double MinimumSide = 48;
    /// <summary>The preview's largest width without a medium (as before media came in).</summary>
    internal const double TextOnlyMaxWidth = 480;
    /// <summary>The preview's largest height without a medium (as before media came in).</summary>
    internal const double TextOnlyMaxHeight = 320;
    /// <summary>Pixels around the reference and the preview that still count as "on the way" from one to the other.</summary>
    internal const int CorridorSlack = 12;

    /// <summary>The first medium of a post that is not marked deleted (null: none); the count is the post's number of files.</summary>
    internal static CapturedAttachment? FirstMedium(IReadOnlyList<CapturedAttachment>? attachments)
    {
        if (attachments == null) return null;
        foreach (CapturedAttachment attachment in attachments)
            if (!attachment.IsDeleted) return attachment;
        return null;
    }

    /// <summary>What the attachment is by its name: a GIF and a video can play in the preview, anything else shows its still picture.</summary>
    internal static QuotePreviewMediaKind Classify(CapturedAttachment? attachment)
    {
        if (attachment == null) return QuotePreviewMediaKind.None;
        string name = attachment.OriginalFileName ?? attachment.MediaId ?? string.Empty;
        if (string.Equals(Path.GetExtension(name), ".gif", StringComparison.OrdinalIgnoreCase)) return QuotePreviewMediaKind.Gif;
        return SavedMediaTypes.Classify(name) == SavedMediaKind.Video ? QuotePreviewMediaKind.Video : QuotePreviewMediaKind.Image;
    }

    /// <summary>
    /// The room for the medium: at most <see cref="ViewportShare"/> of the reader (<paramref name="viewport"/>), inside the window less its margin and
    /// the preview's own chrome, on the side of the reference (<paramref name="anchor"/>, window coordinates) with more room, less the text
    /// (<paramref name="textHeight"/>) that goes below the medium. No room (<see cref="QuotePreviewMediaBox.HasRoom"/> false) when a side is below
    /// <see cref="MinimumSide"/>.
    /// </summary>
    internal static QuotePreviewMediaBox Box(Size viewport, Size window, Rect anchor, double textHeight, Size chrome)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) viewport = window;
        double above = anchor.Top - WindowMargin, below = window.Height - anchor.Bottom - WindowMargin;
        bool down = below >= above;
        double width = Math.Min(viewport.Width * ViewportShare, window.Width - 2 * WindowMargin - chrome.Width);
        double height = Math.Min(viewport.Height * ViewportShare, (down ? below : above) - Math.Max(0, textHeight) - chrome.Height);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < MinimumSide || height < MinimumSide) return new(default, down);
        return new(new Size(Math.Floor(width), Math.Floor(height)), down);
    }

    /// <summary>The shown size of a picture of <paramref name="width"/> × <paramref name="height"/> pixels in <paramref name="box"/>: its aspect kept, made smaller to fit, never larger.</summary>
    internal static Size Fit(double width, double height, Size box)
    {
        if (!(width > 0) || !(height > 0) || !(box.Width > 0) || !(box.Height > 0)) return default;
        double scale = Math.Min(1, Math.Min(box.Width / width, box.Height / height));
        return new Size(Math.Max(1, Math.Round(width * scale)), Math.Max(1, Math.Round(height * scale)));
    }

    /// <summary>The frame before the picture arrived: the provider's pixel size when the capture has it, otherwise 4:3 at the box's width.</summary>
    internal static Size Provisional(CapturedAttachment attachment, Size box) => attachment is { Width: > 0 and int width, Height: > 0 and int height }
        ? Fit(width, height, box) : Fit(box.Width, box.Width * 3 / 4, box);

    /// <summary>The bounds of the decode in physical pixels: the box at the window's render scaling (a picture is never decoded larger than it is shown).</summary>
    internal static (int Width, int Height) DecodeBounds(Size box, double scaling)
    {
        double scale = double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
        return (Math.Max(1, (int)Math.Ceiling(box.Width * scale)), Math.Max(1, (int)Math.Ceiling(box.Height * scale)));
    }

    /// <summary>
    /// The preview's own player: the reader's volume, seek step and file limit, one player at most, repeating, muted as <paramref name="muted"/> says.
    /// It plays whatever the reader's "Clicking a medium" choice is: the preview is not a click.
    /// </summary>
    internal static ReaderInlineVideoOptions VideoOptions(ReaderInlineVideoOptions reader, bool muted) => reader with
    {
        Enabled = true, InPost = false, AutoplayMuted = true, MaxPlayers = 1, Repeat = true, StartMuted = muted, KeepImagesExpanded = false
    };

    /// <summary>"1 of 3" for a post with several files; empty for one.</summary>
    internal static string CountText(int count) => count > 1 ? Texts.T("reader.sidebar.position", 1, count) : string.Empty;

    /// <summary>
    /// Whether the pointer, having left the reference, is on its way to the preview: inside the bounding box of the reference and the preview
    /// (screen pixels), widened by <paramref name="slack"/>. Without a known pointer position it is not (the preview closes at once, as before).
    /// </summary>
    internal static bool InCorridor(PixelPoint? pointer, PixelRect anchor, PixelRect preview, int slack = CorridorSlack)
    {
        if (pointer is not PixelPoint point || anchor.Width <= 0 && anchor.Height <= 0 || preview.Width <= 0 || preview.Height <= 0) return false;
        int left = Math.Min(anchor.X, preview.X) - slack, top = Math.Min(anchor.Y, preview.Y) - slack;
        int right = Math.Max(anchor.Right, preview.Right) + slack, bottom = Math.Max(anchor.Bottom, preview.Bottom) + slack;
        return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
    }
}

/// <summary>What leaving the reference or the preview does.</summary>
internal enum QuotePreviewLeave { Keep, CloseNow, CloseAfterGrace }

/// <summary>
/// The hover life of the quote preview without a window. A request waits for the delay (or opens at once); while it waits, leaving the reference
/// cancels it. An open preview stays while the pointer is over the reference or over the preview: leaving the reference toward the preview gives
/// the pointer <see cref="CloseGraceMs"/> to arrive, leaving it elsewhere closes at once; leaving the preview closes after the grace unless the
/// pointer is back on the reference. A reference that holds the keyboard focus keeps its preview. One preview at a time: a new request replaces it.
/// </summary>
internal sealed class QuotePreviewHoverState
{
    /// <summary>The time the pointer has to cross from the reference to the preview (or back) before it closes.</summary>
    internal const int CloseGraceMs = 250;

    internal bool Pending { get; private set; }
    internal bool IsOpen { get; private set; }
    internal bool OverLink { get; private set; }
    internal bool OverPreview { get; private set; }
    internal bool GracePending { get; private set; }

    /// <summary>A new preview (a pointer on a reference, or the keyboard focus on one); <paramref name="immediate"/> opens it now.</summary>
    internal void Request(bool pointer, bool immediate)
    {
        Pending = !immediate;
        IsOpen = immediate;
        OverLink = pointer;
        OverPreview = false;
        GracePending = false;
    }

    /// <summary>The delay passed: true when the waiting preview opens now.</summary>
    internal bool DelayElapsed()
    {
        if (!Pending) return false;
        Pending = false;
        IsOpen = true;
        return true;
    }

    /// <summary>The pointer is (again) on the reference of the open or waiting preview.</summary>
    internal void EnterLink()
    {
        OverLink = true;
        GracePending = false;
    }

    /// <summary>The pointer left the reference; <paramref name="focused"/>: the reference holds the keyboard focus; <paramref name="towardPreview"/>: it is on its way to the preview.</summary>
    internal QuotePreviewLeave LeaveLink(bool focused, bool towardPreview)
    {
        OverLink = false;
        if (focused) return QuotePreviewLeave.Keep;
        if (!IsOpen || !towardPreview && !OverPreview)
        {
            Reset();
            return QuotePreviewLeave.CloseNow;
        }
        if (OverPreview) return QuotePreviewLeave.Keep;
        GracePending = true;
        return QuotePreviewLeave.CloseAfterGrace;
    }

    /// <summary>The pointer is on the open preview.</summary>
    internal void EnterPreview()
    {
        if (!IsOpen) return;
        OverPreview = true;
        GracePending = false;
    }

    /// <summary>The pointer left the preview; <paramref name="focused"/>: the reference holds the keyboard focus.</summary>
    internal QuotePreviewLeave LeavePreview(bool focused)
    {
        OverPreview = false;
        if (!IsOpen || OverLink || focused) return QuotePreviewLeave.Keep;
        GracePending = true;
        return QuotePreviewLeave.CloseAfterGrace;
    }

    /// <summary>The grace passed: true when the preview closes now (the pointer reached neither the reference nor the preview).</summary>
    internal bool GraceElapsed()
    {
        if (!GracePending) return false;
        GracePending = false;
        if (!IsOpen || OverLink || OverPreview) return false;
        Reset();
        return true;
    }

    internal void Reset()
    {
        Pending = false;
        IsOpen = false;
        OverLink = false;
        OverPreview = false;
        GracePending = false;
    }
}

/// <summary>
/// The medium of a quote preview: the quoted post's first medium, decoded at the shown size (<see cref="ArchiveAttachmentRow.LoadSized"/>, the reader's
/// gated, cancellable decode), with "1 of N" when the post has several files. A GIF animates and a video plays in a player of its own (one at most,
/// repeating, muted unless <see cref="Muted"/> is off) when <see cref="Autoplay"/> is on; otherwise the still picture stays. A medium that is not saved
/// leaves the frame out and shows a short note. <see cref="Clear"/> (the preview closed or shows another post) cancels the load, gives the picture
/// back, retires the GIF and releases the player: nothing is left running. UI thread only.
/// </summary>
internal sealed class QuotePreviewMedia : IReaderInlineVideoOwner
{
    private readonly Grid _frame = new() { Name = "QuotePreviewMediaFrame", ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly Image _picture = new() { Name = "QuotePreviewMediaPicture", Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new()
    {
        Name = "QuotePreviewMediaStatus", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, IsVisible = false
    };
    private readonly TextBlock _countText = new() { Name = "QuotePreviewMediaCount" };
    private readonly Border _count;
    private readonly TextBlock _note = new() { Name = "QuotePreviewMediaNote", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic, IsVisible = false };
    // The inline player always moves its bar into a host; the preview has no bar (a click on the picture pauses and continues).
    private readonly Panel _transport = new();
    private readonly ReaderInlineVideoController _videos = new();
    private CancellationTokenSource? _loading;
    private Bitmap? _bitmap;
    private GalleryGifPlayback? _gif;
    private Size _box;
    private int _request;

    internal QuotePreviewMedia()
    {
        _status.Classes.Add("workspace-meta");
        _note.Classes.Add("workspace-meta");
        _countText.Classes.Add("workspace-meta");
        _count = new Border
        {
            Name = "QuotePreviewMediaCountBadge", Child = _countText, Padding = new Thickness(8, 2), Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsVisible = false, IsHitTestVisible = false
        };
        _count[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Card");
        _count[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _frame.Children.Add(_picture);
        _frame.Children.Add(_status);
        _frame.Children.Add(_count);
        View = new StackPanel { Name = "QuotePreviewMedia", Spacing = 4, Children = { _frame, _note } };
    }

    /// <summary>The frame and the note line; the page puts it above the preview's text.</summary>
    internal Control View { get; }
    internal Control Frame => _frame;
    internal Image Picture => _picture;
    /// <summary>The frame of a medium is shown (loading, a picture or a playing video).</summary>
    internal bool ShowsMedium => _frame.IsVisible;
    internal Bitmap? Shown => _bitmap;
    internal string NoteText => _note.IsVisible ? _note.Text ?? string.Empty : string.Empty;
    internal string CountLabel => _count.IsVisible ? _countText.Text ?? string.Empty : string.Empty;
    internal QuotePreviewMediaKind Kind { get; private set; }
    /// <summary>The preview's own player (test and smoke evidence of its bound and its release).</summary>
    internal ReaderInlineVideoController Videos => _videos;
    internal PlaybackArbitrationGate? PlaybackGate { get => _videos.PlaybackGate; set => _videos.PlaybackGate = value; }
    internal bool HasGif => _gif != null;
    /// <summary>The running load (smoke seam).</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;
    /// <summary>A video or GIF starts by itself ("Play videos and GIFs in quote previews"; on by default).</summary>
    internal bool Autoplay { get; set; } = true;
    /// <summary>The video starts muted ("Mute videos in quote previews"; on by default).</summary>
    internal bool Muted { get; set; } = true;
    /// <summary>The frame took this size (the provider's size first, then the picture's): the page fits the preview's width to it.</summary>
    internal Action<Size>? FrameSized { get; set; }
    /// <summary>Builds the player for a saved video (entry, the preview's options, muted, name); null builds the gallery's inline player into the frame.</summary>
    internal Func<GalleryEntry, ReaderInlineVideoOptions, bool, string, IReaderInlineVideoPlayer>? CreatePlayer { get; set; }

    /// <summary>
    /// Shows <paramref name="row"/>'s medium in <paramref name="box"/> (logical pixels; decoded at <paramref name="scaling"/>); <paramref name="count"/> is the
    /// post's number of files. <paramref name="readerVideo"/> are the reader's video options (volume, seek step, file limit).
    /// </summary>
    internal void Show(ArchiveAttachmentRow row, int count, Size box, double scaling, ReaderInlineVideoOptions readerVideo)
    {
        Clear();
        int request = _request;
        _box = box;
        Kind = QuotePreviewMediaRules.Classify(row.Attachment);
        SetFrame(QuotePreviewMediaRules.Provisional(row.Attachment, box));
        _frame.IsVisible = true;
        _status.Text = Texts.T("reader.sidebar.loading");
        _status.IsVisible = true;
        string counted = QuotePreviewMediaRules.CountText(count);
        _countText.Text = counted;
        _count.IsVisible = counted.Length > 0;
        CancellationTokenSource loading = new();
        _loading = loading;
        LoadTask = LoadAsync(row, box, scaling, readerVideo, request, loading);
    }

    /// <summary>No medium can be shown (not saved, all files deleted): the frame is left out and the note says why.</summary>
    internal void ShowNote(string text)
    {
        Clear();
        SetNote(text);
    }

    private bool IsCurrent(int request, CancellationTokenSource loading) => request == _request && !loading.IsCancellationRequested;

    private async Task LoadAsync(ArchiveAttachmentRow row, Size box, double scaling, ReaderInlineVideoOptions readerVideo, int request, CancellationTokenSource loading)
    {
        CancellationToken token = loading.Token;
        try
        {
            if (row.LoadSized is not { } load) { Unavailable(request, Texts.T("archive.notSaved")); return; }
            (int width, int height) = QuotePreviewMediaRules.DecodeBounds(box, scaling);
            Bitmap? bitmap = await load(width, height, token);
            if (!IsCurrent(request, loading)) { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            if (bitmap == null) { Unavailable(request, Texts.T("archive.notSaved")); return; }
            _bitmap = bitmap;
            _picture.Source = bitmap;
            // The decode is bounded by the box at the render scaling; one at its own size is never shown larger than its pixels.
            SetFrame(QuotePreviewMediaRules.Fit(bitmap.PixelSize.Width, bitmap.PixelSize.Height, box));
            _status.IsVisible = false;
            if (!Autoplay || Kind is not (QuotePreviewMediaKind.Gif or QuotePreviewMediaKind.Video) || row.ResolveEntry is not { } resolve) return;
            GalleryEntry? entry = await resolve(token);
            if (!IsCurrent(request, loading) || entry == null) return;
            string name = row.Attachment.OriginalFileName ?? row.Attachment.MediaId ?? string.Empty;
            if (entry.Kind == SavedMediaKind.Video)
            {
                if (ReaderInlineVideoRules.LimitReason(QuotePreviewMediaRules.VideoOptions(readerVideo, Muted), entry.Asset.Length, name) is { } reason) { SetNote(reason); return; }
                Func<GalleryEntry, ReaderInlineVideoOptions, bool, string, IReaderInlineVideoPlayer> make = CreatePlayer ?? CreateGalleryPlayer;
                await PlayVideoAsync(request, (options, muted) => make(entry, options, muted, name), readerVideo);
            }
            else if (Kind == QuotePreviewMediaKind.Gif)
            {
                // A GIF that cannot animate (too large for the bounded canvas, damaged) keeps its still picture without a note.
                try { await AnimateGifAsync(entry, request, loading); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                {
                    Serilog.Log.Debug(error, "The GIF of a quote preview could not animate");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The reader's own sentence when the file is not saved (or saved in another version); anything else is the generic one, the cause in the log.
            string notSaved = Texts.T("archive.notSaved"), otherVersion = Texts.T("archive.differentVersion");
            string text = error is InvalidDataException && (error.Message == notSaved || error.Message == otherVersion) ? error.Message : notSaved;
            if (error is not InvalidDataException) Serilog.Log.Debug(error, "The medium of a quote preview could not be shown");
            if (_bitmap == null) Unavailable(request, text);
            else if (request == _request) SetNote(Texts.T("reader.sidebar.unavailable"));
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) _loading = null;
            loading.Dispose();
        }
    }

    /// <summary>Plays the shown video in the preview's own player (made by <paramref name="create"/> with the preview's options and mute). False when nothing plays.</summary>
    internal Task<bool> PlayVideoAsync(Func<ReaderInlineVideoOptions, bool, IReaderInlineVideoPlayer> create, ReaderInlineVideoOptions readerVideo) =>
        PlayVideoAsync(_request, create, readerVideo);

    private async Task<bool> PlayVideoAsync(int request, Func<ReaderInlineVideoOptions, bool, IReaderInlineVideoPlayer> create, ReaderInlineVideoOptions readerVideo)
    {
        bool muted = Muted;
        await _videos.ConfigureAsync(QuotePreviewMediaRules.VideoOptions(readerVideo, muted));
        if (request != _request) return false;
        return await _videos.PlayAsync(this, () => create(_videos.Options, muted));
    }

    // The gallery's inline player (the reader's R10 wrapper), its picture under the loading text and the count; its bar goes nowhere.
    private IReaderInlineVideoPlayer CreateGalleryPlayer(GalleryEntry entry, ReaderInlineVideoOptions options, bool muted, string name)
    {
        GalleryInlineVideoPlayer player = new(_frame, _status, _transport, entry, options, muted, name.Length > 0 ? name : Texts.T("reader.video.unnamed"));
        player.Failed += message => Dispatcher.UIThread.Post(() => { if (_videos.IsLive(this)) SetNote(message); });
        player.Clicked += () => OpenActions.Start(() => _videos.ToggleAsync(this), "reader.quotePreview.video");
        return player;
    }

    private async Task AnimateGifAsync(GalleryEntry entry, int request, CancellationTokenSource loading)
    {
        GalleryGifPlayback? gif = await GalleryGifPlayback.OpenAsync(entry, _picture, loading.Token);
        if (gif == null) return; // not a GIF after all: the still picture stays
        if (!IsCurrent(request, loading)) { OpenActions.Start(() => RetireGifAsync(gif), "reader.quotePreview.gif.retire"); return; }
        _gif = gif;
        gif.Adopt();
        gif.SetActive(true);
        // The animation shows its own frames: the still decode goes back.
        Bitmap? still = _bitmap;
        _bitmap = null;
        if (still != null) MediaTileThumbnails.Release(still);
        SetFrame(QuotePreviewMediaRules.Fit(gif.Info.Width, gif.Info.Height, _box));
    }

    private static async Task RetireGifAsync(GalleryGifPlayback gif)
    {
        try { await gif.DisposeAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The playback keeps the ownership of a cleanup that failed (GifRetirementException); the preview only reports it.
            Serilog.Log.Warning(error, "Retiring the GIF of a quote preview failed");
        }
    }

    /// <summary>The preview closed or shows another post: the load is cancelled, the picture given back, the GIF retired and the player released.</summary>
    internal void Clear()
    {
        _request = unchecked(_request + 1);
        CancellationTokenSource? loading = _loading;
        _loading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        GalleryGifPlayback? gif = _gif;
        _gif = null;
        if (gif != null) OpenActions.Start(() => RetireGifAsync(gif), "reader.quotePreview.gif.retire");
        // A player whose native cleanup still runs stays in the frame until it returned (the gallery's contract), but is not seen meanwhile.
        foreach (Control child in _frame.Children)
            if (!ReferenceEquals(child, _picture) && !ReferenceEquals(child, _status) && !ReferenceEquals(child, _count)) child.IsVisible = false;
        OpenActions.Start(() => _videos.ReleaseAllAsync(), "reader.quotePreview.video.release");
        _picture.Source = null;
        Bitmap? bitmap = _bitmap;
        _bitmap = null;
        if (bitmap != null) MediaTileThumbnails.Release(bitmap);
        Kind = QuotePreviewMediaKind.None;
        _frame.IsVisible = false;
        _status.IsVisible = false;
        _count.IsVisible = false;
        _note.IsVisible = false;
        _note.Text = string.Empty;
    }

    private void Unavailable(int request, string text)
    {
        if (request != _request) return;
        _picture.Source = null;
        Bitmap? bitmap = _bitmap;
        _bitmap = null;
        if (bitmap != null) MediaTileThumbnails.Release(bitmap);
        _frame.IsVisible = false;
        _status.IsVisible = false;
        _count.IsVisible = false;
        SetNote(text);
    }

    private void SetNote(string text)
    {
        _note.Text = text;
        _note.IsVisible = text.Length > 0;
    }

    private void SetFrame(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        _frame.Width = size.Width;
        _frame.Height = size.Height;
        FrameSized?.Invoke(size);
    }

    // The preview's player is a plain video in a post for the controller (the gallery's repeat semantics, which the options set on).
    bool IReaderInlineVideoOwner.IsViewer => false;
    void IReaderInlineVideoOwner.PlayerReleased() { }
    void IReaderInlineVideoOwner.CurrentChanged(bool current) { }
    void IReaderInlineVideoOwner.PlaybackChanged(bool playing) { }
}
