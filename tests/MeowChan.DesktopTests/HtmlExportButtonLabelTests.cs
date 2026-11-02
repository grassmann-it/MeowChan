using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class HtmlExportButtonLabelTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public HtmlExportButtonLabelTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData("en", "Export as HTML…")]
    [InlineData("de", "Als HTML exportieren…")]
    // Bundles currently fall back to the command catalog's English title.
    [InlineData("es", "Export as HTML…")]
    [InlineData("fr", "Export as HTML…")]
    [InlineData("ru", "Export as HTML…")]
    public void ProductionCommandContentAndIconLabelUseTheWholeCatalogTitle(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        const string id = "export.open-html-dialog";
        Button button = new();
        var command = new DelegateCommand(() => { });
        button.Command = command;
        CommandBindings.Apply(button, id);
        Assert.Equal(expected, CommandCatalog.Label(id));
        Assert.Equal(expected, button.Content);
        Assert.Equal(expected, AutomationProperties.GetName(button));
        Assert.Equal(CommandBindings.Tooltip(id), ToolTip.GetTip(button));
        Assert.Equal(CommandBindings.Tooltip(id), AutomationProperties.GetHelpText(button));
        Assert.Same(command, button.Command);
        Assert.True(double.IsNaN(button.Width));
        // The actual shared icon-with-label branch calls this selector. It needs
        // no renderer here; the native-window smoke checks its realized content.
        MethodInfo compact = typeof(CommandBindings).GetMethod("CompactLabel", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, compact.Invoke(null, [id]));
    }

    [Theory]
    [InlineData("en", "Export all 123 results as HTML…")]
    [InlineData("de", "Alle 123 Treffer als HTML exportieren…")]
    [InlineData("es", "Exportar los 123 resultados como HTML…")]
    [InlineData("fr", "Exporter les 123 résultats en HTML…")]
    [InlineData("ru", "Экспортировать все 123 результатов в HTML…")]
    public void ResultsProductionContentUsesTheFullCountBearingTitle(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Button button = new();
        var command = new DelegateCommand(() => { });
        button.Command = command;
        foreach (int total in new[] { 0, 1, 123, 1234567 })
        {
            ArchivePage.ApplyResultsExportLabel(button, total);
            string title = Texts.T("export.resultsAction", total);
            Assert.Equal(title, button.Content);
            Assert.Equal(title, AutomationProperties.GetName(button));
            Assert.Equal(title, ToolTip.GetTip(button));
            Assert.Equal(Texts.T("export.resultsScope"), AutomationProperties.GetHelpText(button));
            Assert.Same(command, button.Command);
            Assert.True(double.IsNaN(button.Width));
            if (total == 123) Assert.Equal(expected, button.Content);
        }
    }

    [Fact]
    public void ExistingButtonUpdatesToTheNewLanguageAndCountWithoutReplacingItsCommand()
    {
        Button button = new();
        var command = new DelegateCommand(() => { });
        button.Command = command;
        Texts.SetLanguage("en");
        ArchivePage.ApplyResultsExportLabel(button, 1);
        Assert.Equal("Export all 1 results as HTML…", button.Content);
        Texts.SetLanguage("de");
        ArchivePage.ApplyResultsExportLabel(button, 42);
        Assert.Equal("Alle 42 Treffer als HTML exportieren…", button.Content);
        Assert.Same(command, button.Command);
    }
}
