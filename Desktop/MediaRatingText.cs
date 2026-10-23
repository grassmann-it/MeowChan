using System;
using MeowChan.Desktop.Localization;
namespace MeowChan.Desktop;
internal static class MediaRatingText
{
    internal static string Get(string english, string german) => Texts.Language == "de" ? german : english;
    internal static string Value(int? value) => value.HasValue ? Get($"{value} of 5", $"{value} von 5") : Get("Unrated", "Unbewertet");
    internal static string Label => Get("Rating", "Bewertung");
    internal static string SaveError(Exception error) => WithDetails(
        Get("Could not save the rating.", "Bewertung konnte nicht gespeichert werden."), error);
    internal static string RemoveError(Exception error) => WithDetails(
        Get("Could not remove the rating.", "Bewertung konnte nicht entfernt werden."), error);
    internal static string LoadError(Exception error) => WithDetails(
        Get("Could not load the ratings.", "Bewertungen konnten nicht geladen werden."), error);
    private static string WithDetails(string operation, Exception error) => operation + "\nDetails: " + error.Message;
    internal static string Identity(MeowChan.Favorites.FavoriteAssetKey key) => $"{key.Thread.Source} /{key.Thread.Board}/ {key.Thread.Thread} · {key.MediaIdentifier} · {key.Variant}\nSHA-256: {key.Sha256}";
}
