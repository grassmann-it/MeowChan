using System;
using System.IO;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>The file name of a saved video frame: the medium's name, the position and ".png", never overwriting a file.</summary>
internal static class GalleryFrameFile
{
    internal const int MaximumStemLength = 80;

    internal static string Path(string directory, string? fileName, long positionMs)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        char[] invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
        stem = new string(stem.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (stem.Length == 0) stem = "frame";
        if (stem.Length > MaximumStemLength) stem = stem[..MaximumStemLength];
        TimeSpan at = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        string stamp = (at.TotalHours >= 1 ? at.ToString(@"h\-mm\-ss") : at.ToString(@"m\-ss")) + "-" + at.Milliseconds.ToString("000");
        string candidate = System.IO.Path.Combine(directory, stem + "-" + stamp + ".png");
        for (int n = 2; File.Exists(candidate); n++)
            candidate = System.IO.Path.Combine(directory, stem + "-" + stamp + " (" + n + ").png");
        return candidate;
    }
}
