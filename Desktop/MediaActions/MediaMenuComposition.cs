using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop.MediaActions;

/// <summary>Every place that shows the one media menu. The common actions are identical on all of them.</summary>
internal enum MediaMenuSurface
{
    ArchiveTable,
    ArchiveTiles,
    ArchiveInline,
    MediaPage,
    Gallery,
    /// <summary>The transparent window over a natively rendered video (Windows); the same menu as the gallery viewport.</summary>
    NativeVideoOverlay
}

/// <summary>The common media actions. The declaration order is the menu order on every surface.</summary>
internal enum MediaMenuAction
{
    Open,
    Favorite,
    Rating,
    Tags,
    Export,
    ShowInFolder,
    CopyPath,
    Details,
    ShowInThread,
    OpenProviderPost,
    CopyProviderPost
}

internal enum MediaMenuFavorite { Unknown, Off, On }

/// <summary>How many captured posts the clicked file belongs to (an archive file row can be shared by several).</summary>
internal enum MediaMenuPostScope { None, Single, Several }

/// <summary>
/// The exact occurrence a menu acts on, frozen when the menu opens. Every surface describes its target in this one
/// shape (from a gallery entry or from a media index entry), so equal occurrences compare equal across surfaces and a
/// file shared by several posts is never silently reduced to one of them.
/// </summary>
internal sealed record MediaMenuTarget(ResourceId Thread, string? Directory, string? AssetId, string? PostId, int? Position,
    string? AttachmentRevisionKey, string? Sha256, string? LocalSlot)
{
    internal static MediaMenuTarget Of(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Key.Thread, entry.Manifest.Directory, entry.Asset.AssetId, entry.Post?.PostId, entry.Attachment?.Position,
            entry.AttachmentResolution?.Binding?.Reference.RevisionKey, entry.Asset.Sha256?.ToLowerInvariant(),
            entry.LocalTagEligible ? entry.Asset.RequestedFileName : null);
    }

    internal static MediaMenuTarget Of(MediaLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Thread, entry.Directory, entry.AssetId, entry.PostId, entry.Position, entry.AttachmentRevisionKey,
            entry.Sha256?.ToLowerInvariant(), entry.LocalSlot);
    }

    /// <summary>The file alone, for a saved file row that is shared by several posts and so names no single post.</summary>
    internal static MediaMenuTarget OfFile(GalleryEntry entry) => Of(entry) with
    { PostId = null, Position = null, AttachmentRevisionKey = null, LocalSlot = null };
}

/// <summary>
/// What a surface knows about the clicked occurrence. The surface supplies causes; the composition turns them into
/// availability and a reason, so every surface words the same cause the same way.
/// </summary>
internal sealed record MediaMenuFacts(MediaMenuSurface Surface, MediaMenuTarget? Target)
{
    /// <summary>The saved file is present (the target resolved to a recorded, readable file).</summary>
    internal bool Present { get; init; } = true;
    /// <summary>The surface can open the gallery for this target.</summary>
    internal bool GalleryWired { get; init; } = true;
    /// <summary>The target is part of the sequence the gallery would show (not hidden by a filter).</summary>
    internal bool InGallerySequence { get; init; } = true;
    /// <summary>Several items are selected on the surface, so a single-target action would be ambiguous.</summary>
    internal bool SeveralSelected { get; init; }
    internal MediaMenuFavorite Favorite { get; init; } = MediaMenuFavorite.Unknown;
    internal bool FavoriteWired { get; init; } = true;
    internal bool RatingWired { get; init; } = true;
    /// <summary>False while ratings cannot be read; a nonempty <see cref="RatingBlocked"/> overrides the generic reason.</summary>
    internal bool RatingKnown { get; init; } = true;
    internal string? RatingBlocked { get; init; }
    internal bool TagsWired { get; init; } = true;
    /// <summary>Null when the target can be tagged; otherwise the localized reason it cannot (see <see cref="MediaMenuReasons.Tags"/>).</summary>
    internal string? TagsBlocked { get; init; }
    /// <summary>
    /// True on a surface whose "Tag…" opens the quick tag popover: a target that cannot be tagged keeps the entry enabled and the
    /// popover states <see cref="TagsBlocked"/> as text (plan §1.4) instead of a disabled entry with a tooltip.
    /// </summary>
    internal bool TagsBlockedExplained { get; init; }
    internal bool ExportWired { get; init; } = true;
    internal bool FolderWired { get; init; } = true;
    internal bool CopyPathWired { get; init; } = true;
    internal bool DetailsWired { get; init; } = true;
    internal MediaMenuPostScope Posts { get; init; } = MediaMenuPostScope.None;
    internal bool ShowInThreadWired { get; init; } = true;
    /// <summary>The target is a downloaded Danbooru medium; only then the provider post actions exist.</summary>
    internal bool ProviderMedia { get; init; }
    internal bool ProviderPostKnown { get; init; }
    internal bool ProviderWired { get; init; } = true;
}

/// <summary>One composed menu entry: what it is called, whether it can run and, if not, why.</summary>
internal sealed record MediaMenuItemModel(MediaMenuAction Action, string? CommandId, string? TextKey, string Label,
    bool Available, string? Reason, bool? Checked)
{
    /// <summary>The identity used as the menu item's tag and by the keyboard router.</summary>
    internal string Id => CommandId ?? TextKey ?? Action.ToString();
    /// <summary>Open and show-in-thread group the first section, the file actions the second, the post actions the third.</summary>
    internal int Group => Action switch
    {
        MediaMenuAction.Open or MediaMenuAction.Favorite or MediaMenuAction.Rating or MediaMenuAction.Tags => 0,
        MediaMenuAction.Export or MediaMenuAction.ShowInFolder or MediaMenuAction.CopyPath or MediaMenuAction.Details => 1,
        _ => 2
    };
}

internal sealed record MediaMenuModel(MediaMenuSurface Surface, MediaMenuTarget? Target, IReadOnlyList<MediaMenuItemModel> Items)
{
    internal IEnumerable<string> Unavailable => Items.Where(item => !item.Available && item.Reason != null)
        .Select(item => item.Label + ": " + item.Reason);
}

/// <summary>The words for every cause an action can be unavailable. All surfaces use these and only these.</summary>
internal static class MediaMenuReasons
{
    internal static string NotSaved => Texts.T("media.menu.reason.notSaved");
    internal static string Unavailable => Texts.T("media.menu.reason.unavailable");
    internal static string NotInSequence => Texts.T("media.menu.reason.notInSequence");
    internal static string SeveralSelected => Texts.T("media.menu.reason.severalSelected");
    internal static string NoPost => Texts.T("gallery.noPost");
    internal static string SeveralPosts => Texts.T("media.menu.reason.severalPosts");
    internal static string ProviderPost => Texts.T("media.menu.reason.providerPost");

    /// <summary>
    /// Why a medium cannot be tagged, from the one shared target resolver's answer. Booru media first (never a thread
    /// tag), then a file shared by several posts, then the resolver's own reason.
    /// </summary>
    internal static string Tags(GalleryMediaTagUnavailable reason, bool providerMedia = false, bool ambiguous = false) =>
        providerMedia ? Texts.T("booru.media.tags.unavailable")
        : ambiguous ? Texts.T("tags.media.ambiguous")
        : reason == GalleryMediaTagUnavailable.NoPostVersion ? Texts.T("tags.media.boundElsewhere")
        : Texts.T("tags.media.unsupported");

    internal static string TagsUnknown => Texts.T("tags.unavailable");
}

/// <summary>
/// The one ordered action list of the media menu. Surfaces pass what they know (<see cref="MediaMenuFacts"/>) and show
/// the result; order, labels, availability and reasons come only from here.
/// </summary>
internal static class MediaMenuComposition
{
    internal static IReadOnlyList<MediaMenuAction> Order { get; } = Enum.GetValues<MediaMenuAction>();

    internal static string OpenCommand(MediaMenuSurface surface) => surface == MediaMenuSurface.MediaPage ? "media.open" : "gallery.open";

    internal static MediaMenuModel Compose(MediaMenuFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        List<MediaMenuItemModel> items = new();
        bool inGallery = facts.Surface is MediaMenuSurface.Gallery or MediaMenuSurface.NativeVideoOverlay;
        string? NeedsFile() => facts.Present ? null : MediaMenuReasons.NotSaved;
        void Add(MediaMenuAction action, string? commandId, string label, bool available, string? reason, bool? isChecked = null,
            string? textKey = null) => items.Add(new(action, commandId, textKey, label, available, available ? null : reason, isChecked));

        // "Open in gallery" is meaningless inside the gallery, so the gallery and its overlay do not list it.
        if (!inGallery)
        {
            string reason = NeedsFile() ?? (facts.SeveralSelected ? MediaMenuReasons.SeveralSelected
                : !facts.InGallerySequence ? MediaMenuReasons.NotInSequence : MediaMenuReasons.Unavailable);
            Add(MediaMenuAction.Open, OpenCommand(facts.Surface), CommandCatalog.Label("gallery.open"),
                facts.Present && facts.GalleryWired && facts.InGallerySequence && !facts.SeveralSelected, reason);
        }
        string favoriteLabel = facts.Favorite switch
        {
            MediaMenuFavorite.On => Texts.T("viewer.favorite.remove"),
            MediaMenuFavorite.Off => Texts.T("viewer.favorite.add"),
            _ => CommandCatalog.Label("favorites.toggle")
        };
        Add(MediaMenuAction.Favorite, "favorites.toggle", favoriteLabel,
            facts.Present && facts.FavoriteWired && facts.Favorite != MediaMenuFavorite.Unknown,
            NeedsFile() ?? MediaMenuReasons.Unavailable, facts.Favorite == MediaMenuFavorite.Unknown ? null : facts.Favorite == MediaMenuFavorite.On);
        Add(MediaMenuAction.Rating, "media.rating.edit", CommandCatalog.Label("media.rating.edit"),
            facts.Present && facts.RatingWired && facts.RatingKnown && facts.RatingBlocked == null,
            NeedsFile() ?? facts.RatingBlocked ?? MediaMenuReasons.Unavailable);
        Add(MediaMenuAction.Tags, "media.tags.edit", CommandCatalog.Label("media.tags.edit"),
            facts.Present && facts.TagsWired && (facts.TagsBlocked == null || facts.TagsBlockedExplained),
            NeedsFile() ?? facts.TagsBlocked ?? MediaMenuReasons.Unavailable);
        Add(MediaMenuAction.Export, "media.export-original", CommandCatalog.Label("media.export-original"),
            facts.Present && facts.ExportWired, NeedsFile() ?? MediaMenuReasons.Unavailable);
        Add(MediaMenuAction.ShowInFolder, "media.show-in-folder", CommandCatalog.Label("media.show-in-folder"),
            facts.Present && facts.FolderWired, NeedsFile() ?? MediaMenuReasons.Unavailable);
        Add(MediaMenuAction.CopyPath, "media.copy-path", CommandCatalog.Label("media.copy-path"),
            facts.Present && facts.CopyPathWired, NeedsFile() ?? MediaMenuReasons.Unavailable);
        Add(MediaMenuAction.Details, "media.details", CommandCatalog.Label("media.details"),
            facts.Present && facts.DetailsWired, NeedsFile() ?? MediaMenuReasons.Unavailable);
        // A post-dependent action never picks one of several posts and never acts without one.
        Add(MediaMenuAction.ShowInThread, "gallery.show-in-thread", CommandCatalog.Label("gallery.show-in-thread"),
            facts.ShowInThreadWired && facts.Posts == MediaMenuPostScope.Single && !facts.SeveralSelected,
            facts.Posts == MediaMenuPostScope.Several ? MediaMenuReasons.SeveralPosts
                : facts.Posts == MediaMenuPostScope.None ? MediaMenuReasons.NoPost
                : facts.SeveralSelected ? MediaMenuReasons.SeveralSelected : MediaMenuReasons.Unavailable);
        if (facts.ProviderMedia)
        {
            bool provider = facts.ProviderWired && facts.ProviderPostKnown && !facts.SeveralSelected;
            string reason = facts.SeveralSelected ? MediaMenuReasons.SeveralSelected : MediaMenuReasons.ProviderPost;
            Add(MediaMenuAction.OpenProviderPost, null, Texts.T("booru.media.openPost"), provider, reason, textKey: "booru.media.openPost");
            Add(MediaMenuAction.CopyProviderPost, null, Texts.T("booru.media.copyPost"), provider, reason, textKey: "booru.media.copyPost");
        }
        return new MediaMenuModel(facts.Surface, facts.Target, items);
    }
}
