using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace MeowChan.Desktop;

/// <summary>
/// One local reading anchor. It contains identities, a viewport offset and (for the reading history list) the local thread
/// title as a short snapshot; never post text or quote history.
/// </summary>
internal sealed record LocalReadingPosition(string Source, string Board, string Thread, string PostId,
    double Top, string? SelectedPostId, string? Title = null)
{
    /// <summary>The longest title snapshot kept (characters).</summary>
    internal const int TitleMaximumLength = 200;

    /// <summary>A display-safe title snapshot: control characters become spaces, the result is trimmed and bounded; null when empty.</summary>
    internal static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string clean = new string(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length > TitleMaximumLength) clean = clean[..TitleMaximumLength].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    internal ResourceId ThreadId => new(Source, Board, Thread);
    internal void Validate()
    {
        if (new[] { Source, Board, Thread, PostId }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            || SelectedPostId is { } selected && (selected.Length > 512 || selected.Any(char.IsControl))
            || Title is { } title && (title.Length > 512 || title.Any(char.IsControl))
            || !double.IsFinite(Top) || Math.Abs(Top) > 1000000)
            throw new InvalidDataException("The saved reading position is invalid.");
    }
}

[JsonSerializable(typeof(LocalReadingPosition))]
internal partial class ReadingPositionJsonContext : JsonSerializerContext { }
