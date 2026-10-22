using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace MeowChan.Desktop;

/// <summary>One local reading anchor. It contains identities and a viewport offset, never post text or quote history.</summary>
internal sealed record LocalReadingPosition(string Source, string Board, string Thread, string PostId,
    double Top, string? SelectedPostId)
{
    internal ResourceId ThreadId => new(Source, Board, Thread);
    internal void Validate()
    {
        if (new[] { Source, Board, Thread, PostId }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            || SelectedPostId is { } selected && (selected.Length > 512 || selected.Any(char.IsControl))
            || !double.IsFinite(Top) || Math.Abs(Top) > 1000000)
            throw new InvalidDataException("The saved reading position is invalid.");
    }
}

[JsonSerializable(typeof(LocalReadingPosition))]
internal partial class ReadingPositionJsonContext : JsonSerializerContext { }
