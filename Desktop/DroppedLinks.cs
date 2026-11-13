using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Input;

namespace MeowChan.Desktop;

internal enum DroppedContentKind { Empty, Links, Files }

/// <summary>What a drag over the main window carries. <see cref="Text"/> is set only for links.</summary>
internal sealed record DroppedContent(DroppedContentKind Kind, string? Text = null, int FileCount = 0)
{
    public static readonly DroppedContent Empty = new(DroppedContentKind.Empty);
}

/// <summary>
/// Chooses the dropped text that goes to the Add line's admission path. Splitting, validation
/// and per-entry feedback stay with <see cref="AddInput"/> and the host, exactly as for typed text.
/// Reads are synchronous: the platform may dispose the transfer when the drop handler returns.
/// </summary>
internal static class DroppedLinks
{
    // URL-only formats that some sources offer without plain text. Avalonia 12.1.4 on X11 maps
    // text/uri-list to DataFormat.File and keeps only its file: entries, so browser links reach us
    // there as the text/plain or UTF8_STRING copy browsers add; other backends may expose these raw.
    private static readonly (string Identifier, Encoding Encoding, bool UriList)[] UrlFormats =
    [
        ("text/uri-list", Encoding.UTF8, true),
        ("public.url", Encoding.UTF8, false),
        ("UniformResourceLocatorW", Encoding.Unicode, false),
        ("UniformResourceLocator", Encoding.UTF8, false)
    ];
    private static readonly char[] Separators = [',', '\r', '\n'];

    public static DroppedContent Read(IDataTransfer transfer)
    {
        Uri[] items = transfer.Contains(DataFormat.File)
            ? (transfer.TryGetFiles() ?? []).Select(item => item.Path).ToArray() : [];
        string? text = transfer.Contains(DataFormat.Text) ? transfer.TryGetText() : null;
        if (string.IsNullOrWhiteSpace(text)) text = ReadUrlFormat(transfer);
        return Classify(text, items);
    }

    /// <summary>
    /// Local files or folders win over text, because file managers also offer their paths as text.
    /// Text whose every entry is a local path or file: URI counts as files too.
    /// </summary>
    public static DroppedContent Classify(string? text, IReadOnlyList<Uri> items)
    {
        int localItems = items.Count(uri => !uri.IsAbsoluteUri || uri.IsFile);
        if (localItems > 0) return new(DroppedContentKind.Files, FileCount: localItems);
        string[] entries = (text ?? string.Empty).Split(Separators,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > 0 && entries.All(IsLocalPath))
            return new(DroppedContentKind.Files, FileCount: entries.Length);
        if (!string.IsNullOrWhiteSpace(text)) return new(DroppedContentKind.Links, text);
        string[] remote = items.Select(uri => uri.AbsoluteUri).ToArray();
        return remote.Length > 0 ? new(DroppedContentKind.Links, string.Join("\n", remote)) : DroppedContent.Empty;
    }

    /// <summary>A drop adds links but never moves them: Move could delete the source's text.</summary>
    public static DragDropEffects AcceptedEffect(DragDropEffects requested) =>
        requested.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
        : requested.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
        // X11 reports only the source's suggested action; answering Copy to a Move suggestion is allowed.
        : DragDropEffects.Copy;

    private static bool IsLocalPath(string entry) =>
        Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri) && uri.IsFile;

    private static string? ReadUrlFormat(IDataTransfer transfer)
    {
        foreach (DataFormat format in transfer.Formats)
        {
            if (format.Kind != DataFormatKind.Platform) continue;
            int index = Array.FindIndex(UrlFormats, candidate => candidate.Identifier == format.Identifier);
            if (index < 0) continue;
            string? value = format switch
            {
                DataFormat<string> text => transfer.TryGetValue(text),
                DataFormat<byte[]> bytes => transfer.TryGetValue(bytes) is { } data ? UrlFormats[index].Encoding.GetString(data) : null,
                _ => null
            };
            value = value?.TrimEnd('\0');
            // RFC 2483: one URI per line; lines starting with '#' are comments.
            if (value != null && UrlFormats[index].UriList)
                value = string.Join("\n", value.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.StartsWith('#')));
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
