using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Commands;

/// <summary>Existing named controls retain their executor and content. No alternate feature logic.</summary>
internal static class LegacyCommandAdapters
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WorkspaceMenuItem"] = "navigation.app-menu",
        ["ArchiveFilters"] = "archive.filters-toggle",
        ["ArchiveLayoutList"] = "archive.thread-view", ["ArchiveLayoutTable"] = "archive.thread-view",
        ["ArchiveMediaMode"] = "archive.media-view", ["ArchiveFavoritesFilter"] = "favorites.filter",
        ["ClosePreviewButton"] = "preview.close",
        ["ArchiveExportPrevious"] = "export.open-previous", ["ArchiveExportLegacyFind"] = "export.find-legacy",
        ["ArchiveExportLegacyAssociate"] = "export.associate-legacy", ["ArchiveExportStart"] = "export.start-html",
        ["ArchiveExportBrowse"] = "export.browse-parent", ["ArchiveExportClose"] = "export.close",
        ["ArchiveExportAssociate"] = "export.associate", ["ArchiveExportOpen"] = "location.open-folder",
        ["BoardAdmissionReinclude"] = "admission.reinclude", ["BoardAdmissionRetry"] = "admission.retry-catalog",
        ["BoardAdmissionAddRule"] = "admission.add-rule", ["BoardAdmissionCancel"] = "admission.cancel",
        ["BoardAdmissionApply"] = "admission.apply", ["BoardAdmissionResetChoice"] = "admission.reset-choice",
        ["BoardAdmissionRemoveRule"] = "admission.remove-rule",
        ["CopyVersionButton"] = "about.copy-version", ["CreditsTab"] = "about.show-credits", ["LicensesTab"] = "about.show-licenses",
        ["ProjectLink"] = "about.open-link", ["AuthorLink"] = "about.open-link", ["DonationLink"] = "about.open-link",
        ["ReadingFontSizeInput"] = "reader.font-size", ["ShowShortcutHelpInput"] = "viewer.shortcut-hints",
        ["GalleryVideoPlay"] = "video.play", ["GalleryVideoPause"] = "video.pause", ["GalleryVideoStop"] = "video.stop"
    };
    internal static void Register(Control root)
    {
        foreach (var visual in root.GetVisualDescendants())
            if (visual is Control control && control.Name is string name && Names.TryGetValue(name, out var id))
                CommandBindings.BindExisting(control, id);
    }
}
