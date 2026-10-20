using System.IO;

namespace MeowChan.Gallery;

/// <summary>Renderer choice only. Asset identity, verification and post matching remain independent.</summary>
public enum SavedMediaKind { Image, Video, Other, Unknown }

public static class SavedMediaTypes
{
    public static SavedMediaKind Classify(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => SavedMediaKind.Image,
        ".webm" or ".mp4" or ".mov" or ".mkv" or ".avi" => SavedMediaKind.Video,
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".pdf" or ".zip" => SavedMediaKind.Other,
        _ => SavedMediaKind.Unknown
    };
}
