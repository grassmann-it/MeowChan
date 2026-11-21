using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    internal const string TokenList = "{source} {post} {role} {hash} {ext}";

    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly (string Original, string Source, string Post, string Ext)[] Samples =
    [
        ("photo.jpg", "4chan", "123456789", "jpg"),
        ("IMG_0042.png", "danbooru", "7654321", "png"),
        ("clip.webm", "4chan", "123456790", "webm"),
    ];

    internal static bool IsActive(string? template) => !string.IsNullOrEmpty(template);

    internal static ExportFilenameTemplateCheck Check(string? template) => Preview(template).Check;

    internal static ExportFilenamePreview Preview(string? template)
    {
        if (!IsActive(template))
            return new(new(ExportFilenameTemplateProblem.None), ImmutableArray<ExportFilenamePreviewLine>.Empty);
        if (template!.Length > MaxTemplateLength)
            return Failed(ExportFilenameTemplateProblem.TooLong);
        ImmutableArray<SafeFilenameItem> items = [.. Samples.Select(SampleItem)];
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
        foreach ((string original, _, _, _) in Samples)
        {
            string leaf = result.Inventory[original];
            if (Utf8.GetByteCount(leaf) > MaxResultBytes) return Failed(ExportFilenameTemplateProblem.NameTooLong);
            lines.Add(new ExportFilenamePreviewLine(original, leaf));
        }
        return new(new(ExportFilenameTemplateProblem.None), [.. lines]);
    }

    /// <summary>The templated name of one file, or null (template off or unusable for this file) so the caller keeps its current naming.</summary>
    internal static string? TryRender(string? template, string itemId, string source, string post, string role, string sha256, string extension)
    {
        if (!IsActive(template) || template!.Length > MaxTemplateLength || !template.EndsWith(ExtensionEnding, StringComparison.Ordinal)
            || string.IsNullOrEmpty(itemId)) return null;
        SafeFilenameResult result = SafeFilenameTemplate.Assign(template,
            [new SafeFilenameItem(itemId, new SafeFilenameValues(source, post, role, sha256.ToLowerInvariant(), extension))]);
        if (!result.Success || !result.Inventory.TryGetValue(itemId, out string? leaf)) return null;
        return Utf8.GetByteCount(leaf) > MaxResultBytes ? null : leaf;
    }

    private static ExportFilenamePreview Failed(ExportFilenameTemplateProblem problem) =>
        new(new(problem), ImmutableArray<ExportFilenamePreviewLine>.Empty);

    private static SafeFilenameItem SampleItem((string Original, string Source, string Post, string Ext) sample)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sample:" + sample.Original))).ToLowerInvariant();
        return new SafeFilenameItem(sample.Original, new SafeFilenameValues(sample.Source, sample.Post, "original", hash, sample.Ext));
    }
}
