using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MeowChan.Interop;

internal enum ExportFilenameTemplateProblem
{
    None, TooLong, InvalidTemplate, MustEndWithExtension, UnsafeName, NameTooLong, Unusable
}

internal sealed record ExportFilenameTemplateCheck(ExportFilenameTemplateProblem Problem)
{
    internal bool Ok => Problem == ExportFilenameTemplateProblem.None;
}

/// <summary>One sample row of the settings preview: what the file is called now and what the template makes of it.</summary>
internal sealed record ExportFilenamePreviewLine(string Original, string Result);

internal sealed record ExportFilenamePreview(ExportFilenameTemplateCheck Check, ImmutableArray<ExportFilenamePreviewLine> Lines);

/// <summary>
/// The product-facing side of <see cref="SafeFilenameTemplate"/>: the filename template of exported originals. An empty
/// template means "keep the current naming" (the default), so nothing changes unless the person opts in. The template
/// must end with <c>.{ext}</c>; <c>SafeFilenameTemplate</c> always adds a stable identifier before the extension, so two
/// different files can never share a name. A leaf that would not fit the file system once the exporter adds its content
/// suffix is refused here and falls back to the current naming per file. No IO.
/// </summary>
internal static class ExportFilenameTemplate
{
    /// <summary>Fixed ceiling in characters (documented, not a setting): well below <see cref="SafeFilenameTemplate.MaxNameBytes"/> so a name stays usable.</summary>
    internal const int MaxTemplateLength = 120;

    /// <summary>Fixed ceiling in UTF-8 bytes of a produced name, identifier and extension included. The exporter adds <c>-</c> and 64 hex characters, and file systems allow 255 bytes.</summary>
    internal const int MaxResultBytes = 190;

    internal const string ExtensionEnding = ".{ext}";
    internal const string TokenList = "{source} {post} {role} {hash} {md5} {ext}";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    // The measured digests represent the UTF-8 bytes of "sample:" followed by the original name.
    private static readonly (string Original, string Source, string Post, string Ext, string Md5)[] Samples =
    [
        ("photo.jpg", "4chan", "123456789", "jpg", "dc8a0a7ca226176bb8ed5aa44257d605"),
        ("IMG_0042.png", "danbooru", "7654321", "png", "aa6c7132c9303077c830fe6164defdc4"),
        ("clip.webm", "4chan", "123456790", "webm", "6e2bbdf2a81cc33f6f6c2ad63006ed98"),
    ];

    internal static bool IsActive(string? template) => !string.IsNullOrEmpty(template);

    internal static ExportFilenameTemplateCheck Check(string? template) => Preview(template).Check;

    internal static ExportFilenamePreview Preview(string? template)
    {
        if (!IsActive(template))
            return new(new(ExportFilenameTemplateProblem.None), ImmutableArray<ExportFilenamePreviewLine>.Empty);
        if (template!.Length > MaxTemplateLength)
            return Failed(ExportFilenameTemplateProblem.TooLong);
        bool usesMd5 = template.Contains("{md5}", StringComparison.Ordinal);
        ImmutableArray<SafeFilenameItem> items = [.. Samples.Select(sample => SampleItem(sample, usesMd5))];
        SafeFilenameResult result = SafeFilenameTemplate.Assign(template, items);
        if (!result.Success)
            return Failed(result.Error switch
            {
                SafeFilenameError.InvalidTemplate => ExportFilenameTemplateProblem.InvalidTemplate,
                SafeFilenameError.TemplateTooLarge => ExportFilenameTemplateProblem.TooLong,
                SafeFilenameError.UnsafeName or SafeFilenameError.InvalidUnicode => ExportFilenameTemplateProblem.UnsafeName,
                SafeFilenameError.OutputTooLarge => ExportFilenameTemplateProblem.NameTooLong,
                _ => ExportFilenameTemplateProblem.Unusable
            });
        if (!template.EndsWith(ExtensionEnding, StringComparison.Ordinal))
            return Failed(ExportFilenameTemplateProblem.MustEndWithExtension);
        List<ExportFilenamePreviewLine> lines = new(Samples.Length);
        for (int i = 0; i < items.Length; i++)
        {
            SafeFilenameItem item = items[i];
            string leaf = result.Inventory[item.ItemId];
            if (Utf8.GetByteCount(leaf) > MaxResultBytes) return Failed(ExportFilenameTemplateProblem.NameTooLong);
            if (usesMd5) leaf = WithContentSuffix(leaf, item.Values.MeasuredSha256);
            lines.Add(new ExportFilenamePreviewLine(Samples[i].Original, leaf));
        }
        return new(new(ExportFilenameTemplateProblem.None), [.. lines]);
    }

    /// <summary>The templated name of one file, or null (template off or unusable for this file) so the caller keeps its current naming.</summary>
    internal static string? TryRender(string? template, string itemId, string source, string post, string role, string sha256, string extension,
        string? md5 = null)
    {
        if (!IsActive(template) || template!.Length > MaxTemplateLength || !template.EndsWith(ExtensionEnding, StringComparison.Ordinal)
            || string.IsNullOrEmpty(itemId)) return null;
        SafeFilenameResult result = SafeFilenameTemplate.Assign(template,
            [new SafeFilenameItem(itemId, new SafeFilenameValues(source, post, role, sha256.ToLowerInvariant(), extension, md5))]);
        if (!result.Success || !result.Inventory.TryGetValue(itemId, out string? leaf)) return null;
        return Utf8.GetByteCount(leaf) > MaxResultBytes ? null : leaf;
    }

    private static ExportFilenamePreview Failed(ExportFilenameTemplateProblem problem) =>
        new(new(problem), ImmutableArray<ExportFilenamePreviewLine>.Empty);

    internal static string WithContentSuffix(string name, string suffix)
    {
        string extension = Path.GetExtension(name);
        return (extension.Length == 0 ? name : name[..^extension.Length]) + "-" + suffix + extension;
    }

    internal static string ItemIdentity(string source, string board, string thread, string mediaId, string variant) =>
        source + "\0" + board + "\0" + thread + "\0" + mediaId + "\0" + variant;

    private static SafeFilenameItem SampleItem((string Original, string Source, string Post, string Ext, string Md5) sample, bool usesMd5)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("sample:" + sample.Original)));
        string id = usesMd5 ? ItemIdentity(sample.Source, "g", sample.Post, "456", "original") : sample.Original;
        return new SafeFilenameItem(id, new SafeFilenameValues(sample.Source, sample.Post, "original", hash, sample.Ext, sample.Md5));
    }
}
