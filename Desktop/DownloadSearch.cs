using System;

namespace MeowChan.Desktop;

/// <summary>Literal, local-only search over values already present in session snapshots.</summary>
internal static class DownloadSearch
{
    internal static bool Matches(ThreadRow row, string? query) => Matches(query,
        row.Title, row.BoardName, row.Snapshot.Id.Board, row.ThreadId, row.Url);
    internal static bool Matches(BoardRow row, string? query) => Matches(query,
        row.NiceName, row.Snapshot.Name, row.BoardCode, row.Url);
    internal static bool Matches(string? query, params string?[] values)
    {
        string text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;
        foreach (string? value in values)
            if (value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }
}
