using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CommandLabelLocalizationTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public CommandLabelLocalizationTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EveryCommandLabelKeyExistsInTheCatalogRatherThanOnlyInTheCommandFallback(string language)
    {
        // Checking T(key) != key alone would miss an English fallback in a preview.
        // Read each real embedded pack as well as checking the production loader.
        IReadOnlyDictionary<string, string> strings = language switch
        {
            "en" => BuiltInCatalog.English,
            "de" => BuiltInCatalog.German,
            _ => ReadBundledStrings(language)
        };
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Assert.Empty(Texts.Warnings);
        Assert.NotEmpty(CommandCatalog.All);
        foreach (CommandDefinition definition in CommandCatalog.All)
        {
            Assert.True(strings.TryGetValue(definition.LabelKey, out string? label),
                $"{language}: missing {definition.LabelKey} ({definition.Id})");
            Assert.False(string.IsNullOrWhiteSpace(label), $"{language}: empty {definition.LabelKey}");
            Assert.Equal(label, Texts.T(definition.LabelKey));
            Assert.Equal(label, CommandCatalog.Label(definition.Id));
        }

        foreach (string key in new[] { "command.reader.close.compact", "command.reader.expand.compact",
            "command.gallery.open.compact", "command.window.fullscreen.compact" })
        {
            Assert.True(strings.TryGetValue(key, out string? label), $"{language}: missing {key}");
            Assert.False(string.IsNullOrWhiteSpace(label), $"{language}: empty {key}");
            Assert.Equal(label, Texts.T(key));
        }
    }

    [Fact]
    public void FullLabelsPreserveLegacyFallbacksExceptTheExplicitMergedSourcesTitles()
    {
        // Only new full-label entries belong to this migration. Existing feature
        // catalogs deliberately have a few context-specific titles differing from
        // CommandDefinition's unused fallback; do not overwrite those titles.
        var commands = CommandCatalog.All.ToDictionary(command => command.LabelKey, StringComparer.Ordinal);
        foreach (var entry in CommandLabelCatalog.Entries)
        {
            if (entry.Key.EndsWith(".compact", StringComparison.Ordinal)) continue;
            Assert.True(commands.TryGetValue(entry.Key, out CommandDefinition? definition), entry.Key);
            // WP deliberately supersedes these two legacy entries. Keep checking both commands and the actual
            // built-in registration, rather than skipping their old rows or accepting arbitrary non-empty titles.
            (string english, string german) = entry.Key switch
            {
                "command.navigation.sources" => ("Plugins", "Plugins"),
                "command.navigation.plugins" => ("Show all plugins", "Alle Plugins anzeigen"),
                _ => (entry.English, entry.German)
            };
            Assert.Equal(english, definition!.LabelEn);
            Assert.Equal(german, definition.LabelDe);
            Assert.Equal(english, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(german, BuiltInCatalog.German[entry.Key]);
        }
    }

    public static IEnumerable<object[]> NavigationAndSettingsLabels()
    {
        // Exact examples, not a blanket "different from English" rule: names,
        // technical terms and numeric labels may legitimately be identical.
        var labels = new (string Id, string En, string De, string Es, string Fr, string Ru)[]
        {
            ("navigation.downloads", "Downloads", "Downloads", "Descargas", "Téléchargements", "Загрузки"),
            ("navigation.continue-reading", "Continue reading", "Weiterlesen", "Continuar leyendo", "Continuer la lecture", "Продолжить чтение"),
            ("navigation.archive", "Archive", "Archiv", "Archivo", "Archives", "Архив"),
            ("navigation.rules", "Rules", "Regeln", "Reglas", "Règles", "Правила"),
            // The merged page is plain "Plugins" again (Tom, 14.11.2026): the pack labels equal their nav.plugins words.
            ("navigation.sources", "Plugins", "Plugins", "Complementos", "Extensions", "Плагины"),
            ("navigation.diagnostics", "Diagnostics", "Diagnose", "Diagnóstico", "Diagnostics", "Диагностика"),
            ("navigation.status", "Status", "Status", "Estado", "État", "Состояние"),
            ("navigation.plugins", "Show all plugins", "Alle Plugins anzeigen", "Mostrar todos los complementos", "Afficher toutes les extensions", "Показать все плагины"),
            ("navigation.settings", "Settings", "Einstellungen", "Configuración", "Paramètres", "Настройки"),
            ("navigation.about", "About MeowChan", "Über MeowChan", "Acerca de MeowChan", "À propos de MeowChan", "О программе MeowChan"),
            ("navigation.tools-menu", "More", "Mehr", "Más", "Plus", "Ещё"),
            ("navigation.app-menu", "App menu", "Appmenü", "Menú de la aplicación", "Menu de l’application", "Меню приложения"),
            ("settings.browse-folder", "Browse…", "Durchsuchen…", "Examinar…", "Parcourir…", "Обзор…"),
            ("settings.discard", "Discard draft", "Entwurf verwerfen", "Descartar borrador", "Abandonner le brouillon", "Отбросить черновик"),
            ("settings.save", "Save", "Speichern", "Guardar", "Enregistrer", "Сохранить"),
            ("sections.open-navigation", "Navigate sections", "Abschnitte öffnen", "Navegar por las secciones", "Naviguer dans les sections", "Перейти к разделам"),
            ("sections.go-to", "Go to selected section", "Ausgewählten Abschnitt öffnen", "Ir a la sección seleccionada", "Accéder à la section sélectionnée", "Перейти к выбранному разделу"),
            ("appearance.theme", "Change theme", "Farbschema wechseln", "Cambiar tema", "Changer de thème", "Изменить тему"),
            ("export.open-html-dialog", "Export", "Exportieren", "Exportar", "Exporter", "Экспортировать"),
            ("gallery.actual-size", "1:1", "1:1", "1:1", "1:1", "1:1")
        };
        foreach (var label in labels)
        {
            yield return ["en", label.Id, label.En];
            yield return ["de", label.Id, label.De];
            yield return ["es", label.Id, label.Es];
            yield return ["fr", label.Id, label.Fr];
            yield return ["ru", label.Id, label.Ru];
        }
    }

    [Theory]
    [MemberData(nameof(NavigationAndSettingsLabels))]
    public void ApplyUsesLocalizedCatalogTitlesForVisibleContentAndAccessibility(string language, string id, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Button button = new() { Content = "Previous label" };
        var command = new DelegateCommand(() => { });
        button.Command = command;

        CommandBindings.Apply(button, id);

        Assert.Equal(expected, CommandCatalog.Label(id));
        Assert.Equal(expected, button.Content);
        Assert.Equal(expected, AutomationProperties.GetName(button));
        Assert.Equal(CommandBindings.Tooltip(id), ToolTip.GetTip(button));
        Assert.Equal(CommandBindings.Tooltip(id), AutomationProperties.GetHelpText(button));
        Assert.Same(command, button.Command);
        // The icon-with-label branch must also use the full title unless the
        // command explicitly has one of the four catalogued compact titles.
        Assert.Equal(expected, CompactLabel(id));
    }

    [Theory]
    [InlineData("en", "Close", "Reader", "Gallery", "Full screen")]
    [InlineData("de", "Schließen", "Reader", "Galerie", "Vollbild")]
    [InlineData("es", "Cerrar", "Lector", "Galería", "Pantalla completa")]
    [InlineData("fr", "Fermer", "Lecteur", "Galerie", "Plein écran")]
    [InlineData("ru", "Закрыть", "Читалка", "Галерея", "Полный экран")]
    public void AllFourCompactLabelsAreLocalizedWithoutChangingTheFullTitles(string language,
        string close, string read, string gallery, string fullscreen)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        foreach (var (id, expected) in new[] { ("reader.close", close), ("reader.expand", read),
            ("gallery.open", gallery), ("window.fullscreen", fullscreen) })
        {
            Assert.Equal(expected, Texts.T("command." + id + ".compact"));
            Assert.Equal(expected, CompactLabel(id));
            Assert.Equal(Texts.T(CommandCatalog.Get(id).LabelKey), CommandCatalog.Label(id));
        }
    }

    [Fact]
    public void ReapplyingTheSameButtonAcrossAllLanguagesDoesNotRetainAnEnglishOrGermanLabel()
    {
        Button button = new();
        var command = new DelegateCommand(() => { });
        button.Command = command;
        foreach (var (language, expected) in new[] { ("de", "Einstellungen"), ("es", "Configuración"),
            ("fr", "Paramètres"), ("ru", "Настройки"), ("en", "Settings") })
        {
            Texts.SetLanguage(language);
            CommandBindings.Apply(button, "navigation.settings");
            Assert.Equal(expected, button.Content);
            Assert.Equal(expected, AutomationProperties.GetName(button));
            Assert.Same(command, button.Command);
        }
    }

    // Exercise the existing private selector used by the real icon-with-label
    // branch without requiring a bitmap renderer or adding production test API.
    private static string CompactLabel(string id) => (string)typeof(CommandBindings)
        .GetMethod("CompactLabel", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [id])!;

    private static IReadOnlyDictionary<string, string> ReadBundledStrings(string language)
    {
        string resource = "MeowChan.Desktop.Localization.Packs." + language + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded pack: " + resource);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.Equal(language, document.RootElement.GetProperty("code").GetString());
        Assert.EndsWith(" (Untested)", document.RootElement.GetProperty("nativeName").GetString());
        // ToDictionary also rejects duplicate keys; an overwritten duplicate
        // must not pass coverage checks by merely exposing its final value.
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
    }
}
