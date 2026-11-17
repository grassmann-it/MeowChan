using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>The thumbnail of a result card: frame, corner marks and the bitmap cache (bounded by the page).</summary>
internal sealed partial class BooruWorkbenchView
{
    private const int PreviewSize = 112;
    private const int MaximumPreviewBytes = 8 * 1024 * 1024;

    private sealed record CachedBitmap(byte[] Bytes, Bitmap Bitmap);

    private readonly Dictionary<string, CachedBitmap> _bitmaps = new(StringComparer.Ordinal);

    private Control CreatePreview(BooruResultRowView row)
    {
        Border frame = new()
        {
            Width = PreviewSize, Height = PreviewSize, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true,
            CornerRadius = new CornerRadius(5)
        };
        if (row.Preview == BooruPreviewState.Ready && PreviewBitmap(row) is { } bitmap)
        {
            // D3: the thumbnail is flat on the card (no box, no edge of its own); only the placeholder has a quiet slot.
            // Decorative next to the card's title line; not a second "Post #n".
            Image image = new() { Source = bitmap, Stretch = Stretch.Uniform };
            AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
            frame.Child = image;
        }
        else
        {
            frame.Classes.Add("workspace-thumb-placeholder");
            string key = row.Preview switch
            {
                BooruPreviewState.Loading => "booru.ui.post.previewLoading",
                BooruPreviewState.None => "booru.ui.post.noPreview",
                _ => "booru.ui.post.previewUnavailable"
            };
            TextBlock placeholder = new()
            {
                Text = Texts.T(key), Classes = { "workspace-meta" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8)
            };
            AutomationProperties.SetAccessibilityView(placeholder, AccessibilityView.Raw);
            frame.Child = placeholder;
        }
        return frame;
    }

    /// <summary>
    /// I7: fixed corners on the result preview, from the post data already shown (no request): the type (GIF, video) at the top
    /// right and a "deleted"/"banned" mark at the bottom left. Static borders over the preview; the card keeps its size.
    /// </summary>
    private static Control WithPreviewCorners(Control preview, BooruPostView post)
    {
        string? kind = post.Original?.Extension?.TrimStart('.').ToLowerInvariant() switch
        {
            "gif" => Texts.T("media.tile.kind.gif"),
            "mp4" or "webm" or "zip" => Texts.T("media.tile.kind.video"),
            _ => null
        };
        string? withdrawn = post.IsDeleted ? Texts.T("booru.post.deleted") : post.IsBanned ? Texts.T("booru.post.banned") : null;
        if (kind == null && withdrawn == null) return preview;
        Panel corners = new() { Width = PreviewSize, Height = PreviewSize, VerticalAlignment = VerticalAlignment.Top, Children = { preview } };
        Border badge = WorkspaceChips.CreateCornerBadge("BooruResultKind:" + post.PostId, HorizontalAlignment.Right, VerticalAlignment.Top);
        badge.Margin = new Thickness(0, 4, 4, 0);
        badge.IsVisible = kind != null;
        ((TextBlock)badge.Child!).Text = kind;
        corners.Children.Add(badge);
        Border mark = WorkspaceChips.CreateCornerMark("BooruResultWithdrawn:" + post.PostId, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        mark.Margin = new Thickness(4, 0, 0, 4);
        WorkspaceChips.SetCornerMark(mark, withdrawn == null ? null : WorkspaceChips.MarkWithdrawn, withdrawn);
        corners.Children.Add(mark);
        return corners;
    }

    private Bitmap? PreviewBitmap(BooruResultRowView row)
    {
        if (row.PreviewBytes is not { Length: > 0 } bytes || bytes.Length > MaximumPreviewBytes) return null;
        string id = row.Post.PostId;
        if (_bitmaps.TryGetValue(id, out CachedBitmap? cached) && ReferenceEquals(cached.Bytes, bytes)) return cached.Bitmap;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKCodec? codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false));
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > 40_000_000) return null;
            stream.Position = 0;
            int width = Math.Min(ImageOrientation.DisplaySize(codec).Width, PreviewSize * 2);
            Bitmap bitmap = ImageOrientation.DecodeToWidth(stream, width);
            if (cached is not null) cached.Bitmap.Dispose();
            _bitmaps[id] = new CachedBitmap(bytes, bitmap);
            return bitmap;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void PruneBitmaps(IEnumerable<BooruResultRowView> rows)
    {
        HashSet<string> keep = rows.Select(row => row.Post.PostId).ToHashSet(StringComparer.Ordinal);
        foreach (string id in _bitmaps.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            // The old cards are already detached by the rebuild, so the image can be released.
            _bitmaps[id].Bitmap.Dispose();
            _bitmaps.Remove(id);
        }
    }

    private void DisposePreviews()
    {
        foreach (CachedBitmap cached in _bitmaps.Values) cached.Bitmap.Dispose();
        _bitmaps.Clear();
    }
}
