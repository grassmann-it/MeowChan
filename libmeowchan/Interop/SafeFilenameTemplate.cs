using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeowChan.Interop;

internal sealed record SafeFilenameValues(string Source, string Post, string Role,
    string MeasuredSha256, string Ext);
internal sealed record SafeFilenameItem(string ItemId, SafeFilenameValues Values);
internal enum SafeFilenameError
{
    None, InvalidInput, TemplateTooLarge, InvalidUnicode, InvalidTemplate,
    InvalidValue, InvalidHash, InvalidExtension, TooManyItems, DuplicateItemId,
    UnsafeName, OutputTooLarge, Collision
}
internal sealed record SafeFilenameResult(SafeFilenameError Error,
    ImmutableSortedDictionary<string, string> Inventory)
{
    internal bool Success => Error == SafeFilenameError.None;
}

/// <summary>Pure selected-batch naming only. Measured hashes/public values are caller obligations.
/// No path authorization, IO, sanitization, truncation or export completion.</summary>
internal static class SafeFilenameTemplate
{
    internal const int MaxTemplateBytes = 4096;
    internal const int MaxNameBytes = 240;
    internal const int MaxItems = 1000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static SafeFilenameResult Fail(SafeFilenameError error) =>
        new(error, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal));

    internal static SafeFilenameResult Assign(string template, ImmutableArray<SafeFilenameItem> items)
    {
        if (template is null || items.IsDefault) return Fail(SafeFilenameError.InvalidInput);
        if (items.Length > MaxItems) return Fail(SafeFilenameError.TooManyItems);
        try
        {
            if (template.Length > MaxTemplateBytes || Utf8.GetByteCount(template) > MaxTemplateBytes)
                return Fail(SafeFilenameError.TemplateTooLarge);
            var parts = new List<(string Text, bool Token)>();
            int start = 0;
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] == '}') return Fail(SafeFilenameError.InvalidTemplate);
                if (template[i] != '{') continue;
                parts.Add((template.Substring(start, i - start), false));
                int end = template.IndexOf('}', i + 1);
                if (end < 0) return Fail(SafeFilenameError.InvalidTemplate);
                string token = template.Substring(i + 1, end - i - 1);
                if (token is not ("source" or "post" or "role" or "hash" or "ext"))
                    return Fail(SafeFilenameError.InvalidTemplate);
                parts.Add((token, true));
                i = end;
                start = end + 1;
            }
            parts.Add((template.Substring(start), false));
            var ordered = new SortedDictionary<string, SafeFilenameValues>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item is null || item.ItemId is null || item.Values is null ||
                    item.ItemId.Length == 0 || item.ItemId.Length > 4096 ||
                    Utf8.GetByteCount(item.ItemId) > 4096) return Fail(SafeFilenameError.InvalidInput);
                if (!ordered.TryAdd(item.ItemId, item.Values)) return Fail(SafeFilenameError.DuplicateItemId);
            }
            var inventory = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in ordered)
            {
                var v = pair.Value;
                foreach (string value in new[] { v.Source, v.Post, v.Role })
                {
                    if (value is null || value.Length > MaxNameBytes || Utf8.GetByteCount(value) > MaxNameBytes ||
                        !SafeLeaf(value)) return Fail(SafeFilenameError.InvalidValue);
                }
                if (v.MeasuredSha256 is null || v.MeasuredSha256.Length != 64)
                    return Fail(SafeFilenameError.InvalidHash);
                foreach (char c in v.MeasuredSha256)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        return Fail(SafeFilenameError.InvalidHash);
                if (v.Ext is null || v.Ext.Length is < 1 or > 16) return Fail(SafeFilenameError.InvalidExtension);
                foreach (char c in v.Ext)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
                        return Fail(SafeFilenameError.InvalidExtension);
                var output = new StringBuilder();
                foreach (var part in parts)
                {
                    string text = !part.Token ? part.Text : part.Text switch
                    {
                        "source" => v.Source, "post" => v.Post, "role" => v.Role,
                        "hash" => v.MeasuredSha256, _ => v.Ext
                    };
                    if (output.Length + text.Length > MaxNameBytes) return Fail(SafeFilenameError.OutputTooLarge);
                    output.Append(text);
                }
                string leaf = output.ToString();
                if (!SafeLeaf(leaf)) return Fail(SafeFilenameError.UnsafeName);
                string suffix = "~" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(pair.Key))).ToLowerInvariant();
                int dot = leaf.LastIndexOf('.');
                leaf = dot > 0 ? leaf.Insert(dot, suffix) : leaf + suffix;
                if (Utf8.GetByteCount(leaf) > MaxNameBytes) return Fail(SafeFilenameError.OutputTooLarge);
                if (!SafeLeaf(leaf)) return Fail(SafeFilenameError.UnsafeName);
                string canonical;
                try { canonical = leaf.Normalize(NormalizationForm.FormC); }
                // Some globalization backends reject valid UTF-8 noncharacters such as U+FFFE.
                // Keep this catch local: unrelated argument/programming errors must not be hidden.
                catch (ArgumentException) { return Fail(SafeFilenameError.InvalidUnicode); }
                if (!names.Add(canonical)) return Fail(SafeFilenameError.Collision);
                inventory.Add(pair.Key, leaf);
            }
            // Empty batches still validate the template's literal filename hazards.
            if (ordered.Count == 0)
            {
                var probe = new StringBuilder();
                foreach (var part in parts) probe.Append(part.Token ? "x" : part.Text);
                if (!SafeLeaf(probe.ToString())) return Fail(SafeFilenameError.UnsafeName);
            }
            return new(SafeFilenameError.None, inventory.ToImmutable());
        }
        catch (EncoderFallbackException) { return Fail(SafeFilenameError.InvalidUnicode); }
    }

    private static bool SafeLeaf(string text)
    {
        if (text.Length == 0 || text.Contains("..", StringComparison.Ordinal) ||
            text.EndsWith('.') || text.EndsWith(' ')) return false;
        foreach (char c in text)
        {
            if ("<>:\"/\\|?*{}".IndexOf(c) >= 0 || char.IsControl(c)) return false;
        }
        // Strict UTF-8 validation remains at the API boundary; inspect valid surrogate
        // pairs as scalars here so supplementary Cf cannot masquerade as two surrogates.
        foreach (Rune rune in text.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) return false;
        string stem = text.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$") return false;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
            stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')) return false;
        return true;
    }
}
