using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;

namespace MeowChan.DesktopTests;

// Pure helper/source-contract checks: no windows, host, profile or media access.
[Collection("Localization")]
public sealed class LegacyRatingLocalizationTests : IDisposable
{
    private readonly string _originalLanguage = Texts.Language;

    public LegacyRatingLocalizationTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_originalLanguage); }

    [Theory]
    [InlineData("en", "Rating", "4 of 5", "Unrated", "Rating: 4 of 5")]
    [InlineData("de", "Bewertung", "4 von 5", "Unbewertet", "Bewertung: 4 von 5")]
    [InlineData("es", "Valoración", "4 de 5", "Sin valorar", "Valoración: 4 de 5")]
    [InlineData("fr", "Évaluation", "4 sur 5", "Non évaluée", "Évaluation : 4 sur 5")]
    [InlineData("ru", "Оценка", "4 из 5", "Без оценки", "Оценка: 4 из 5")]
    public void ExistingValueAndLabelAliasesResolveBundledCatalogs(string language, string label,
        string value, string unrated, string accessibleValue)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Assert.Equal(label, MediaRatingText.Label);
        Assert.Equal(value, MediaRatingText.Value(4));
        Assert.Equal(unrated, MediaRatingText.Value(null));
        Assert.Equal(accessibleValue, MediaRatingText.AccessibleValue(MediaRatingText.Value(4)));
        for (int number = 1; number <= 5; number++)
            Assert.Equal(Texts.T("media.rating.value", number), MediaRatingText.Value(number));
    }

    [Theory]
    [InlineData("en", "Could not save the rating.", "Could not remove the rating.", "Could not load the ratings.", "Details: ")]
    [InlineData("de", "Bewertung konnte nicht gespeichert werden.", "Bewertung konnte nicht entfernt werden.", "Bewertungen konnten nicht geladen werden.", "Details: ")]
    [InlineData("es", "No se pudo guardar la valoración.", "No se pudo eliminar la valoración.", "No se pudieron cargar las valoraciones.", "Detalles: ")]
    [InlineData("fr", "Impossible d’enregistrer l’évaluation.", "Impossible de supprimer l’évaluation.", "Impossible de charger les évaluations.", "Détails : ")]
    [InlineData("ru", "Не удалось сохранить оценку.", "Не удалось удалить оценку.", "Не удалось загрузить оценки.", "Подробности: ")]
    public void ExistingErrorAliasesLocalizeOnlyTheirWrapper(string language, string save,
        string remove, string load, string details)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        const string raw = "media.rating.label {0} {1:N2} <raw> ü 日本\nprovider original";
        Exception error = new InvalidOperationException(raw);
        Assert.Equal(save + "\n" + details + raw, MediaRatingText.SaveError(error));
        Assert.Equal(remove + "\n" + details + raw, MediaRatingText.RemoveError(error));
        Assert.Equal(load + "\n" + details + raw, MediaRatingText.LoadError(error));
    }

    [Fact]
    public void RepeatedLanguageSwitchesUseCurrentCatalogWithoutRewritingIdentityData()
    {
        FavoriteAssetKey key = new(new("synthetic", "board", "42"), "media-{0}", "original", new string('a', 64));
        foreach (string language in new[] { "en", "es", "fr", "ru", "de", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.Equal(MediaRatingText.LocalizedLabel, MediaRatingText.Label);
            Assert.Equal(MediaRatingText.LocalizedValue(3), MediaRatingText.Value(3));
            string shaLabel = language == "fr" ? "SHA-256 : " : "SHA-256: ";
            Assert.Equal("synthetic /board/ 42 · media-{0} · original\n" + shaLabel + new string('a', 64),
                MediaRatingText.Identity(key));
        }
    }

    [Fact]
    public void ArchivePresentationUsesExistingLocalizedHelpers()
    {
        string tile = ReadSource("ArchiveMediaTileView.cs");
        Assert.Contains("MediaRatingText.AccessibleValue(row.RatingLabel)", tile);
        Assert.DoesNotContain("MediaRatingText.Label + \": \"", tile);
        string row = ReadSource("ArchivePresentation.cs");
        Assert.Contains("MediaRatingText.LocalizedValue(Rating)", row);
        string archive = ReadSource("Views/Archive/ArchivePage.Ratings.cs");
        Assert.Contains("MediaRatingText.LocalizedValue(null)", archive);
        Assert.Contains(".Columns[3].Header = MediaRatingText.LocalizedLabel;", archive);
        // Provider errors stay plain text, not catalog keys or format templates.
        Assert.Contains("_mediaRatingError = error.Message;", archive);
        Assert.Contains("ArchiveRatingStatus.Text = _mediaRatingError;", archive);
    }

    private static string ReadSource(string relative, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop", relative)));
}
