using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class MenuIconFamilyTests
{
    public static TheoryData<string, string, string[]> Families => new()
    {
        { "sliders", "Controls, rules and settings currently share this mask.",
            ["navigation.rules", "plugins.show-settings", "plugins.show-grants", "rules.edit-scope", "gallery.tools-toggle",
                "gallery.overlay-toggle", "gallery.overlay-autohide-toggle", "video.speed-choose"] },
        { "maximize", "Media focus and window fullscreen currently share the four corners.",
            ["media.focus-mode", "window.fullscreen"] },
        { "workspace-expand", "Expanding the embedded viewer into its workspace.",
            ["viewer.focus-mode"] },
        { "rotate-ccw", "Reset and undo commands share this mask with backward transport.",
            ["plugins.discard", "plugins.rollback", "gallery.layout.reset", "settings.discard", "rules.discard",
                "rules.template-undo-apply", "admission.reset-choice", "table.reset-layout", "tags.quick.mark-previous",
                "tags.quick.undo", "video.seek-back", "video.step-back", "statistics.reset", "media.filter.reset-all",
                "settings.reset-default"] },
        { "rotate-cw", "Image rotation currently shares its mask with forward transport.",
            ["gallery.rotate", "video.seek-forward", "video.step-forward"] },
        { "refresh-cw", "Refresh and retry, with filter polarity as the existing exception.",
            ["plugins.refresh", "updates.check", "watch.check", "archive.refresh", "archive.saved-searches.replace",
                "admission.retry-catalog", "diagnostics.retry-selection", "diagnostics.retry-filtered", "tags.filter-toggle"] },
        { "monitor", "Show the app or its statistics. Follow system uses this mask as a theme state.",
            ["navigation.statistics", "app.show"] },
        { "power", "App shutdown and enable or disable switches.",
            ["plugins.enable", "plugins.disable", "app.quit", "rules.toggle-enabled"] },
        { "image", "Open or return to media, media display options and frame capture.",
            ["tagging.preview-column", "gallery.open", "gallery.open-results", "reader.media.activate", "gallery.fit",
                "gallery.context-close", "reader.view.media-only", "reader.view.textless-large", "video.snapshot"] },
        { "file-text", "Text and post documents, including the gallery post panel and the Reader Thread menu.",
            ["plugins.edit", "gallery.context-toggle", "gallery.show-in-thread", "reader.thread-menu",
                "about.open-license", "about.show-licenses"] },
        { "layers", "Drawing post content over the gallery rather than opening a panel.", ["gallery.post-overlay-toggle"] },
        { "external-link", "Open elsewhere, with profile switching as the existing exception.",
            ["profile.switch", "location.open-source", "reader.expand", "about.open-link", "media.open-external", "post.open-source"] },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void ReviewedMasksOnlyAppearOnDocumentedCommands(string icon, string reason, string[] ids)
    {
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(ids.Order(StringComparer.Ordinal),
            CommandCatalog.All.Where(command => command.Icon == icon).Select(command => command.Id).Order(StringComparer.Ordinal));
    }
}
