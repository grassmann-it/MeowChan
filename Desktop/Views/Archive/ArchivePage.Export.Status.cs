using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    /// <summary>
    /// Presentation-only provenance for the two retained export outcome controls. It keeps
    /// host wording as catalog keys and preserves paths, identities, warnings and exception
    /// details as raw text; it never carries export work, history or cancellation state.
    /// </summary>
    internal sealed class ArchiveExportStatusText
    {
        internal static readonly ArchiveExportStatusText Empty = new();
        private readonly Line[] _lines;

        private ArchiveExportStatusText(params Line[] lines) => _lines = lines;

        internal static ArchiveExportStatusText Raw(string? text)
        {
            string value = text ?? string.Empty;
            return value.Length == 0 ? Empty : new(Line.Raw(value));
        }

        internal static ArchiveExportStatusText Failed(string detail) =>
            new(Line.Localized("export.failed"), Line.Raw(detail));

        internal static ArchiveExportStatusText Localized(string key, params object?[] arguments) =>
            new(Line.Localized(key, arguments ?? Array.Empty<object?>()));

        internal static ArchiveExportStatusText SingleResult(ArchiveHtmlExportResult result)
        {
            List<Line> lines = new()
            {
                Line.Localized("archive.exportDone", result.PostCount, result.FileCount,
                    result.Omissions.Count, result.Warnings.Count),
                Line.Raw(result.Directory)
            };
            if (result.BackupDirectory != null)
                lines.Add(Line.Localized("export.backup", result.BackupDirectory));
            return new(lines.ToArray());
        }

        internal static ArchiveExportStatusText CollectionResult(ArchiveCollectionHtmlExportResult result)
        {
            List<Line> lines = new()
            {
                Line.Localized("export.collectionDone", result.ThreadCount, result.PostCount, result.FileCount,
                    result.Omissions.Count, result.Warnings.Count),
                Line.Raw(result.Directory)
            };
            if (result.Metadata != null)
                foreach (ArchiveCollectionMember member in result.Metadata.Members)
                    lines.Add(Line.Localized("export.collectionMemberDone", ExportMemberLabel(member.Thread), member.PostCount,
                        result.Omissions.Count(item => item.Thread == member.Thread)));
            foreach (var omissions in result.Omissions.GroupBy(item => (item.Thread, item.Reason)))
                lines.Add(new Line(new[]
                {
                    Segment.Raw(ExportMemberLabel(omissions.Key.Thread) + ": "),
                    Segment.Localized("export.omission." + omissions.Key.Reason, Array.Empty<object?>()),
                    Segment.Raw(" × " + omissions.Count())
                }));
            foreach (string warning in result.Warnings)
                lines.Add(Line.Raw(warning));
            if (result.BackupDirectory != null)
                lines.Add(Line.Localized("export.backup", result.BackupDirectory));
            return new(lines.ToArray());
        }

        internal string Render() => _lines.Length == 0 ? string.Empty : string.Join("\n", _lines.Select(line => line.Render()));

        // A provenance object may itself be an argument to another catalog format.
        // Render on demand so string.Format cannot freeze its text in the creating language.
        public override string ToString() => Render();

        private sealed record Line(IReadOnlyList<Segment> Segments)
        {
            internal static Line Raw(string text) => new(new[] { Segment.Raw(text) });
            internal static Line Localized(string key, params object?[] arguments) =>
                new(new[] { Segment.Localized(key, arguments) });
            internal string Render() => string.Concat(Segments.Select(segment => segment.Render()));
        }

        private sealed record Segment(string? RawText, string? Key, object?[] Arguments)
        {
            internal static Segment Raw(string text) => new(text, null, Array.Empty<object?>());
            internal static Segment Localized(string key, object?[]? arguments)
            {
                object?[] copied = (object?[])(arguments ?? Array.Empty<object?>()).Clone();
                return new(null, key, copied);
            }
            internal string Render() => RawText ?? Texts.T(Key!, Arguments);
        }
    }

    private ArchiveExportStatusText _exportStatusText = ArchiveExportStatusText.Empty;
    private ArchiveExportStatusText _exportErrorText = ArchiveExportStatusText.Empty;

    private void ExportStatus(string? message) => ExportStatus(ArchiveExportStatusText.Raw(message));
    private void ExportStatus(ArchiveExportStatusText message)
    {
        _exportStatusText = message;
        RenderExportStatusText();
    }

    private void ExportError(string? message)
    {
        if (_ended) return;
        ExportError(ArchiveExportStatusText.Raw(message));
    }

    private void ExportError(ArchiveExportStatusText message)
    {
        if (_ended) return;
        _exportErrorText = message;
        RenderExportErrorText();
    }

    private void RenderExportStatusText()
    {
        if (_exportCard == null) return;
        Control<TextBlock>("ArchiveExportStatus").Text = _exportStatusText.Render();
    }

    private void RenderExportErrorText()
    {
        if (_exportCard == null) return;
        string rendered = _exportErrorText.Render();
        Control<TextBlock>("ArchiveExportError").Text = rendered;
        Control<MeowChan.Desktop.Controls.WorkspaceBanner>("ArchiveExportErrorBanner").IsVisible = !string.IsNullOrEmpty(rendered);
    }
}
