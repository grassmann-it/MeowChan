using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Ratings;

namespace MeowChan.DesktopTests;

// Pure helper/source-contract coverage; native-window execution is intentionally separate.
[Collection("Localization")]
public sealed class MediaRatingDialogLanguageRefreshTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public MediaRatingDialogLanguageRefreshTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    public static IEnumerable<object[]> Languages() =>
        new[] { "en", "de", "es", "fr", "ru" }.Select(language => new object[] { language });

    [Theory]
    [MemberData(nameof(Languages))]
    public void CatalogHelpersUseTheRealFiveLanguageValues(string language)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        var expected = Expected(language);
        foreach (string key in new[] { "media.rating.label", "media.rating.unrated", "media.rating.search",
            "media.rating.noSaved", "media.rating.noMatching", "media.rating.confirmRemove", "media.rating.removeThis",
            "media.rating.cancel", "media.rating.close", "media.rating.retry", "media.rating.help",
            "media.rating.keyboardHelp", "media.rating.error.save", "media.rating.error.remove",
            "media.rating.error.load", "media.rating.error.details" })
            Assert.Equal(expected[key], MediaRatingText.Catalog(key));
        Assert.Equal(expected["media.rating.label"], MediaRatingText.LocalizedLabel);
        string value = string.Format(expected["media.rating.value"], 4);
        Assert.Equal(value, MediaRatingText.LocalizedValue(4));
        Assert.Equal(expected["media.rating.unrated"], MediaRatingText.LocalizedValue(null));
        Assert.Equal(string.Format(expected["media.rating.saved"], value), MediaRatingText.LocalizedSaved(4));
        Assert.Equal(string.Format(expected["media.rating.keyboardSave"], value), MediaRatingText.KeyboardValue(4));
        Assert.Equal(string.Format(expected["media.rating.accessibleValue"], expected["media.rating.label"], value),
            MediaRatingText.AccessibleValue(value));
        Assert.Equal(string.Format(expected["media.rating.summary"], 2, 7), MediaRatingText.Catalog("media.rating.summary", 2, 7));
        string identity = string.Format(expected["media.rating.identity"], "4chan", "g", "42", "image", "original", new string('a', 64));
        Assert.Equal(identity, MediaRatingText.LocalizedIdentity(Key()));
        Assert.Equal(string.Format(expected["media.rating.row"], identity, value), MediaRatingText.Row(new MediaRatingEntry(Key(), 4)));
        const string payload = "media.rating.label\ncommand.media.rating.manage {0} {1:N2} <raw>";
        foreach (string key in new[] { "media.rating.error.save", "media.rating.error.remove", "media.rating.error.load" })
        {
            string error = expected[key] + "\n" + expected["media.rating.error.details"] + payload;
            Assert.Equal(error, MediaRatingText.FormatError(MediaRatingText.Catalog(key), payload));
            Assert.Equal(error, MediaRatingText.LocalizedError(key, new Exception(payload)));
        }

    }

    [Theory]
    [InlineData("en", "Manage saved ratings…", "Remove rating", "Remove selected rating…")]
    [InlineData("de", "Gespeicherte Bewertungen verwalten …", "Bewertung entfernen", "Ausgewählte Bewertung entfernen …")]
    [InlineData("es", "Gestionar valoraciones guardadas…", "Quitar valoración", "Quitar valoración seleccionada…")]
    [InlineData("fr", "Gérer les évaluations enregistrées…", "Supprimer l’évaluation", "Supprimer l’évaluation sélectionnée…")]
    [InlineData("ru", "Управление сохранёнными оценками…", "Удалить оценку", "Удалить выбранную оценку…")]
    public void ManagerTitleAndCommandButtonsUseLiteralFiveLanguageExpectations(string language, string title, string clear, string remove)
    {
        Texts.SetLanguage(language);
        Assert.Equal(title, CommandCatalog.Label("media.rating.manage"));
        Assert.Equal(clear, CommandCatalog.Label("media.rating.clear"));
        Assert.Equal(remove, CommandCatalog.Label("media.rating.remove-selected"));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void PreviewHasEveryRatingKeyRatherThanSilentlyUsingEnglishFallback(string language)
    {
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
            "MeowChan.Desktop.Localization.Packs." + language + ".json")!;
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.EndsWith("(Untested)", document.RootElement.GetProperty("nativeName").GetString());
        JsonElement strings = document.RootElement.GetProperty("strings");
        foreach (var pair in Expected(language))
            Assert.Equal(pair.Value, strings.GetProperty(pair.Key).GetString());
    }

    [Theory]
    [InlineData("MediaRatingDialog")]
    [InlineData("MediaRatingsManagerDialog")]
    public void SubscriptionDispatchesAndRejectsCallbacksAfterClosed(string dialog)
    {
        string source = ReadSource(dialog);
        Assert.Contains("Texts.Changed += OnLanguageChanged;", source);
        Assert.Contains("Volatile.Write(ref _closed, true); Texts.Changed -= OnLanguageChanged;", source);
        string callback = Between(source, "private void OnLanguageChanged(", "private void ApplyTexts()");
        Assert.Contains("if (Volatile.Read(ref _closed)) return;", callback);
        Assert.Contains("Dispatcher.UIThread.CheckAccess()", callback);
        Assert.Contains("Dispatcher.UIThread.Post(ApplyTexts)", callback);
        string apply = Between(source, "private void ApplyTexts()", "private void Refresh");
        Assert.True(apply.IndexOf("if (Volatile.Read(ref _closed)) return;", StringComparison.Ordinal)
            < apply.IndexOf("Title =", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MediaRatingDialog")]
    [InlineData("MediaRatingsManagerDialog")]
    public void RefreshOnlyChangesPresentationNotPendingOperationsOrFocus(string dialog)
    {
        string apply = Between(ReadSource(dialog), "private void ApplyTexts()", "private void Refresh");
        foreach (string forbidden in new[] { "Reload();", "Filter();", ".Focus()", "ItemsSource =", "SelectedItem =",
            "_busy =", "_attempt =", "_confirmed =", "_frozen =", "_confirmationOrigin =", "_keyboardChoice =",
            "_rating.Value =", "_rating.DefaultValue =", ".IsEnabled =", ".IsVisible =", "_search.Text =" })
            Assert.DoesNotContain(forbidden, apply);
    }

    [Fact]
    public void DialogRefreshUsesCatalogForAllOwnedPresentationAndPreservesRawErrorDetails()
    {
        string source = ReadSource("MediaRatingDialog");
        string apply = Between(source, "private void ApplyTexts()", "private void RefreshValueText()");
        foreach (string required in new[] { "Title = MediaRatingText.LocalizedLabel", "_heading.Text = Title", "_identity.Text = MediaRatingText.LocalizedIdentity(_key)", "_help.Text = MediaRatingText.Catalog",
            "_confirmedValue.Text = ConfirmedText()", "RefreshValueText();", "CommandBindings.Apply(_clear,",
            "_close.Content = MediaRatingText.Catalog", "_retry.Content = MediaRatingText.Catalog",
            "CommandBindings.BindShortcutHelp(_rating,", "RefreshErrorText();" })
            Assert.Contains(required, apply);
        Assert.Contains("MediaRatingText.FormatError(_errorLabel(), _errorDetails)", source);
        Assert.Contains("_errorDetails = error.Message;", source);
        Assert.DoesNotContain("Texts.T(_errorDetails", source);
        Assert.DoesNotContain("Catalog(_errorDetails", source);
    }

    [Fact]
    public void ManagerRefreshUsesCatalogForSummaryFrozenIdentityAndBoundRowAccessibility()
    {
        string source = ReadSource("MediaRatingsManagerDialog");
        string apply = Between(source, "private void ApplyTexts()", "private void RefreshErrorText()");
        foreach (string required in new[] { "Title = CommandCatalog.Label", "_search.PlaceholderText = MediaRatingText.Catalog",
            "AutomationProperties.SetName(_search,", "_frozen is MediaRatingEntry entry", "CommandBindings.Apply(_remove,", "_confirm.Content = MediaRatingText.Catalog", "_cancel.Content = MediaRatingText.Catalog",
            "_close.Content = MediaRatingText.Catalog", "row.RefreshTexts()", "RefreshSummary();",
            "MediaRatingText.Catalog(\"media.rating.confirmRemove\")", "(_list.SelectedItem as Row)?.ToString()", "RefreshErrorText();" })
            Assert.Contains(required, apply);
        Assert.Contains("new Binding(nameof(Row.ValueText)) { Source = row }", source);
        Assert.Contains("new Binding(nameof(Row.AccessibleText)) { Source = row }", source);
        Assert.Contains("MediaRatingText.FormatError(_errorLabel(), _errorDetails)", source);
    }

    [Fact]
    public void ExistingRowNotifiesValueAndAccessibilityWithoutChangingItsEntry()
    {
        Type type = typeof(MediaRatingsManagerDialog).GetNestedType("Row", BindingFlags.NonPublic)!;
        MediaRatingEntry entry = new(Key(), 4);
        object row = Activator.CreateInstance(type, entry)!;
        List<string?> notifications = new();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            notifications.Clear();
            type.GetMethod("RefreshTexts")!.Invoke(row, null);
            Assert.Equal(new[] { "ValueText", "AccessibleText" }, notifications);
            var expected = Expected(language);
            string value = string.Format(expected["media.rating.value"], 4);
            string identity = string.Format(expected["media.rating.identity"], "4chan", "g", "42", "image", "original", new string('a', 64));
            Assert.Equal(value, type.GetProperty("ValueText")!.GetValue(row));
            Assert.Equal(string.Format(expected["media.rating.row"], identity, value), type.GetProperty("AccessibleText")!.GetValue(row));
            Assert.Same(entry, type.GetProperty("Entry")!.GetValue(row));
        }
    }

    [Fact]
    public void KeyboardDraftAndErrorRefreshKeepTheirOriginalStateAndProvenanceContracts()
    {
        string editor = ReadSource("MediaRatingDialog");
        string value = Between(editor, "private void RefreshValueText()", "private void RefreshErrorText()");
        Assert.Contains("_keyboardChoice", value);
        Assert.Contains("MediaRatingText.KeyboardValue((int)_rating.Value)", value);
        Assert.Contains("MediaRatingText.LocalizedValue(_confirmed)", value);
        Assert.Contains("AutomationProperties.SetName(_rating, MediaRatingText.AccessibleValue", value);
        Assert.DoesNotContain("_rating.Value =", value);
        foreach (string dialog in new[] { "MediaRatingDialog", "MediaRatingsManagerDialog" })
        {
            string source = ReadSource(dialog);
            Assert.DoesNotContain("MediaRatingText.Get(", source);
            Assert.Contains("_errorDetails = error.Message;", source);
            string refresh = Between(source, "private void RefreshErrorText()", dialog == "MediaRatingDialog"
                ? "private string ConfirmedText()" : "private void RefreshSummary()");
            Assert.Contains("MediaRatingText.FormatError(_errorLabel(), _errorDetails)", refresh);
            Assert.DoesNotContain(".Message", refresh);
            Assert.DoesNotContain("Texts.T", refresh);
            Assert.DoesNotContain("MediaRatingText.Catalog", refresh);
        }
    }

    // Literal preview expectations intentionally retain their Untested review status.
    private static IReadOnlyDictionary<string, string> Expected(string language) => language switch
    {
        "en" => new Dictionary<string, string>
        {
            ["media.rating.row"] = "{0} · {1}",
            ["media.rating.accessibleValue"] = "{0}: {1}",
            ["media.rating.label"] = "Rating",
            ["media.rating.value"] = "{0} of 5",
            ["media.rating.unrated"] = "Unrated",
            ["media.rating.saved"] = "Saved rating: {0}",
            ["media.rating.identity"] = "{0} /{1}/ {2} · {3} · {4}\nSHA-256: {5}",
            ["media.rating.search"] = "Search identities",
            ["media.rating.noSaved"] = "No saved ratings.",
            ["media.rating.noMatching"] = "No matching ratings.",
            ["media.rating.summary"] = "{0} shown · {1} saved ratings",
            ["media.rating.confirmRemove"] = "Remove rating for this exact identity?",
            ["media.rating.removeThis"] = "Remove this rating",
            ["media.rating.cancel"] = "Cancel",
            ["media.rating.close"] = "Close",
            ["media.rating.retry"] = "Retry",
            ["media.rating.help"] = "Click to save • Arrows choose, Enter or Space saves",
            ["media.rating.keyboardHelp"] = "Arrow keys choose; Enter or Space saves. Escape closes without saving a keyboard choice.",
            ["media.rating.keyboardSave"] = "{0} (Enter or Space to save)",
            ["media.rating.error.save"] = "Could not save the rating.",
            ["media.rating.error.remove"] = "Could not remove the rating.",
            ["media.rating.error.load"] = "Could not load the ratings.",
            ["media.rating.error.details"] = "Details: ",
        },
        "de" => new Dictionary<string, string>
        {
            ["media.rating.row"] = "{0} · {1}",
            ["media.rating.accessibleValue"] = "{0}: {1}",
            ["media.rating.label"] = "Bewertung",
            ["media.rating.value"] = "{0} von 5",
            ["media.rating.unrated"] = "Unbewertet",
            ["media.rating.saved"] = "Gespeicherte Bewertung: {0}",
            ["media.rating.identity"] = "{0} /{1}/ {2} · {3} · {4}\nSHA-256: {5}",
            ["media.rating.search"] = "Identitäten durchsuchen",
            ["media.rating.noSaved"] = "Keine gespeicherten Bewertungen.",
            ["media.rating.noMatching"] = "Keine passenden Bewertungen.",
            ["media.rating.summary"] = "{0} angezeigt · {1} gespeicherte Bewertungen",
            ["media.rating.confirmRemove"] = "Bewertung für genau diese Identität entfernen?",
            ["media.rating.removeThis"] = "Diese Bewertung entfernen",
            ["media.rating.cancel"] = "Abbrechen",
            ["media.rating.close"] = "Schließen",
            ["media.rating.retry"] = "Wiederholen",
            ["media.rating.help"] = "Klick speichert • Pfeile wählen, Enter oder Leertaste speichert",
            ["media.rating.keyboardHelp"] = "Pfeiltasten wählen; Enter oder Leertaste speichert. Escape schließt ohne Übernahme einer Tastaturauswahl.",
            ["media.rating.keyboardSave"] = "{0} (Enter oder Leertaste zum Speichern)",
            ["media.rating.error.save"] = "Bewertung konnte nicht gespeichert werden.",
            ["media.rating.error.remove"] = "Bewertung konnte nicht entfernt werden.",
            ["media.rating.error.load"] = "Bewertungen konnten nicht geladen werden.",
            ["media.rating.error.details"] = "Details: ",
        },
        "es" => new Dictionary<string, string>
        {
            ["media.rating.row"] = "{0} · {1}",
            ["media.rating.accessibleValue"] = "{0}: {1}",
            ["media.rating.label"] = "Valoración",
            ["media.rating.value"] = "{0} de 5",
            ["media.rating.unrated"] = "Sin valorar",
            ["media.rating.saved"] = "Valoración guardada: {0}",
            ["media.rating.identity"] = "{0} /{1}/ {2} · {3} · {4}\nSHA-256: {5}",
            ["media.rating.search"] = "Buscar identidades",
            ["media.rating.noSaved"] = "No hay valoraciones guardadas.",
            ["media.rating.noMatching"] = "No hay valoraciones coincidentes.",
            ["media.rating.summary"] = "{0} mostradas · {1} valoraciones guardadas",
            ["media.rating.confirmRemove"] = "¿Eliminar la valoración de esta identidad exacta?",
            ["media.rating.removeThis"] = "Eliminar esta valoración",
            ["media.rating.cancel"] = "Cancelar",
            ["media.rating.close"] = "Cerrar",
            ["media.rating.retry"] = "Reintentar",
            ["media.rating.help"] = "Haz clic para guardar • Las flechas eligen; Enter o Espacio guarda",
            ["media.rating.keyboardHelp"] = "Las flechas eligen; Enter o Espacio guarda. Escape cierra sin guardar una elección del teclado.",
            ["media.rating.keyboardSave"] = "{0} (pulsa Enter o Espacio para guardar)",
            ["media.rating.error.save"] = "No se pudo guardar la valoración.",
            ["media.rating.error.remove"] = "No se pudo eliminar la valoración.",
            ["media.rating.error.load"] = "No se pudieron cargar las valoraciones.",
            ["media.rating.error.details"] = "Detalles: ",
        },
        "fr" => new Dictionary<string, string>
        {
            ["media.rating.row"] = "{0} · {1}",
            ["media.rating.accessibleValue"] = "{0} : {1}",
            ["media.rating.label"] = "Évaluation",
            ["media.rating.value"] = "{0} sur 5",
            ["media.rating.unrated"] = "Non évaluée",
            ["media.rating.saved"] = "Évaluation enregistrée : {0}",
            ["media.rating.identity"] = "{0} /{1}/ {2} · {3} · {4}\nSHA-256 : {5}",
            ["media.rating.search"] = "Rechercher des identités",
            ["media.rating.noSaved"] = "Aucune évaluation enregistrée.",
            ["media.rating.noMatching"] = "Aucune évaluation correspondante.",
            ["media.rating.summary"] = "{0} affichées · {1} évaluations enregistrées",
            ["media.rating.confirmRemove"] = "Supprimer l’évaluation de cette identité exacte ?",
            ["media.rating.removeThis"] = "Supprimer cette évaluation",
            ["media.rating.cancel"] = "Annuler",
            ["media.rating.close"] = "Fermer",
            ["media.rating.retry"] = "Réessayer",
            ["media.rating.help"] = "Cliquez pour enregistrer • Les flèches choisissent, Entrée ou Espace enregistre",
            ["media.rating.keyboardHelp"] = "Les flèches choisissent ; Entrée ou Espace enregistre. Échap ferme sans enregistrer un choix au clavier.",
            ["media.rating.keyboardSave"] = "{0} (appuyez sur Entrée ou Espace pour enregistrer)",
            ["media.rating.error.save"] = "Impossible d’enregistrer l’évaluation.",
            ["media.rating.error.remove"] = "Impossible de supprimer l’évaluation.",
            ["media.rating.error.load"] = "Impossible de charger les évaluations.",
            ["media.rating.error.details"] = "Détails : ",
        },
        "ru" => new Dictionary<string, string>
        {
            ["media.rating.row"] = "{0} · {1}",
            ["media.rating.accessibleValue"] = "{0}: {1}",
            ["media.rating.label"] = "Оценка",
            ["media.rating.value"] = "{0} из 5",
            ["media.rating.unrated"] = "Без оценки",
            ["media.rating.saved"] = "Сохранённая оценка: {0}",
            ["media.rating.identity"] = "{0} /{1}/ {2} · {3} · {4}\nSHA-256: {5}",
            ["media.rating.search"] = "Поиск идентификаторов",
            ["media.rating.noSaved"] = "Сохранённых оценок нет.",
            ["media.rating.noMatching"] = "Подходящих оценок нет.",
            ["media.rating.summary"] = "Показано: {0} · сохранённых оценок: {1}",
            ["media.rating.confirmRemove"] = "Удалить оценку для этой точной идентичности?",
            ["media.rating.removeThis"] = "Удалить эту оценку",
            ["media.rating.cancel"] = "Отмена",
            ["media.rating.close"] = "Закрыть",
            ["media.rating.retry"] = "Повторить",
            ["media.rating.help"] = "Нажмите, чтобы сохранить • Выберите стрелками, сохраните клавишей Enter или пробелом",
            ["media.rating.keyboardHelp"] = "Стрелки выбирают; Enter или пробел сохраняет. Escape закрывает окно без сохранения выбора с клавиатуры.",
            ["media.rating.keyboardSave"] = "{0} (Enter или пробел — сохранить)",
            ["media.rating.error.save"] = "Не удалось сохранить оценку.",
            ["media.rating.error.remove"] = "Не удалось удалить оценку.",
            ["media.rating.error.load"] = "Не удалось загрузить оценки.",
            ["media.rating.error.details"] = "Подробности: ",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    private static FavoriteAssetKey Key() =>
        new(new("4chan", "g", "42"), "image", "original", new string('a', 64));

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing anchor: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Missing anchor: " + end);
        return source[first..last];
    }

    private static string ReadSource(string dialog, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop", dialog + ".cs")));
}
