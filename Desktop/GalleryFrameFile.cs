using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MeowChan.Desktop;

/// <summary>
/// The file name of a saved video frame: the medium's name, the position and ".png". A frame is first written under a
/// temporary name next to its destination and then moved into place without replacing anything, so an existing file is
/// never overwritten, not even by a file that appears between the name check and the save.
/// </summary>
internal static class GalleryFrameFile
{
    internal const int MaximumStemLength = 80;
    private const int MaximumNumberedNames = 1000;

    /// <summary>The first free name for a frame; only a hint, <see cref="Commit"/> is what never replaces a file.</summary>
    internal static string Path(string directory, string? fileName, long positionMs)
    {
        (string stem, string stamp) = Parts(fileName, positionMs);
        string candidate = Candidate(directory, stem, stamp, 1);
        for (int n = 2; File.Exists(candidate) && n < MaximumNumberedNames; n++)
            candidate = Candidate(directory, stem, stamp, n);
        return candidate;
    }

    /// <summary>A temporary name in <paramref name="directory"/> for the frame being written (a PNG, as the encoder picks the format by extension).</summary>
    internal static string TemporaryPath(string directory) =>
        System.IO.Path.Combine(directory, ".meowchan-frame-" + Guid.NewGuid().ToString("N") + ".png");

    /// <summary>
    /// Moves the finished <paramref name="temporary"/> file to the first free frame name and returns it, or null when no
    /// name could be taken. An existing file is never replaced; the temporary file is gone afterwards either way.
    /// </summary>
    internal static string? Commit(string temporary, string directory, string? fileName, long positionMs)
    {
        (string stem, string stamp) = Parts(fileName, positionMs);
        try
        {
            for (int n = 1; n < MaximumNumberedNames; n++)
            {
                string candidate = Candidate(directory, stem, stamp, n);
                if (File.Exists(candidate)) continue;
                try { File.Move(temporary, candidate, overwrite: false); return candidate; }
                catch (IOException) when (File.Exists(candidate)) { }
            }
            return null;
        }
        finally { Discard(temporary); }
    }

    /// <summary>Removes an unfinished temporary frame file; a file that cannot be removed is left, never an error.</summary>
    internal static void Discard(string temporary)
    {
        try { if (File.Exists(temporary)) File.Delete(temporary); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// True when a remembered frames folder may be used without asking: it is set and is not the folder of the archive
    /// export, which is shown to people and reused for other things.
    /// </summary>
    internal static bool IsDedicatedFolder(string? remembered, string? exportDirectory)
    {
        if (string.IsNullOrWhiteSpace(remembered)) return false;
        if (string.IsNullOrWhiteSpace(exportDirectory)) return true;
        try
        {
            string left = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(remembered));
            string right = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(exportDirectory));
            return !string.Equals(left, right, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static string Stem(string? fileName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var builder = new StringBuilder(stem.Length);
        for (int i = 0; i < stem.Length; i++)
        {
            char c = stem[i];
            if (char.IsHighSurrogate(c) && i + 1 < stem.Length && char.IsLowSurrogate(stem[i + 1]))
            {
                builder.Append(c).Append(stem[++i]);
                continue;
            }
            bool replace = char.IsSurrogate(c) || System.IO.Path.GetInvalidFileNameChars().AsSpan().Contains(c)
                || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            builder.Append(replace ? '_' : c);
        }
        stem = builder.ToString().Trim().Trim('.');
        if (stem.Length == 0) return "frame";
        if (stem.Length <= MaximumStemLength) return stem;
        // Cut between text elements so a surrogate pair or a letter with its combining marks is never split.
        int end = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(stem);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (end + element.Length > MaximumStemLength) break;
            end += element.Length;
        }
        stem = stem[..end].TrimEnd().TrimEnd('.');
        return stem.Length == 0 ? "frame" : stem;
    }

    private static (string Stem, string Stamp) Parts(string? fileName, long positionMs)
    {
        TimeSpan at = TimeSpan.FromMilliseconds(Math.Max(0, positionMs));
        string stamp = (at.TotalHours >= 1 ? at.ToString(@"h\-mm\-ss") : at.ToString(@"m\-ss")) + "-" + at.Milliseconds.ToString("000");
        return (Stem(fileName), stamp);
    }

    private static string Candidate(string directory, string stem, string stamp, int number) =>
        System.IO.Path.Combine(directory, stem + "-" + stamp + (number > 1 ? " (" + number + ")" : string.Empty) + ".png");
}
