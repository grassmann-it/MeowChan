using System;
using MeowChan.Gallery;

namespace MeowChan.Desktop.Controls;

// Filmstrip flags (decision 123): the small marks a filmstrip tile carries besides the kind badge (video length, GIF): the favourite star, the own
// rating and the "no tags yet" mark. They are the shared media badges (MediaBadges.cs: same icons, same corners, same disc), so a tile looks like a
// tile of the media page. This file is the pure part (no control, no I/O), so the rules are tested without a display:
//   FilmstripFlagSettings  what the person switched on (Settings, "Filmstrip shows")
//   FilmstripFlagFacts     what is known of one medium (taken from the loaded stores by the owner)
//   FilmstripFlagModel     settings + tagging view + a facts source -> the badge state and options of a tile

/// <summary>When the "no tags yet" mark shows on a filmstrip tile (<see cref="Config.GalleryFilmstripFlagUntagged"/>).</summary>
internal enum FilmstripUntaggedMode { Off, WithTaggingView, Always }

/// <summary>The three flags, as a set (what a facts source is asked for).</summary>
[Flags]
internal enum FilmstripFlagKinds { None = 0, Favorite = 1, Rating = 2, Untagged = 4 }

/// <summary>What the person chose to see on the filmstrip tiles; the defaults are the shipped ones (favourite and rating on, untagged with the tagging view).</summary>
internal readonly record struct FilmstripFlagSettings(bool Favorite = true, bool Rating = true, FilmstripUntaggedMode Untagged = FilmstripUntaggedMode.WithTaggingView)
{
    // (not new(): a record struct's parameterless new() ignores the optional defaults of its primary constructor)
    internal static FilmstripFlagSettings Default => new(true, true, FilmstripUntaggedMode.WithTaggingView);

    internal static FilmstripFlagSettings FromConfig(Config config) => new(
        config.GalleryFilmstripFlagFavorite, config.GalleryFilmstripFlagRating, ModeOf(config.GalleryFilmstripFlagUntagged));

    /// <summary>The stored text of the untagged choice as a mode; anything unknown reads as "with the tagging view" (like <see cref="Config.NormalizeGalleryFilmstripUntagged"/>).</summary>
    internal static FilmstripUntaggedMode ModeOf(string? stored) => Config.NormalizeGalleryFilmstripUntagged(stored) switch
    {
        Config.GalleryFilmstripUntaggedAlways => FilmstripUntaggedMode.Always,
        Config.GalleryFilmstripUntaggedOff => FilmstripUntaggedMode.Off,
        _ => FilmstripUntaggedMode.WithTaggingView
    };

    /// <summary>The stored text of a mode (the inverse of <see cref="ModeOf"/>).</summary>
    internal static string TextOf(FilmstripUntaggedMode mode) => mode switch
    {
        FilmstripUntaggedMode.Always => Config.GalleryFilmstripUntaggedAlways,
        FilmstripUntaggedMode.Off => Config.GalleryFilmstripUntaggedOff,
        _ => Config.GalleryFilmstripUntaggedWithTagging
    };

    /// <summary>
    /// Whether the "no tags yet" mark may show now: always with <see cref="FilmstripUntaggedMode.Always"/>, only while the tagging view (the right
    /// tag panel of the gallery) is open with <see cref="FilmstripUntaggedMode.WithTaggingView"/>, never with <see cref="FilmstripUntaggedMode.Off"/>.
    /// </summary>
    internal bool ShowsUntagged(bool taggingViewOpen) => Untagged switch
    {
        FilmstripUntaggedMode.Always => true,
        FilmstripUntaggedMode.WithTaggingView => taggingViewOpen,
        _ => false
    };

    /// <summary>The flags that are shown now (what a facts source needs to read; a flag that is off costs nothing per tile).</summary>
    internal FilmstripFlagKinds Shown(bool taggingViewOpen) =>
        (Favorite ? FilmstripFlagKinds.Favorite : 0) | (Rating ? FilmstripFlagKinds.Rating : 0) | (ShowsUntagged(taggingViewOpen) ? FilmstripFlagKinds.Untagged : 0);
}

/// <summary>
/// What is known of one medium. <paramref name="Rating"/>: the own rating (1 to 5), null when not rated or unknown. <paramref name="Untagged"/>: true when
/// the medium could carry own tags and has none (the media page's rule, thread tags do not count), false when it has some, null when that is not known
/// (tags still loading or unreadable, or the medium cannot carry tags): never a mark then.
/// </summary>
internal readonly record struct FilmstripFlagFacts(bool Favorite = false, int? Rating = null, bool? Untagged = null);

/// <summary>
/// The flags of a filmstrip: the settings, whether the tagging view is open, and where the facts come from. <see cref="StateFor"/> adds the flags that
/// are shown to the kind badge of a tile; the source is asked only for the flags that are shown, and not at all when none is.
/// </summary>
internal sealed class FilmstripFlagModel
{
    private FilmstripFlagSettings _settings = FilmstripFlagSettings.Default;
    private bool _taggingViewOpen;

    /// <summary>Reads what is known of an entry for the flags asked for, from data that is already loaded (no file, no wait). Null: no flag at all.</summary>
    internal Func<GalleryEntry, FilmstripFlagKinds, FilmstripFlagFacts>? Source { get; set; }

    internal FilmstripFlagSettings Settings => _settings;
    internal bool TaggingViewOpen => _taggingViewOpen;

    /// <summary>The flags shown now.</summary>
    internal FilmstripFlagKinds Shown => _settings.Shown(_taggingViewOpen);

    /// <summary>Takes other settings; true when what is shown changed (the live tiles then draw again).</summary>
    internal bool Configure(FilmstripFlagSettings settings)
    {
        FilmstripFlagKinds before = Shown;
        _settings = settings;
        return Shown != before;
    }

    /// <summary>The tagging view opened or closed; true when that changes what is shown.</summary>
    internal bool SetTaggingViewOpen(bool open)
    {
        FilmstripFlagKinds before = Shown;
        _taggingViewOpen = open;
        return Shown != before;
    }

    /// <summary>What the tiles draw: the options of the badge view (the untagged mark only while it is shown).</summary>
    internal MediaBadgeOptions Options => MediaBadgeOptions.FilmstripWith(Shown.HasFlag(FilmstripFlagKinds.Untagged));

    /// <summary>The badge state of a tile: <paramref name="kind"/> (the badges of the file's kind) with the flags that are shown.</summary>
    internal MediaBadgeState StateFor(GalleryEntry entry, in MediaBadgeState kind)
    {
        FilmstripFlagKinds shown = Shown;
        if (shown == FilmstripFlagKinds.None || Source == null) return kind;
        return Compose(kind, Source(entry, shown), shown);
    }

    /// <summary>The pure rule: the flags of <paramref name="shown"/> taken from <paramref name="facts"/> over <paramref name="kind"/>; a flag that is not shown never appears.</summary>
    internal static MediaBadgeState Compose(in MediaBadgeState kind, in FilmstripFlagFacts facts, FilmstripFlagKinds shown) => kind with
    {
        Favorite = shown.HasFlag(FilmstripFlagKinds.Favorite) && facts.Favorite,
        Rating = shown.HasFlag(FilmstripFlagKinds.Rating) && facts.Rating is >= 1 ? facts.Rating : null,
        // The untagged mark is the badge model's "TagCount 0"; the count itself is not a flag of the strip, so a tagged medium shows nothing.
        TagCount = shown.HasFlag(FilmstripFlagKinds.Untagged) && facts.Untagged == true ? 0 : null
    };
}

/// <summary>
/// What the filmstrip's facts were read from (the published tag snapshot, the favourite list, the rating list), compared by reference like the media
/// page's <c>MediaLibraryUserData.SameSourcesAs</c>: a notification of the session that changed none of them redraws nothing.
/// </summary>
internal readonly record struct FilmstripFlagSources(object? Tags, object? Favorites, object? Ratings)
{
    /// <summary>True when both were read from the very same stores.</summary>
    internal bool Same(FilmstripFlagSources other) =>
        ReferenceEquals(Tags, other.Tags) && ReferenceEquals(Favorites, other.Favorites) && ReferenceEquals(Ratings, other.Ratings);
}
