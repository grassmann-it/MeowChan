using System;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>Protocol 1.10a: different pages do not make unrelated meanings of a gesture safe.
/// These are semantic families, not a list of coincidentally equal default gestures.</summary>
internal static class ShortcutMeaning
{
    private static readonly string[][] Families =
    {
        new[] { "tagging.continue-here", "thread.read", "media.open", "gallery.open", "reader.media.activate" },
        new[] { "tagging.mark-skipped", "tags.queue.skip" },
        new[] { "tags.quick.commit-next", "tags.queue.done-next" },
        new[] { "tagging.remove", "watch.remove" },
        new[] { "tagging.show-in-media", "thread.show-in-media" },
        new[] { "plugins.refresh", "watch.check" },
        new[] { "selection.select-all", "media.select-all", "archive.select-page" },
        new[] { "media.tile-size.larger", "archive.media-tile-size.larger", "reader.font-size.larger" },
        new[] { "media.tile-size.smaller", "archive.media-tile-size.smaller", "reader.font-size.smaller" },
        new[] { "media.tile-size.reset", "archive.media-tile-size.reset", "reader.font-size.reset" },
        new[] { "watch.retry-media", "diagnostics.retry-selection" },
        new[] { "board.filters", "archive.filters-toggle" },
        new[] { "location.open-folder", "media.show-in-folder" },
        new[] { "location.open-source", "media.open-external" },
        new[] { "selection.copy-links", "diagnostics.copy", "statistics.copy-row", "media.copy-path", "post.copy-link" },
        new[] { "archive.previous-page", "pagination.previous" },
        new[] { "archive.next-page", "pagination.next" },
        new[] { "input.focus-download-search", "reader.find", "input.focus-search", "input.focus-media-search", "input.focus-settings-search", "input.focus-diagnostics-search" },
        new[] { "reader.close", "gallery.close" },
        new[] { "reader.history-back", "gallery.return", "media.return" },
        new[] { "reader.previous-thread", "gallery.previous" },
        new[] { "reader.next-thread", "gallery.next" },
        new[] { "export.open-html-dialog", "media.export-original" },
        new[] { "reader.sidebar.toggle", "media.panel.toggle" },
        new[] { "thread.tags", "tags.quick.thread-section" }
    };

    internal static bool Same(string first, string second) => first == second
        || Families.Any(family => family.Contains(first, StringComparer.Ordinal) && family.Contains(second, StringComparer.Ordinal));

    // Execution contexts deliberately include Reader/Media video fallback routes. Runtime
    // precedence is not permission for the editor to promise a shadowed binding.
    internal static bool Compete(string first, string second) => !Same(first, second)
        || EffectiveShortcutBindings.ExecutionContexts(first).Intersect(EffectiveShortcutBindings.ExecutionContexts(second)).Any()
        || EffectiveShortcutBindings.ContextsOverlap(first, second);
}
