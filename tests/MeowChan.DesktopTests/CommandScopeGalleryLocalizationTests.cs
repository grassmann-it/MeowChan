using System.Text.Json;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure catalog/selector checks. These intentionally do not construct a GalleryViewer or
// request an Avalonia renderer: the UI state and command routing remain outside this delta.
[Collection("Localization")]
public sealed class CommandScopeGalleryLocalizationTests : IDisposable
{
    private readonly string _originalLanguage = Texts.Language;

    public CommandScopeGalleryLocalizationTests() => Texts.Initialize();

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_originalLanguage);
    }

    [Fact]
    public void ScopeHintsKeepExactContextRoutingAndUnknownCommandsNull()
    {
        Texts.SetLanguage("en");

        Assert.Equal("Large reader post area · current thread",
            CommandCatalog.ScopeHint("gallery.open", CommandScope.Reader));
        Assert.Equal("Current reader thread · outside inputs and dialogs",
            CommandCatalog.ScopeHint("export.open-html-dialog", CommandScope.Reader));

        // The two reader-only presentations deliberately require exact Reader context;
        // combined flags must not broaden their scope or change command identity.
        Assert.Null(CommandCatalog.ScopeHint("gallery.open"));
        Assert.Null(CommandCatalog.ScopeHint("gallery.open", CommandScope.Reader | CommandScope.Gallery));
        Assert.Null(CommandCatalog.ScopeHint("export.open-html-dialog", CommandScope.Reader | CommandScope.Downloads));
        Assert.Equal("command.gallery.open", CommandCatalog.Get("gallery.open").LabelKey);
        Assert.Equal("command.export.open-html-dialog", CommandCatalog.Get("export.open-html-dialog").LabelKey);

        var aliases = new[]
        {
            ("archive.previous-page", "Results-list focus · within page boundaries"),
            ("archive.next-page", "Results-list focus · within page boundaries"),
            ("navigation.downloads", "Outside inputs and overlays · when available"),
            ("navigation.archive", "Outside inputs and overlays · when available"),
            ("navigation.rules", "Outside inputs and overlays · when available"),
            ("navigation.sources", "Outside inputs and overlays · when available"),
            ("navigation.diagnostics", "Outside inputs and overlays · when available"),
            ("session.pause", "Outside inputs and overlays · when available"),
        };
        foreach (var (id, expected) in aliases)
            Assert.Equal(expected, CommandCatalog.ScopeHint(id));

        Assert.Equal("Results list · this page only", CommandCatalog.ScopeHint("archive.select-page"));
        Assert.Equal("Focused downloads list", CommandCatalog.ScopeHint("selection.select-all"));
        Assert.Equal("Focused downloads, archive or plugin list", CommandCatalog.ScopeHint("menu.context-open"));
        Assert.Equal("Downloads · URL input pastes text", CommandCatalog.ScopeHint("watch.import-clipboard"));
        Assert.Equal("Downloads · local list", CommandCatalog.ScopeHint("input.focus-download-search"));
        Assert.Equal("Archive · search results", CommandCatalog.ScopeHint("input.focus-search"));
        Assert.Equal("Separate button · all results of the current search",
            CommandCatalog.ScopeHint("export.open-all-results-html-dialog"));
        Assert.Null(CommandCatalog.ScopeHint("gallery.close"));
        Assert.Null(CommandCatalog.ScopeHint("not-a-command"));
    }

    [Fact]
    public void ScopeEntriesPreserveLegacyEnglishGermanTextAndLocalizeTooltipSuffixes()
    {
        var expected = new (string Key, string English, string German, string Spanish, string French, string Russian)[]
        {
            ("command.scope.gallery-open.reader", "Large reader post area · current thread", "Post-Bereich im großen Reader · aktuell gelesener Thread", "Área de publicaciones del lector grande · hilo actual", "Zone des messages du grand lecteur · fil actuel", "Область постов в развёрнутой читалке · текущий тред"),
            ("command.scope.export-open-html.reader", "Current reader thread · outside inputs and dialogs", "Aktuell gelesener Thread · außerhalb von Eingaben und Dialogen", "Hilo actual del lector · fuera de entradas y diálogos", "Fil actuel du lecteur · hors des champs de saisie et des dialogues", "Текущий тред в читалке · вне полей ввода и диалогов"),
            ("command.scope.reader.expand", "In the thread list: Enter", "In der Threadliste: Enter", "En la lista de hilos: Enter", "Dans la liste des fils : Enter", "В списке тредов: Enter"),
            ("command.scope.reader.find", "With the focus in the reader", "Mit Fokus im Reader", "Con el foco en el lector", "Avec le focus dans le lecteur", "Когда фокус в читалке"),
            ("command.scope.archive.select-page", "Results list · this page only", "Ergebnisliste · nur diese Seite", "Lista de resultados · solo esta página", "Liste des résultats · cette page uniquement", "Список результатов · только эта страница"),
            ("command.scope.archive.page-navigation", "Results-list focus · within page boundaries", "Fokus in der Ergebnisliste · innerhalb der Seitengrenzen", "Foco en la lista de resultados · dentro de los límites de página", "Focus sur la liste des résultats · dans les limites des pages", "Фокус в списке результатов · в пределах страниц"),
            ("command.scope.selection.select-all", "Focused downloads list", "Fokussierte Downloadliste", "Lista de descargas enfocada", "Liste des téléchargements ciblée", "Список загрузок в фокусе"),
            ("command.scope.menu.context-open", "Focused downloads, archive or plugin list", "Fokussierte Download-, Archiv- oder Pluginliste", "Lista de descargas, archivo o complementos enfocada", "Liste ciblée des téléchargements, de l’archive ou des extensions", "Список загрузок, архива или плагинов в фокусе"),
            ("command.scope.watch.import-clipboard", "Downloads · URL input pastes text", "Downloads · URL-Eingabe fügt Text ein", "Descargas · la entrada de URL pega texto", "Téléchargements · le champ URL colle le texte", "Загрузки · поле URL вставляет текст"),
            ("command.scope.input.focus-download-search", "Downloads · local list", "Downloads · lokale Liste", "Descargas · lista local", "Téléchargements · liste locale", "Загрузки · локальный список"),
            ("command.scope.input.focus-search", "Archive · search results", "Archiv · Treffer durchsuchen", "Archivo · resultados de búsqueda", "Archives · résultats de recherche", "Архив · результаты поиска"),
            ("command.scope.export.open-all-results-html-dialog", "Separate button · all results of the current search", "Eigener Button · alle Treffer der aktuellen Suche", "Botón separado · todos los resultados de la búsqueda actual", "Bouton séparé · tous les résultats de la recherche actuelle", "Отдельная кнопка · все результаты текущего поиска"),
            ("command.scope.navigation.generic", "Outside inputs and overlays · when available", "Außerhalb von Eingaben und Overlays · bei verfügbarer Aktion", "Fuera de entradas y superposiciones · cuando está disponible", "Hors des champs de saisie et des superpositions · si disponible", "Вне полей ввода и оверлеев · если действие доступно"),
        };

        Assert.Equal(expected.Select(item => item.Key), CommandLabelCatalog.ScopeEntries.Select(item => item.Key));
        foreach (var item in expected)
        {
            foreach (var (language, value) in new[]
            {
                ("en", item.English), ("de", item.German), ("es", item.Spanish),
                ("fr", item.French), ("ru", item.Russian)
            })
            {
                Texts.SetLanguage(language);
                Assert.Equal(value, Texts.T(item.Key));
                Assert.Equal(value, ReadStrings(language)[item.Key]);
            }
        }

        foreach (var (language, suffix) in new[]
        {
            ("en", "Results list · this page only"),
            ("de", "Ergebnisliste · nur diese Seite"),
            ("es", "Lista de resultados · solo esta página"),
            ("fr", "Liste des résultats · cette page uniquement"),
            ("ru", "Список результатов · только эта страница")
        })
        {
            Texts.SetLanguage(language);
            string label = CommandCatalog.Label("archive.select-page");
            string? shortcut = CommandCatalog.ShortcutDisplay("archive.select-page");
            // HB1: the hint is the second line of the tooltip, the key stays on the first.
            Assert.Equal(label + (shortcut is null ? "" : " · " + shortcut) + "\n" + suffix,
                CommandBindings.Tooltip("archive.select-page"));
        }
    }

    [Fact]
    public void ScopeAndPresentationEntriesAreSeparateFromCommandFallbackEntriesAndComposedInBothBuiltIns()
    {
        string[] commandKeys = CommandLabelCatalog.Entries.Select(item => item.Key).ToArray();
        string[] scopeKeys = CommandLabelCatalog.ScopeEntries.Select(item => item.Key).ToArray();
        string[] presentationKeys = CommandLabelCatalog.PresentationEntries.Select(item => item.Key).ToArray();
        string[] addedKeys = scopeKeys.Concat(presentationKeys).ToArray();

        Assert.Equal(addedKeys.Length, addedKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(addedKeys.Intersect(commandKeys, StringComparer.Ordinal));
        Assert.DoesNotContain("command.scope.gallery-open.reader", commandKeys);
        Assert.DoesNotContain("command.gallery.close.compact", commandKeys);
        Assert.Equal(addedKeys.Length, addedKeys.Count(key => BuiltInCatalog.English.ContainsKey(key)));
        Assert.Equal(addedKeys.Length, addedKeys.Count(key => BuiltInCatalog.German.ContainsKey(key)));
        Assert.Equal(BuiltInCatalog.English.Count, BuiltInCatalog.German.Count);

        foreach (var entry in CommandLabelCatalog.ScopeEntries.Concat(CommandLabelCatalog.PresentationEntries))
        {
            Assert.Equal(entry.English, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(entry.German, BuiltInCatalog.German[entry.Key]);
        }
    }

    [Theory]
    [InlineData("en", "Close", "Fit", "Post", "Favorite", "Original", "Full screen")]
    [InlineData("de", "Schließen", "Einpassen", "Post", "Favorit", "Original", "Vollbild")]
    [InlineData("es", "Cerrar", "Ajustar", "Publicación", "Favorito", "Original", "Pantalla completa")]
    [InlineData("fr", "Fermer", "Ajuster", "Message", "Favori", "Original", "Plein écran")]
    [InlineData("ru", "Закрыть", "Вписать", "Пост", "Избранное", "Оригинал", "Полный экран")]
    public void GalleryCompactSelectorsStayLocalizedWhileFullCommandTitlesRemainSeparate(string language,
        string close, string fit, string context, string favorite, string export, string fullscreen)
    {
        Texts.SetLanguage(language);
        Assert.Equal(close, MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryClose"));
        Assert.Equal(fit, MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryFit"));
        Assert.Equal(context, MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryToggleContext"));
        Assert.Equal(favorite, MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryFavorite"));
        Assert.Equal(export, MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryExportOriginal"));
        Assert.Equal(fullscreen, MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryFullscreen"));
        Assert.Null(MeowChan.Desktop.GalleryViewer.CompactLabelFor("GalleryNotAControl"));
        Assert.NotEqual(close, CommandCatalog.Label("gallery.close"));
        Assert.NotEqual(export, CommandCatalog.Label("media.export-original"));
    }

    [Fact]
    public void ResidualMediaRatingFilterKeysAreDisjointAndBundledAcrossAllPreviewLanguages()
    {
        var expected = new (string Key, string English, string German, string Spanish, string French, string Russian)[]
        {
            ("media.rating.unavailable", "Unavailable", "Nicht verfügbar", "No disponible", "Indisponible", "Недоступно"),
            ("media.rating.filter.all", "All ratings", "Alle Bewertungen", "Todas las valoraciones", "Toutes les évaluations", "Все оценки"),
            ("media.rating.filter.atLeast", "At least {0}", "Mindestens {0}", "Al menos {0}", "Au moins {0}", "Не менее {0}"),
            ("media.rating.filter.label", "Rating filter", "Bewertungsfilter", "Filtro de valoraciones", "Filtre d’évaluation", "Фильтр оценок"),
            ("media.rating.filter.noLongerMatches", "The media no longer matches the rating filter.", "Das Medium passt nicht mehr zum Bewertungsfilter.", "El medio ya no coincide con el filtro de valoración.", "Le média ne correspond plus au filtre d’évaluation.", "Медиа больше не соответствует фильтру оценок."),
            ("media.rating.filter.sortDescending", "Rating descending", "Bewertung absteigend", "Valoraciones descendentes", "Évaluations décroissantes", "Оценки по убыванию"),
        };

        Assert.Equal(expected.Select(item => item.Key), MediaRatingCatalog.Entries
            .Where(item => item.Key.StartsWith("media.rating.filter", StringComparison.Ordinal) || item.Key == "media.rating.unavailable")
            .Select(item => item.Key));
        foreach (var item in expected)
        {
            foreach (var (language, value) in new[]
            {
                ("en", item.English), ("de", item.German), ("es", item.Spanish),
                ("fr", item.French), ("ru", item.Russian)
            })
            {
                Texts.SetLanguage(language);
                Assert.Equal(value, Texts.T(item.Key));
                Assert.Equal(value, ReadStrings(language)[item.Key]);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ReadStrings(string language)
    {
        if (language is "en") return BuiltInCatalog.English;
        if (language is "de") return BuiltInCatalog.German;
        string resource = "MeowChan.Desktop.Localization.Packs." + language + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded pack: " + resource);
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }
}
