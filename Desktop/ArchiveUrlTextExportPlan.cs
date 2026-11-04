using System;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interop.Links;

namespace MeowChan.Desktop;

internal enum ArchiveUrlTextExportPlanError
{
    None, InvalidIdentity, IdentityMismatch, MissingUrl, LimitExceeded,
    InvalidUnicode, InvalidPage, PageIdentityMismatch
}

/// <summary>
/// Operations-local inert snapshot of one saved thread page. No availability,
/// consent, persistence, or acquisition authority is represented by this plan.
/// </summary>
internal sealed class ArchiveUrlTextExportPlan
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _utf8;

    private ArchiveUrlTextExportPlan(ResourceId id, string url, byte[] utf8)
    {
        Id = id;
        Url = url;
        _utf8 = (byte[])utf8.Clone();
    }

    internal ResourceId Id { get; }
    internal string Url { get; }
    internal int ByteCount => _utf8.Length;
    internal byte[] CopyUtf8() => (byte[])_utf8.Clone();

    internal static ArchiveUrlTextExportPlan? TryCreate(ResourceId? selectedId,
        ArchiveThreadSummary? summary, out ArchiveUrlTextExportPlanError error)
    {
        error = ArchiveUrlTextExportPlanError.InvalidIdentity;
        if (selectedId is null || summary?.Id is null
            || selectedId.Source is not ("4chan" or "8kun")
            || string.IsNullOrEmpty(selectedId.Board) || selectedId.Board.Length > 256
            || string.IsNullOrEmpty(selectedId.Thread) || selectedId.Thread.Length > 256)
            return null;
        error = ArchiveUrlTextExportPlanError.IdentityMismatch;
        if (summary.Id != selectedId) return null;
        string? url = summary.Url;
        error = ArchiveUrlTextExportPlanError.MissingUrl;
        if (string.IsNullOrEmpty(url)) return null;
        // Bound UTF-16 work before encoding, slicing, parsing or concatenation.
        error = ArchiveUrlTextExportPlanError.LimitExceeded;
        if (url.Length > PlainUrlText.MaxLineCharacters) return null;
        int count;
        try { count = StrictUtf8.GetByteCount(url); }
        catch (EncoderFallbackException)
        {
            error = ArchiveUrlTextExportPlanError.InvalidUnicode;
            return null;
        }
        // Reserve the final LF before allocating the encoded input.
        if (count >= PlainUrlText.MaxBytes) return null;
        byte[] input = StrictUtf8.GetBytes(url);
        PlainUrlText codec = PlainUrlText.ForSupportedChanPages();
        LinkTextResult parsed = codec.Parse(input);
        error = ArchiveUrlTextExportPlanError.InvalidPage;
        if (parsed.Issues.Count != 0 || parsed.Links.Count != 1
            || parsed.Links[0].Kind != PassiveLinkKind.Post
            || !string.Equals(parsed.Links[0].Text, url, StringComparison.Ordinal)
            || url.Contains('#')) return null;

        // The core owns the grammar; these span comparisons only narrow it to
        // the selected exact identity. No Uri normalization or numeric aliases.
        string prefix = selectedId.Source == "4chan" ? "https://boards.4chan.org/" : "https://8kun.top/";
        string route = selectedId.Source == "4chan" ? "/thread/" : "/res/";
        string suffix = selectedId.Source == "4chan" ? "" : ".html";
        error = ArchiveUrlTextExportPlanError.PageIdentityMismatch;
        int expectedLength = prefix.Length + selectedId.Board.Length + route.Length
            + selectedId.Thread.Length + suffix.Length;
        if (url.Length != expectedLength || !url.StartsWith(prefix, StringComparison.Ordinal)) return null;
        ReadOnlySpan<char> rest = url.AsSpan(prefix.Length);
        if (!rest[..selectedId.Board.Length].SequenceEqual(selectedId.Board.AsSpan())) return null;
        rest = rest[selectedId.Board.Length..];
        if (!rest[..route.Length].SequenceEqual(route.AsSpan())) return null;
        rest = rest[route.Length..];
        if (!rest[..selectedId.Thread.Length].SequenceEqual(selectedId.Thread.AsSpan())
            || !rest[selectedId.Thread.Length..].SequenceEqual(suffix.AsSpan())) return null;

        LinkTextExport exported = codec.Export(input);
        error = ArchiveUrlTextExportPlanError.InvalidPage;
        if (exported.Issues.Count != 0 || exported.Utf8.Length != count + 1
            || exported.Utf8[^1] != (byte)'\n'
            || !exported.Utf8.AsSpan(0, count).SequenceEqual(input)) return null;
        error = ArchiveUrlTextExportPlanError.None;
        return new(selectedId, url, exported.Utf8);
    }
}
