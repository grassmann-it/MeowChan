using System;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

/// <summary>Reader display seams consumed by the later Config/Settings package (P1).</summary>
public partial class ArchivePage
{
    private readonly ReaderDisplayPreferences _readerDisplayPreferences = new();

    /// <summary>Live, clamped reader display values. P1 maps Config.ReaderThumbnailWidth and ReaderDateStyle here.</summary>
    internal ReaderDisplayPreferences ReaderDisplayPreferences => _readerDisplayPreferences;

    internal int ReaderThumbnailWidth
    {
        get => _readerDisplayPreferences.ReaderThumbnailWidth;
        set
        {
            int width = global::MeowChan.Desktop.ReaderDisplayPreferences.ClampThumbnailWidth(value);
            if (width == _readerDisplayPreferences.ReaderThumbnailWidth) return;
            _readerDisplayPreferences.ReaderThumbnailWidth = width;
            ArchivePosts.SetValue(ReaderPostLayout.ThumbnailWidthProperty, width);
            if (_thread != null) RenderThread();
        }
    }

    internal ReaderDateStyle ReaderDateStyle
    {
        get => _readerDisplayPreferences.ReaderDateStyle;
        set
        {
            ReaderDateStyle style = Enum.IsDefined(value) ? value : ReaderDateStyle.System;
            if (style == _readerDisplayPreferences.ReaderDateStyle) return;
            _readerDisplayPreferences.ReaderDateStyle = style;
            if (_thread != null) RenderThread();
        }
    }

    /// <summary>
    /// P1 mapping seam: applies the three reader settings without introducing persistence here. ReaderPageSize is the
    /// existing P0 property; its own 20–200 clamp and anchor-preserving render remain authoritative.
    /// </summary>
    internal void ApplyReaderDisplayPreferences(int thumbnailWidth, ReaderDateStyle dateStyle, int pageSize)
    {
        ReaderThumbnailWidth = thumbnailWidth;
        ReaderDateStyle = dateStyle;
        ReaderPageSize = pageSize;
    }

    private void InitializeReaderDisplay()
    {
        ArchivePosts.SetValue(ReaderPostLayout.ThumbnailWidthProperty,
            _readerDisplayPreferences.ReaderThumbnailWidth);
    }
}
