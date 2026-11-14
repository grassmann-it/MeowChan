using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The catalog commands the shared context menus of the rules, statistics, chips, notifications and settings use.</summary>
[Collection("Localization")]
public sealed class ContextMenuCatalogTests : IDisposable
{
    public ContextMenuCatalogTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly string[] Ids =
    [
        "rules.toggle-enabled", "rules.delete", "rules.copy-summary", "statistics.copy-row", "statistics.copy-source", "statistics.reset",
        "archive.copy-filter", "tags.filter-toggle", "tags.filter-include", "tags.filter-exclude", "tags.copy-name", "feedback.copy-text",
        "feedback.dismiss", "feedback.dismiss-all", "settings.reset-default", "settings.copy-id"
    ];

    [Fact]
    public void EveryMenuCommandIsInTheCatalogWithoutAGestureOfItsOwn()
    {
        foreach (string id in Ids)
        {
            Assert.True(CommandCatalog.TryGet(id, out CommandDefinition? command), id);
            Assert.Null(command!.Shortcut);
            Assert.Empty(command.DefaultGestures);
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)));
            Assert.Equal(command.LabelEn, CommandCatalog.Label(id));
        }
    }

    [Fact]
    public void TheLabelsOfTheNewCommandsComeFromTheirCatalogInBothBuiltInLanguages()
    {
        foreach ((string key, string english, string german) in ContextMenuTexts.Entries)
        {
            Assert.Equal(english, BuiltInCatalog.English[key]);
            Assert.Equal(german, BuiltInCatalog.German[key]);
        }
        foreach (CommandDefinition command in Ids.Select(CommandCatalog.Get))
        {
            Texts.SetLanguage("de");
            Assert.Equal(command.LabelDe, CommandCatalog.Label(command.Id));
            Texts.SetLanguage("en");
        }
    }

    [Fact]
    public void TheContextKeyCommandNamesEverySurfaceThatHasAMenuNow()
    {
        CommandDefinition open = CommandCatalog.Get("menu.context-open");
        foreach (string surface in new[] { "rules-overview-row", "statistics-table", "settings-row", "archive-active-filter-chip", "tag-filter-picker", "notification" })
            Assert.Contains(surface, open.Contexts);
        foreach (string context in new[] { "rules", "statistics", "settings", "notification" })
            Assert.Contains(context, open.ExecutionContexts);
        Assert.Equal("Shift+F10", open.Shortcut);
    }
}
