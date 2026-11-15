using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class MediaRatingText
{
    internal static string Catalog(string key, params object?[] arguments) => Texts.T(key, arguments);
    // Existing callers share the same catalog-backed presentation as the dialogs.
    internal static string Value(int? value) => LocalizedValue(value);
    internal static string Label => LocalizedLabel;
    internal static string SaveError(Exception error) => LocalizedError("media.rating.error.save", error);
    internal static string RemoveError(Exception error) => LocalizedError("media.rating.error.remove", error);
    internal static string LoadError(Exception error) => LocalizedError("media.rating.error.load", error);
    internal static string Identity(MeowChan.Favorites.FavoriteAssetKey key) => LocalizedIdentity(key);

    internal static string LocalizedValue(int? value) => value.HasValue
        ? Catalog("media.rating.value", value.Value) : Catalog("media.rating.unrated");
    internal static string LocalizedLabel => Catalog("media.rating.label");
    internal static string LocalizedUnavailable => Catalog("media.rating.unavailable");
    internal static string LocalizedFilterAll => Catalog("media.rating.filter.all");
    internal static string LocalizedFilterAtLeast(int value) => Catalog("media.rating.filter.atLeast", value);
    internal static string LocalizedFilterLabel => Catalog("media.rating.filter.label");
    internal static string LocalizedFilterNoLongerMatches => Catalog("media.rating.filter.noLongerMatches");
    internal static string LocalizedIdentity(MeowChan.Favorites.FavoriteAssetKey key) => Catalog(
        "media.rating.identity", key.Thread.Source, key.Thread.Board, key.Thread.Thread,
        key.MediaIdentifier, key.Variant, key.Sha256);
    internal static string AccessibleValue(string value) => Catalog("media.rating.accessibleValue", LocalizedLabel, value);
    internal static string Row(MeowChan.Ratings.MediaRatingEntry entry) => Catalog("media.rating.row", LocalizedIdentity(entry.Key), LocalizedValue(entry.Value));

    // Payload is never a lookup key or format template; key-like strings and braces stay raw.
    internal static string FormatError(string label, string? details) =>
        label + "\n" + Catalog("media.rating.error.details") + details;
    internal static string LocalizedError(string key, Exception error) => FormatError(Catalog(key), error.Message);
}
