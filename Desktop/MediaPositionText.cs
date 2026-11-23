using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class MediaPositionText
{
    internal static string? Short(GalleryTagPanelPosition? position)
    {
        if (position is not { Number: > 0 } value) return null;
        return Texts.T(value.More ? "tags.quick.gallery.positionMoreShort" : "tags.quick.gallery.positionShort",
            value.Number, value.Count);
    }

    internal static string? Long(GalleryTagPanelPosition? position, bool marked = false)
    {
        if (position is not { } value) return null;
        string text = value.Number <= 0 ? Texts.T("thread.media.total", value.Count)
            : Texts.T(value.Queue ? "tagging.position" : value.More ? "tags.quick.gallery.positionMore" : "tags.quick.gallery.position",
                value.Number, value.Count);
        return marked ? Texts.T("tags.quick.marked.counter", text) : text;
    }
}
