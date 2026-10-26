using System;

namespace MeowChan.Desktop;

/// <summary>Literal, local-only search over values already present in session snapshots.</summary>
internal static class DownloadSearch
{
    // Prepare once per capture/search revision, then reuse for every row in a bounded filter pass.
    // The row overloads avoid a params-array allocation and repeated Trim for every progress refresh.
    internal readonly struct Query(string? text)
    {
        private readonly string _text = text?.Trim() ?? string.Empty;
        private bool Contains(string? value) => value?.Contains(_text, StringComparison.OrdinalIgnoreCase) == true;
        internal bool Matches(ThreadRow row) => string.IsNullOrEmpty(_text) || Contains(row.Title) || Contains(row.BoardName)
            || Contains(row.Snapshot.Id.Board) || Contains(row.ThreadId) || Contains(row.Url);
        internal bool Matches(BoardRow row) => string.IsNullOrEmpty(_text) || Contains(row.NiceName)
            || Contains(row.Snapshot.Name) || Contains(row.BoardCode) || Contains(row.Url);
    }
    internal static bool Matches(ThreadRow row, string? query) => new Query(query).Matches(row);
    internal static bool Matches(BoardRow row, string? query) => new Query(query).Matches(row);
    internal static bool Matches(string? query, params string?[] values)
    {
        string text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return true;
        foreach (string? value in values)
            if (value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }
}
